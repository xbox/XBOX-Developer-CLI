// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Configuration;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace XboxDevCLI.Services;

public sealed record LogoutResult(int RemovedAccountCount, IReadOnlyList<string> Usernames);

/// <summary>
/// Handles MSAL-based authentication with Microsoft Entra: interactive browser sign-in with a
/// persisted token cache in user mode, or app-only tokens from a customer service principal in
/// the automation modes selected by <c>XBOX_AUTHENTICATION__MODE</c>.
/// </summary>
public class AuthenticationService
{
    internal const string LinuxUnencryptedCacheFallbackWarning =
        "Warning: secure Linux keyring is unavailable. XboxDevCLI is using an unencrypted token cache file protected only by filesystem permissions.";

    private sealed record AuthenticationState(
        AutomationAuthenticationSettings Settings,
        string[] Scopes,
        IPublicClientApplication? PublicApp);

    private readonly AuthenticationState _state;
    private readonly Lazy<IAppTokenProvider> _provider;
    private readonly Lock _automationIdentityLock = new();
    private (string TenantId, string ObjectId, string AppId)? _automationIdentity;
    private IAccount? _account;
    private bool _cacheRegistered;
    private readonly SemaphoreSlim _cacheSemaphore = new(1, 1);

    /// <summary>
    /// Creates the service for interactive user sign-in. Commands get the service for the
    /// selected mode from <c>Program.GetAuthentication</c> instead.
    /// </summary>
    public AuthenticationService(IConfiguration configuration)
        : this(configuration, publicClientApp: null, AutomationAuthenticationSettings.UserMode)
    {
    }

    /// <summary>
    /// Constructor that accepts an externally-provided <see cref="IPublicClientApplication"/>
    /// for testability. Always uses <see cref="AuthenticationMode.User"/>.
    /// </summary>
    internal AuthenticationService(IConfiguration configuration, IPublicClientApplication? publicClientApp)
        : this(configuration, publicClientApp, AutomationAuthenticationSettings.UserMode)
    {
    }

    /// <summary>
    /// Creates the service for the mode <paramref name="settings"/> selects, validating the
    /// configuration it needs. It doesn't read the token cache or acquire a token.
    /// </summary>
    internal AuthenticationService(
        IConfiguration configuration,
        IPublicClientApplication? publicClientApp,
        AutomationAuthenticationSettings settings,
        Func<AutomationAuthenticationSettings, IReadOnlyList<string>, IAppTokenProvider>? providerFactory = null)
    {
        SelectedModeName = settings.ModeName;
        _state = Initialize(configuration, settings, publicClientApp);
        providerFactory ??= (selected, scopes) => AppTokenProviderFactory.Create(selected, scopes);
        _provider = new Lazy<IAppTokenProvider>(() => providerFactory(_state.Settings, _state.Scopes));
    }

    /// <summary>
    /// Creates the service for the mode the captured <paramref name="environment"/> selects,
    /// validating its settings and <paramref name="configuration"/>.
    /// </summary>
    internal static AuthenticationService Create(
        IConfiguration configuration,
        AutomationAuthenticationSettings.EnvironmentSnapshot environment,
        Func<AutomationAuthenticationSettings, IReadOnlyList<string>, IAppTokenProvider>? providerFactory = null) =>
        new(configuration, publicClientApp: null, AutomationAuthenticationSettings.Load(environment, File.Exists), providerFactory);

