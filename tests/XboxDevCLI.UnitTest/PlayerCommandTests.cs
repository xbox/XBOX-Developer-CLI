// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Configuration;
using Microsoft.Identity.Client;
using Moq;
using System.CommandLine;
using System.Net;
using System.Text.Json;
using XboxDevCLI.PlayFab.Commands;
using XboxDevCLI.Services;
using XboxDevCLI.Output;
using Xunit;

namespace XboxDevCLI.UnitTest;

/// <summary>
/// Coverage for the <c>playfab player</c> command group: command/option structure, the request
/// body actually serialized, output shaping, and the error path.
/// </summary>
public class PlayerCommandTests
{
    private const string PlayFabId = "ABC123DEF456";
    private const string AccountInfoPath = "/Admin/GetUserAccountInfo";

    private const string OkEnvelope =
        """{"code":200,"status":"OK","data":{"UserInfo":{"PlayFabId":"ABC123DEF456"}}}""";

    #region Command structure

    [Fact]
    public void Player_HasAccountGet()
    {
        var player = PlayerCommandFactory.Create();

        Assert.Equal("player", player.Name);
        var account = Assert.Single(player.Subcommands, c => c.Name == "account");
        Assert.Contains(account.Subcommands, c => c.Name == "get");
    }

    [Fact]
    public void AccountGet_RequiresPlayFabId()
    {
        var getAccount = GetAccountCommand();

        var option = Assert.Single(getAccount.Options, o => o.Name == "--playfab-id");
        Assert.True(option.Required);
    }

    [Theory]
    [InlineData("--email")]
    [InlineData("--username")]
    [InlineData("--display-name")]
    [InlineData("--xbox-id")]
    [InlineData("--master-player-account-id")]
    [InlineData("--xbox-sandbox")]
    public void AccountGet_ExposesNoOtherLookupOption(string option)
    {
        // GetUserAccountInfo also matches on email, username and display name, and platform
        // identifiers could be resolved via a second call. None is exposed: see the remarks on
        // PlayerCommandFactory for why each was deliberately left out.
        var getAccount = GetAccountCommand();

        Assert.DoesNotContain(getAccount.Options, o => o.Name == option);
    }

