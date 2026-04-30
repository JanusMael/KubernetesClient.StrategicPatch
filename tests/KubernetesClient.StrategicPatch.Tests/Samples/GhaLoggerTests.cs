using KubernetesClient.StrategicPatch.Samples.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace KubernetesClient.StrategicPatch.Tests.Samples;

// GhaLogger tests mutate process-global environment variables (GITHUB_ACTIONS,
// GITHUB_STEP_SUMMARY) and so must serialise against each other. The rest of the suite
// continues to run in parallel; only this class is pinned.
[TestClass]
[DoNotParallelize]
public sealed class GhaLoggerTests
{
    [TestMethod]
    public void OutsideGha_EmitsNothing()
    {
        var sw = new StringWriter();
        var gha = new GhaLogger(NullLogger.Instance, stdout: sw, forceGha: false);

        gha.Group("hello");
        gha.Notice("title", "msg");
        gha.Warning("title", "msg");
        gha.Error("title", "msg");
        gha.EndGroup();

        Assert.AreEqual(string.Empty, sw.ToString());
    }

    [TestMethod]
    public void InsideGha_GroupAndEndGroup_AreEmitted()
    {
        var sw = new StringWriter();
        var gha = new GhaLogger(NullLogger.Instance, stdout: sw, forceGha: true);

        gha.Group("Generated SMP Patch (107 bytes)");
        gha.EndGroup();

        var lines = sw.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        CollectionAssert.AreEqual(
            new[] { "::group::Generated SMP Patch (107 bytes)", "::endgroup::" },
            lines);
    }

    [TestMethod]
    public void InsideGha_Notice_IncludesTitleAndMessage()
    {
        var sw = new StringWriter();
        var gha = new GhaLogger(NullLogger.Instance, stdout: sw, forceGha: true);

        gha.Notice("Scale Skipped", "No state drift detected for default/api.");

        Assert.AreEqual(
            "::notice title=Scale Skipped::No state drift detected for default/api." + Environment.NewLine,
            sw.ToString());
    }

    [TestMethod]
    public void Annotations_EscapeNewlinesPercentAndCarriageReturn()
    {
        var sw = new StringWriter();
        var gha = new GhaLogger(NullLogger.Instance, stdout: sw, forceGha: true);

        gha.Error("multi\nline\rweird%char", "body");

        var output = sw.ToString();
        StringAssert.Contains(output, "::error title=multi%0Aline%0Dweird%25char::body");
    }

    [TestMethod]
    public void AppendStepSummary_WhenEnvSet_AppendsLine()
    {
        var path = Path.GetTempFileName();
        try
        {
            Environment.SetEnvironmentVariable("GITHUB_STEP_SUMMARY", path);
            var gha = new GhaLogger(NullLogger.Instance, forceGha: true);

            gha.AppendStepSummary("| 2026-04-29 | api | replicas=5 | 107 bytes | dry-run |");
            gha.AppendStepSummary("| 2026-04-29 | api | replicas=6 | 107 bytes | ok |");

            var lines = File.ReadAllLines(path);
            Assert.HasCount(2, lines);
            StringAssert.Contains(lines[0], "replicas=5");
            StringAssert.Contains(lines[1], "replicas=6");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITHUB_STEP_SUMMARY", null);
            File.Delete(path);
        }
    }

    [TestMethod]
    public void AppendStepSummary_WhenEnvUnset_IsNoOp()
    {
        Environment.SetEnvironmentVariable("GITHUB_STEP_SUMMARY", null);
        var gha = new GhaLogger(NullLogger.Instance, forceGha: true);

        // Should simply return without throwing.
        gha.AppendStepSummary("anything");
    }

    [TestMethod]
    public void IsGitHubActions_ReadsEnvWhenForceFlagOmitted()
    {
        var prev = Environment.GetEnvironmentVariable("GITHUB_ACTIONS");
        try
        {
            Environment.SetEnvironmentVariable("GITHUB_ACTIONS", "true");
            var on = new GhaLogger(NullLogger.Instance);
            Assert.IsTrue(on.IsGitHubActions);

            Environment.SetEnvironmentVariable("GITHUB_ACTIONS", "false");
            var off = new GhaLogger(NullLogger.Instance);
            Assert.IsFalse(off.IsGitHubActions);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITHUB_ACTIONS", prev);
        }
    }
}