    private static AuthenticationState Initialize(
        IConfiguration configuration,
        AutomationAuthenticationSettings settings,
        IPublicClientApplication? publicClientApp)
    {
        var scopes = (configuration["AzureAd:Scopes"]
            ?? throw new InvalidOperationException("AzureAd:Scopes is not configured."))
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (settings.IsAutomation)
        {
            // AzureAd:ClientId/TenantId configure the CLI's own public client for user sign-in;
            // service-principal modes take the customer identity from their own settings.
            AppTokenProviderFactory.GetSingleDefaultScope(scopes);
            return new AuthenticationState(settings, scopes, null);
        }

        var clientId = configuration["AzureAd:ClientId"]
            ?? throw new InvalidOperationException("AzureAd:ClientId is not configured.");

        var tenantId = configuration["AzureAd:TenantId"]
            ?? throw new InvalidOperationException("AzureAd:TenantId is not configured.");

        var authority = $"https://login.microsoftonline.com/{tenantId}";

        try
        {
            var app = publicClientApp ?? PublicClientApplicationBuilder
                .Create(clientId)
                .WithAuthority(authority)
                .WithRedirectUri("http://localhost")
                .Build();
            return new AuthenticationState(settings, scopes, app);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            // MSAL rejects a malformed tenant or client ID, such as an empty --tenant-id, this way.
            // Reported like a missing setting, it fails each command with its message rather than
            // crashing the CLI.
            throw new InvalidOperationException(
                $"AzureAd:TenantId or AzureAd:ClientId is not valid: {exception.Message}",
                exception);
        }
    }

    /// <summary>The selected mode's name, such as "user" or "clientsecret".</summary>
    internal string SelectedModeName { get; }

    /// <summary>Whether the service signs in as a service principal rather than a user.</summary>
    public bool IsAutomation => _state.Settings.IsAutomation;

    /// <summary>
    /// Explains why an interactive account command does nothing in a service-principal mode.
    /// </summary>
    internal string GetNotApplicableMessage(string commandName) =>
        $"'{CliIdentity.CommandName} auth {commandName}' doesn't apply when {AutomationAuthenticationSettings.ModeVariable} "
        + $"is '{_state.Settings.ModeName}': each command authenticates as the configured service principal. "
        + $"To sign in as a user, unset {AutomationAuthenticationSettings.ModeVariable} and the other "
        + $"{AutomationAuthenticationSettings.Prefix}* settings.";

    private IPublicClientApplication PublicApp(string commandName) =>
        _state.PublicApp ?? throw new InvalidOperationException(GetNotApplicableMessage(commandName));

    /// <summary>
    /// Performs an interactive login via the system browser and signs out every other cached
    /// account, so the chosen account is the one later commands use.
    /// </summary>
    public virtual async Task<AuthenticationResult> LoginAsync(CancellationToken cancellationToken)
    {
        var app = PublicApp("login");
        await EnsureCacheRegisteredAsync(cancellationToken);

        var result = await AcquireTokenInteractiveAsync(cancellationToken);

        // Commands use the first cached account, and MSAL does not list accounts in sign-in order.
        // Sign-in already succeeded, so the cleanup ignores Ctrl-C: stopping it would leave several
        // accounts cached and every command refusing to pick one. It's local cache work.
        foreach (var account in await GetAccountsAsync(app, CancellationToken.None))
        {
            if (!Equals(account.HomeAccountId, result.Account.HomeAccountId))
            {
                await RemoveAccountAsync(app, account, CancellationToken.None);
            }
        }

        return result;
    }

    /// <summary>
    /// Lets the user pick an account in the system browser. Tests override it to sign in without one.
    /// </summary>
    protected virtual async Task<AuthenticationResult> AcquireTokenInteractiveAsync(CancellationToken cancellationToken)
    {
        return await PublicApp("login")
            .AcquireTokenInteractive(_state.Scopes)
            .WithPrompt(Prompt.SelectAccount)
            .ExecuteAsync(cancellationToken);
    }

    /// <summary>
    /// Acquires a token without user interaction. In user mode this reads the cache and returns
    /// null if no cached token is available. In a service-principal mode it acquires an app-only
    /// token from the configured credential and throws on failure, never falling back to user
    /// sign-in or another credential.
    /// </summary>
    public virtual async Task<AuthenticationResult?> AcquireTokenSilentAsync(CancellationToken cancellationToken)
    {
        var state = _state;
        if (state.Settings.IsAutomation)
        {
            var token = await _provider.Value.AcquireTokenAsync(cancellationToken);
            EnsureAutomationIdentity(token);
            return token;
        }

        await EnsureCacheRegisteredAsync(cancellationToken);

        var account = await GetCachedAccountAsync(cancellationToken);
        if (account == null)
        {
            return null;
        }

        try
        {
            return await state.PublicApp!
                .AcquireTokenSilent(state.Scopes, account)
                .ExecuteAsync(cancellationToken);
        }
        catch (MsalUiRequiredException)
        {
            return null;
        }
    }

