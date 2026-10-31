// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace XboxDevCLI.Services;

/// <summary>
/// Writes an optional local audit trail for PlayFab API invocations.
/// </summary>
/// <remarks>
/// Guards against repudiation: without a local record, neither a user nor an agent leaves
/// evidence of the API operations it ran. The log is opt-in, stays on the user's machine,
/// and records metadata only — never request bodies, responses, or tokens.
/// <para>
/// This is intentionally separate from the telemetry pipeline. Telemetry is anonymous and
/// remote, and <c>docs/telemetry.md</c> commits to never collecting PlayFab title IDs; an audit
/// trail is attributable and local, and the title ID is exactly what makes it useful. The two
/// have opposite requirements, so they do not share a sink.
/// </para>
/// </remarks>
internal sealed class AuditLogger : IDisposable
{
    private static readonly Uri NormalizationBase =
        new("https://placeholder.invalid/", UriKind.Absolute);

    /// <summary>Audit files roll daily; this many days are retained.</summary>
    private const int RetainedFileCountLimit = 31;

    private readonly Logger? _logger;
    private readonly string _titleId;

    internal AuditLogger(IConfiguration configuration, string titleId, string? logPath = null)
    {
        _titleId = titleId;

        if (!configuration.GetValue("AuditLog:Enabled", false))
        {
            return;
        }

        var path = logPath ?? Path.Combine(CliState.GetDefaultDirectory(), "audit.jsonl");

        // Configured in code rather than from configuration on purpose: an audit trail must not
        // be silently disableable by a stray minimum-level setting. The only supported switch is
        // AuditLog:Enabled, above.
        try
        {
            _logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.Fallible(
                    sink => sink.File(
                        new CompactJsonFormatter(),
                        path,
                        restrictedToMinimumLevel: LogEventLevel.Information,
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: RetainedFileCountLimit,
                        fileSizeLimitBytes: null,
                        shared: true),
                    new AuditFailureListener())
                .CreateLogger();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: could not initialize the audit log: {ex.Message}");
        }
    }

    /// <summary>
    /// Records a completed invocation.
    /// </summary>
    internal void WriteInvocation(string apiPath, int exitStatus)
    {
        if (_logger == null)
        {
            return;
        }

        var resolvedApiPath = ResolveApiPath(apiPath);

        try
        {
            _logger.Information(
                "PlayFab invocation {ResolvedApiPath} {TitleId} {ExitStatus}",
                resolvedApiPath,
                _titleId,
                exitStatus);
        }
        catch (Exception ex)
        {
            // Auditing must not change the outcome of an API invocation. But a silent failure
            // would leave the caller unable to tell "nothing was changed" from "the record was
            // lost", so surface it on stderr instead of swallowing it.
            Console.Error.WriteLine(
                $"Warning: could not write the audit log entry for {resolvedApiPath}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        try
        {
            _logger?.Dispose();
        }
        catch (Exception ex)
        {
            // Disposal flushes and closes the sink, so it can fail for the same reasons a write
            // can (a full disk, a revoked handle). The client is disposed after the API call has
            // already completed, so letting that escape would turn a successful call into a
            // failed command — report it on stderr instead, exactly as WriteInvocation does.
            Console.Error.WriteLine($"Warning: could not close the audit log: {ex.Message}");
        }
    }

    private static string ResolveApiPath(string apiPath)
    {
        var relative = apiPath.Trim().Replace('\\', '/').TrimStart('/');
        if (Uri.TryCreate(NormalizationBase, relative, out var resolved))
        {
            return resolved.AbsolutePath;
        }

        return "/" + relative.Split('?', '#')[0];
    }

    private sealed class AuditFailureListener : ILoggingFailureListener
    {
        public void OnLoggingFailed(
            object sender,
            LoggingFailureKind kind,
            string message,
            IReadOnlyCollection<LogEvent>? events,
            Exception? exception) =>
            Console.Error.WriteLine($"Warning: could not write the audit log: {message}");
    }
}
