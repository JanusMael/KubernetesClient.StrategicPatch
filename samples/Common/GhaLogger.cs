using System.Globalization;
using Microsoft.Extensions.Logging;

namespace KubernetesClient.StrategicPatch.Samples.Common;

/// <summary>
/// <see cref="ILogger"/> wrapper that emits GitHub Actions workflow commands when running
/// inside a GHA runner (<c>GITHUB_ACTIONS=true</c>). Off the runner it falls through to a
/// caller-supplied inner logger so local development output stays unchanged.
/// </summary>
/// <remarks>
/// All workflow-command syntax is contained here so the strategic-merge library itself can
/// stay CI-vendor-agnostic. The <see cref="Group(string)"/> / <see cref="EndGroup"/> /
/// <see cref="Notice(string,string)"/> / <see cref="Error(string,string)"/> APIs match the GHA
/// reference at <c>https://docs.github.com/en/actions/using-workflows/workflow-commands-for-github-actions</c>.
/// </remarks>
public sealed class GhaLogger : ILogger
{
    private readonly ILogger _inner;
    private readonly TextWriter _stdout;
    private readonly bool _isGha;

    public GhaLogger(ILogger inner, TextWriter? stdout = null, bool? forceGha = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _stdout = stdout ?? Console.Out;
        _isGha = forceGha
            ?? string.Equals(
                Environment.GetEnvironmentVariable("GITHUB_ACTIONS"),
                "true",
                StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when running inside a GHA runner (or forced via the constructor flag).</summary>
    public bool IsGitHubActions => _isGha;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _inner.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
        => _inner.Log(logLevel, eventId, state, exception, formatter);

    /// <summary>Opens a collapsible group; pair with <see cref="EndGroup"/>.</summary>
    public void Group(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        if (!_isGha)
        {
            return;
        }
        _stdout.WriteLine($"::group::{Escape(title)}");
    }

    public void EndGroup()
    {
        if (!_isGha)
        {
            return;
        }
        _stdout.WriteLine("::endgroup::");
    }

    /// <summary>Emits an annotation at the notice level.</summary>
    public void Notice(string title, string message) => Annotate("notice", title, message);

    /// <summary>Emits an annotation at the warning level.</summary>
    public void Warning(string title, string message) => Annotate("warning", title, message);

    /// <summary>Emits an annotation at the error level.</summary>
    public void Error(string title, string message) => Annotate("error", title, message);

    /// <summary>
    /// Appends a markdown line to <c>$GITHUB_STEP_SUMMARY</c> when set. The path is reread on
    /// every call so a test (or the runner) can rewrite it between writes.
    /// </summary>
    public void AppendStepSummary(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var path = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (string.IsNullOrEmpty(path))
        {
            return;
        }
        File.AppendAllText(path, markdown.EndsWith('\n') ? markdown : markdown + Environment.NewLine);
    }

    private void Annotate(string level, string title, string message)
    {
        if (!_isGha)
        {
            return;
        }
        // Workflow commands escape: %0A for newline, %0D for carriage return, %25 for percent.
        _stdout.WriteLine(
            string.Format(CultureInfo.InvariantCulture,
                "::{0} title={1}::{2}",
                level, Escape(title), Escape(message)));
    }

    private static string Escape(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }
        return value
            .Replace("%", "%25", StringComparison.Ordinal)
            .Replace("\r", "%0D", StringComparison.Ordinal)
            .Replace("\n", "%0A", StringComparison.Ordinal);
    }
}
