// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Identity.Client;
using Moq;
using XboxDevCLI.PlayFab.Commands;
using XboxDevCLI.PlayFab.Generated;
using XboxDevCLI.Services;
using XboxDevCLI.Output;
using Xunit;

namespace XboxDevCLI.UnitTest;

[Collection("ConsoleRedirect")]
public class TitleDataCommandTests
{
    private static IConfiguration CreateTestConfiguration(
        string titleId = "TEST123",
        string baseUrlTemplate = "https://{0}.playfabapi.com",
        long? maxResponseBytes = null)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PlayFab:TitleId"] = titleId,
                ["PlayFab:BaseUrlTemplate"] = baseUrlTemplate,
                ["AzureAd:ClientId"] = "test-client-id",
                ["AzureAd:TenantId"] = "test-tenant-id",
                ["AzureAd:Scopes"] = "api://test/.default",
                ["PlayFab:MaxResponseBytes"] = maxResponseBytes?.ToString(),
            })
            .Build();
    }

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

    private static PlayFabApiClient CreateClientWithHandler(
        HttpMessageHandler handler,
        Mock<AuthenticationService>? mockAuth = null,
        long? maxResponseBytes = null)
    {
        var config = CreateTestConfiguration(maxResponseBytes: maxResponseBytes);
        if (mockAuth is null)
        {
            mockAuth = new Mock<AuthenticationService>(config) { CallBase = false };
            mockAuth.Setup(a => a.AcquireTokenSilentAsync(It.IsAny<CancellationToken>())).ReturnsAsync(CreateMockAuthResult("token"));
        }

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://TEST123.playfabapi.com/") };
        return new PlayFabApiClient(config, mockAuth.Object, httpClient);
    }

    private static HttpResponseMessage Ok(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json) };

    private static HttpResponseMessage Error(HttpStatusCode statusCode, string message) =>
        new(statusCode)
        {
            Content = new StringContent($$"""{"code":400,"status":"BadRequest","error":"BadRequest","errorCode":1,"errorMessage":"{{message}}"}"""),
        };

    [Fact]
    public void TitleDataCommand_HasExpectedSubcommandsAndOptions()
    {
        var command = TitleDataCommandFactory.Create();

        Assert.Equal("title-data", command.Name);
        Assert.Equal(3, command.Subcommands.Count);
        Assert.Contains(command.Subcommands, c => c.Name == "get");
        Assert.Contains(command.Subcommands, c => c.Name == "set");
        Assert.Contains(command.Subcommands, c => c.Name == "delete");

        var get = command.Subcommands.Single(c => c.Name == "get");
        Assert.Contains(get.Arguments, a => a.Name == "key");
        Assert.Contains(get.Options, o => o.Name == "--internal");
        Assert.Contains(get.Options, o => o.Name == "--key");
        Assert.Contains(get.Options, o => o.Name == "--keys");
        Assert.Contains(get.Options, o => o.Name == "--value-only");

        var set = command.Subcommands.Single(c => c.Name == "set");
        Assert.Contains(set.Options, o => o.Name == "--internal");
        Assert.Contains(set.Options, o => o.Name == "--key");
        Assert.Contains(set.Options, o => o.Name == "--value");
        Assert.Contains(set.Options, o => o.Name == "--data");

        var delete = command.Subcommands.Single(c => c.Name == "delete");
        var keys = Assert.Single(delete.Options, o => o.Name == "--keys");
        Assert.True(keys.Required);
    }

    [Fact]
    public void PlayFabCommand_RegistersTitleData()
    {
        var playfab = PlayFabCommandFactory.Create();

        Assert.Contains(playfab.Subcommands, c => c.Name == "title-data");
    }

    [Fact]
    public void BuildGetCall_MapsClientAndInternalEndpoints()
    {
        var (clientPath, clientRequest) = TitleDataCommandFactory.BuildGetCall(internalTier: false, new List<string> { "a", "b" });
        var (internalPath, internalRequest) = TitleDataCommandFactory.BuildGetCall(internalTier: true, null);

        Assert.Equal("/Admin/GetTitleData", clientPath);
        Assert.Equal(new List<string> { "a", "b" }, clientRequest.Keys);
        Assert.Equal("/Admin/GetTitleInternalData", internalPath);
        Assert.Null(internalRequest.Keys);
    }

    [Fact]
    public void BuildSetCall_MapsClientAndInternalEndpoints()
    {
        var (clientPath, clientRequest) = TitleDataCommandFactory.BuildSetCall(internalTier: false, "motd", "hi");
        var (internalPath, internalRequest) = TitleDataCommandFactory.BuildSetCall(internalTier: true, "secret", null);

        Assert.Equal("/Admin/SetTitleData", clientPath);
        Assert.Equal("motd", clientRequest.Key);
        Assert.Equal("hi", clientRequest.Value);
        Assert.Equal("/Admin/SetTitleInternalData", internalPath);
        Assert.Equal("secret", internalRequest.Key);
        Assert.Null(internalRequest.Value);
    }

    [Fact]
    public void TitleDataEndpoints_AreKnownPlayFabEndpoints()
    {
        var known = PlayFabApiMetadata.AllEndpoints.Select(e => e.Path).ToHashSet();

        Assert.Contains("/Admin/GetTitleData", known);
        Assert.Contains("/Admin/GetTitleInternalData", known);
        Assert.Contains("/Admin/SetTitleData", known);
        Assert.Contains("/Admin/SetTitleInternalData", known);
    }

    [Fact]
    public async Task AllFourEndpoints_AreReachableThroughClient()
    {
        var handler = new TitleDataMockHandler((_, _, _) => Ok("""{"code":200,"status":"OK","data":{"Data":{}}}"""));
        using var client = CreateClientWithHandler(handler);

        foreach (var (apiPath, request) in new[]
                 {
                     TitleDataCommandFactory.BuildGetCall(false, null),
                     TitleDataCommandFactory.BuildGetCall(true, null),
                 })
        {
            await client.CallApiAsync(apiPath, JsonSerializer.Serialize(request), CancellationToken.None);
        }

        foreach (var (apiPath, request) in new[]
                 {
                     TitleDataCommandFactory.BuildSetCall(false, "a", "1"),
                     TitleDataCommandFactory.BuildSetCall(true, "b", "2"),
                 })
        {
            await client.CallApiAsync(apiPath, JsonSerializer.Serialize(request), CancellationToken.None);
        }

        Assert.Contains(handler.Requests, r => r.Uri.Contains("/Admin/GetTitleData", StringComparison.Ordinal));
        Assert.Contains(handler.Requests, r => r.Uri.Contains("/Admin/GetTitleInternalData", StringComparison.Ordinal));
        Assert.Contains(handler.Requests, r => r.Uri.Contains("/Admin/SetTitleData", StringComparison.Ordinal));
        Assert.Contains(handler.Requests, r => r.Uri.Contains("/Admin/SetTitleInternalData", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunGetAsync_SingleKey_PrintsBareValueByDefault()
    {
        using var client = CreateClientWithHandler(new TitleDataMockHandler((_, _, _) =>
            Ok("""{"code":200,"status":"OK","data":{"Data":{"motd":"hello players"}}}""")));
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        // --key with no explicit structured-output request prints just the value.
        var exit = await TitleDataCommandFactory.RunGetAsync(
            client, false, null, "motd", null, valueOnly: false, explicitStructuredOutput: false, OutputOptions.Json, stdout, stderr, cancellationToken: CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal("hello players", stdout.ToString().Trim());
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public async Task RunGetAsync_PositionalKey_PrintsBareValueByDefault()
    {
        using var client = CreateClientWithHandler(new TitleDataMockHandler((_, _, _) =>
            Ok("""{"code":200,"status":"OK","data":{"Data":{"motd":"hello players"}}}""")));
        var stdout = new StringWriter();

        var exit = await TitleDataCommandFactory.RunGetAsync(
            client, false, "motd", null, null, valueOnly: false, explicitStructuredOutput: false, OutputOptions.Json, stdout, new StringWriter(), cancellationToken: CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal("hello players", stdout.ToString().Trim());
    }

    [Fact]
    public async Task RunGetAsync_ValueOnlyFlag_WarnsAndStillPrintsBareValue()
    {
        using var client = CreateClientWithHandler(new TitleDataMockHandler((_, _, _) =>
            Ok("""{"code":200,"status":"OK","data":{"Data":{"motd":"hello players"}}}""")));
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        // --value-only is a deprecated alias; combining it with a single positional key is not an error.
        var exit = await TitleDataCommandFactory.RunGetAsync(
            client, false, "motd", null, null, valueOnly: true, explicitStructuredOutput: false, OutputOptions.Json, stdout, stderr, cancellationToken: CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal("hello players", stdout.ToString().Trim());
        Assert.Contains("Warning: --value-only is deprecated", stderr.ToString());
    }

    [Fact]
    public async Task RunGetAsync_ValueOnlyFlag_DoesNotOverrideExplicitStructuredOutput()
    {
        using var client = CreateClientWithHandler(new TitleDataMockHandler((_, _, _) =>
            Ok("""{"code":200,"status":"OK","data":{"Data":{"motd":"hello players"}}}""")));
        var stdout = new StringWriter();

        // The flag is not an output switch: an explicit structured-output request still wins.
        var exit = await TitleDataCommandFactory.RunGetAsync(
            client, false, "motd", null, null, valueOnly: true, explicitStructuredOutput: true, OutputOptions.Json, stdout, new StringWriter(), cancellationToken: CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("\"code\"", stdout.ToString());
        Assert.Contains("\"Data\"", stdout.ToString());
        Assert.NotEqual("hello players", stdout.ToString().Trim());
    }

    [Theory]
    [InlineData("playfab title-data get DemoKey", false)]
    [InlineData("playfab title-data get --key DemoKey", false)]
    [InlineData("playfab title-data get DemoKey -o json", true)]
    [InlineData("playfab title-data get DemoKey --output json-compact", true)]
    [InlineData("playfab title-data get DemoKey --data-only", true)]
    [InlineData("playfab title-data get DemoKey -o json --data-only", true)]
    [InlineData("playfab title-data get", false)]
    public void Get_Parse_DistinguishesOmittedOutputFromExplicitStructuredRequest(string args, bool explicitStructuredOutput)
    {
        var parseResult = Program.BuildRootCommand().Parse(args, Program.NoResponseFileConfig);

        Assert.Empty(parseResult.Errors);
        Assert.Equal(explicitStructuredOutput, TitleDataCommandFactory.RequestsExplicitStructuredOutput(parseResult));
    }

    [Fact]
    public void GetCommand_Help_DescribesStructuredJsonNotEnvelope()
    {
        var get = TitleDataCommandFactory.Create().Subcommands.Single(c => c.Name == "get");
        var key = Assert.Single(get.Arguments, a => a.Name == "key");

        Assert.Contains("structured JSON", key.Description);
        Assert.DoesNotContain("envelope", key.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunGetAsync_SingleKey_ExplicitOutput_PrintsEnvelope()
    {
        using var client = CreateClientWithHandler(new TitleDataMockHandler((_, _, _) =>
            Ok("""{"code":200,"status":"OK","data":{"Data":{"motd":"hello players"}}}""")));
        var stdout = new StringWriter();

        // An explicit -o json request wins over the value-only default: the full envelope is printed.
        var exit = await TitleDataCommandFactory.RunGetAsync(
            client, false, "motd", null, null, valueOnly: false, explicitStructuredOutput: true, OutputOptions.Json, stdout, new StringWriter(), cancellationToken: CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("\"code\"", stdout.ToString());
        Assert.Contains("\"Data\"", stdout.ToString());
    }

    [Fact]
    public async Task RunGetAsync_SingleKey_DataOnly_PrintsStructuredPayload()
    {
        using var client = CreateClientWithHandler(new TitleDataMockHandler((_, _, _) =>
            Ok("""{"code":200,"status":"OK","data":{"Data":{"motd":"hello players"}}}""")));
        var stdout = new StringWriter();

        // --data-only is an explicit structured-output request; it strips the envelope but keeps JSON.
        var exit = await TitleDataCommandFactory.RunGetAsync(
            client, false, "motd", null, null, valueOnly: false, explicitStructuredOutput: true, new OutputOptions("json-compact", true), stdout, new StringWriter(), cancellationToken: CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.DoesNotContain("\"code\"", stdout.ToString());
        Assert.Contains("\"Data\"", stdout.ToString());
    }

    [Fact]
    public async Task RunGetAsync_SingleKey_MissingKey_ReturnsOne()
    {
        using var client = CreateClientWithHandler(new TitleDataMockHandler((_, _, _) =>
            Ok("""{"code":200,"status":"OK","data":{"Data":{"other":"x"}}}""")));
        var stderr = new StringWriter();

        var exit = await TitleDataCommandFactory.RunGetAsync(
            client, false, "motd", null, null, valueOnly: false, explicitStructuredOutput: false, OutputOptions.Json, new StringWriter(), stderr, cancellationToken: CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains("not found", stderr.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunGetAsync_SingleKey_MissingKeyWithStructuredOutput_ReturnsOne(bool dataOnly)
    {
        using var client = CreateClientWithHandler(new TitleDataMockHandler((_, _, _) =>
            Ok("""{"code":200,"status":"OK","data":{"Data":{"other":"x"}}}""")));
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await TitleDataCommandFactory.RunGetAsync(
            client, false, "motd", null, null, valueOnly: false, explicitStructuredOutput: true,
            new OutputOptions("json", dataOnly), stdout, stderr, cancellationToken: CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Contains("not found", stderr.ToString());
    }

    [Fact]
    public async Task RunGetAsync_PositionalAndKeyOption_Rejected()
    {
        using var client = CreateClientWithHandler(new TitleDataMockHandler((_, _, _) => Ok("""{"code":200,"status":"OK","data":{}}""")));
        var stderr = new StringWriter();

        var exit = await TitleDataCommandFactory.RunGetAsync(
            client, false, "a", "b", null, valueOnly: false, explicitStructuredOutput: false, OutputOptions.Json, new StringWriter(), stderr, cancellationToken: CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains("positional key or --key", stderr.ToString());
    }

    [Fact]
    public async Task RunGetAsync_PositionalKeyAndKeys_Rejected()
    {
        using var client = CreateClientWithHandler(new TitleDataMockHandler((_, _, _) => Ok("""{"code":200,"status":"OK","data":{}}""")));
        var stderr = new StringWriter();

        var exit = await TitleDataCommandFactory.RunGetAsync(
            client, false, "a", null, "b,c", valueOnly: false, explicitStructuredOutput: false, OutputOptions.Json, new StringWriter(), stderr, cancellationToken: CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains("single key or --keys", stderr.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RunGetAsync_PositionalKeyAndBlankKeys_Rejected(string blankKeys)
    {
        var handler = new TitleDataMockHandler((_, _, _) => Ok("""{"code":200,"status":"OK","data":{}}"""));
        using var client = CreateClientWithHandler(handler);
        var stderr = new StringWriter();

        var exit = await TitleDataCommandFactory.RunGetAsync(
            client, false, "a", null, blankKeys, valueOnly: false, explicitStructuredOutput: false,
            OutputOptions.Json, new StringWriter(), stderr, cancellationToken: CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains("single key or --keys", stderr.ToString());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RunGetAsync_KeyOptionAndKeys_Rejected()
    {
        using var client = CreateClientWithHandler(new TitleDataMockHandler((_, _, _) => Ok("""{"code":200,"status":"OK","data":{}}""")));
        var stderr = new StringWriter();

        var exit = await TitleDataCommandFactory.RunGetAsync(
            client, false, null, "a", "b,c", valueOnly: false, explicitStructuredOutput: false, OutputOptions.Json, new StringWriter(), stderr, cancellationToken: CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains("single key or --keys", stderr.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RunGetAsync_BlankPositionalKey_Rejected(string blankKey)
    {
        var handler = new TitleDataMockHandler((_, _, _) => Ok("""{"code":200,"status":"OK","data":{}}"""));
        using var client = CreateClientWithHandler(handler);
        var stderr = new StringWriter();

        // A present-but-blank positional key must be rejected, not silently treated as an all-keys read.
        var exit = await TitleDataCommandFactory.RunGetAsync(
            client, false, blankKey, null, null, valueOnly: false, explicitStructuredOutput: false, OutputOptions.Json, new StringWriter(), stderr, cancellationToken: CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains("Key must not be blank", stderr.ToString());
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RunGetAsync_BlankKeyOption_Rejected(string blankKey)
    {
        var handler = new TitleDataMockHandler((_, _, _) => Ok("""{"code":200,"status":"OK","data":{}}"""));
        using var client = CreateClientWithHandler(handler);
        var stderr = new StringWriter();

        var exit = await TitleDataCommandFactory.RunGetAsync(
            client, false, null, blankKey, null, valueOnly: false, explicitStructuredOutput: false, OutputOptions.Json, new StringWriter(), stderr, cancellationToken: CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains("Key must not be blank", stderr.ToString());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RunGetAsync_ValueOnlyWithoutKey_Rejected()
    {
        using var client = CreateClientWithHandler(new TitleDataMockHandler((_, _, _) => Ok("""{"code":200,"status":"OK","data":{}}""")));
        var stderr = new StringWriter();

        var exit = await TitleDataCommandFactory.RunGetAsync(
            client, false, null, null, "a,b", valueOnly: true, explicitStructuredOutput: false, OutputOptions.Json, new StringWriter(), stderr, cancellationToken: CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains("requires a single key", stderr.ToString());
    }

    [Fact]
    public async Task RunGetAsync_MultiKey_SendsKeys()
    {
        var handler = new TitleDataMockHandler((_, _, _) =>
            Ok("""{"code":200,"status":"OK","data":{"Data":{"a":"1","b":"2"}}}"""));
        using var client = CreateClientWithHandler(handler);

        var exit = await TitleDataCommandFactory.RunGetAsync(
            client, false, null, null, "a, b", valueOnly: false, explicitStructuredOutput: false, OutputOptions.Json, new StringWriter(), new StringWriter(), cancellationToken: CancellationToken.None);

        Assert.Equal(0, exit);
        using var body = JsonDocument.Parse(handler.Requests.Single().Body!);
        var keys = body.RootElement.GetProperty("Keys").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(new List<string?> { "a", "b" }, keys);
    }

    [Fact]
    public async Task RunGetAsync_AllKeys_OmitsKeys()
    {
        var handler = new TitleDataMockHandler((_, _, _) =>
            Ok("""{"code":200,"status":"OK","data":{"Data":{"a":"1"}}}"""));
        using var client = CreateClientWithHandler(handler);

        var exit = await TitleDataCommandFactory.RunGetAsync(
            client, false, null, null, null, valueOnly: false, explicitStructuredOutput: false, OutputOptions.Json, new StringWriter(), new StringWriter(), cancellationToken: CancellationToken.None);

        Assert.Equal(0, exit);
        using var body = JsonDocument.Parse(handler.Requests.Single().Body!);
        Assert.False(body.RootElement.TryGetProperty("Keys", out _));
    }

    [Fact]
    public async Task RunGetAsync_Internal_UsesInternalEndpoint()
    {
        var handler = new TitleDataMockHandler((_, _, _) =>
            Ok("""{"code":200,"status":"OK","data":{"Data":{"secret":"s"}}}"""));
        using var client = CreateClientWithHandler(handler);

        var exit = await TitleDataCommandFactory.RunGetAsync(
            client, true, "secret", null, null, valueOnly: false, explicitStructuredOutput: false, OutputOptions.Json, new StringWriter(), new StringWriter(), cancellationToken: CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("/Admin/GetTitleInternalData", handler.Requests.Single().Uri);
    }

    [Fact]
    public async Task RunGetAsync_OutputRendering_HonorsJsonCompactAndDataOnly()
    {
        const string responseJson = """{"code":200,"status":"OK","data":{"Data":{"a":"1"}}}""";
        using var prettyClient = CreateClientWithHandler(new TitleDataMockHandler((_, _, _) => Ok(responseJson)));
        var pretty = new StringWriter();
        await TitleDataCommandFactory.RunGetAsync(prettyClient, false, null, null, null, false, false, OutputOptions.Json, pretty, new StringWriter(), CancellationToken.None);

        using var compactClient = CreateClientWithHandler(new TitleDataMockHandler((_, _, _) => Ok(responseJson)));
        var compact = new StringWriter();
        await TitleDataCommandFactory.RunGetAsync(compactClient, false, null, null, null, false, false, OutputOptions.JsonCompact, compact, new StringWriter(), CancellationToken.None);

        using var dataOnlyClient = CreateClientWithHandler(new TitleDataMockHandler((_, _, _) => Ok(responseJson)));
        var dataOnly = new StringWriter();
        await TitleDataCommandFactory.RunGetAsync(dataOnlyClient, false, null, null, null, false, true, new OutputOptions("json-compact", true), dataOnly, new StringWriter(), CancellationToken.None);

        Assert.Contains("\n", pretty.ToString().TrimEnd());
        Assert.DoesNotContain("\n", compact.ToString().TrimEnd());
        Assert.Contains("\"code\"", compact.ToString());
        Assert.DoesNotContain("\"code\"", dataOnly.ToString());
        Assert.Contains("\"Data\"", dataOnly.ToString());
    }

    [Fact]
    public async Task RunGetAsync_ApiError_ReturnsOneAndWritesError()
    {
        var handler = new TitleDataMockHandler((_, _, _) => Error(HttpStatusCode.BadRequest, "bad keys"));
        using var client = CreateClientWithHandler(handler);
        var stderr = new StringWriter();

        var exit = await TitleDataCommandFactory.RunGetAsync(
            client, false, null, null, null, false, false, OutputOptions.Json, new StringWriter(), stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains("bad keys", stderr.ToString());
    }

    [Fact]
    public async Task RunSetAsync_KeyValue_SetsSingleKey()
    {
        var handler = new TitleDataMockHandler((_, _, _) => Ok("""{"code":200,"status":"OK","data":{}}"""));
        using var client = CreateClientWithHandler(handler);
        var stdout = new StringWriter();

        // The value is stored verbatim, including '=' characters.
        var exit = await TitleDataCommandFactory.RunSetAsync(
            client, false, "motd", "some value=3", null, OutputOptions.JsonCompact, stdout, new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("/Admin/SetTitleData", handler.Requests.Single().Uri);
        AssertSetBody(handler.Requests[0].Body!, "motd", "some value=3");
        using var summary = JsonDocument.Parse(stdout.ToString());
        Assert.Equal(new[] { "motd" }, summary.RootElement.GetProperty("applied").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task RunSetAsync_Internal_UsesInternalEndpoint()
    {
        var handler = new TitleDataMockHandler((_, _, _) => Ok("""{"code":200,"status":"OK","data":{}}"""));
        using var client = CreateClientWithHandler(handler);

        var exit = await TitleDataCommandFactory.RunSetAsync(
            client, true, "secret", "1", null, OutputOptions.Json, new StringWriter(), new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("/Admin/SetTitleInternalData", handler.Requests.Single().Uri);
    }

    [Fact]
    public async Task RunSetAsync_DataObject_StringifiesValues()
    {
        var handler = new TitleDataMockHandler((_, _, _) => Ok("""{"code":200,"status":"OK","data":{}}"""));
        using var client = CreateClientWithHandler(handler);
        const string dataJson = """{"s":"text","n":5,"b":true,"o":{"x":1},"a":[1,2]}""";

        var exit = await TitleDataCommandFactory.RunSetAsync(
            client, false, null, null, dataJson, OutputOptions.Json, new StringWriter(), new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal(5, handler.Requests.Count);
        AssertSetBody(handler.Requests[0].Body!, "s", "text");
        AssertSetBody(handler.Requests[1].Body!, "n", "5");
        AssertSetBody(handler.Requests[2].Body!, "b", "true");
        AssertSetBody(handler.Requests[3].Body!, "o", """{"x":1}""");
        AssertSetBody(handler.Requests[4].Body!, "a", "[1,2]");
    }

    [Fact]
    public async Task RunSetAsync_DataObject_NullRejects()
    {
        var handler = new TitleDataMockHandler((_, _, _) => Ok("""{"code":200,"status":"OK","data":{}}"""));
        using var client = CreateClientWithHandler(handler);
        var stderr = new StringWriter();

        var exit = await TitleDataCommandFactory.RunSetAsync(
            client, false, null, null, """{"a":null}""", OutputOptions.Json, new StringWriter(), stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains("title-data delete", stderr.ToString());
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("""["a","b"]""")]
    [InlineData("\"hi\"")]
    [InlineData("5")]
    [InlineData("true")]
    public async Task RunSetAsync_DataNonObject_ReturnsFriendlyError(string dataJson)
    {
        var handler = new TitleDataMockHandler((_, _, _) => Ok("""{"code":200,"status":"OK","data":{}}"""));
        using var client = CreateClientWithHandler(handler);
        var stderr = new StringWriter();

        var exit = await TitleDataCommandFactory.RunSetAsync(
            client, false, null, null, dataJson, OutputOptions.Json, new StringWriter(), stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains("expected a JSON object", stderr.ToString());
        Assert.DoesNotContain("Dictionary", stderr.ToString());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RunSetAsync_KeyValueAndData_Rejected()
    {
        using var client = CreateClientWithHandler(new TitleDataMockHandler((_, _, _) => Ok("""{"code":200,"status":"OK","data":{}}""")));
        var stderr = new StringWriter();

        var exit = await TitleDataCommandFactory.RunSetAsync(
            client, false, "a", "1", """{"b":"2"}""", OutputOptions.Json, new StringWriter(), stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains("not both", stderr.ToString());
    }

    [Fact]
    public async Task RunSetAsync_NothingToSet_ReturnsOne()
    {
        using var client = CreateClientWithHandler(new TitleDataMockHandler((_, _, _) => Ok("""{"code":200,"status":"OK","data":{}}""")));
        var stderr = new StringWriter();

        var exit = await TitleDataCommandFactory.RunSetAsync(
            client, false, null, null, null, OutputOptions.Json, new StringWriter(), stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains("Nothing to set", stderr.ToString());
    }

    [Fact]
    public async Task RunSetAsync_PartialFailure_StopsAndReportsSummary()
    {
        var handler = new TitleDataMockHandler((_, _, index) =>
            index == 1 ? Ok("""{"code":200,"status":"OK","data":{}}""") : Error(HttpStatusCode.BadRequest, "bad b"));
        using var client = CreateClientWithHandler(handler);
        var stderr = new StringWriter();

        var exit = await TitleDataCommandFactory.RunSetAsync(
            client, false, null, null, """{"a":"1","b":"2","c":"3"}""", OutputOptions.JsonCompact, new StringWriter(), stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Equal(2, handler.Requests.Count);
        using var summary = JsonDocument.Parse(stderr.ToString());
        Assert.Equal("a", summary.RootElement.GetProperty("applied").EnumerateArray().Single().GetString());
        Assert.Equal("b", summary.RootElement.GetProperty("failed").GetProperty("key").GetString());
        Assert.Contains("bad b", summary.RootElement.GetProperty("failed").GetProperty("error").GetRawText());
    }

    /// <summary>
    /// Ctrl-C on a later key keeps the summary: the earlier keys are applied, and the in-flight
    /// key is reported as unknown rather than failed, because PlayFab may have applied it.
    /// </summary>
    [Fact]
    public async Task RunSetAsync_CanceledMidway_ReportsTheInFlightKeyAsUnknown()
    {
        using var cts = new CancellationTokenSource();
        var handler = new TitleDataMockHandler((_, _, index) =>
        {
            if (index == 1)
            {
                return Ok("""{"code":200,"status":"OK","data":{}}""");
            }

            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });
        using var client = CreateClientWithHandler(handler);
        var stderr = new StringWriter();

        var exit = await TitleDataCommandFactory.RunSetAsync(
            client, false, null, null, """{"a":"1","b":"2","c":"3"}""", OutputOptions.JsonCompact, new StringWriter(), stderr,
            cts.Token);

        Assert.Equal(1, exit);
        Assert.Equal(2, handler.Requests.Count);
        using var summary = JsonDocument.Parse(stderr.ToString());
        Assert.Equal("a", summary.RootElement.GetProperty("applied").EnumerateArray().Single().GetString());
        Assert.False(summary.RootElement.TryGetProperty("failed", out _));
        var unknown = summary.RootElement.GetProperty("unknown");
        Assert.Equal("b", unknown.GetProperty("key").GetString());
        Assert.Contains("canceled", unknown.GetProperty("error").GetString());
        Assert.Contains("the request was canceled", unknown.GetProperty("outcomeUnknown").GetString());
    }

    /// <summary>
    /// A server error on a later key keeps the summary and reports that key as unknown, not
    /// failed: a 5xx such as a gateway timeout can come after PlayFab wrote it.
    /// </summary>
    [Fact]
    public async Task RunSetAsync_ServerErrorOnALaterKey_ReportsItAsUnknown()
    {
        var handler = new TitleDataMockHandler((_, _, index) =>
            index == 1 ? Ok("""{"code":200,"status":"OK","data":{}}""") : Error(HttpStatusCode.GatewayTimeout, "timeout"));
        using var client = CreateClientWithHandler(handler);
        var stderr = new StringWriter();

        var exit = await TitleDataCommandFactory.RunSetAsync(
            client, false, null, null, """{"a":"1","b":"2","c":"3"}""", OutputOptions.JsonCompact, new StringWriter(), stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Equal(2, handler.Requests.Count);
        using var summary = JsonDocument.Parse(stderr.ToString());
        Assert.Equal("a", summary.RootElement.GetProperty("applied").EnumerateArray().Single().GetString());
        Assert.False(summary.RootElement.TryGetProperty("failed", out _));
        var unknown = summary.RootElement.GetProperty("unknown");
        Assert.Equal("b", unknown.GetProperty("key").GetString());
        Assert.Equal("timeout", unknown.GetProperty("error").GetProperty("errorMessage").GetString());
        Assert.StartsWith(
            "/Admin/SetTitleData may have reached PlayFab, but the request failed with HTTP 504",
            unknown.GetProperty("outcomeUnknown").GetString());
    }

    /// <summary>
    /// A later key rejected with a 4xx whose body fails to arrive is still reported as failed,
    /// not unknown: the status alone proves PlayFab rejected it.
    /// </summary>
    [Fact]
    public async Task RunSetAsync_RejectedKeyWhoseBodyFails_ReportsItAsFailed()
    {
        var handler = new TitleDataMockHandler((_, _, index) =>
            index == 1
                ? Ok("""{"data":{}}""")
                : new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StreamContent(new PlayFabApiClientTests.FailingStream(new IOException("response lost"))),
                });
        using var client = CreateClientWithHandler(handler);
        var stderr = new StringWriter();

        var exit = await TitleDataCommandFactory.RunSetAsync(
            client, false, null, null, """{"a":"1","b":"2","c":"3"}""", OutputOptions.JsonCompact, new StringWriter(), stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Equal(2, handler.Requests.Count);
        using var summary = JsonDocument.Parse(stderr.ToString());
        Assert.Equal("a", summary.RootElement.GetProperty("applied").EnumerateArray().Single().GetString());
        Assert.False(summary.RootElement.TryGetProperty("unknown", out _));
        var failed = summary.RootElement.GetProperty("failed");
        Assert.Equal("b", failed.GetProperty("key").GetString());
        Assert.Contains("HTTP 400", failed.GetProperty("error").GetString());
    }

    /// <summary>
    /// A later key's 5xx with a body over the size limit is still reported as unknown, with the
    /// size-limit diagnostic, and the earlier keys stay in the summary.
    /// </summary>
    [Fact]
    public async Task RunSetAsync_OversizedServerErrorOnALaterKey_ReportsItAsUnknown()
    {
        var handler = new TitleDataMockHandler((_, _, index) =>
            index == 1
                ? Ok("""{"data":{}}""")
                : new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent(new string('x', 64)),
                });
        using var client = CreateClientWithHandler(handler, maxResponseBytes: 32);
        var stderr = new StringWriter();

        var exit = await TitleDataCommandFactory.RunSetAsync(
            client, false, null, null, """{"a":"1","b":"2","c":"3"}""", OutputOptions.JsonCompact, new StringWriter(), stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Equal(2, handler.Requests.Count);
        using var summary = JsonDocument.Parse(stderr.ToString());
        Assert.Equal("a", summary.RootElement.GetProperty("applied").EnumerateArray().Single().GetString());
        Assert.False(summary.RootElement.TryGetProperty("failed", out _));
        var unknown = summary.RootElement.GetProperty("unknown");
        Assert.Equal("b", unknown.GetProperty("key").GetString());
        var error = unknown.GetProperty("error").GetString();
        Assert.Contains("HTTP 500", error);
        Assert.Contains("PlayFab:MaxResponseBytes", error);
        Assert.StartsWith(
            "/Admin/SetTitleData may have reached PlayFab, but the request failed with HTTP 500",
            unknown.GetProperty("outcomeUnknown").GetString());
    }

    /// <summary>
    /// A 2xx means the key was written, even when its response is too large to read, so the
    /// key counts as applied and the remaining keys are still written.
    /// </summary>
    [Fact]
    public async Task RunSetAsync_OversizedSuccessOnALaterKey_CountsItAsApplied()
    {
        var handler = new TitleDataMockHandler((_, _, index) =>
            index == 2
                ? Ok("""{"data":{"padding":"PADDING"}}""".Replace("PADDING", new string('x', 64)))
                : Ok("""{"data":{}}"""));
        using var client = CreateClientWithHandler(handler, maxResponseBytes: 32);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await TitleDataCommandFactory.RunSetAsync(
            client, false, null, null, """{"a":"1","b":"2","c":"3"}""", OutputOptions.JsonCompact, stdout, stderr,
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Empty(stderr.ToString());
        using var summary = JsonDocument.Parse(stdout.ToString());
        Assert.Equal(
            ["a", "b", "c"],
            summary.RootElement.GetProperty("applied").EnumerateArray().Select(e => e.GetString()));
    }

    /// <summary>
    /// A sign-in failure on a later key keeps the summary, and that key is reported as not sent.
    /// </summary>
    [Fact]
    public async Task RunSetAsync_SignInFailsOnALaterKey_ReportsItAsNotSent()
    {
        var config = CreateTestConfiguration();
        var mockAuth = new Mock<AuthenticationService>(config) { CallBase = false };
        mockAuth
            .SetupSequence(a => a.AcquireTokenSilentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateMockAuthResult("token"))
            .ThrowsAsync(new InvalidOperationException("sign-in expired"));
        var handler = new TitleDataMockHandler((_, _, _) => Ok("""{"data":{}}"""));
        using var client = CreateClientWithHandler(handler, mockAuth);
        var stderr = new StringWriter();

        var exit = await TitleDataCommandFactory.RunSetAsync(
            client, false, null, null, """{"a":"1","b":"2","c":"3"}""", OutputOptions.JsonCompact, new StringWriter(), stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Single(handler.Requests);
        using var summary = JsonDocument.Parse(stderr.ToString());
        Assert.Equal("a", summary.RootElement.GetProperty("applied").EnumerateArray().Single().GetString());
        Assert.False(summary.RootElement.TryGetProperty("unknown", out _));
        var notSent = summary.RootElement.GetProperty("notSent");
        Assert.Equal("b", notSent.GetProperty("key").GetString());
        Assert.Equal("sign-in expired", notSent.GetProperty("error").GetString());
    }

    /// <summary>
    /// Ctrl-C while signing in for a later key keeps the summary too, and marks that key as not
    /// sent, because its request never left.
    /// </summary>
    [Fact]
    public async Task RunDeleteAsync_CanceledWhileSigningInForALaterKey_ReportsItAsNotSent()
    {
        using var cts = new CancellationTokenSource();
        var mockAuth = new Mock<AuthenticationService>(CreateTestConfiguration()) { CallBase = false };
        mockAuth
            .SetupSequence(a => a.AcquireTokenSilentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateMockAuthResult("token"))
            .Returns(() =>
            {
                cts.Cancel();
                return Task.FromCanceled<Microsoft.Identity.Client.AuthenticationResult?>(cts.Token);
            });
        var handler = new TitleDataMockHandler((_, _, _) => Ok("""{"code":200,"status":"OK","data":{}}"""));
        using var client = CreateClientWithHandler(handler, mockAuth);
        var stderr = new StringWriter();

        var exit = await TitleDataCommandFactory.RunDeleteAsync(
            client, false, "a,b,c", OutputOptions.JsonCompact, new StringWriter(), stderr, cts.Token);

        Assert.Equal(1, exit);
        Assert.Single(handler.Requests);
        using var summary = JsonDocument.Parse(stderr.ToString());
        Assert.Equal("a", summary.RootElement.GetProperty("applied").EnumerateArray().Single().GetString());
        Assert.False(summary.RootElement.TryGetProperty("unknown", out _));
        Assert.Equal("b", summary.RootElement.GetProperty("notSent").GetProperty("key").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RunSetAsync_BlankKey_Rejected(string blankKey)
    {
        var handler = new TitleDataMockHandler((_, _, _) => Ok("""{"code":200,"status":"OK","data":{}}"""));
        using var client = CreateClientWithHandler(handler);
        var stderr = new StringWriter();

        var exit = await TitleDataCommandFactory.RunSetAsync(
            client, false, blankKey, "v", null, OutputOptions.Json, new StringWriter(), stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains("--key must not be blank", stderr.ToString());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RunSetAsync_KeyWithoutValue_Rejected()
    {
        var handler = new TitleDataMockHandler((_, _, _) => Ok("""{"code":200,"status":"OK","data":{}}"""));
        using var client = CreateClientWithHandler(handler);
        var stderr = new StringWriter();

        var exit = await TitleDataCommandFactory.RunSetAsync(
            client, false, "a", null, null, OutputOptions.Json, new StringWriter(), stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains("--key and --value must be used together", stderr.ToString());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RunSetAsync_ValueWithoutKey_Rejected()
    {
        var handler = new TitleDataMockHandler((_, _, _) => Ok("""{"code":200,"status":"OK","data":{}}"""));
        using var client = CreateClientWithHandler(handler);
        var stderr = new StringWriter();

        var exit = await TitleDataCommandFactory.RunSetAsync(
            client, false, null, "v", null, OutputOptions.Json, new StringWriter(), stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains("--key and --value must be used together", stderr.ToString());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void TryParseDataObject_BlankKey_Rejected()
    {
        var ok = TitleDataCommandFactory.TryParseDataObject("""{"":"value"}""", out _, out var error);

        Assert.False(ok);
        Assert.Contains("keys must not be blank", error);
    }

    [Fact]
    public async Task RunDeleteAsync_LoopsSetTitleDataWithNullValues()
    {
        var handler = new TitleDataMockHandler((_, _, _) => Ok("""{"code":200,"status":"OK","data":{}}"""));
        using var client = CreateClientWithHandler(handler);

        var exit = await TitleDataCommandFactory.RunDeleteAsync(
            client, false, "a,b", OutputOptions.Json, new StringWriter(), new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.Contains("/Admin/SetTitleData", r.Uri));
        AssertDeleteBody(handler.Requests[0].Body!, "a");
        AssertDeleteBody(handler.Requests[1].Body!, "b");
    }

    [Fact]
    public async Task RunDeleteAsync_PartialFailure_StopsAndReportsSummary()
    {
        var handler = new TitleDataMockHandler((_, _, index) =>
            index == 1 ? Ok("""{"code":200,"status":"OK","data":{}}""") : Error(HttpStatusCode.BadRequest, "bad b"));
        using var client = CreateClientWithHandler(handler);
        var stderr = new StringWriter();

        var exit = await TitleDataCommandFactory.RunDeleteAsync(
            client, false, "a,b,c", OutputOptions.JsonCompact, new StringWriter(), stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Equal(2, handler.Requests.Count);
        using var summary = JsonDocument.Parse(stderr.ToString());
        Assert.Equal("a", summary.RootElement.GetProperty("applied").EnumerateArray().Single().GetString());
        Assert.Equal("b", summary.RootElement.GetProperty("failed").GetProperty("key").GetString());
        Assert.Contains("bad b", summary.RootElement.GetProperty("failed").GetProperty("error").GetRawText());
    }

    [Fact]
    public async Task RunDeleteAsync_EmptyKeys_ReturnsOne()
    {
        var handler = new TitleDataMockHandler((_, _, _) => Ok("""{"code":200,"status":"OK","data":{}}"""));
        using var client = CreateClientWithHandler(handler);
        var stderr = new StringWriter();

        var exit = await TitleDataCommandFactory.RunDeleteAsync(
            client, false, " , ", OutputOptions.Json, new StringWriter(), stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains("at least one key", stderr.ToString());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RunDeleteAsync_SucceedsWithoutConfirm()
    {
        var handler = new TitleDataMockHandler((_, _, _) => Ok("""{"code":200,"status":"OK","data":{}}"""));
        using var client = CreateClientWithHandler(handler);

        var exit = await TitleDataCommandFactory.RunDeleteAsync(
            client, false, "a", OutputOptions.Json, new StringWriter(), new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Single(handler.Requests);
        Assert.Contains("/Admin/SetTitleData", handler.Requests.Single().Uri);
    }

    [Fact]
    public async Task RunDeleteAsync_ApiError_ReturnsOne()
    {
        var handler = new TitleDataMockHandler((_, _, _) => Error(HttpStatusCode.BadRequest, "delete failed"));
        using var client = CreateClientWithHandler(handler);
        var stderr = new StringWriter();

        var exit = await TitleDataCommandFactory.RunDeleteAsync(
            client, false, "a", OutputOptions.Json, new StringWriter(), stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains("delete failed", stderr.ToString());
    }

    private static void AssertSetBody(string bodyJson, string expectedKey, string expectedValue)
    {
        using var body = JsonDocument.Parse(bodyJson);
        Assert.Equal(expectedKey, body.RootElement.GetProperty("Key").GetString());
        Assert.Equal(expectedValue, body.RootElement.GetProperty("Value").GetString());
    }

    private static void AssertDeleteBody(string bodyJson, string expectedKey)
    {
        using var body = JsonDocument.Parse(bodyJson);
        Assert.Equal(expectedKey, body.RootElement.GetProperty("Key").GetString());
        Assert.True(body.RootElement.TryGetProperty("Value", out var value));
        Assert.Equal(JsonValueKind.Null, value.ValueKind);
    }
}

internal sealed class TitleDataMockHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, string, int, HttpResponseMessage> _responder;

    public List<(string Method, string Uri, string? Body)> Requests { get; } = new();

    public TitleDataMockHandler(Func<HttpRequestMessage, string, int, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content != null
            ? await request.Content.ReadAsStringAsync(cancellationToken)
            : null;
        Requests.Add((request.Method.Method, request.RequestUri?.ToString() ?? string.Empty, body));
        return _responder(request, body ?? string.Empty, Requests.Count);
    }
}
