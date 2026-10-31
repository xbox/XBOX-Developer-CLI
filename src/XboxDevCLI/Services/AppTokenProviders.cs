// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.Core;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.AppConfig;

namespace XboxDevCLI.Services;

/// <summary>Acquires an app-only access token for one automation authentication mode.</summary>
internal interface IAppTokenProvider
{
    Task<AuthenticationResult> AcquireTokenAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Builds the token provider for the selected automation mode. Each mode uses exactly one
/// credential source; a failure is reported rather than retried with another source.
/// </summary>
internal static partial class AppTokenProviderFactory
{
    internal const string GitHubOidcAudience = "api://AzureADTokenExchange";
    private static readonly TimeSpan GitHubRequestTimeout = TimeSpan.FromSeconds(30);

    public static IAppTokenProvider Create(
        AutomationAuthenticationSettings settings,
        IReadOnlyList<string> scopes,
        IMsalHttpClientFactory? httpClientFactory = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var scope = GetSingleDefaultScope(scopes);

        return settings.Mode switch
        {
            AuthenticationMode.ClientSecret => CreateConfidential(settings, scope, builder => builder.WithClientSecret(
                ReadCredentialFile(settings.CredentialFile!, VariableName(settings, "FILE"))), httpClientFactory),
            AuthenticationMode.Certificate => CreateConfidential(settings, scope, builder => builder.WithCertificate(
                LoadCertificate(settings)), httpClientFactory),
            AuthenticationMode.FederatedToken => CreateConfidential(settings, scope, builder => builder.WithClientAssertion(
                (AssertionRequestOptions _) => Task.FromResult(
                    ReadCredentialFile(settings.CredentialFile!, VariableName(settings, "FILE")))), httpClientFactory),
            AuthenticationMode.GitHubOidc => CreateGitHubOidc(settings, scope, httpClientFactory),
            AuthenticationMode.ManagedIdentity => CreateManagedIdentity(settings, scope, httpClientFactory),
            AuthenticationMode.AzureCli => new AzureCliTokenProvider(AzureCliTokenProvider.CreateCredential(), scope),
            _ => throw new AuthenticationConfigurationException(
                $"Mode '{settings.ModeName}' does not use a service-principal token provider."),
        };
    }

    /// <summary>
    /// Client-credential flows request <c>&lt;resource&gt;/.default</c>, so the configured
    /// <c>AzureAd:Scopes</c> must be exactly one such scope.
    /// </summary>
    internal static string GetSingleDefaultScope(IReadOnlyList<string> scopes)
    {
        if (scopes is not [var scope] || !scope.EndsWith("/.default", StringComparison.Ordinal)
            || !ScopePattern().IsMatch(scope))
        {
            throw new AuthenticationConfigurationException(
                "Service-principal authentication requires AzureAd:Scopes (or --scopes) to be a single '<resource>/.default' scope.");
        }

        return scope;
    }

    internal static string ReadCredentialFile(string path, string variableName) =>
        RequireContents(ReadFile(path, variableName).Trim(), variableName);

    /// <summary>
    /// Reads a PFX password exactly, because whitespace is significant in a password. Only one
    /// trailing line ending, which editors and <c>echo</c> add, is removed.
    /// </summary>
    internal static string ReadPasswordFile(string path, string variableName)
    {
        var contents = ReadFile(path, variableName);
        if (contents.EndsWith("\r\n", StringComparison.Ordinal))
        {
            contents = contents[..^2];
        }
        else if (contents.EndsWith('\n'))
        {
            contents = contents[..^1];
        }

        return RequireContents(contents, variableName);
    }

