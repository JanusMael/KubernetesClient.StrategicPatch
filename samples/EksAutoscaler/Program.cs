using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using k8s;
using k8s.Autorest;
using k8s.Models;
using KubernetesClient.StrategicPatch;
using KubernetesClient.StrategicPatch.Samples.Common;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;

// EKS-Fargate-style autoscaler sample. Demonstrates the four pieces called out in the rough plan:
//
//   1. Ghost Request Prevention   — branch on StrategicPatchResult.IsEmpty before calling the API.
//   2. Resilient Execution        — Polly retry on 429 / 5xx with exponential backoff.
//   3. GitHub Step Summary inject — markdown table row written to $GITHUB_STEP_SUMMARY.
//   4. EKS Audit-Log correlation  — Audit-Id response header surfaced in error annotations.
//
// Runs offline by default against a fixture file under ./fixtures/. Set --live to point at a real
// cluster (kwok or otherwise) using whichever kubeconfig context is current.

var fixturePath = Path.Combine(AppContext.BaseDirectory, "fixtures", "deployment.json");
var fixtureArg = args.FirstOrDefault(a => a.StartsWith("--fixture=", StringComparison.Ordinal));
if (fixtureArg is not null)
{
    fixturePath = fixtureArg["--fixture=".Length..];
}
var live = args.Contains("--live");
var desiredReplicas = ParseDesiredReplicas(args, fallback: 5);

using var loggerFactory = LoggerFactory.Create(b =>
    b.AddConsole(o => o.FormatterName = Microsoft.Extensions.Logging.Console.ConsoleFormatterNames.Simple)
     .SetMinimumLevel(LogLevel.Information));
var inner = loggerFactory.CreateLogger("eks-autoscaler");
var gha = new GhaLogger(inner);

var deploymentName = Environment.GetEnvironmentVariable("DEPLOYMENT_NAME") ?? "api";
var deploymentNs = Environment.GetEnvironmentVariable("DEPLOYMENT_NAMESPACE") ?? "default";

inner.LogInformation(
    "EksAutoscaler starting deployment={Deploy} namespace={Ns} desiredReplicas={Replicas} mode={Mode} gha={Gha}",
    deploymentName, deploymentNs, desiredReplicas, live ? "live" : "offline", gha.IsGitHubActions);

V1Deployment current;
IKubernetes? client = null;
if (live)
{
    var config = KubernetesClientConfiguration.BuildConfigFromConfigFile();
    client = new Kubernetes(config);
    try
    {
        current = await client.AppsV1.ReadNamespacedDeploymentAsync(deploymentName, deploymentNs);
    }
    catch (HttpOperationException ex)
    {
        var auditId = TryGetHeader(ex, "Audit-Id");
        gha.Error(
            $"EKS read failed (Audit-Id: {auditId})",
            $"Failed to read deployment {deploymentNs}/{deploymentName}: {ex.Message}");
        return 1;
    }
}
else
{
    if (!File.Exists(fixturePath))
    {
        inner.LogError("Fixture not found: {Path}", fixturePath);
        return 66; // EX_NOINPUT
    }
    var fixtureJson = await File.ReadAllTextAsync(fixturePath);
    current = KubernetesJson.Deserialize<V1Deployment>(fixtureJson)
        ?? throw new InvalidOperationException("Fixture deserialised to null.");
}

// Sparse-modified pattern: only the fields we want to assert. Other fields stay null and are
// dropped at serialisation, so admission-controller-injected annotations / sidecars are preserved
// when combined with IgnoreNullValuesInModified.
var sparseModified = new V1Deployment
{
    ApiVersion = current.ApiVersion ?? "apps/v1",
    Kind = current.Kind ?? "Deployment",
    Metadata = new V1ObjectMeta
    {
        Name = current.Metadata.Name,
        NamespaceProperty = current.Metadata.NamespaceProperty,
    },
    Spec = new V1DeploymentSpec { Replicas = desiredReplicas },
};

var options = new StrategicPatchOptions
{
    IgnoreNullValuesInModified = true,
    EnforceOptimisticConcurrency = true,
    Logger = gha,
};

var result = current.CreateStrategicPatch(sparseModified, options);

