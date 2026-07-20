namespace Spectari.Util;

internal enum LogAudience
{
    DiagnosticOnly,
    OperatorAndDiagnostic,
}

/// <summary>Classifies legacy console events without coupling pipeline classes
/// to the UI sink.</summary>
internal static class LogEventClassifier
{
    internal static LogAudience Classify(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return LogAudience.DiagnosticOnly;

        if (line.StartsWith("[ready]", StringComparison.Ordinal)
            || line.StartsWith("[childjob]", StringComparison.Ordinal))
            return LogAudience.OperatorAndDiagnostic;

        if (line.StartsWith("[pipeline]", StringComparison.Ordinal))
            return ContainsAny(line, "is stalled", "Starting a one-time CPU recovery",
                    "CPU recovery is already active")
                ? LogAudience.OperatorAndDiagnostic
                : LogAudience.DiagnosticOnly;

        if (line.StartsWith("[stream-controller]", StringComparison.Ordinal)
            || line.StartsWith("[host-access]", StringComparison.Ordinal))
            return line.Contains("failed", StringComparison.OrdinalIgnoreCase)
                ? LogAudience.OperatorAndDiagnostic
                : LogAudience.DiagnosticOnly;

        if (StartsWithAny(line,
                "Failed to start:",
                "Video pipeline stalled;",
                "GPU encoder exited;",
                "Warning: libx264",
                "Asking for administrator approval",
                "Still configuring the port"))
            return LogAudience.OperatorAndDiagnostic;

        if (line.StartsWith("[ws]", StringComparison.Ordinal))
            return ContainsAny(line, " connected", " disconnected", " rejected")
                ? LogAudience.OperatorAndDiagnostic
                : LogAudience.DiagnosticOnly;

        if (line.StartsWith("[shutdown]", StringComparison.Ordinal))
            return ContainsAny(line, "stopping", "stopped", "done", "did not finish")
                ? LogAudience.OperatorAndDiagnostic
                : LogAudience.DiagnosticOnly;

        if (line.StartsWith("[audio]", StringComparison.Ordinal))
            return ContainsAny(line, "failed", "error", "without audio", "feeding silence",
                    "default output device changed", "not running")
                ? LogAudience.OperatorAndDiagnostic
                : LogAudience.DiagnosticOnly;

        if (line.StartsWith("[capture]", StringComparison.Ordinal))
            return ContainsAny(line, "failed", "unavailable", "no frames", "never delivered",
                    "worth trying", "selected window no longer exists", "switching to")
                ? LogAudience.OperatorAndDiagnostic
                : LogAudience.DiagnosticOnly;

        if (line.StartsWith("[encoder]", StringComparison.Ordinal))
            return ContainsAny(line, "failed", "falling back", "could not run", "exited unexpectedly")
                ? LogAudience.OperatorAndDiagnostic
                : LogAudience.DiagnosticOnly;

        if (line.StartsWith("[http]", StringComparison.Ordinal))
            return ContainsAny(line, "THIS PC ONLY", "server failed")
                ? LogAudience.OperatorAndDiagnostic
                : LogAudience.DiagnosticOnly;

        return LogAudience.DiagnosticOnly;
    }

    private static bool ContainsAny(string line, params string[] values) =>
        values.Any(value => line.Contains(value, StringComparison.OrdinalIgnoreCase));

    private static bool StartsWithAny(string line, params string[] values) =>
        values.Any(value => line.StartsWith(value, StringComparison.OrdinalIgnoreCase));
}
