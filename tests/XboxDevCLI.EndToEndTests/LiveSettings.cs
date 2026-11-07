// Copyright (C) Microsoft Corporation. All rights reserved.

namespace XboxDevCLI.EndToEndTests;

public enum LiveService
{
    Authentication,
    PlayFab,
    XboxPartner,
}

/// <summary>
/// The harness inputs for a live run, read from <c>E2E_*</c> and GitHub OIDC variables.
/// </summary>
/// <remarks>
/// The harness's own variables are deliberately not <c>XBOX_*</c>: the CLI reads every
/// <c>XBOX_</c> variable as configuration, so the child process gets only the ones the
/// harness sets itself. Live tests are opt-in. Without <c>E2E_GITHUB_ENVIRONMENT</c> they skip, so an
/// ordinary <c>dotnet test</c> never contacts a service. Once a run opts in, any missing or
/// inconsistent setting fails instead of skipping.
/// </remarks>
internal sealed record LiveSettings(
    E2EProfile Profile,
    string CliDirectory,
    string Scope,
    string ClientId,
    string TenantId)
{
    internal const string CliPathVariable = "E2E_CLI_PATH";
    internal const string EnvironmentVariable = "E2E_GITHUB_ENVIRONMENT";
    internal const string ClientIdVariable = "XBOX_AUTHENTICATION__CLIENTID";
    internal const string TenantIdVariable = "XBOX_AUTHENTICATION__TENANTID";
    internal const string ScopesVariable = "XBOX_AZUREAD__SCOPES";
    internal const string OidcRequestUrlVariable = "ACTIONS_ID_TOKEN_REQUEST_URL";
    internal const string OidcRequestTokenVariable = "ACTIONS_ID_TOKEN_REQUEST_TOKEN";

    internal const string NotRequestedReason =
        "Live E2E tests run only when " + EnvironmentVariable + " is set.";

    /// <summary>
    /// Returns why a test for <paramref name="service"/> should skip, or null to run it.
    /// </summary>
    /// <remarks>
    /// Configuration that fails to load returns null on purpose: the test then runs, reaches
    /// <see cref="Load"/>, and fails with the real error rather than quietly skipping.
    /// </remarks>
    internal static string? GetSkipReason(
        LiveService service,
        Func<string, string?> getVariable)
    {
        if (string.IsNullOrWhiteSpace(getVariable(EnvironmentVariable)))
        {
            return NotRequestedReason;
        }

        E2EProfile profile;
        try
        {
            profile = E2EProfiles.Load(getVariable);
        }
        catch (InvalidDataException)
        {
            return null;
        }

        return service switch
        {
            LiveService.PlayFab when !profile.PlayFab.Enabled =>
                $"PlayFab is disabled in environment '{profile.Environment}': {profile.PlayFab.DisabledReason}",
            LiveService.XboxPartner when !profile.XboxPartner.Enabled =>
                $"XboxPartner is disabled in environment '{profile.Environment}': {profile.XboxPartner.DisabledReason}",
            _ => null,
        };
    }

    internal static LiveSettings Load(Func<string, string?> getVariable)
    {
        if (string.IsNullOrWhiteSpace(getVariable(EnvironmentVariable)))
        {
            throw new InvalidOperationException(NotRequestedReason);
        }

        var profile = E2EProfiles.Load(getVariable);
        var errors = new List<string>();

        var cliDirectory = getVariable(CliPathVariable);
        if (string.IsNullOrWhiteSpace(cliDirectory))
        {
            errors.Add($"{CliPathVariable} must name the directory that contains the CLI to test.");
        }
        else if (!File.Exists(Path.Combine(cliDirectory, CliRunner.ExecutableName))
            || !File.Exists(Path.Combine(cliDirectory, CliRunner.CandidateDefaultsFileName)))
        {
            errors.Add(
                $"{CliPathVariable} '{cliDirectory}' must contain {CliRunner.ExecutableName} " +
                $"and {CliRunner.CandidateDefaultsFileName}.");
        }

        foreach (var required in new[]
                 {
                     ClientIdVariable,
                     TenantIdVariable,
                     OidcRequestUrlVariable,
                     OidcRequestTokenVariable,
                 })
        {
            if (string.IsNullOrWhiteSpace(getVariable(required)))
            {
                errors.Add(
                    required.StartsWith("ACTIONS_", StringComparison.Ordinal)
                        ? $"{required} is not set. The job needs the id-token: write permission."
                        : $"{required} is not set. Set it in GitHub environment '{profile.Environment}'.");
            }
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "Live E2E settings are incomplete:" + System.Environment.NewLine + "  " +
                string.Join(System.Environment.NewLine + "  ", errors));
        }

        var scope = CliRunner.GetEffectiveScope(CliRunner.ReadCandidateDefaults(cliDirectory!), profile);

        return new LiveSettings(
            profile,
            Path.GetFullPath(cliDirectory!),
            scope,
            getVariable(ClientIdVariable)!,
            getVariable(TenantIdVariable)!);
    }
}
