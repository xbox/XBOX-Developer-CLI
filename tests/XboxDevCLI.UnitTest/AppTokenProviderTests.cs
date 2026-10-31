// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Identity.Client;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

[Collection("ConsoleRedirect")]
public sealed class AppTokenProviderTests : IDisposable
{
    private const string Tenant = "15fc7b3e-912c-4c49-ad3d-734293d93a23";
    private const string Client = "3778efb3-5411-47ab-a790-b5db0eed39a6";
    private const string Scope = "448adbda-b8d8-4f33-a1b0-ac58cf44d4c1/.default";

    private readonly string _directory = Directory.CreateTempSubdirectory("xboxdevcli-sp-tests-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData("openid")]
    [InlineData("a/.default b/.default")]
    [InlineData("a/.default & calc")]
    public void GetSingleDefaultScope_RejectsAnythingButOneDefaultScope(string scopes)
    {
        Assert.Throws<AuthenticationConfigurationException>(() =>
            AppTokenProviderFactory.GetSingleDefaultScope(scopes.Split(' ')));
    }

    [Fact]
    public void Create_SelectsProviderForEachMode()
    {
        var secretFile = WriteFile("secret.txt", "  s3cret \n");
        var tokenFile = WriteFile("oidc.jwt", "eyJ.e30.sig");
        var pfx = WritePfx("cert.pfx", password: null);

        Assert.IsType<ConfidentialClientTokenProvider>(Create(new(AuthenticationMode.ClientSecret, Tenant, Client, secretFile)));
        Assert.IsType<ConfidentialClientTokenProvider>(Create(new(AuthenticationMode.Certificate, Tenant, Client, pfx)));
        Assert.IsType<ConfidentialClientTokenProvider>(Create(new(AuthenticationMode.FederatedToken, Tenant, Client, tokenFile)));
        Assert.IsType<ConfidentialClientTokenProvider>(Create(new(
            AuthenticationMode.GitHubOidc, Tenant, Client, GitHubRequestUrl: "https://example/t", GitHubRequestToken: "x")));
        Assert.IsType<ManagedIdentityTokenProvider>(Create(new(AuthenticationMode.ManagedIdentity)));
        Assert.IsType<ManagedIdentityTokenProvider>(Create(new(AuthenticationMode.ManagedIdentity, ClientId: Client)));
        Assert.IsType<AzureCliTokenProvider>(Create(new(AuthenticationMode.AzureCli)));
        Assert.Throws<AuthenticationConfigurationException>(() => Create(AutomationAuthenticationSettings.UserMode));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(3600, false)]
    public async Task FederatedToken_AcquisitionReadsRotatedAssertionOnlyOnRefresh(int expiresIn, bool refreshes)
    {
        var tokenFile = WriteFile("rotating.jwt", " first-assertion\n");
        var assertions = new List<string>();
        using var client = new HttpClient(new StubHandler(async (request, cancellationToken) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/token", StringComparison.Ordinal))
            {
                var form = ParseForm(await request.Content!.ReadAsStringAsync(cancellationToken));
                Assert.Equal(Client, form["client_id"]);
                Assert.Equal(Scope, form["scope"]);
                Assert.Equal("client_credentials", form["grant_type"]);
                Assert.Equal("urn:ietf:params:oauth:client-assertion-type:jwt-bearer", form["client_assertion_type"]);
                assertions.Add(form["client_assertion"]);
                return TokenResponse($"access-token-{assertions.Count}", expiresIn);
            }

            return DiscoveryResponse(request);
        }));
        var provider = AppTokenProviderFactory.Create(
            new(AuthenticationMode.FederatedToken, Tenant, Client, tokenFile), [Scope], new StubMsalHttpClientFactory(client));

        var first = await provider.AcquireTokenAsync();
        File.WriteAllText(tokenFile, " second-assertion\r\n");
        var second = await provider.AcquireTokenAsync();

        Assert.Equal("access-token-1", first.AccessToken);
        Assert.Equal(refreshes ? "access-token-2" : "access-token-1", second.AccessToken);
        Assert.Equal(refreshes ? ["first-assertion", "second-assertion"] : ["first-assertion"], assertions);
    }