    private void EnsureAutomationIdentity(AuthenticationResult token)
    {
        var claims = AccessTokenClaims.TryDecode(token.AccessToken);
        var principalType = claims?.PrincipalType ?? "unknown";
        if (claims is null || principalType != "app")
        {
            throw new AuthenticationConfigurationException(
                $"Authentication mode '{SelectedModeName}' returned a token whose principal type is "
                + $"'{principalType}', not an app-only token. Check the selected credential and its "
                + "configuration. To sign in as a user, unset "
                + $"{AutomationAuthenticationSettings.ModeVariable} and the other "
                + $"{AutomationAuthenticationSettings.Prefix}* settings, then run '{CliIdentity.CommandName} auth login'.");
        }

        if (string.IsNullOrWhiteSpace(claims.TenantId)
            || string.IsNullOrWhiteSpace(claims.ObjectId)
            || string.IsNullOrWhiteSpace(claims.AppId))
        {
            throw new AuthenticationConfigurationException(
                $"Authentication mode '{SelectedModeName}' returned an app-only token without the tenant, "
                + "object, or application ID needed to keep this invocation on one identity. "
                + "Check the selected credential and its configuration, then rerun the command.");
        }

        lock (_automationIdentityLock)
        {
            var identity = (claims.TenantId, claims.ObjectId, claims.AppId);
            if (_automationIdentity is not null && _automationIdentity != identity)
            {
                throw new AuthenticationConfigurationException(
                    $"The service-principal identity changed during this invocation in '{SelectedModeName}' "
                    + "mode. No token for the new identity will be used. Check the selected credential and "
                    + "its configuration, then rerun the command.");
            }

            _automationIdentity = identity;
        }
    }

    /// <summary>
    /// Gets the signed-in account, or null if no accounts are cached. <see cref="LoginAsync"/>
    /// keeps a single account in the cache. If more than one is cached anyway, this throws rather
    /// than guess, because MSAL's account order is arbitrary. The service acts as one account: it
    /// keeps the first account it reads, and throws if a later read finds another, as when a
    /// sign-in in another terminal replaces the account while a command waits for confirmation.
    /// So saved context read with one account is never sent with another's token.
    /// </summary>
    public virtual async Task<IAccount?> GetCachedAccountAsync(CancellationToken cancellationToken)
    {
        var app = PublicApp("status");
        await EnsureCacheRegisteredAsync(cancellationToken);

        var accounts = (await GetAccountsAsync(app, cancellationToken)).ToList();
        if (accounts.Count > 1)
        {
            throw new InvalidOperationException(
                $"More than one account is signed in. Run '{CliIdentity.CommandName} auth login' to choose one.");
        }

        var account = accounts.FirstOrDefault();
        if (account is not null
            && (Interlocked.CompareExchange(ref _account, account, null) ?? account) is var first
            && SignedInIdentity.FromAccount(first).Id != SignedInIdentity.FromAccount(account).Id)
        {
            throw new InvalidOperationException(
                "The signed-in account changed while this command ran. Run it again.");
        }

        return account;
    }

    /// <summary>
    /// Lists the cached accounts. MSAL's lookup takes no token, so the wait is bounded instead:
    /// Ctrl-C stops waiting even though the lookup itself runs on.
    /// </summary>
    private static Task<IEnumerable<IAccount>> GetAccountsAsync(
        IPublicClientApplication app, CancellationToken cancellationToken) =>
        app.GetAccountsAsync().WaitAsync(cancellationToken);

