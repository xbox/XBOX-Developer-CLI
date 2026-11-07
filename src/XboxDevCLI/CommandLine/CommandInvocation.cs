// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using XboxDevCLI.Services;

namespace XboxDevCLI.CommandLine;

/// <summary>
/// One invocation's shared dependencies: the authentication inputs captured from the environment,
/// the configuration loaded when the invocation began, and the sign-in service. <see cref="Prepare"/>
/// validates what the command declares it needs (<see cref="CommandRequirements"/>), and writes the
/// warnings for the endpoints it declares it calls (<see cref="CommandEndpoints"/>), before the
/// command runs; the getters then return that prepared state and never load or validate anything
/// themselves.
/// </summary>
/// <remarks>
/// Context reparsing gives the reparsed invocation a <see cref="ForResolved">copy</see> that shares
/// the captured inputs, the loaded configuration, the notes and warnings already written, and the
/// sign-in service, so the command acts as the account that read its saved values and each note
/// is written once.
/// </remarks>
internal sealed class CommandInvocation
{
    private readonly ParseResult _parseResult;
    private readonly Written _written;
    private bool _configurationPrepared;
    private AuthenticationService? _authentication;

    /// <summary>
    /// Captures the authentication inputs and loads the configuration
    /// <paramref name="layerConfiguration"/> builds, looking for a retired settings file in
    /// <paramref name="installDirectory"/>, by default the executable's. Neither validates,
    /// writes, or throws for invalid settings.
    /// </summary>
    internal CommandInvocation(ParseResult parseResult, Func<IConfiguration> layerConfiguration, string? installDirectory = null)
        : this(
            parseResult,
            AutomationAuthenticationSettings.CaptureEnvironment(),
            LoadedConfiguration.Load(layerConfiguration, installDirectory),
            new Written())
    {
    }

    private CommandInvocation(
        ParseResult parseResult,
        AutomationAuthenticationSettings.EnvironmentSnapshot authenticationEnvironment,
        LoadedConfiguration loadedConfiguration,
        Written written)
    {
        _parseResult = parseResult;
        AuthenticationEnvironment = authenticationEnvironment;
        LoadedConfiguration = loadedConfiguration;
        _written = written;
    }

    /// <summary>The authentication inputs, captured once when the invocation began.</summary>
    internal AutomationAuthenticationSettings.EnvironmentSnapshot AuthenticationEnvironment { get; }

    /// <summary>
    /// The configuration, loaded once when the invocation began, with its error and warnings.
    /// Telemetry reads its settings from it when it loaded; commands use <see cref="Configuration"/>.
    /// </summary>
    internal LoadedConfiguration LoadedConfiguration { get; }

    /// <summary>The selected mode's name, or <c>invalid</c>, without validating anything.</summary>
    internal string AuthenticationModeName => AuthenticationEnvironment.SelectedModeName;

    /// <summary>
    /// True unless the inputs select user sign-in, including when they're invalid, so inspection
    /// never falls back to a user account.
    /// </summary>
    internal bool IsAutomationSelected =>
        AuthenticationModeName != AutomationAuthenticationSettings.UserMode.ModeName;

    /// <summary>The prepared configuration.</summary>
    internal IConfiguration Configuration =>
        _configurationPrepared ? LoadedConfiguration.Configuration! : throw NotPrepared(nameof(Configuration));

    /// <summary>The prepared sign-in service for the selected mode.</summary>
    internal AuthenticationService Authentication =>
        _authentication ?? throw NotPrepared(nameof(Authentication));

    /// <summary>
    /// The loaded configuration, or the error loading it, for a caller that tests the layering
    /// itself. Commands use <see cref="Configuration"/>, after <see cref="Prepare"/>.
    /// </summary>
    internal IConfiguration LoadConfiguration() => LoadedConfiguration.GetOrThrow();

    /// <summary>
    /// Prepares what <paramref name="requirement"/> needs. Each component validates its own
    /// inputs: the configuration must have loaded, and the sign-in service checks its settings and
    /// credential files, refusing options that don't apply to the selected mode. Then the note
    /// about a retired settings file, and the warning for each of <paramref name="endpoints"/>
    /// that's been changed, are written to stderr, once per invocation. Nothing reads the token
    /// cache, acquires a token, or changes anything. Running it again does nothing more.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The built-in settings are missing, or the sign-in settings aren't valid.
    /// </exception>
    /// <exception cref="InvalidDataException">The built-in settings aren't valid JSON.</exception>
    internal void Prepare(CommandRequirement requirement, ServiceEndpoints endpoints)
    {
        if (requirement == CommandRequirement.None)
        {
            return;
        }

        var configuration = LoadedConfiguration.GetOrThrow();
        _configurationPrepared = true;
        WriteRetiredSettingsNotice();
        WriteEndpointWarnings(endpoints);

        if (requirement == CommandRequirement.Authentication && _authentication is null)
        {
            RefuseUserAuthenticationOptionsInAutomation();
            ContextResolver.RefuseProfileInAutomation(
                _parseResult,
                ContextProfiles.Normalize(_parseResult.GetValue(ContextProfiles.Option)));
            _authentication = AuthenticationService.Create(configuration, AuthenticationEnvironment);
        }
    }

