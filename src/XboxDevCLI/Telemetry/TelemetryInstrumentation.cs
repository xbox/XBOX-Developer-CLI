// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using XboxDevCLI.Safety;
using XboxDevCLI.Services;

namespace XboxDevCLI.Telemetry;

/// <summary>The fixed set of <c>error.type</c> values the telemetry event can carry.</summary>
internal static class TelemetryErrorTypes
{
    internal const string Usage = "usage";
    internal const string Context = "context";
    internal const string Auth = "auth";
    internal const string NotConfirmed = "not_confirmed";
    internal const string Transport = "transport";
    internal const string ServiceRejected = "service_rejected";
    internal const string ServiceError = "service_error";
    internal const string LocalTool = "local_tool";
    internal const string Io = "io";
    internal const string Cancelled = "cancelled";
    internal const string Other = "_OTHER";

    internal static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Usage, Context, Auth, NotConfirmed, Transport, ServiceRejected, ServiceError, LocalTool, Io,
        Cancelled, Other,
    };
}

/// <summary>
/// The one telemetry event the CLI sends per invocation, before sanitization. Every field is
/// sanitized in <see cref="TelemetryInstrumentation.ToAttributes"/>, the only place the event's
/// attributes are built.
/// </summary>
internal sealed record CommandInvocationEvent(
    string Command,
    string AuthMode,
    int ExitCode,
    string? ErrorType,
    int? HttpStatusCode,
    TimeSpan Duration,
    IReadOnlyCollection<string> Options,
    IReadOnlyCollection<string> ContextKeys,
    bool Ci);

internal static class TelemetryInstrumentation
{
    internal const string SchemaVersion = "3";
    internal const string EventName = "xbox.cli.command_invocation";
    internal const string CustomEventNameAttribute = "microsoft.custom_event.name";
    internal const string Unknown = "unknown";

