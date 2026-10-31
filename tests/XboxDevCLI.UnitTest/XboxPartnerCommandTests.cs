// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Identity.Client;
using XboxDevCLI.Commands;
using XboxDevCLI.Safety;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

[Collection("ConsoleRedirect")]
public class XboxPartnerCommandTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SellerList_UnexpectedFailurePrintsOnlyTheMessage(bool authenticationFailure)
    {
        Exception exception = authenticationFailure
            ? new MsalServiceException("invalid_client", "AADSTS7000215: Invalid client secret.")
            : new NotSupportedException("The credential provider is unavailable.");
        var client = new FakeXboxPartnerClient { Exception = exception };

        var result = await InvokeAsync(client, ["seller", "list"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Equal(exception.Message + Environment.NewLine, result.Stderr);
    }

    [Theory]
    [InlineData("seller", "list")]
    [InlineData("game", "list")]
    [InlineData("game", "get", "1234ABCD")]
    public async Task RequestTimeout_KeepsTheTimeoutMessage(params string[] args)
    {
        var client = new FakeXboxPartnerClient { Exception = new TaskCanceledException("Transport timeout.") };

        var result = await InvokeAsync(
            client, args[0] == "game" ? [.. args, "--seller-id", "1234567"] : args);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Equal("The XboxPartner request timed out." + Environment.NewLine, result.Stderr);
    }

    [Theory]
    [InlineData("seller", "list")]
    [InlineData("game", "list")]
    [InlineData("game", "get", "1234ABCD")]
    public async Task CallerCancellation_IsNotReportedAsALocalFailure(params string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        var client = new FakeXboxPartnerClient
        {
            OnRequest = () => cancellation.Cancel(),
            Exception = new TaskCanceledException("Caller cancellation must not be formatted as a local failure."),
        };

        var result = await InvokeAsync(
            client, args[0] == "game" ? [.. args, "--seller-id", "1234567"] : args, cancellation.Token);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.DoesNotContain(client.Exception.Message, result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("timed out", result.Stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// Program.FormatOutput returns unparseable input unchanged, so an HTML or truncated 2xx
    /// body would otherwise reach stdout with a success exit code.
    /// </summary>
    [Fact]
    public async Task SellerList_RejectsNonJsonSuccessBody()
    {
        var client = new FakeXboxPartnerClient
        {
            SellersResponse = "<html><body>gateway</body></html>",
        };

        var result = await InvokeAsync(client, ["seller", "list"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout.Trim());
        Assert.Contains("was not valid JSON", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SellerList_TableShowsSellerIdAndNameColumns()
    {
        var client = new FakeXboxPartnerClient
        {
            SellersResponse = """
                {"sellers":[{"sellerId":"1234567","name":"Nod Publishers"}],"isStub":false}
                """,
        };

        var result = await InvokeAsync(
            client,
            ["seller", "list", "--output", "table"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("SELLER ID", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("NAME", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("1234567", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("Nod Publishers", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("\"sellers\"", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SellerList_NotFoundExplainsDeploymentInsteadOfNoSellers()
    {
        var client = new FakeXboxPartnerClient
        {
            Exception = new XboxPartnerApiException(
                HttpStatusCode.NotFound,
                "Not Found",
                string.Empty,
                new Uri("https://gateway.example/xboxpartner/v1/sellers")),
        };

        var result = await InvokeAsync(client, ["seller", "list"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("deployment or base URL issue", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("HTTP 200 with an empty sellers array", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("no sellers are associated", result.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SellerList_RejectsPlaintextRemoteGatewayBeforeSendingTheToken()
    {
        var client = new FakeXboxPartnerClient();

        var result = await InvokeAsync(
            client,
            ["seller", "list", "--base-url", "http://gateway.example"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("XboxPartner:BaseUrl", result.Stderr, StringComparison.Ordinal);

        // The guard must run before the client is reached, so the bearer token is never
        // put on the wire in plaintext.
        Assert.Null(client.LastBaseUrl);
    }

    [Fact]
    public async Task SellerList_VerboseEchoesJsonGatewayBody()
    {
        var client = new FakeXboxPartnerClient
        {
            Exception = new XboxPartnerApiException(
                HttpStatusCode.BadRequest,
                "Bad Request",
                """{"detail":"Seller id has an invalid format."}""",
                new Uri("https://gateway.example/xboxpartner/v1/sellers")),
        };

        var result = await InvokeAsync(
            client,
            ["seller", "list", "--verbose", "--output", "json-compact"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(
            """{"detail":"Seller id has an invalid format."}""",
            result.Stderr,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SellerList_VerbosePassesThroughNonJsonGatewayBody()
    {
        var client = new FakeXboxPartnerClient
        {
            Exception = new XboxPartnerApiException(
                HttpStatusCode.BadGateway,
                "Bad Gateway",
                "<html>gateway down</html>",
                new Uri("https://gateway.example/xboxpartner/v1/sellers")),
        };

        var result = await InvokeAsync(client, ["seller", "list", "--verbose"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("<html>gateway down</html>", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SellerList_WithoutVerboseOmitsRawGatewayBody()
    {
        var client = new FakeXboxPartnerClient
        {
            Exception = new XboxPartnerApiException(
                HttpStatusCode.BadGateway,
                "Bad Gateway",
                "<html>gateway down</html>",
                new Uri("https://gateway.example/xboxpartner/v1/sellers")),
        };

        var result = await InvokeAsync(client, ["seller", "list"]);

        Assert.Equal(1, result.ExitCode);
        Assert.DoesNotContain("<html>", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveBaseUrl_WithoutConfigurationOrOptionReportsAnActionableError()
    {
        var baseUrlOption = XboxPartnerCommandSupport.CreateBaseUrlOption();
        var command = new Command("probe");
        command.Options.Add(baseUrlOption);
        var configuration = new ConfigurationBuilder().Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => XboxPartnerCommandSupport.ResolveBaseUrl(
                command.Parse([]),
                baseUrlOption,
                configuration));

        Assert.Contains("XboxPartner:BaseUrl", exception.Message, StringComparison.Ordinal);
        Assert.Contains("--base-url", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveBaseUrl_UsesTheShippedAppsettingsGateway()
    {
        var baseUrlOption = XboxPartnerCommandSupport.CreateBaseUrlOption();
        var command = new Command("probe");
        command.Options.Add(baseUrlOption);
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        var baseUrl = XboxPartnerCommandSupport.ResolveBaseUrl(
            command.Parse([]),
            baseUrlOption,
            configuration);

        Assert.Equal(GatewayBaseUrl, baseUrl);
        Assert.StartsWith("https://", baseUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveBaseUrl_OptionOverridesConfiguredValue()
    {
        var baseUrlOption = XboxPartnerCommandSupport.CreateBaseUrlOption();
        var command = new Command("probe");
        command.Options.Add(baseUrlOption);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["XboxPartner:BaseUrl"] = "http://127.0.0.1:9000",
            })
            .Build();

        var baseUrl = XboxPartnerCommandSupport.ResolveBaseUrl(
            command.Parse(["--base-url", "http://localhost:54848"]),
            baseUrlOption,
            configuration);

        Assert.Equal("http://localhost:54848", baseUrl);
    }

    [Fact]
    public void ResolveBaseUrl_OverriddenGatewayWarnsOnStandardError()
    {
        var baseUrlOption = XboxPartnerCommandSupport.CreateBaseUrlOption();
        var command = new Command("probe");
        command.Options.Add(baseUrlOption);
        var originalError = Console.Error;
        using var stderr = new StringWriter();

        try
        {
            Console.SetError(stderr);
            XboxPartnerCommandSupport.ResolveBaseUrl(
                command.Parse(["--base-url", "https://evil.example.com"]),
                baseUrlOption,
                new ConfigurationBuilder().Build());
        }
        finally
        {
            Console.SetError(originalError);
        }

        Assert.Contains("evil.example.com", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveBaseUrl_ShippedGatewayDoesNotWarn()
    {
        var baseUrlOption = XboxPartnerCommandSupport.CreateBaseUrlOption();
        var command = new Command("probe");
        command.Options.Add(baseUrlOption);
        var originalError = Console.Error;
        using var stderr = new StringWriter();

        try
        {
            Console.SetError(stderr);
            XboxPartnerCommandSupport.ResolveBaseUrl(
                command.Parse(["--base-url", EndpointOverrideWarning.DefaultXboxPartnerBaseUrl]),
                baseUrlOption,
                new ConfigurationBuilder().Build());
        }
        finally
        {
            Console.SetError(originalError);
        }

        Assert.Empty(stderr.ToString());
    }

    [Fact]
    public void ResolveBaseUrl_TrimsTrailingSlashFromConfiguredValue()
    {
        var baseUrlOption = XboxPartnerCommandSupport.CreateBaseUrlOption();
        var command = new Command("probe");
        command.Options.Add(baseUrlOption);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["XboxPartner:BaseUrl"] = "http://localhost:54848/",
            })
            .Build();

        var baseUrl = XboxPartnerCommandSupport.ResolveBaseUrl(
            command.Parse([]),
            baseUrlOption,
            configuration);

        Assert.Equal("http://localhost:54848", baseUrl);
    }

    [Fact]
    public void ResolveBaseUrl_ConfigurationOnlyOverloadRequiresAConfiguredGateway()
    {
        var configuration = new ConfigurationBuilder().Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => XboxPartnerCommandSupport.ResolveBaseUrl(configuration));

        Assert.Contains("XboxPartner:BaseUrl", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveBaseUrl_ConfigurationOnlyOverloadTrimsTrailingSlash()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["XboxPartner:BaseUrl"] = "http://localhost:54848/",
            })
            .Build();

        string baseUrl = XboxPartnerCommandSupport.ResolveBaseUrl(configuration);

        Assert.Equal("http://localhost:54848", baseUrl);
    }

    /// <summary>
    /// The gateway URL the commands under test resolve to, read from the shipped
    /// appsettings.json rather than hardcoded, so these tests stay aligned with whatever
    /// configuration ships.
    /// </summary>
    private static readonly string GatewayBaseUrl = ResolveShippedGatewayBaseUrl();

    private static string ResolveShippedGatewayBaseUrl()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();
        var baseUrl = configuration["XboxPartner:BaseUrl"];
        Assert.False(
            string.IsNullOrWhiteSpace(baseUrl),
            "appsettings.json must ship XboxPartner:BaseUrl; the seller commands have no code default.");
        return baseUrl!.TrimEnd('/');
    }

    private static async Task<CapturedInvocation> InvokeAsync(
        FakeXboxPartnerClient client,
        string[] args,
        CancellationToken cancellationToken = default)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);

        try
        {
            var root = Program.BuildRootCommand(
                () => SellerCommandFactory.Create(_ => client),
                gameCommandFactory: () => GameCommandFactory.Create(_ => client));
            var exitCode = await root.Parse(args, Program.NoResponseFileConfig)
                .InvokeAsync(cancellationToken: cancellationToken);
            return new CapturedInvocation(exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private sealed record CapturedInvocation(int ExitCode, string Stdout, string Stderr);

    private sealed class FakeXboxPartnerClient : IXboxPartnerClient
    {
        public Task<XboxPartnerGameCreationJob> CreateGameAsync(
            string baseUrl, string sellerId, CreateXboxGameRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<XboxPartnerGameCreationJob> GetGameCreationJobAsync(
            string baseUrl, string sellerId, string jobId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public string SellersResponse { get; init; } = """{"sellers":[],"isStub":false}""";

        public Exception? Exception { get; init; }

        public Action? OnRequest { get; init; }

        public string? LastBaseUrl { get; private set; }

        public Task<XboxPartnerSellersResult> GetSellersAsync(
            string baseUrl,
            CancellationToken cancellationToken = default)
        {
            LastBaseUrl = baseUrl;
            OnRequest?.Invoke();
            if (Exception != null)
            {
                throw Exception;
            }

            return Task.FromResult(new XboxPartnerSellersResult(SellersResponse));
        }

        public Task<string> GetGamesAsync(
            string baseUrl,
            string sellerId,
            string? titleId = null,
            string? storeId = null,
            string? keyword = null,
            CancellationToken cancellationToken = default)
        {
            OnRequest?.Invoke();
            if (Exception != null)
            {
                throw Exception;
            }

            return Task.FromResult("""{"games":[]}""");
        }

        public void Dispose()
        {
        }
    }
}