    /// <summary>Makes <paramref name="authentication"/> the prepared sign-in service, for tests.</summary>
    internal void UseAuthentication(AuthenticationService authentication)
    {
        _ = LoadedConfiguration.GetOrThrow();
        _configurationPrepared = true;
        _authentication = authentication;
    }

    /// <summary>
    /// The invocation for <paramref name="resolved"/>, the same command line with context values
    /// filled in. It shares this invocation's captured inputs, loaded configuration, written
    /// warnings, and sign-in service, adding the resolved title the way configuration loading adds
    /// the flag.
    /// </summary>
    internal CommandInvocation ForResolved(ParseResult resolved) =>
        new(
            resolved,
            AuthenticationEnvironment,
            LoadedConfiguration.With(new Dictionary<string, string?>
            {
                ["PlayFab:TitleId"] = resolved.GetValue(Program.PlayFabTitleIdOption)?.Trim(),
            }),
            _written)
        {
            _configurationPrepared = _configurationPrepared,
            _authentication = _authentication,
        };

    /// <summary>
    /// Writes the note about a settings file an earlier release read, once per invocation, so a
    /// user who changed a setting there learns it no longer applies. It isn't written with
    /// <c>--output json</c> or <c>json-compact</c>, where a command such as <c>auth status</c>
    /// writes its errors to stderr as JSON, which a line of text before them would break.
    /// </summary>
    private void WriteRetiredSettingsNotice()
    {
        if (LoadedConfiguration.RetiredSettingsNotice is not { } notice
            || _written.RetiredSettingsNotice
            || IsJsonOutput())
        {
            return;
        }

        _written.RetiredSettingsNotice = true;
        Console.Error.WriteLine(notice);
    }

    private bool IsJsonOutput() =>
        _parseResult.GetResult(Program.OutputOption) is not null
        && _parseResult.GetValue(Program.OutputOption) is { } format
        && format.StartsWith("json", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Writes the warning for each of <paramref name="endpoints"/> that's been changed and isn't
    /// already written. The warning names where requests and the access token will go, so it's
    /// written before the command sends anything.
    /// </summary>
    private void WriteEndpointWarnings(ServiceEndpoints endpoints)
    {
        foreach (var (endpoint, warning) in LoadedConfiguration.Warnings.OrderBy(entry => entry.Key))
        {
            if (!endpoints.HasFlag(endpoint) || _written.Endpoints.HasFlag(endpoint))
            {
                continue;
            }

            _written.Endpoints |= endpoint;
            Console.Error.WriteLine(warning);
        }
    }

    /// <summary>
    /// Refuses <c>--client-id</c> and <c>--tenant-id</c> in a service-principal mode rather than
    /// ignoring them, saying where that mode takes its identity from.
    /// </summary>
    private void RefuseUserAuthenticationOptionsInAutomation()
    {
        if (!IsAutomationSelected)
        {
            return;
        }

        var mode = AuthenticationModeName;
        foreach (var option in new[] { Program.ClientIdOption, Program.TenantIdOption })
        {
            if (_parseResult.GetResult(option) is not { Implicit: false })
            {
                continue;
            }

            var guidance = mode switch
            {
                "clientsecret" or "certificate" or "federatedtoken" or "githuboidc" =>
                    $"Set {(option == Program.ClientIdOption
                        ? AutomationAuthenticationSettings.ClientIdVariable
                        : AutomationAuthenticationSettings.TenantIdVariable)} instead.",
                "azurecli" => "Select the service principal or managed identity by signing in to the Azure CLI.",
                "managedidentity" when option == Program.ClientIdOption =>
                    $"Set {AutomationAuthenticationSettings.ClientIdVariable} for a user-assigned managed identity, "
                    + "or leave it unset for the system-assigned identity.",
                "managedidentity" => "The managed identity determines the tenant.",
                _ => $"Check {AutomationAuthenticationSettings.ModeVariable} and its settings.",
            };
            throw new AuthenticationConfigurationException(
                $"{option.Name} doesn't apply when {AutomationAuthenticationSettings.ModeVariable} is '{mode}'. "
                + $"Remove {option.Name}. {guidance}");
        }
    }

    // A command reading a dependency it didn't declare is a bug, not a user error, so it isn't
    // one of the failures the error boundary reports as a message.
    private static UnreachableException NotPrepared(string dependency) =>
        new($"{dependency} was read before the invocation prepared it. Declare the command's "
            + $"{nameof(CommandRequirement)} with {nameof(CommandRequirements)}.{nameof(CommandRequirements.Requires)}.");

    /// <summary>What the invocation has written to stderr, shared with its resolved copy.</summary>
    private sealed class Written
    {
        internal ServiceEndpoints Endpoints { get; set; } = ServiceEndpoints.None;

        internal bool RetiredSettingsNotice { get; set; }
    }
}