    /// <summary>
    /// The context keys whose names may appear in <c>xbox.cli.context_keys</c> when they had a
    /// value, from the command line, an <c>XBOX_</c> variable, or saved context. Never their values.
    /// </summary>
    internal static readonly IReadOnlySet<string> ContextKeyNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "playfab-title-id", "sandbox-id", "seller-id", "title-id",
    };

    /// <summary>Environment variables whose presence, never value, marks a CI run.</summary>
    internal static readonly IReadOnlyList<string> CiVariables =
    [
        "CI", "TF_BUILD", "GITHUB_ACTIONS", "BUILD_NUMBER", "RUN_ID", "TEAMCITY_VERSION", "JENKINS_URL",
        "CODEBUILD_BUILD_ID", "GITLAB_CI", "CIRCLECI", "BUILDKITE", "APPVEYOR", "TRAVIS",
    ];

    internal static readonly string ServiceVersion =
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(Program).Assembly.GetName().Version?.ToString()
        ?? Unknown;

    /// <summary>
    /// Builds the event's attributes. Anything unexpected becomes <c>unknown</c> or <c>_OTHER</c>,
    /// or is dropped, so a value the CLI didn't intend to send never leaves the process.
    /// </summary>
    internal static IReadOnlyList<KeyValuePair<string, object?>> ToAttributes(CommandInvocationEvent invocation)
    {
        var errorType = invocation.ErrorType is null ? null : SanitizeErrorType(invocation.ErrorType);
        var outcome = errorType == TelemetryErrorTypes.Cancelled ? "cancelled"
            : invocation.ExitCode == 0 ? "success"
            : "failure";
        if (outcome == "success")
        {
            errorType = null;
        }
        else
        {
            errorType ??= TelemetryErrorTypes.Other;
        }

        var attributes = new List<KeyValuePair<string, object?>>
        {
            new(CustomEventNameAttribute, EventName),
            new("xbox.cli.schema_version", SchemaVersion),
            new("service.version", ServiceVersion),
            new("os.type", GetOsType()),
            new("os.version", GetOsMajorVersion()),
            new("host.arch", GetHostArch(RuntimeInformation.OSArchitecture)),
            new("process.runtime.name", ".NET"),
            new("process.runtime.version", Environment.Version.ToString()),
            new("xbox.cli.command", SanitizeCommandName(invocation.Command)),
            new("xbox.cli.auth_mode", SanitizeCommandName(invocation.AuthMode)),
            new("xbox.cli.outcome", outcome),
            new("process.exit.code", invocation.ExitCode.ToString(CultureInfo.InvariantCulture)),
        };

        if (errorType is not null)
        {
            attributes.Add(new("error.type", errorType));
        }

        if (invocation.HttpStatusCode is >= 100 and <= 599 and var status)
        {
            attributes.Add(new("http.response.status_code", status.ToString(CultureInfo.InvariantCulture)));
        }

        attributes.Add(new(
            "xbox.cli.duration_ms",
            ((long)Math.Max(0, invocation.Duration.TotalMilliseconds)).ToString(CultureInfo.InvariantCulture)));
        attributes.Add(new("xbox.cli.options", JoinNames(invocation.Options, IsSafeName)));
        attributes.Add(new("xbox.cli.context_keys", JoinNames(invocation.ContextKeys, ContextKeyNames.Contains)));
        attributes.Add(new("xbox.cli.ci", invocation.Ci ? "true" : "false"));
        return attributes;
    }

    /// <summary>What <paramref name="exception"/> says about why the invocation failed.</summary>
    internal static string ClassifyException(Exception exception) => exception switch
    {
        OperationCanceledException { InnerException: TimeoutException } => TelemetryErrorTypes.Transport,
        OperationCanceledException => TelemetryErrorTypes.Cancelled,
        DestructiveOperationNotConfirmedException => TelemetryErrorTypes.NotConfirmed,
        // A status reached before the body failed to arrive, such as a 200, isn't the failure's reason.
        PlayFabApiException { HttpStatusCode: >= 400 and < 600 and var status } => ClassifyStatus(status),
        PlayFabApiException { InnerException: IOException or HttpRequestException } => TelemetryErrorTypes.Transport,
        PlayFabApiException { InnerException: { } inner } => ClassifyException(inner),
        XboxPartnerApiException partner => ClassifyStatus((int)partner.StatusCode),
        XboxPartnerResponseException { InnerException: OperationCanceledException or HttpRequestException or IOException } =>
            TelemetryErrorTypes.Transport,
        XboxPartnerResponseException => TelemetryErrorTypes.ServiceError,
        AuthenticationConfigurationException or MsalException or MsalCachePersistenceException =>
            TelemetryErrorTypes.Auth,
        HttpRequestException { StatusCode: { } status } => ClassifyStatus((int)status),
        HttpRequestException or TimeoutException => TelemetryErrorTypes.Transport,
        IOException or UnauthorizedAccessException or InvalidDataException => TelemetryErrorTypes.Io,
        _ => TelemetryErrorTypes.Other,
    };

    /// <summary>Maps an HTTP failure status to <c>service_rejected</c> (4xx) or <c>service_error</c> (5xx).</summary>
    internal static string ClassifyStatus(int statusCode) => statusCode switch
    {
        >= 400 and < 500 => TelemetryErrorTypes.ServiceRejected,
        >= 500 and < 600 => TelemetryErrorTypes.ServiceError,
        _ => TelemetryErrorTypes.Other,
    };

    /// <summary>
    /// The canonical long names of the options the user supplied to <paramref name="parseResult"/>'s
    /// command or its parents, without dashes. Read from the parsed options, never the arguments or
    /// their values.
    /// </summary>
    internal static IReadOnlyList<string> GetSuppliedOptions(ParseResult parseResult)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        for (SymbolResult? result = parseResult.CommandResult; result is not null; result = result.Parent)
        {
            if (result is not CommandResult command)
            {
                continue;
            }

            foreach (var child in command.Children)
            {
                if (child is OptionResult { Implicit: false } option)
                {
                    names.Add(option.Option.Name.TrimStart('-', '/'));
                }
            }
        }

        return [.. names];
    }

    /// <summary>Whether any CI variable is set, by presence only. Never reads more than whether it's empty.</summary>
    internal static bool IsCi(Func<string, string?> getEnvironmentVariable) =>
        CiVariables.Any(name => !string.IsNullOrEmpty(getEnvironmentVariable(name)));

    internal static string SanitizeCommandName(string? commandName)
    {
        if (string.IsNullOrWhiteSpace(commandName) || commandName.Length > 64)
        {
            return Unknown;
        }

        return IsSafeName(commandName) ? commandName.ToLowerInvariant() : Unknown;
    }

    internal static string SanitizeErrorType(string errorType) =>
        TelemetryErrorTypes.All.Contains(errorType) ? errorType : TelemetryErrorTypes.Other;

    internal static string GetOsType()
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

        return Unknown;
    }

    internal static string GetOsMajorVersion() =>
        Environment.OSVersion.Version.Major.ToString(CultureInfo.InvariantCulture);

    internal static string GetHostArch(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "amd64",
        Architecture.Arm64 => "arm64",
        _ => Unknown,
    };

    private static bool IsSafeName(string name) =>
        name.Length is > 0 and <= 64
        && name.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static string JoinNames(IEnumerable<string> names, Func<string, bool> allowed) =>
        string.Join(',', names
            .Where(allowed)
            .Select(name => name.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal));
}
