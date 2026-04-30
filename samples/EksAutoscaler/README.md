# EksAutoscaler sample

Demonstrates how to combine `KubernetesClient.StrategicPatch` with the
operational concerns called out in `Plan-v2.md` Phase 7:

| Concern                          | Where it lives                                            |
| -------------------------------- | --------------------------------------------------------- |
| Ghost-request prevention         | `if (result.IsEmpty)` skip                                |
| Resilient execution              | Polly retry on 429 / 5xx                                  |
| GHA workflow commands            | `samples/Common/GhaLogger.cs`                             |
| `$GITHUB_STEP_SUMMARY` markdown  | `samples/Common/GhaLogger.cs::AppendStepSummary`          |
| EKS audit-log correlation        | `Audit-Id` header extraction on `HttpOperationException`  |

The library itself is CI-vendor-agnostic. Everything GHA / EKS-shaped lives here.

## Run offline (default)

Reads `fixtures/deployment.json` and dry-runs the patch — nothing hits a cluster.

```bash
dotnet run --project samples/EksAutoscaler -- --replicas=5
```

Set `GITHUB_ACTIONS=true` to see the workflow commands:

```bash
GITHUB_ACTIONS=true \
GITHUB_STEP_SUMMARY=$(mktemp) \
dotnet run --project samples/EksAutoscaler -- --replicas=5
cat "$GITHUB_STEP_SUMMARY"
```

Pass `--replicas=<N>` (or `DESIRED_REPLICAS=<N>`) to drive the input. Pass a
value matching the fixture (currently `replicas: 3`) to exercise the
`::notice title=Scale Skipped::` path.

## Run against a real cluster

Append `--live`. The current kubeconfig context is used.

For local end-to-end verification with no cloud cost,
[kwok](https://kwok.sigs.k8s.io/) is a good choice — it stubs the K8s API
without scheduling real Pods.

```bash
kwokctl create cluster --name smp-sample
kubectl apply -f fixtures/deployment.json     # seed the deployment
GITHUB_ACTIONS=true \
GITHUB_STEP_SUMMARY=$(mktemp) \
dotnet run --project samples/EksAutoscaler -- --live --replicas=5
```

## What you'll see

A non-empty diff:

```
::group::Generated SMP Patch (107 bytes)
{
  "spec": { "replicas": 5 },
  "metadata": { "uid": "...", "resourceVersion": "12345" }
}
::endgroup::
```

A no-op diff:

```
::notice title=Scale Skipped::No state drift detected for default/api. Skipping EKS API call.
```

A 409 conflict on `--live`:

```
::notice title=Scale Aborted (Audit: <header-value>)::State drifted (409). Fargate is likely provisioning default/api.
```

A non-409 server rejection:

```
::error title=EKS Webhook Rejection (Audit: <header-value>)::<response body>
```