// 1. Ghost Request Prevention.
if (result.IsEmpty)
{
    gha.Notice("Scale Skipped",
        $"No state drift detected for {deploymentNs}/{deploymentName}. Skipping EKS API call.");
    inner.LogInformation("Empty patch — nothing to do.");
    return 0;
}

gha.Group($"Generated SMP Patch ({result.PayloadBytes} bytes)");
// Write directly to stdout so the body is sequenced inside the group; ILogger writes go through
// a separate flush path and would otherwise race past ::endgroup::.
Console.Out.WriteLine(PrettyPrint((string)result.Patch.Content));
gha.EndGroup();

if (!live)
{
    inner.LogInformation(
        "Offline mode: would PATCH {Ns}/{Name} with {Bytes}-byte SMP body. Use --live to apply.",
        deploymentNs, deploymentName, result.PayloadBytes);
    AppendStepSummary(gha, deploymentName, deploymentNs, desiredReplicas, result.PayloadBytes, status: "dry-run");
    return 0;
}

// 2. Resilient Execution.
var retry = new ResiliencePipelineBuilder()
    .AddRetry(new RetryStrategyOptions
    {
        ShouldHandle = new PredicateBuilder().Handle<HttpOperationException>(ex =>
            ex.Response.StatusCode == HttpStatusCode.TooManyRequests
            || (int)ex.Response.StatusCode >= 500),
        BackoffType = DelayBackoffType.Exponential,
        Delay = TimeSpan.FromSeconds(1),
        MaxRetryAttempts = 3,
        OnRetry = args =>
        {
            inner.LogWarning(
                "Retrying after {Attempt} failures (next delay {Delay}): {Reason}",
                args.AttemptNumber, args.RetryDelay, args.Outcome.Exception?.Message);
            return ValueTask.CompletedTask;
        },
    })
    .Build();

try
{
    await retry.ExecuteAsync(async _ =>
    {
        await client!.AppsV1.PatchNamespacedDeploymentAsync(
            result.Patch, deploymentName, deploymentNs);
    });

    AppendStepSummary(gha, deploymentName, deploymentNs, desiredReplicas, result.PayloadBytes, status: "ok");
    inner.LogInformation("Patched {Ns}/{Name} successfully.", deploymentNs, deploymentName);
    return 0;
}
catch (HttpOperationException ex)
{
    // 4. EKS Audit-Log correlation.
    var auditId = TryGetHeader(ex, "Audit-Id") ?? "UNKNOWN";
    if (ex.Response.StatusCode == HttpStatusCode.Conflict)
    {
        gha.Notice(
            $"Scale Aborted (Audit: {auditId})",
            $"State drifted (409). Fargate is likely provisioning {deploymentNs}/{deploymentName}.");
        AppendStepSummary(gha, deploymentName, deploymentNs, desiredReplicas, result.PayloadBytes, status: "conflict");
        return 0;
    }
    gha.Error(
        $"EKS Webhook Rejection (Audit: {auditId})",
        ex.Response.Content ?? ex.Message);
    AppendStepSummary(gha, deploymentName, deploymentNs, desiredReplicas, result.PayloadBytes, status: "error");
    return 1;
}

// ---- helpers ---------------------------------------------------------------------------------

static int ParseDesiredReplicas(string[] args, int fallback)
{
    var arg = args.FirstOrDefault(a => a.StartsWith("--replicas=", StringComparison.Ordinal));
    if (arg is null)
    {
        var env = Environment.GetEnvironmentVariable("DESIRED_REPLICAS");
        if (int.TryParse(env, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fromEnv))
        {
            return fromEnv;
        }
        return fallback;
    }
    return int.Parse(arg["--replicas=".Length..], CultureInfo.InvariantCulture);
}

static string? TryGetHeader(HttpOperationException ex, string name)
{
    if (ex.Response.Headers is null)
    {
        return null;
    }
    return ex.Response.Headers.TryGetValue(name, out var values) ? values.FirstOrDefault() : null;
}

static string PrettyPrint(string json)
{
    var node = JsonNode.Parse(json);
    return node?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true })
        ?? json;
}

static void AppendStepSummary(GhaLogger gha, string name, string ns, int replicas, int bytes, string status)
{
    var ts = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    gha.AppendStepSummary($"| {ts} | {ns}/{name} | replicas={replicas} | {bytes} bytes | {status} |");
}