    private static string ReadFile(string path, string variableName)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            throw new AuthenticationConfigurationException($"Could not read the file named by {variableName}.");
        }
    }

    private static string RequireContents(string contents, string variableName)
    {
        if (contents.Length == 0)
        {
            throw new AuthenticationConfigurationException($"The file named by {variableName} is empty.");
        }

        return contents;
    }

    internal static X509Certificate2 LoadCertificate(AutomationAuthenticationSettings settings)
    {
        var password = settings.CertificatePasswordFile is null
            ? null
            : ReadPasswordFile(settings.CertificatePasswordFile, VariableName(settings, "PASSWORDFILE"));

        X509Certificate2 certificate;
        try
        {
            // Ephemeral keys never touch the user's key store; macOS doesn't support them.
            var flags = OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet;
            certificate = X509CertificateLoader.LoadPkcs12FromFile(settings.CredentialFile!, password, flags);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            throw new AuthenticationConfigurationException(
                $"Could not load the PFX certificate named by {VariableName(settings, "PATH")}. "
                + $"Check that it is a PKCS#12 file and that {VariableName(settings, "PASSWORDFILE")} names its password, if it has one.");
        }

        if (!certificate.HasPrivateKey)
        {
            certificate.Dispose();
            throw new AuthenticationConfigurationException(
                $"The certificate named by {VariableName(settings, "PATH")} has no private key.");
        }

        return certificate;
    }

    /// <summary>
    /// Requests a GitHub Actions OIDC ID token with the Entra token-exchange audience. GitHub
    /// supplies the request URL and bearer token through <c>ACTIONS_ID_TOKEN_REQUEST_*</c>.
    /// </summary>
    internal static async Task<string> RequestGitHubOidcTokenAsync(
        HttpClient httpClient,
        string requestUrl,
        string requestToken,
        CancellationToken cancellationToken)
    {
        var separator = requestUrl.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{requestUrl}{separator}audience={Uri.EscapeDataString(GitHubOidcAudience)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", requestToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(GitHubRequestTimeout);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("Timed out requesting a GitHub Actions OIDC token.");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"GitHub Actions OIDC token request failed with HTTP {(int)response.StatusCode}. "
                    + "Check that the workflow job has 'permissions: id-token: write'.");
            }

            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("value", out var value)
                    && value.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(value.GetString()))
                {
                    return value.GetString()!;
                }
            }
            catch (JsonException)
            {
            }

            throw new InvalidOperationException("GitHub Actions OIDC token response did not contain a token.");
        }
    }

    private static IAppTokenProvider CreateGitHubOidc(
        AutomationAuthenticationSettings settings,
        string scope,
        IMsalHttpClientFactory? httpClientFactory)
    {
        var httpClient = httpClientFactory?.GetHttpClient() ?? new HttpClient();
        return CreateConfidential(settings, scope, builder => builder.WithClientAssertion(
            (AssertionRequestOptions options) => RequestGitHubOidcTokenAsync(
                httpClient,
                settings.GitHubRequestUrl!,
                settings.GitHubRequestToken!,
                options.CancellationToken)), httpClientFactory);
    }

    private static ConfidentialClientTokenProvider CreateConfidential(
        AutomationAuthenticationSettings settings,
        string scope,
        Func<ConfidentialClientApplicationBuilder, ConfidentialClientApplicationBuilder> withCredential,
        IMsalHttpClientFactory? httpClientFactory)
    {
        var builder = ConfidentialClientApplicationBuilder
            .Create(settings.ClientId!)
            .WithAuthority($"https://login.microsoftonline.com/{settings.TenantId}");
        if (httpClientFactory is not null)
        {
            builder.WithHttpClientFactory(httpClientFactory);
        }

        return new ConfidentialClientTokenProvider(withCredential(builder).Build(), [scope]);
    }

    private static ManagedIdentityTokenProvider CreateManagedIdentity(
        AutomationAuthenticationSettings settings,
        string scope,
        IMsalHttpClientFactory? httpClientFactory)
    {
        var builder = ManagedIdentityApplicationBuilder.Create(settings.ClientId is null
            ? ManagedIdentityId.SystemAssigned
            : ManagedIdentityId.WithUserAssignedClientId(settings.ClientId));
        if (httpClientFactory is not null)
        {
            builder.WithHttpClientFactory(httpClientFactory);
        }

        return new ManagedIdentityTokenProvider(builder.Build(), scope[..^"/.default".Length]);
    }

    private static string VariableName(AutomationAuthenticationSettings settings, string setting) =>
        $"{AutomationAuthenticationSettings.Prefix}{settings.ModeName.ToUpperInvariant()}__{setting}";

    // Resource URIs and GUIDs only.
    [GeneratedRegex(@"^[A-Za-z0-9:/._-]+$")]
    private static partial Regex ScopePattern();
}