    [Fact]
    public void AccountGet_HelpNamesTheMasterAccountIdRequirement()
    {
        // The title player account ID looks identical to the PlayFab ID but is a different value
        // this API rejects, and Game Manager shows both — so the help has to disambiguate.
        var option = Assert.Single(GetAccountCommand().Options, o => o.Name == "--playfab-id");

        Assert.Contains("master player account", option.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("title player account", option.Description, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region player account get action

    [Fact]
    public async Task RunGetAccountAsync_SendsPlayFabIdToGetUserAccountInfo()
    {
        var handler = new PlayerMockHandler(Ok(OkEnvelope));
        using var client = CreateClientWithHandler(handler);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await PlayerCommandFactory.RunGetAccountAsync(
            client, PlayFabId, OutputOptions.Json, stdout, stderr,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains(AccountInfoPath, handler.LastRequestUri, StringComparison.Ordinal);

        using var body = JsonDocument.Parse(handler.CapturedBody!);
        Assert.Equal(PlayFabId, body.RootElement.GetProperty("PlayFabId").GetString());
        // Unset match keys must be omitted entirely, not sent as null: GetUserAccountInfo applies
        // a priority order when several are present, which would decide the match for us.
        Assert.False(body.RootElement.TryGetProperty("Email", out _));
        Assert.False(body.RootElement.TryGetProperty("Username", out _));
        Assert.False(body.RootElement.TryGetProperty("TitleDisplayName", out _));

        Assert.NotEmpty(stdout.ToString());
        Assert.Empty(stderr.ToString());
    }

    [Fact]
    public async Task RunGetAccountAsync_TrimsTheSuppliedId()
    {
        var handler = new PlayerMockHandler(Ok(OkEnvelope));
        using var client = CreateClientWithHandler(handler);

        var exitCode = await PlayerCommandFactory.RunGetAccountAsync(
            client, $"  {PlayFabId}  ", OutputOptions.Json, new StringWriter(), new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        using var body = JsonDocument.Parse(handler.CapturedBody!);
        Assert.Equal(PlayFabId, body.RootElement.GetProperty("PlayFabId").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RunGetAccountAsync_BlankId_FailsWithoutCallingTheApi(string? playFabId)
    {
        // A blank flag value must not reach PlayFab as a real lookup key.
        var handler = new PlayerMockHandler(Ok(OkEnvelope));
        using var client = CreateClientWithHandler(handler);
        var stderr = new StringWriter();

        var exitCode = await PlayerCommandFactory.RunGetAccountAsync(
            client, playFabId, OutputOptions.Json, new StringWriter(), stderr,
            CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Null(handler.LastRequestUri);
        Assert.Contains("--playfab-id", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunGetAccountAsync_DataOnly_StripsEnvelope()
    {
        var handler = new PlayerMockHandler(Ok(OkEnvelope));
        using var client = CreateClientWithHandler(handler);
        var stdout = new StringWriter();

        var exitCode = await PlayerCommandFactory.RunGetAccountAsync(
            client, PlayFabId, new OutputOptions("json-compact", true), stdout, new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        var output = stdout.ToString().Trim();
        Assert.StartsWith("{\"UserInfo\"", output, StringComparison.Ordinal);
        Assert.DoesNotContain("\"status\"", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunGetAccountAsync_ApiError_WritesToStderrAndReturnsOne()
    {
        var handler = new PlayerMockHandler(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                """{"code":400,"status":"BadRequest","error":"AccountNotFound","errorCode":1001,"errorMessage":"User not found"}"""),
        });
        using var client = CreateClientWithHandler(handler);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await PlayerCommandFactory.RunGetAccountAsync(
            client, PlayFabId, OutputOptions.Json, stdout, stderr,
            CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Empty(stdout.ToString());
        Assert.Contains("AccountNotFound", stderr.ToString(), StringComparison.Ordinal);
    }

    #endregion

    #region Test helpers

    private static Command GetAccountCommand() =>
        PlayerCommandFactory.Create()
            .Subcommands.First(c => c.Name == "account")
            .Subcommands.First(c => c.Name == "get");

    private static HttpResponseMessage Ok(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json) };

    private static IConfiguration CreateTestConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PlayFab:TitleId"] = "TEST123",
                ["PlayFab:BaseUrlTemplate"] = "https://{0}.playfabapi.com",
                ["AzureAd:ClientId"] = "test-client-id",
                ["AzureAd:TenantId"] = "test-tenant-id",
                ["AzureAd:Scopes"] = "api://test/.default",
            })
            .Build();

    private static AuthenticationResult CreateMockAuthResult(string accessToken) =>
        new(
            accessToken: accessToken,
            isExtendedLifeTimeToken: false,
            uniqueId: "test-unique-id",
            expiresOn: DateTimeOffset.UtcNow.AddHours(1),
            extendedExpiresOn: DateTimeOffset.UtcNow.AddHours(1),
            tenantId: "test-tenant-id",
            account: Mock.Of<IAccount>(),
            idToken: "test-id-token",
            scopes: new[] { "api://test/.default" },
            correlationId: Guid.NewGuid());

    private static PlayFabApiClient CreateClientWithHandler(HttpMessageHandler handler)
    {
        var config = CreateTestConfiguration();
        var mockAuth = new Mock<AuthenticationService>(config) { CallBase = false };
        mockAuth.Setup(a => a.AcquireTokenSilentAsync(It.IsAny<CancellationToken>())).ReturnsAsync(CreateMockAuthResult("token"));
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://TEST123.playfabapi.com/") };
        return new PlayFabApiClient(config, mockAuth.Object, httpClient);
    }

    #endregion
}

/// <summary>
/// Mock handler that captures the outgoing request URI and body for assertion, then returns a
/// fixed response.
/// </summary>
internal sealed class PlayerMockHandler : HttpMessageHandler
{
    private readonly HttpResponseMessage _response;

    public PlayerMockHandler(HttpResponseMessage response) => _response = response;

    public string? CapturedBody { get; private set; }

    public string? LastRequestUri { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequestUri = request.RequestUri?.ToString();
        if (request.Content != null)
        {
            CapturedBody = await request.Content.ReadAsStringAsync(cancellationToken);
        }

        return _response;
    }
}