    [Fact]
    public async Task GitHubOidc_AcquisitionExchangesFreshAssertionOnRefresh()
    {
        var oidcRequests = 0;
        var assertions = new List<string>();
        using var client = new HttpClient(new StubHandler(async (request, cancellationToken) =>
        {
            if (request.RequestUri!.Host == "pipelines.example")
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
                Assert.Equal("request-token", request.Headers.Authorization.Parameter);
                Assert.Equal("api://AzureADTokenExchange", ParseForm(request.RequestUri.Query[1..])["audience"]);
                return JsonResponse(new { value = $"github-assertion-{++oidcRequests}" });
            }

            if (request.RequestUri.AbsolutePath.EndsWith("/token", StringComparison.Ordinal))
            {
                var form = ParseForm(await request.Content!.ReadAsStringAsync(cancellationToken));
                assertions.Add(form["client_assertion"]);
                return TokenResponse($"access-token-{assertions.Count}", assertions.Count == 1 ? 1 : 3600);
            }

            return DiscoveryResponse(request);
        }));
        var provider = AppTokenProviderFactory.Create(
            new(AuthenticationMode.GitHubOidc, Tenant, Client,
                GitHubRequestUrl: "https://pipelines.example/oidc?api-version=2.0", GitHubRequestToken: "request-token"),
            [Scope], new StubMsalHttpClientFactory(client));

