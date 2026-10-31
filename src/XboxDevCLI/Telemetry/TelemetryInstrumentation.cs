// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using System.Runtime.InteropServices;
using XboxDevCLI.PlayFab.Generated;
using XboxDevCLI.Services;

namespace XboxDevCLI.Telemetry;

internal static class TelemetryInstrumentation
{
    internal const string SourceName = "Microsoft.XboxDevCLI";
    internal const string SchemaVersion = "2";

    internal static readonly string ServiceVersion =
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(Program).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    internal static readonly ActivitySource ActivitySource = new(SourceName, ServiceVersion);
    internal static readonly Meter Meter = new(SourceName, ServiceVersion);

    private static readonly Counter<long> CommandInvocations =
        Meter.CreateCounter<long>("xboxdevcli.command.invocations", description: "CLI command invocations.");

    private static readonly Histogram<double> CommandDuration =
        Meter.CreateHistogram<double>("xboxdevcli.command.duration", "ms", "CLI command duration.");

    private static readonly Counter<long> PlayFabRequests =
        Meter.CreateCounter<long>("xboxdevcli.playfab.requests", description: "PlayFab dependency calls.");

    private static readonly Histogram<double> PlayFabRequestDuration =
        Meter.CreateHistogram<double>("xboxdevcli.playfab.duration", "ms", "PlayFab dependency call duration.");

    private static readonly Counter<long> UnhandledFailures =
        Meter.CreateCounter<long>("xboxdevcli.process.unhandled_failures", description: "Unhandled CLI failures.");

    private static readonly IReadOnlyDictionary<string, PlayFabApiMetadata.ApiEndpoint> KnownEndpoints =
        PlayFabApiMetadata.AllEndpoints
            .GroupBy(endpoint => NormalizeApiPath(endpoint.Path), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.OrdinalIgnoreCase);

    internal static void RecordCommand(string commandName, int exitCode, TimeSpan duration, string authMode)
    {
        var tags = CreateCommonTags();
        tags.Add("cli.command", SanitizeCommandName(commandName));
        tags.Add("cli.outcome", exitCode == 0 ? "success" : "failure");
        tags.Add("cli.exit_code", BucketExitCode(exitCode));
        tags.Add("cli.auth_mode", authMode);

        CommandInvocations.Add(1, tags);
        CommandDuration.Record(duration.TotalMilliseconds, tags);
    }

    internal static void RecordUnhandledFailure(Exception exception)
    {
        var tags = CreateCommonTags();
        tags.Add("error.type", ClassifyException(exception));
        UnhandledFailures.Add(1, tags);
    }

    internal static PlayFabDependencyOperation StartPlayFabDependency(
        string apiPath,
        bool telemetryEnabled)
    {
        var normalizedPath = NormalizeApiPath(apiPath);
        var known = KnownEndpoints.TryGetValue(normalizedPath, out var endpoint);
        var group = known ? endpoint!.Group : "unknown";
        var operation = known ? endpoint!.OperationId : "unknown";

        Activity? activity = null;
        if (telemetryEnabled)
        {
            activity = ActivitySource.StartActivity("PlayFab API", ActivityKind.Client);
            activity?.SetTag("rpc.system", "playfab");
            activity?.SetTag("rpc.service", group);
            activity?.SetTag("rpc.method", operation);
        }

        return new PlayFabDependencyOperation(activity, group, operation, telemetryEnabled);
    }

    internal static string ClassifyException(Exception exception) => exception switch
    {
        PlayFabApiException { HttpStatusCode: null, InnerException: { } inner } => ClassifyException(inner),
        OperationCanceledException => "cancelled",
        HttpRequestException => "transport",
        PlayFabApiException => "playfab_api",
        InvalidOperationException => "invalid_operation",
        ArgumentException => "invalid_argument",
        _ => "unexpected",
    };

    private static TagList CreateCommonTags() => new()
    {
        { "telemetry.schema_version", SchemaVersion },
        { "service.version", ServiceVersion },
        { "os.type", GetOsType() },
        { "process.runtime.name", RuntimeInformation.FrameworkDescription },
    };

    private static string SanitizeCommandName(string commandName)
    {
        if (string.IsNullOrWhiteSpace(commandName) || commandName.Length > 64)
        {
            return "unknown";
        }

        return commandName.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            ? commandName.ToLowerInvariant()
            : "unknown";
    }

    private static string BucketExitCode(int exitCode) => exitCode switch
    {
        0 => "0",
        1 => "1",
        _ => "other",
    };

    private static string NormalizeApiPath(string apiPath) =>
        "/" + apiPath.Trim().TrimStart('/');

    private static string GetOsType()
    {
        if (OperatingSystem.IsWindows())
        {
            return "windows";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "darwin";
        }

        if (OperatingSystem.IsLinux())
        {
            return "linux";
        }

        return "unknown";
    }

    internal sealed class PlayFabDependencyOperation : IDisposable
    {
        private readonly Activity? _activity;
        private readonly string _group;
        private readonly string _operation;
        private readonly bool _telemetryEnabled;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private bool _completed;

        internal PlayFabDependencyOperation(
            Activity? activity,
            string group,
            string operation,
            bool telemetryEnabled)
        {
            _activity = activity;
            _group = group;
            _operation = operation;
            _telemetryEnabled = telemetryEnabled;
        }

        internal void SetSuccess(int httpStatusCode)
        {
            Complete("success", BucketHttpStatus(httpStatusCode), errorType: null);
            _activity?.SetStatus(ActivityStatusCode.Ok);
        }

        internal void SetFailure(Exception exception, int? httpStatusCode = null)
        {
            var errorType = ClassifyException(exception);
            Complete("failure", BucketHttpStatus(httpStatusCode), errorType);
            _activity?.SetTag("error.type", errorType);
            _activity?.SetStatus(ActivityStatusCode.Error);
        }

        public void Dispose()
        {
            if (!_completed)
            {
                Complete("failure", "none", "unexpected");
                _activity?.SetStatus(ActivityStatusCode.Error);
            }

            _activity?.Dispose();
        }

        private void Complete(string outcome, string httpStatus, string? errorType)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            _stopwatch.Stop();

            // Enforce opt-out at the instrumentation layer: when telemetry is disabled we
            // record no metrics, rather than relying on the absence of a MeterProvider
            // listener elsewhere.
            if (!_telemetryEnabled)
            {
                return;
            }

            var tags = CreateCommonTags();
            tags.Add("rpc.service", _group);
            tags.Add("rpc.method", _operation);
            tags.Add("dependency.outcome", outcome);
            tags.Add("http.response.status_code_class", httpStatus);
            if (errorType != null)
            {
                tags.Add("error.type", errorType);
            }

            PlayFabRequests.Add(1, tags);
            PlayFabRequestDuration.Record(_stopwatch.Elapsed.TotalMilliseconds, tags);
            _activity?.SetTag("http.response.status_code_class", httpStatus);
        }

        private static string BucketHttpStatus(int? statusCode)
        {
            if (statusCode == null)
            {
                return "none";
            }

            return (statusCode.Value / 100) switch
            {
                2 => "2xx",
                3 => "3xx",
                4 => "4xx",
                5 => "5xx",
                _ => "other",
            };
        }
    }
}