    /// <summary>
    /// Removes a cached account. Like the lookup, MSAL's removal takes no token: Ctrl-C starts no
    /// further removal and stops waiting, though one already started runs on.
    /// </summary>
    private static Task RemoveAccountAsync(
        IPublicClientApplication app, IAccount account, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return app.RemoveAsync(account).WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Removes every cached MSAL account, clearing the persisted token cache.
    /// </summary>
    public virtual async Task<LogoutResult> LogoutAsync(CancellationToken cancellationToken)
    {
        var app = PublicApp("logout");
        await EnsureCacheRegisteredAsync(cancellationToken);

        var accounts = (await GetAccountsAsync(app, cancellationToken)).ToArray();
        var usernames = new List<string>(accounts.Length);

        foreach (var account in accounts)
        {
            await RemoveAccountAsync(app, account, cancellationToken);
            usernames.Add(account.Username);
        }

        return new LogoutResult(usernames.Count, usernames);
    }

    private async Task EnsureCacheRegisteredAsync(CancellationToken cancellationToken)
    {
        if (_cacheRegistered)
        {
            return;
        }

        await _cacheSemaphore.WaitAsync(cancellationToken);
        try
        {
            if (_cacheRegistered)
            {
                return;
            }

            var cacheHelper = await CreateCacheHelperAsync();
            cacheHelper.RegisterCache(_state.PublicApp!.UserTokenCache);
            _cacheRegistered = true;
        }
        finally
        {
            _cacheSemaphore.Release();
        }
    }

    /// <summary>
    /// Creates an <see cref="MsalCacheHelper"/> for the persisted token cache, configured per OS.
    /// Windows protects the cache with DPAPI automatically; Linux and macOS require an explicit
    /// secret backend. Without <c>WithLinuxKeyring</c>/<c>WithMacKeyChain</c> the helper throws
    /// <see cref="ArgumentNullException"/> (parameter <c>attributeKey1</c>) on construction, which
    /// would break every authenticated command on those platforms. On headless Linux with no
    /// usable keyring (CI runners, many containers), this falls back to an unencrypted file so the
    /// CLI still works instead of crashing.
    /// </summary>
    private static async Task<MsalCacheHelper> CreateCacheHelperAsync()
    {
        const string cacheFileName = "xboxdevcli_tokencache.bin";
        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XboxDevCLI");

        var encryptedProperties = new StorageCreationPropertiesBuilder(cacheFileName, cacheDir)
            .WithLinuxKeyring(
                schemaName: "com.xboxdevcli.tokencache",
                collection: "default",
                secretLabel: "XboxDevCLI MSAL token cache",
                attribute1: new KeyValuePair<string, string>("Version", "1"),
                attribute2: new KeyValuePair<string, string>("Product", "XboxDevCLI"))
            .WithMacKeyChain(serviceName: "XboxDevCLI", accountName: "MSALCache")
            .Build();

        var cacheHelper = await MsalCacheHelper.CreateAsync(encryptedProperties);

        try
        {
            // Probe the backend. On a desktop with a working keyring/keychain this succeeds and
            // the cache stays encrypted; on headless Linux it throws and we fall back below.
            cacheHelper.VerifyPersistence();
            return cacheHelper;
        }
        catch (MsalCachePersistenceException)
        {
            // We reach here when an OS secret backend is expected but unusable — typically a
            // headless Linux host (CI runner, container, SSH session) with no Secret Service:
            // libsecret/gnome-keyring is absent or there is no D-Bus session to reach it.
            // Rather than fail every authenticated command, fall back to an unencrypted file.
            // Trade-off: on Linux the token is persisted in plaintext, protected only by file-
            // system permissions (the file lives under the user's LocalApplicationData dir).
            // Windows (DPAPI) and macOS (Keychain) are unaffected — they don't hit this path.
            if (OperatingSystem.IsLinux())
            {
                WriteLinuxUnencryptedCacheFallbackWarning(Console.Error);
            }

            var unencryptedProperties = new StorageCreationPropertiesBuilder(cacheFileName, cacheDir)
                .WithLinuxUnprotectedFile()
                .WithMacKeyChain(serviceName: "XboxDevCLI", accountName: "MSALCache")
                .Build();

            return await MsalCacheHelper.CreateAsync(unencryptedProperties);
        }
    }

    internal static void WriteLinuxUnencryptedCacheFallbackWarning(TextWriter stderr)
    {
        stderr.WriteLine(LinuxUnencryptedCacheFallbackWarning);
    }
}
