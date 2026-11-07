// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Collections;
using System.Text.RegularExpressions;

namespace XboxDevCLI.Services;

/// <summary>
/// How the CLI authenticates. <see cref="User"/> is the interactive sign-in through
/// <c>auth login</c>; every other mode authenticates as a customer-owned service principal.
/// </summary>
internal enum AuthenticationMode
{
    User,
    ClientSecret,
    Certificate,
    FederatedToken,
    GitHubOidc,
    AzureCli,
    ManagedIdentity,
}

/// <summary>
/// Raised when the automation authentication environment variables are missing, inconsistent,
/// or point at an unreadable credential. Messages name settings, never their contents.
/// </summary>
internal sealed class AuthenticationConfigurationException(string message)
    : InvalidOperationException(message);

/// <summary>
/// Automation (service principal) authentication settings, read only from
/// <c>XBOX_AUTHENTICATION__*</c> environment variables. They aren't built-in settings or command
/// options, and they carry file paths
/// rather than secrets. Like every <c>XBOX_</c> variable they also appear in configuration, under
/// <c>Authentication</c>, but nothing reads them there.
/// </summary>
internal sealed partial record AutomationAuthenticationSettings(
    AuthenticationMode Mode,
    string? TenantId = null,
    string? ClientId = null,
    string? CredentialFile = null,
    string? CertificatePasswordFile = null,
    string? GitHubRequestUrl = null,
    string? GitHubRequestToken = null)
{
    internal const string Prefix = "XBOX_AUTHENTICATION__";
    internal const string ModeVariable = Prefix + "MODE";
    internal const string GitHubRequestUrlVariable = "ACTIONS_ID_TOKEN_REQUEST_URL";
    internal const string GitHubRequestTokenVariable = "ACTIONS_ID_TOKEN_REQUEST_TOKEN";

    internal static readonly AutomationAuthenticationSettings UserMode = new(AuthenticationMode.User);

    private static readonly IReadOnlyDictionary<string, AuthenticationMode> ModeNames =
        new Dictionary<string, AuthenticationMode>(StringComparer.OrdinalIgnoreCase)
        {
            ["user"] = AuthenticationMode.User,
            ["clientsecret"] = AuthenticationMode.ClientSecret,
            ["certificate"] = AuthenticationMode.Certificate,
            ["federatedtoken"] = AuthenticationMode.FederatedToken,
            ["githuboidc"] = AuthenticationMode.GitHubOidc,
            ["azurecli"] = AuthenticationMode.AzureCli,
            ["managedidentity"] = AuthenticationMode.ManagedIdentity,
        };

    private sealed record Setting(string Name, AuthenticationMode[] Required, AuthenticationMode[] Optional)
    {
        public bool AppliesTo(AuthenticationMode mode) => Required.Contains(mode) || Optional.Contains(mode);

        public IEnumerable<AuthenticationMode> Modes => Required.Concat(Optional);
    }

    internal const string TenantIdVariable = Prefix + "TENANTID";
    internal const string ClientIdVariable = Prefix + "CLIENTID";

    private static readonly AuthenticationMode[] AppRegistrationModes =
    [
        AuthenticationMode.ClientSecret,
        AuthenticationMode.Certificate,
        AuthenticationMode.FederatedToken,
        AuthenticationMode.GitHubOidc,
    ];

    // The tenant and client IDs are shared by every mode that names an app registration; each
    // credential input belongs to exactly one mode, so a variable's applicability is explicit.
    private static readonly Setting[] KnownSettings =
    [
        new("TENANTID", AppRegistrationModes, []),
        new("CLIENTID", AppRegistrationModes, [AuthenticationMode.ManagedIdentity]),
        new("CLIENTSECRET__FILE", [AuthenticationMode.ClientSecret], []),
        new("CERTIFICATE__PATH", [AuthenticationMode.Certificate], []),
        new("CERTIFICATE__PASSWORDFILE", [], [AuthenticationMode.Certificate]),
        new("FEDERATEDTOKEN__FILE", [AuthenticationMode.FederatedToken], []),
    ];

    public bool IsAutomation => Mode != AuthenticationMode.User;

    /// <summary>The value of <c>XBOX_AUTHENTICATION__MODE</c> that selects <see cref="Mode"/>.</summary>
    public string ModeName => GetModeName(Mode);

    internal static string GetModeName(AuthenticationMode mode) =>
        ModeNames.First(pair => pair.Value == mode).Key;

    /// <summary>
    /// Captured authentication inputs. Conflicts are retained as a diagnostic, not rejected or
    /// resolved to an arbitrary identity. Inspection never checks credentials or touches files.
    /// </summary>
    internal sealed class EnvironmentSnapshot
    {
        internal EnvironmentSnapshot(Dictionary<string, string> variables, string? conflictingVariable)
        {
            Variables = variables.AsReadOnly();
            ConflictingVariable = conflictingVariable;
            var value = Get(ModeVariable);
            ModeExplicit = value is not null;
            Mode = value is null ? AuthenticationMode.User
                : ModeNames.TryGetValue(value, out var mode) ? mode : null;
            SelectedModeName = conflictingVariable is not null || Mode is null
                || (Mode == AuthenticationMode.User && variables.Any(pair =>
                    pair.Key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
                    && !pair.Key.Equals(ModeVariable, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(pair.Value)))
                ? "invalid"
                : GetModeName(Mode.Value);
        }

        internal IReadOnlyDictionary<string, string> Variables { get; }
        internal string? ConflictingVariable { get; }
        internal AuthenticationMode? Mode { get; }
        internal bool ModeExplicit { get; }
        internal string SelectedModeName { get; }
        internal bool IsAutomationSelected => SelectedModeName != "user";

        internal string? Get(string name) =>
            Variables.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value.Trim()
                : null;
    }

    /// <summary>
    /// Validates <paramref name="environment"/> and returns the selected mode's settings. Fails
    /// on an unknown mode, an unknown or inactive-mode setting, a missing required setting, a
    /// malformed identifier, or a credential file that does not exist. It never infers a mode
    /// from which settings or ambient GitHub inputs happen to be present.
    /// </summary>
    internal static AutomationAuthenticationSettings Load(
        EnvironmentSnapshot environment,
        Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(fileExists);

        if (environment.ConflictingVariable is { } conflictingVariable)
        {
            throw new AuthenticationConfigurationException(
                $"Environment variable names for {conflictingVariable} differ only by case "
                + "and have conflicting values. Remove the duplicate variables.");
        }

        if (environment.Mode is not { } mode)
        {
            throw new AuthenticationConfigurationException(
                $"{ModeVariable} has an unknown value. Use one of: {string.Join(", ", ModeNames.Keys)}.");
        }

        string? Get(string name) => environment.Get(name);

        foreach (var name in environment.Variables.Keys
            .Where(key => key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            .Where(key => !string.Equals(key, ModeVariable, StringComparison.OrdinalIgnoreCase))
            .Where(key => Get(key) is not null)
            .Order(StringComparer.OrdinalIgnoreCase))
        {
            var suffix = name[Prefix.Length..];
            var setting = KnownSettings.FirstOrDefault(
                known => string.Equals(known.Name, suffix, StringComparison.OrdinalIgnoreCase));
            if (setting is null)
            {
                throw new AuthenticationConfigurationException(
                    $"{name} is not a recognized authentication setting. Check its spelling or remove it.");
            }

            if (!setting.AppliesTo(mode))
            {
                var settingModes = setting.Modes.Select(GetModeName).ToArray();
                var modeChoice = settingModes.Length == 1
                    ? $"{ModeVariable}={settingModes[0]}"
                    : $"{ModeVariable} to one of: {string.Join(", ", settingModes)}";
                throw new AuthenticationConfigurationException(mode == AuthenticationMode.User
                    ? $"{name} is set, but {ModeVariable} is {(environment.ModeExplicit ? "'user'" : "not set")}. "
                        + $"Set {modeChoice} to authenticate as a service principal, or remove {name}."
                    : $"{name} doesn't apply when {ModeVariable} is '{GetModeName(mode)}'. "
                        + $"Remove {name}, or set {modeChoice}.");
            }
        }

        string? Read(string settingName)
        {
            var setting = KnownSettings.Single(known => known.Name == settingName);
            var value = Get(Prefix + setting.Name);
            if (value is null && setting.Required.Contains(mode))
            {
                throw new AuthenticationConfigurationException(
                    $"{Prefix}{setting.Name} is required when {ModeVariable} is '{GetModeName(mode)}'.");
            }

            return value;
        }

        string ReadTenant()
        {
            var tenant = Read("TENANTID")!;
            if (!TenantPattern().IsMatch(tenant) || IsMultiTenantAlias(tenant))
            {
                throw new AuthenticationConfigurationException(
                    $"{TenantIdVariable} must be a tenant ID (GUID) or verified domain, "
                    + "not 'common', 'organizations', or 'consumers'.");
            }

            return tenant;
        }

        string? ReadClientId()
        {
            var clientId = Read("CLIENTID");
            if (clientId is not null && !Guid.TryParse(clientId, out _))
            {
                throw new AuthenticationConfigurationException(
                    $"{ClientIdVariable} must be an application (client) ID GUID.");
            }

            return clientId;
        }

        string? ReadFile(string settingName)
        {
            var path = Read(settingName);
            if (path is not null && !fileExists(path))
            {
                throw new AuthenticationConfigurationException(
                    $"The file named by {Prefix}{settingName} does not exist or cannot be accessed.");
            }

            return path;
        }

        switch (mode)
        {
            case AuthenticationMode.User:
                return UserMode;
            case AuthenticationMode.ClientSecret:
                return new(mode, ReadTenant(), ReadClientId(), ReadFile("CLIENTSECRET__FILE"));
            case AuthenticationMode.Certificate:
                return new(mode, ReadTenant(), ReadClientId(), ReadFile("CERTIFICATE__PATH"), ReadFile("CERTIFICATE__PASSWORDFILE"));
            case AuthenticationMode.FederatedToken:
                return new(mode, ReadTenant(), ReadClientId(), ReadFile("FEDERATEDTOKEN__FILE"));
            case AuthenticationMode.GitHubOidc:
                var tenantId = ReadTenant();
                var clientId = ReadClientId();
                var requestUrl = Get(GitHubRequestUrlVariable);
                var requestToken = Get(GitHubRequestTokenVariable);
                if (requestUrl is null || requestToken is null)
                {
                    throw new AuthenticationConfigurationException(
                        $"{ModeVariable} is 'githuboidc', but {GitHubRequestUrlVariable} and {GitHubRequestTokenVariable} "
                        + "are not both set. Run inside GitHub Actions with 'permissions: id-token: write'.");
                }

                if (!Uri.TryCreate(requestUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                {
                    throw new AuthenticationConfigurationException(
                        $"{GitHubRequestUrlVariable} must be an absolute https URL.");
                }

                return new(mode, tenantId, clientId, GitHubRequestUrl: requestUrl, GitHubRequestToken: requestToken);
            case AuthenticationMode.AzureCli:
                return new(mode);
            case AuthenticationMode.ManagedIdentity:
                return new(mode, ClientId: ReadClientId());
            default:
                throw new AuthenticationConfigurationException($"Unsupported authentication mode '{mode}'.");
        }
    }

    private static bool IsMultiTenantAlias(string tenant) =>
        tenant.Equals("common", StringComparison.OrdinalIgnoreCase)
        || tenant.Equals("organizations", StringComparison.OrdinalIgnoreCase)
        || tenant.Equals("consumers", StringComparison.OrdinalIgnoreCase);

    // A GUID or a DNS-style verified domain such as contoso.onmicrosoft.com.
    [GeneratedRegex(@"^([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}|[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)+)$")]
    private static partial Regex TenantPattern();

    /// <summary>
    /// Raw environment reader. Tests replace it so inherited credentials never select a mode.
    /// Each invocation captures its own snapshot before inspecting or validating the settings.
    /// </summary>
    internal static Func<IDictionary> ReadEnvironment { get; set; } = Environment.GetEnvironmentVariables;

    internal static EnvironmentSnapshot CaptureEnvironment() => Capture(ReadEnvironment());

    /// <summary>
    /// Copies authentication variables and GitHub token-request inputs without validation.
    /// Names match case-insensitively, but conflicting values are never silently selected.
    /// </summary>
    internal static EnvironmentSnapshot Capture(IDictionary environment)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? conflictingVariable = null;
        foreach (DictionaryEntry entry in environment)
        {
            if (entry.Key is string key && entry.Value is string value
                && (key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
                    || key.Equals(GitHubRequestUrlVariable, StringComparison.OrdinalIgnoreCase)
                    || key.Equals(GitHubRequestTokenVariable, StringComparison.OrdinalIgnoreCase)))
            {
                if (result.TryGetValue(key, out var existingValue)
                    && !string.Equals(existingValue, value, StringComparison.Ordinal))
                {
                    conflictingVariable ??= key.ToUpperInvariant();
                }
                else
                {
                    result[key] = value;
                }
            }
        }

        return new EnvironmentSnapshot(result, conflictingVariable);
    }
}