/// <summary>Client-credentials token acquisition; MSAL caches the app token in memory.</summary>
internal sealed class ConfidentialClientTokenProvider(IConfidentialClientApplication app, string[] scopes) : IAppTokenProvider
{
    public Task<AuthenticationResult> AcquireTokenAsync(CancellationToken cancellationToken = default) =>
        app.AcquireTokenForClient(scopes).ExecuteAsync(cancellationToken);
}

/// <summary>Token acquisition with the hosting Azure compute's managed identity.</summary>
internal sealed class ManagedIdentityTokenProvider(IManagedIdentityApplication app, string resource) : IAppTokenProvider
{
    private static readonly TimeSpan AcquisitionTimeout = TimeSpan.FromSeconds(10);
    private const string HostGuidance =
        "The 'managedidentity' mode requires supported Azure compute with a managed identity enabled. "
        + "On a laptop or on-premises runner, select another XBOX_AUTHENTICATION__MODE.";

    public async Task<AuthenticationResult> AcquireTokenAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(AcquisitionTimeout);

        try
        {
            return await app.AcquireTokenForManagedIdentity(resource).ExecuteAsync(timeout.Token);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"Managed-identity token acquisition timed out after 10 seconds. {HostGuidance}", ex);
        }
        catch (Exception ex) when (ex is MsalException or HttpRequestException)
        {
            throw new InvalidOperationException(
                $"Could not acquire a managed-identity token. {HostGuidance} {ex.Message}", ex);
        }
    }
}

/// <summary>
/// Token acquisition through the Azure CLI's signed-in service principal, which also covers
/// Azure DevOps workload-identity service connections running inside <c>AzureCLI@2</c>.
/// <see cref="AuthenticationService"/> enforces app-only tokens and invocation identity.
/// </summary>
internal sealed class AzureCliTokenProvider(TokenCredential credential, string scope) : IAppTokenProvider
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);
    private AuthenticationResult? _cached;

    internal static Azure.Identity.AzureCliCredential CreateCredential() =>
        new(new Azure.Identity.AzureCliCredentialOptions { ProcessTimeout = ProcessTimeout });

    public async Task<AuthenticationResult> AcquireTokenAsync(CancellationToken cancellationToken = default)
    {
        if (_cached is not null && _cached.ExpiresOn - RefreshMargin > DateTimeOffset.UtcNow)
        {
            return _cached;
        }

        AccessToken token;
        try
        {
            token = await credential.GetTokenAsync(new TokenRequestContext([scope]), cancellationToken);
        }
        catch (Azure.Identity.AuthenticationFailedException ex)
        {
            throw new InvalidOperationException($"Azure CLI could not get an access token. {ex.Message}", ex);
        }

        var claims = AccessTokenClaims.TryDecode(token.Token);
        _cached = new AuthenticationResult(
            accessToken: token.Token,
            isExtendedLifeTimeToken: false,
            uniqueId: null,
            expiresOn: token.ExpiresOn,
            extendedExpiresOn: token.ExpiresOn,
            tenantId: claims?.TenantId,
            account: null,
            idToken: null,
            scopes: [scope],
            correlationId: Guid.Empty,
            tokenType: token.TokenType ?? "Bearer");
        return _cached;
    }
}