        Assert.Equal("access-token-1", (await provider.AcquireTokenAsync()).AccessToken);
        Assert.Equal("access-token-2", (await provider.AcquireTokenAsync()).AccessToken);
        Assert.Equal("access-token-2", (await provider.AcquireTokenAsync()).AccessToken);
        Assert.Equal(2, oidcRequests);
        Assert.Equal(["github-assertion-1", "github-assertion-2"], assertions);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(Client)]
    public async Task ManagedIdentity_AcquisitionUsesConfiguredIdentityAndCachesToken(string? clientId)
    {
        var originalEndpoint = Environment.GetEnvironmentVariable("IDENTITY_ENDPOINT");
        var originalHeader = Environment.GetEnvironmentVariable("IDENTITY_HEADER");
        try
        {
            Environment.SetEnvironmentVariable("IDENTITY_ENDPOINT", "http://localhost/managed-identity");
            Environment.SetEnvironmentVariable("IDENTITY_HEADER", "identity-header");
            var requests = 0;
            using var client = new HttpClient(new StubHandler(request =>
            {
                requests++;
                Assert.Equal("http://localhost/managed-identity", request.RequestUri!.GetLeftPart(UriPartial.Path));
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("identity-header", Assert.Single(request.Headers.GetValues("X-IDENTITY-HEADER")));
                var query = ParseForm(request.RequestUri.Query[1..]);
                Assert.Equal(Scope[..^"/.default".Length], query["resource"]);
                Assert.Equal(clientId, query.GetValueOrDefault("client_id"));
                return JsonResponse(new
                {
                    access_token = "managed-identity-token",
                    token_type = "Bearer",
                    expires_on = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
                    resource = Scope[..^"/.default".Length],
                });
            }));
            var provider = AppTokenProviderFactory.Create(
                new(AuthenticationMode.ManagedIdentity, ClientId: clientId), [Scope], new StubMsalHttpClientFactory(client));

            Assert.Equal("managed-identity-token", (await provider.AcquireTokenAsync()).AccessToken);
            Assert.Equal("managed-identity-token", (await provider.AcquireTokenAsync()).AccessToken);
            Assert.Equal(1, requests);
        }
        finally
        {
            Environment.SetEnvironmentVariable("IDENTITY_ENDPOINT", originalEndpoint);
            Environment.SetEnvironmentVariable("IDENTITY_HEADER", originalHeader);
        }
    }

    [Theory]
    [InlineData("rejected")]
    [InlineData("unreachable")]
    [InlineData("cancelled")]
    public async Task ManagedIdentity_FailuresExplainHostRequirementAndPreserveCancellation(string failure)
    {
        var originalEndpoint = Environment.GetEnvironmentVariable("IDENTITY_ENDPOINT");
        var originalHeader = Environment.GetEnvironmentVariable("IDENTITY_HEADER");
        try
        {
            Environment.SetEnvironmentVariable("IDENTITY_ENDPOINT", "http://localhost/managed-identity");
            Environment.SetEnvironmentVariable("IDENTITY_HEADER", "identity-header");
            using var cancellation = new CancellationTokenSource();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var client = new HttpClient(new StubHandler(async (_, token) =>
            {
                started.TrySetResult();
                if (failure == "unreachable")
                {
                    throw new HttpRequestException("Managed-identity endpoint is unreachable.");
                }

                if (failure == "rejected")
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent("""{"error":"invalid_request","error_description":"Identity not found."}"""),
                    };
                }

                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("The request should have been cancelled.");
            }));
            var provider = AppTokenProviderFactory.Create(
                new(AuthenticationMode.ManagedIdentity), [Scope], new StubMsalHttpClientFactory(client));

            var acquisition = provider.AcquireTokenAsync(cancellation.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (failure == "cancelled")
            {
                await cancellation.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    acquisition.WaitAsync(TimeSpan.FromSeconds(5)));
                return;
            }

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                acquisition.WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.Contains("requires supported Azure compute with a managed identity enabled", error.Message, StringComparison.Ordinal);
            Assert.Contains("XBOX_AUTHENTICATION__MODE", error.Message, StringComparison.Ordinal);
            Assert.NotNull(error.InnerException);
        }
        finally
        {
            Environment.SetEnvironmentVariable("IDENTITY_ENDPOINT", originalEndpoint);
            Environment.SetEnvironmentVariable("IDENTITY_HEADER", originalHeader);
        }
    }

    [Fact]
    public void ReadCredentialFile_TrimsAndRejectsEmptyOrUnreadable()
    {
        Assert.Equal("value", AppTokenProviderFactory.ReadCredentialFile(WriteFile("a", " value\r\n"), "VAR"));

        var empty = Assert.Throws<AuthenticationConfigurationException>(() =>
            AppTokenProviderFactory.ReadCredentialFile(WriteFile("b", "  \n"), "VAR"));
        Assert.Contains("VAR is empty", empty.Message, StringComparison.Ordinal);

        var missing = Assert.Throws<AuthenticationConfigurationException>(() =>
            AppTokenProviderFactory.ReadCredentialFile(Path.Combine(_directory, "missing"), "VAR"));
        Assert.Contains("Could not read the file named by VAR", missing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadCertificate_UsesPasswordFile()
    {
        var pfx = WritePfx("protected.pfx", password: "pfx-pass");
        var passwordFile = WriteFile("password.txt", "pfx-pass\n");

        using var certificate = AppTokenProviderFactory.LoadCertificate(
            new(AuthenticationMode.Certificate, Tenant, Client, pfx, passwordFile));

        Assert.True(certificate.HasPrivateKey);
    }

    [Fact]
    public void LoadCertificate_PreservesPasswordWhitespace()
    {
        var pfx = WritePfx("spaced.pfx", password: " pfx-pass ");
        var passwordFile = WriteFile("spaced-password.txt", " pfx-pass \r\n");

        using var certificate = AppTokenProviderFactory.LoadCertificate(
            new(AuthenticationMode.Certificate, Tenant, Client, pfx, passwordFile));

        Assert.True(certificate.HasPrivateKey);
    }

    [Fact]
    public void ReadPasswordFile_RemovesOnlyOneLineEnding()
    {
        Assert.Equal(" p \n", AppTokenProviderFactory.ReadPasswordFile(WriteFile("p1", " p \n\n"), "VAR"));
        Assert.Equal("p", AppTokenProviderFactory.ReadPasswordFile(WriteFile("p2", "p"), "VAR"));

        var empty = Assert.Throws<AuthenticationConfigurationException>(() =>
            AppTokenProviderFactory.ReadPasswordFile(WriteFile("p3", "\r\n"), "VAR"));
        Assert.Contains("VAR is empty", empty.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadCertificate_WrongPassword_FailsWithoutSecrets()
    {
        var pfx = WritePfx("protected.pfx", password: "pfx-pass");
        var passwordFile = WriteFile("password.txt", "wrong-pass");

        var error = Assert.Throws<AuthenticationConfigurationException>(() => AppTokenProviderFactory.LoadCertificate(
            new(AuthenticationMode.Certificate, Tenant, Client, pfx, passwordFile)));

        Assert.Contains("CERTIFICATE__PATH", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("wrong-pass", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadCertificate_WithoutPrivateKey_Fails()
    {
        using var rsa = RSA.Create(2048);
        using var withKey = new CertificateRequest("CN=xboxdevcli-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var publicOnly = X509CertificateLoader.LoadCertificate(withKey.RawData);
        var path = Path.Combine(_directory, "public.pfx");
        File.WriteAllBytes(path, publicOnly.Export(X509ContentType.Pkcs12));

        var error = Assert.Throws<AuthenticationConfigurationException>(() => AppTokenProviderFactory.LoadCertificate(
            new(AuthenticationMode.Certificate, Tenant, Client, path)));

        Assert.Contains("no private key", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestGitHubOidcToken_SendsAudienceAndBearer()
    {
        HttpRequestMessage? captured = null;
        using var client = new HttpClient(new StubHandler(request =>
        {
            captured = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"count":1,"value":"github-jwt"}""", Encoding.UTF8, "application/json"),
            };
        }));

        var token = await AppTokenProviderFactory.RequestGitHubOidcTokenAsync(
            client, "https://pipelines.example/token?api-version=2.0", "request-token", CancellationToken.None);

        Assert.Equal("github-jwt", token);
        Assert.Equal(
            "https://pipelines.example/token?api-version=2.0&audience=api%3A%2F%2FAzureADTokenExchange",
            captured!.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer", captured.Headers.Authorization!.Scheme);
        Assert.Equal("request-token", captured.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task RequestGitHubOidcToken_FailureMentionsPermission()
    {
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AppTokenProviderFactory.RequestGitHubOidcTokenAsync(client, "https://p.example/t", "x", CancellationToken.None));

        Assert.Contains("HTTP 403", error.Message, StringComparison.Ordinal);
        Assert.Contains("id-token: write", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"count":0}""")]
    [InlineData("not json")]
    public async Task RequestGitHubOidcToken_WithoutValue_Fails(string body)
    {
        using var client = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AppTokenProviderFactory.RequestGitHubOidcTokenAsync(client, "https://p.example/t", "x", CancellationToken.None));
    }

    [Fact]
    public async Task AzureCli_AcceptsAppTokenAndCachesUntilNearExpiry()
    {
        var expires = DateTimeOffset.UtcNow.AddHours(1);
        var token = AutomationTestTokens.Jwt($$"""{"tid":"{{Tenant}}","oid":"o","sub":"o","appid":"{{Client}}","idtyp":"app"}""");
        var credential = new FakeCredential(new Azure.Core.AccessToken(token, expires));
        var provider = new AzureCliTokenProvider(credential, Scope);

        var first = await provider.AcquireTokenAsync();
        var second = await provider.AcquireTokenAsync();

        Assert.Same(first, second);
        Assert.Equal(1, credential.Calls);
        Assert.Equal([Scope], credential.Scopes!);
        Assert.Equal(token, first.AccessToken);
        Assert.Equal(Tenant, first.TenantId);
        Assert.Equal(expires, first.ExpiresOn);
        Assert.Equal([Scope], first.Scopes);
    }

    [Fact]
    public async Task AzureCli_RefreshesWithinFiveMinutesOfExpiry()
    {
        var firstToken = AutomationTestTokens.Jwt($$"""{"tid":"{{Tenant}}","oid":"o","appid":"{{Client}}","idtyp":"app"}""");
        var refreshedToken = AutomationTestTokens.Jwt($$"""{"tid":"{{Tenant}}","oid":"o","azp":"{{Client}}","idtyp":"app"}""");
        var credential = new FakeCredential(
            new Azure.Core.AccessToken(firstToken, DateTimeOffset.UtcNow.AddMinutes(4)),
            new Azure.Core.AccessToken(refreshedToken, DateTimeOffset.UtcNow.AddHours(1)));
        var provider = new AzureCliTokenProvider(credential, Scope);

        var first = await provider.AcquireTokenAsync();
        var refreshed = await provider.AcquireTokenAsync();

        Assert.Equal(2, credential.Calls);
        Assert.Equal(firstToken, first.AccessToken);
        Assert.Equal(refreshedToken, refreshed.AccessToken);
        Assert.Same(refreshed, await provider.AcquireTokenAsync());
        Assert.Equal(2, credential.Calls);
    }

    [Fact]
    public async Task AzureCli_CredentialFailure_IsReported()
    {
        var provider = new AzureCliTokenProvider(
            new FakeCredential(new Azure.Identity.CredentialUnavailableException("Please run 'az login' to set up an account.")),
            Scope);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.AcquireTokenAsync());

        Assert.Contains("Azure CLI could not get an access token", error.Message, StringComparison.Ordinal);
        Assert.Contains("az login", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AzureCli_Cancellation_Propagates()
    {
        var provider = new AzureCliTokenProvider(new FakeCredential(new OperationCanceledException()), Scope);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.AcquireTokenAsync());
    }

    private static IAppTokenProvider Create(AutomationAuthenticationSettings settings) =>
        AppTokenProviderFactory.Create(settings, [Scope]);

    private string WriteFile(string name, string contents)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, contents);
        return path;
    }

    private string WritePfx(string name, string? password)
    {
        using var rsa = RSA.Create(2048);
        using var certificate = new CertificateRequest("CN=xboxdevcli-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pkcs12, password));
        return path;
    }

    private sealed class FakeCredential : Azure.Core.TokenCredential
    {
        private readonly Azure.Core.AccessToken[] _tokens = [];
        private readonly Exception? _error;

        public FakeCredential(params Azure.Core.AccessToken[] tokens) => _tokens = tokens;

        public FakeCredential(Exception error) => _error = error;

        public int Calls { get; private set; }

        public string[]? Scopes { get; private set; }

        public override Azure.Core.AccessToken GetToken(Azure.Core.TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override ValueTask<Azure.Core.AccessToken> GetTokenAsync(
            Azure.Core.TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Calls++;
            Scopes = requestContext.Scopes;
            return _error is null
                ? ValueTask.FromResult(_tokens[Math.Min(Calls - 1, _tokens.Length - 1)])
                : ValueTask.FromException<Azure.Core.AccessToken>(_error);
        }
    }

    private static Dictionary<string, string> ParseForm(string form) =>
        form.Split('&').Select(part => part.Split('=', 2)).ToDictionary(
            part => WebUtility.UrlDecode(part[0]), part => WebUtility.UrlDecode(part[1]));

    private static HttpResponseMessage TokenResponse(string token, int expiresIn) =>
        JsonResponse(new { access_token = token, token_type = "Bearer", expires_in = expiresIn, scope = Scope });

    private static HttpResponseMessage DiscoveryResponse(HttpRequestMessage request)
    {
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("login.microsoftonline.com", request.RequestUri!.Host);
        if (request.RequestUri.AbsolutePath == "/common/discovery/instance")
        {
            return JsonResponse(new
            {
                tenant_discovery_endpoint = $"https://login.microsoftonline.com/{Tenant}/v2.0/.well-known/openid-configuration",
                metadata = new[]
                {
                    new
                    {
                        preferred_network = "login.microsoftonline.com",
                        preferred_cache = "login.microsoftonline.com",
                        aliases = new[] { "login.microsoftonline.com" },
                    },
                },
            });
        }

        Assert.Equal($"/{Tenant}/v2.0/.well-known/openid-configuration", request.RequestUri.AbsolutePath);
        return JsonResponse(new
        {
            token_endpoint = $"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/token",
            authorization_endpoint = $"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/authorize",
            issuer = $"https://login.microsoftonline.com/{Tenant}/v2.0",
        });
    }

    private static HttpResponseMessage JsonResponse(object body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    private sealed class StubMsalHttpClientFactory(HttpClient client) : IMsalHttpClientFactory
    {
        public HttpClient GetHttpClient() => client;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
            : this((request, _) => Task.FromResult(respond(request)))
        {
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }
}

public class AccessTokenClaimsTests
{
    [Fact]
    public void TryDecode_AppToken_UsesIdtyp()
    {
        var claims = AccessTokenClaims.TryDecode(AutomationTestTokens.Jwt(
            """{"tid":"t","oid":"o","sub":"o","appid":"a","aud":"api","idtyp":"app","roles":["R1"]}"""));

        Assert.NotNull(claims);
        Assert.Equal("app", claims!.PrincipalType);
        Assert.Equal("t", claims.TenantId);
        Assert.Equal("o", claims.ObjectId);
        Assert.Equal("a", claims.AppId);
        Assert.Equal("api", claims.Audience);
        Assert.Equal(["R1"], claims.Roles);
    }

    [Theory]
    [InlineData("""{"oid":"o","sub":"o","azp":"a"}""", "app")]
    [InlineData("""{"oid":"o","sub":"x","scp":"user_impersonation"}""", "user")]
    [InlineData("""{"oid":"o","sub":"x"}""", "unknown")]
    public void TryDecode_WithoutIdtyp_InfersPrincipalType(string payload, string expected)
    {
        Assert.Equal(expected, AccessTokenClaims.TryDecode(AutomationTestTokens.Jwt(payload))!.PrincipalType);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("opaque-token")]
    [InlineData("a.!!!.c")]
    [InlineData("a.WzFd.c")]
    public void TryDecode_NonJwt_ReturnsNull(string? token)
    {
        Assert.Null(AccessTokenClaims.TryDecode(token));
    }
}

internal static class AutomationTestTokens
{
    public static string Jwt(string payloadJson) =>
        "eyJhbGciOiJub25lIn0." + Base64Url(payloadJson) + ".sig";

    public static AuthenticationResult Result(string accessToken, string? tenantId = "tenant") =>
        new(
            accessToken: accessToken,
            isExtendedLifeTimeToken: false,
            uniqueId: null,
            expiresOn: DateTimeOffset.UtcNow.AddHours(1),
            extendedExpiresOn: DateTimeOffset.UtcNow.AddHours(1),
            tenantId: tenantId,
            account: null,
            idToken: null,
            scopes: ["448adbda-b8d8-4f33-a1b0-ac58cf44d4c1/.default"],
            correlationId: Guid.Empty);

    private static string Base64Url(string json) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
