// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Runtime.ExceptionServices;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using XboxDevCLI.Output;
using XboxDevCLI.Safety;

namespace XboxDevCLI.CommandLine;

/// <summary>
/// An invocation's configuration as loaded: the layered settings if they could be read, else
/// the error reading them, and the warnings about settings a command may use. Loading never
/// writes anything and never throws for configuration that's missing, unreadable, or invalid;
/// whoever uses the configuration decides what to do with the error and the warnings.
/// </summary>
internal sealed class LoadedConfiguration
{
    internal const string InvalidBuiltInSettingsMessage =
        "The built-in settings in this build of the XBOX CLI aren't valid JSON. Reinstall it.";

    // Per async flow, so a test that replaces the directory doesn't affect tests running beside it.
    private static readonly AsyncLocal<string?> InstallDirectory = new();

    private readonly ExceptionDispatchInfo? _error;

    private LoadedConfiguration(
        IConfiguration? configuration,
        ExceptionDispatchInfo? error,
        IReadOnlyDictionary<ServiceEndpoints, string> warnings,
        string? retiredSettingsNotice)
    {
        Configuration = configuration;
        _error = error;
        Warnings = warnings;
        RetiredSettingsNotice = retiredSettingsNotice;
    }

    /// <summary>The layered configuration, or <c>null</c> when it couldn't be loaded.</summary>
    internal IConfiguration? Configuration { get; }

    /// <summary>Why the configuration couldn't be loaded, or <c>null</c> when it loaded.</summary>
    internal Exception? Error => _error?.SourceException;

    /// <summary>
    /// The warning about each service endpoint that's been changed from the one shipped with the
    /// CLI, for a command that sends a token to it to write.
    /// </summary>
    internal IReadOnlyDictionary<ServiceEndpoints, string> Warnings { get; }

    /// <summary>
    /// The note that a settings file an earlier release read is still beside the executable and
    /// is now ignored, or <c>null</c> when there's none.
    /// </summary>
    internal string? RetiredSettingsNotice { get; }

    /// <summary>
    /// Loads the configuration <paramref name="layer"/> builds, keeping a failure, and looks in
    /// <paramref name="installDirectory"/>, by default the executable's, for a settings file an
    /// earlier release read.
    /// </summary>
    internal static LoadedConfiguration Load(Func<IConfiguration> layer, string? installDirectory = null)
    {
        var notice = GetRetiredSettingsNotice(installDirectory ?? InstallDirectory.Value ?? AppContext.BaseDirectory);
        try
        {
            return FromConfiguration(layer(), notice);
        }
        catch (Exception exception) when (IsConfigurationError(exception))
        {
            // The JSON parser's exceptions aren't failures the error boundary reports, so they
            // become one that is, keeping the parser's as the inner exception.
            var error = exception is JsonException or FormatException
                ? new InvalidDataException(InvalidBuiltInSettingsMessage, exception)
                : exception;
            return new LoadedConfiguration(
                null, ExceptionDispatchInfo.Capture(error), new Dictionary<ServiceEndpoints, string>(), notice);
        }
    }

    /// <summary>
    /// Looks for a retired settings file in <paramref name="directory"/> instead of the
    /// executable's, for the calling async flow, until the result is disposed. For tests.
    /// </summary>
    internal static IDisposable UseInstallDirectory(string directory)
    {
        var previous = InstallDirectory.Value;
        InstallDirectory.Value = directory;
        return new RestoreInstallDirectory(previous);
    }

    /// <summary>
    /// The note for the first <c>appsettings*.json</c> in <paramref name="directory"/>, which
    /// earlier releases read and this one doesn't, so a user who changed a setting there learns
    /// it no longer applies. <c>null</c> when there's none or the directory can't be read.
    /// </summary>
    internal static string? GetRetiredSettingsNotice(string directory)
    {
        try
        {
            var file = Directory.EnumerateFiles(directory, "appsettings*.json")
                .Order(StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            return file is null
                ? null
                : $"Note: '{TerminalText.SingleLine(file)}' is no longer read. Settings come from the built-in defaults, XBOX_ "
                    + "environment variables, and command options. Delete the file to hide this note.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The configuration, or the error loading it, thrown as it was first thrown.</summary>
    /// <exception cref="InvalidOperationException">The built-in settings are missing from the build.</exception>
    /// <exception cref="InvalidDataException">The built-in settings aren't valid JSON.</exception>
    internal IConfiguration GetOrThrow()
    {
        _error?.Throw();
        return Configuration!;
    }

    /// <summary>
    /// This configuration with <paramref name="overrides"/> layered on top. The warnings are
    /// kept, so the overrides must not change an endpoint, and a failure is kept as it is.
    /// </summary>
    internal LoadedConfiguration With(IDictionary<string, string?> overrides) =>
        Configuration is null
            ? this
            : new LoadedConfiguration(
                new ConfigurationBuilder().AddConfiguration(Configuration).AddInMemoryCollection(overrides).Build(),
                null,
                Warnings,
                RetiredSettingsNotice);

    /// <summary>The failures loading configuration reports for settings that are missing, unreadable, or invalid.</summary>
    internal static bool IsConfigurationError(Exception exception) =>
        exception is IOException or InvalidDataException or FormatException or UnauthorizedAccessException
            or InvalidOperationException or JsonException;

    private sealed class RestoreInstallDirectory(string? previous) : IDisposable
    {
        public void Dispose() => InstallDirectory.Value = previous;
    }

    private static LoadedConfiguration FromConfiguration(IConfiguration configuration, string? retiredSettingsNotice)
    {
        var warnings = new Dictionary<ServiceEndpoints, string>();
        if (EndpointOverrideWarning.GetWarning(configuration[EndpointOverrideWarning.ConfigurationKey]) is { } playFab)
        {
            warnings[ServiceEndpoints.PlayFab] = playFab;
        }

        // A missing or blank gateway is an error the command reports when it reads the URL, so
        // there's nothing to warn about.
        var gateway = configuration[EndpointOverrideWarning.XboxPartnerConfigurationKey];
        if (!string.IsNullOrWhiteSpace(gateway) && EndpointOverrideWarning.GetXboxPartnerWarning(gateway) is { } xboxPartner)
        {
            warnings[ServiceEndpoints.XboxPartner] = xboxPartner;
        }

        return new LoadedConfiguration(configuration, null, warnings, retiredSettingsNotice);
    }
}
