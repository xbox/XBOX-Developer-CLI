// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.IO.Pipes;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Identity.Client;
using Moq;
using XboxDevCLI.PlayFab.Commands;
using XboxDevCLI.Services;
using XboxDevCLI.Output;
using Xunit;

namespace XboxDevCLI.UnitTest;

public class CloudScriptCommandTests
{
    [Fact]
    public void CloudScriptCommand_IsNamedCloudscript()
    {
        var command = CloudScriptCommandFactory.Create();
        Assert.Equal("cloudscript", command.Name);
    }

    [Fact]
    public void CloudScriptCommand_HasClassicAndFunctionGroups()
    {
        var command = CloudScriptCommandFactory.Create();

        Assert.Equal(2, command.Subcommands.Count);
        Assert.Contains(command.Subcommands, c => c.Name == "classic");
        Assert.Contains(command.Subcommands, c => c.Name == "function");
    }

    [Fact]
    public void ClassicGroup_HasExpectedSubcommands()
    {
        var command = CloudScriptClassicCommandFactory.Create();

        Assert.Equal("classic", command.Name);
        Assert.Equal(
            ["revision", "version"],
            command.Subcommands.Select(c => c.Name).Order(StringComparer.Ordinal));

        var revision = command.Subcommands.First(c => c.Name == "revision");
        Assert.Equal(
            ["create", "get", "publish"],
            revision.Subcommands.Select(c => c.Name).Order(StringComparer.Ordinal));

        var version = command.Subcommands.First(c => c.Name == "version");
        Assert.Equal(["list"], version.Subcommands.Select(c => c.Name));
    }

    [Fact]
    public void FunctionGroup_HasExpectedSubcommands()
    {
        var command = CloudScriptFunctionCommandFactory.Create();

        var expectedNames = new[] { "list", "register", "unregister" };

        Assert.Equal("function", command.Name);
        Assert.Equal(expectedNames.Length, command.Subcommands.Count);
        foreach (var name in expectedNames)
        {
            Assert.Contains(command.Subcommands, c => c.Name == name);
        }
    }

    [Fact]
    public void FunctionRegister_RequiresAnExplicitTriggerType()
    {
        var register = CloudScriptFunctionCommandFactory.Create()
            .Subcommands.First(c => c.Name == "register");

        var typeOption = Assert.Single(register.Options, o => o.Name == "--type");
        Assert.True(typeOption.Required);
    }

    [Fact]
    public void FunctionRegisterAndUnregister_SayTheyAffectThePlayFabRegistration()
    {
        var function = CloudScriptFunctionCommandFactory.Create();

        foreach (var name in new[] { "register", "unregister" })
        {
            var description = function.Subcommands.First(c => c.Name == name).Description;
            Assert.NotNull(description);
            Assert.Contains("does not", description, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Azure Function itself", description, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void BuildGetRevisionCall_WithValues_MapsVersionAndRevision()
    {
        var (apiPath, request) = CloudScriptClassicCommandFactory.BuildGetRevisionCall(2, 3);

        Assert.Equal("/Admin/GetCloudScriptRevision", apiPath);
        Assert.Equal(2d, request.Version);
        Assert.Equal(3d, request.Revision);
    }

    [Fact]
    public void BuildGetRevisionCall_WithNulls_LeavesVersionAndRevisionNull()
    {
        var (apiPath, request) = CloudScriptClassicCommandFactory.BuildGetRevisionCall(null, null);

        Assert.Equal("/Admin/GetCloudScriptRevision", apiPath);
        Assert.Null(request.Version);
        Assert.Null(request.Revision);
    }

    [Fact]
    public void BuildPublishCall_MapsVersionAndRevision()
    {
        var (apiPath, request) = CloudScriptClassicCommandFactory.BuildPublishCall(1, 5);

        Assert.Equal("/Admin/SetPublishedRevision", apiPath);
        Assert.Equal(1d, request.Version);
        Assert.Equal(5d, request.Revision);
    }

    [Theory]
    [InlineData(null, "/CloudScript/ListFunctions")]
    [InlineData("http", "/CloudScript/ListHttpFunctions")]
    [InlineData("queued", "/CloudScript/ListQueuedFunctions")]
    [InlineData("eventhub", "/CloudScript/ListEventHubFunctions")]
    public void BuildListFunctionsApiPath_SelectsCorrectPath(string? type, string expected)
    {
        Assert.Equal(expected, CloudScriptFunctionCommandFactory.BuildListFunctionsApiPath(type));
    }

    [Fact]
    public void BuildRegisterHttpFunctionCall_MapsFields()
    {
        var (apiPath, request) = CloudScriptFunctionCommandFactory.BuildRegisterHttpFunctionCall("Fn1", "https://fn.com/api");

        Assert.Equal("/CloudScript/RegisterHttpFunction", apiPath);
        Assert.Equal("Fn1", request.FunctionName);
        Assert.Equal("https://fn.com/api", request.FunctionUrl);
    }

    [Fact]
    public void BuildUnregisterFunctionCall_MapsName()
    {
        var (apiPath, request) = CloudScriptFunctionCommandFactory.BuildUnregisterFunctionCall("OldFunc");

        Assert.Equal("/CloudScript/UnregisterFunction", apiPath);
        Assert.Equal("OldFunc", request.FunctionName);
    }

    [Fact]
    public void RegisterHttpFunctionRequest_SerializesWireFieldNames()
    {
        var (_, request) = CloudScriptFunctionCommandFactory.BuildRegisterHttpFunctionCall("MyFunc", "https://myfunc/api/run");

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(request));

        Assert.Equal("MyFunc", doc.RootElement.GetProperty("FunctionName").GetString());
        Assert.Equal("https://myfunc/api/run", doc.RootElement.GetProperty("FunctionUrl").GetString());
    }

    [Theory]
    [InlineData("https://nodpublishers.azurewebsites.net/api/MatchWebhook?code=SECRETKEY0==")]
    [InlineData("https://nodpublishers.azurewebsites.net/api/MatchWebhook?clientId=default&CODE=SECRETKEY0==")]
    [InlineData("https://nodpublishers.azurewebsites.net/api/MatchWebhook?%63ode=SECRETKEY0==")]
    public void FunctionRegister_RefusesAKeyInTheUrlWithoutEchoingIt(string url)
    {
        var parsed = Program.BuildRootCommand().Parse(
            ["playfab", "cloudscript", "function", "register", "--type", "http", "--name", "MatchWebhook", "--url", url],
            Program.NoResponseFileConfig);

        var error = Assert.Single(parsed.Errors);
        Assert.Equal(CloudScriptFunctionCommandFactory.FunctionKeyInUrlMessage, error.Message);
        Assert.Contains("--key-file", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRETKEY0", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://nodpublishers.azurewebsites.net/api/PublicStatus")]
    [InlineData("https://nodpublishers.azurewebsites.net/api/PublicStatus?codec=h264")]
    [InlineData("https://nodpublishers.azurewebsites.net/api/PublicStatus#code=fragment")]
    [InlineData("https://nodpublishers.azurewebsites.net/api/PublicStatus#help?code=example")]
    public void FunctionRegister_AcceptsAUrlWithoutAKey(string url)
    {
        var parsed = Program.BuildRootCommand().Parse(
            ["playfab", "cloudscript", "function", "register", "--type", "http", "--name", "PublicStatus", "--url", url],
            Program.NoResponseFileConfig);

        Assert.Empty(parsed.Errors);
    }

    [Theory]
    [InlineData("https://nodpublishers.azurewebsites.net/api/Fn", "abc/DEF==",
        "https://nodpublishers.azurewebsites.net/api/Fn?code=abc/DEF==")]
    [InlineData("https://nodpublishers.azurewebsites.net/api/Fn?clientId=default", "abc==",
        "https://nodpublishers.azurewebsites.net/api/Fn?clientId=default&code=abc==")]
    [InlineData("https://nodpublishers.azurewebsites.net/api/Fn?", "abc",
        "https://nodpublishers.azurewebsites.net/api/Fn?code=abc")]
    [InlineData("https://nodpublishers.azurewebsites.net/api/Fn#top", "abc",
        "https://nodpublishers.azurewebsites.net/api/Fn?code=abc#top")]
    [InlineData("https://nodpublishers.azurewebsites.net/api/Fn", "a+b&c",
        "https://nodpublishers.azurewebsites.net/api/Fn?code=a%2Bb%26c")]
    public void AppendFunctionKey_AddsTheKeyAsTheCodeParameter(string url, string key, string expected)
    {
        Assert.Equal(expected, CloudScriptFunctionCommandFactory.AppendFunctionKey(url, key));
    }

    [Fact]
    public async Task ReadFunctionKeyAsync_ReadsAFileAndTrimsItsNewline()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "abc/DEF==\r\n");

            var key = await CloudScriptFunctionCommandFactory.ReadFunctionKeyAsync(
                path,
                () => throw new InvalidOperationException("stdin must not be checked"),
                () => throw new InvalidOperationException("stdin must not be read"),
                CancellationToken.None);

            Assert.Equal("abc/DEF==", key);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadFunctionKeyAsync_ReadsPipedStdinForDash()
    {
        var key = await CloudScriptFunctionCommandFactory.ReadFunctionKeyAsync(
            "-", () => true, () => Utf8Stream("abc/DEF==\n"), CancellationToken.None);

        Assert.Equal("abc/DEF==", key);
    }

    [Fact]
    public async Task ReadFunctionKeyAsync_RefusesStdinThatIsATerminal()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CloudScriptFunctionCommandFactory.ReadFunctionKeyAsync(
                "-",
                () => false,
                () => throw new InvalidOperationException("a terminal must not be read"),
                CancellationToken.None));

        Assert.Equal(CloudScriptFunctionCommandFactory.FunctionKeyFromTerminalMessage, exception.Message);
    }

    [Fact]
    public async Task ReadFunctionKeyAsync_StopsWhenCanceledWhileThePipeStaysOpen()
    {
        var writer = new AnonymousPipeServerStream(PipeDirection.Out);
        var reader = new AnonymousPipeClientStream(PipeDirection.In, writer.ClientSafePipeHandle);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        try
        {
            // Task.Run and the bound make a read that ignores cancellation fail with a
            // TimeoutException instead of hanging the test, even if it blocks before returning a task.
            var read = Task.Run(() => CloudScriptFunctionCommandFactory.ReadFunctionKeyAsync(
                "-", () => true, () => reader, cancellation.Token));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            // Closing the writer first ends any read still waiting, so disposing the reader can't block.
            writer.Dispose();
            reader.Dispose();
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \n")]
    public async Task ReadFunctionKeyAsync_RefusesAnEmptyKey(string content)
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CloudScriptFunctionCommandFactory.ReadFunctionKeyAsync(
                "-", () => true, () => Utf8Stream(content), CancellationToken.None));

        Assert.Contains("empty", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadFunctionKeyAsync_NamesTheOptionWhenTheFileIsMissing()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "function-key.txt");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CloudScriptFunctionCommandFactory.ReadFunctionKeyAsync(
                path, () => true, () => Stream.Null, CancellationToken.None));

        Assert.Contains("--key-file", exception.Message, StringComparison.Ordinal);
    }

    private static MemoryStream Utf8Stream(string content) => new(Encoding.UTF8.GetBytes(content));

    [Fact]
    public void GetRevisionRequest_WithNulls_OmitsVersionAndRevisionWhenIgnoringNulls()
    {
        var (_, request) = CloudScriptClassicCommandFactory.BuildGetRevisionCall(null, null);
        var options = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(request, options));

        Assert.False(doc.RootElement.TryGetProperty("Version", out _));
        Assert.False(doc.RootElement.TryGetProperty("Revision", out _));
    }

    #region Run Method Tests (SetAction lambda logic)

    private static IConfiguration CreateTestConfiguration()
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PlayFab:TitleId"] = "TEST123",
                ["PlayFab:BaseUrlTemplate"] = "https://{0}.playfabapi.com",
                ["AzureAd:ClientId"] = "test-client-id",
                ["AzureAd:TenantId"] = "test-tenant-id",
                ["AzureAd:Scopes"] = "api://test/.default",
            })
            .Build();
    }

    private static AuthenticationResult CreateMockAuthResult(string accessToken)
    {
        return new AuthenticationResult(
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
    }

    private static PlayFabApiClient CreateClientWithHandler(HttpMessageHandler handler)
    {
        var config = CreateTestConfiguration();
        var mockAuth = new Mock<AuthenticationService>(config) { CallBase = false };
        mockAuth.Setup(a => a.AcquireTokenSilentAsync(It.IsAny<CancellationToken>())).ReturnsAsync(CreateMockAuthResult("token"));
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://TEST123.playfabapi.com/") };
        return new PlayFabApiClient(config, mockAuth.Object, httpClient);
    }

    private static HttpResponseMessage Ok(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json) };

    // ----- classic revision get -----

    [Fact]
    public async Task RunGetRevisionAsync_Success_SendsVersionAndRevisionAndPrintsData()
    {
        var responseJson = """{"code":200,"status":"OK","data":{"Version":2,"Revision":3,"IsPublished":true}}""";
        var handler = new FileCommandMockHandler(Ok(responseJson));
        using var client = CreateClientWithHandler(handler);
        var stdout = new StringWriter();

        var exit = await CloudScriptClassicCommandFactory.RunGetRevisionAsync(
            client, 2, 3, OutputOptions.Json, stdout, new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("/Admin/GetCloudScriptRevision", handler.LastRequestUri);
        Assert.NotNull(handler.CapturedBody);
        using var body = JsonDocument.Parse(handler.CapturedBody!);
        Assert.Equal(2, body.RootElement.GetProperty("Version").GetInt32());
        Assert.Equal(3, body.RootElement.GetProperty("Revision").GetInt32());
        Assert.Contains("IsPublished", stdout.ToString());
    }

    [Fact]
    public async Task RunGetRevisionAsync_DataOutput_StripsEnvelope()
    {
        var responseJson = """{"code":200,"status":"OK","data":{"Version":5}}""";
        using var client = CreateClientWithHandler(new FileCommandMockHandler(Ok(responseJson)));
        var stdout = new StringWriter();

        var exit = await CloudScriptClassicCommandFactory.RunGetRevisionAsync(
            client, null, null, new OutputOptions("json-compact", true), stdout, new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exit);
        var output = stdout.ToString().Trim();
        Assert.Equal("""{"Version":5}""", output);
    }

    [Fact]
    public async Task RunGetRevisionAsync_ApiError_ReturnsOneAndWritesStderr()
    {
        var errorJson = """{"code":400,"status":"BadRequest","error":"InvalidParams","errorCode":1000,"errorMessage":"bad"}""";
        var handler = new FileCommandMockHandler(
            new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(errorJson) });
        using var client = CreateClientWithHandler(handler);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await CloudScriptClassicCommandFactory.RunGetRevisionAsync(
            client, 1, 1, OutputOptions.Json, stdout, stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.NotEqual(string.Empty, stderr.ToString());
    }

    // ----- classic version list -----

    [Fact]
    public async Task RunListVersionsAsync_Success_CallsVersionsEndpoint()
    {
        var responseJson = """{"code":200,"status":"OK","data":{"Versions":[{"Version":1}]}}""";
        var handler = new FileCommandMockHandler(Ok(responseJson));
        using var client = CreateClientWithHandler(handler);
        var stdout = new StringWriter();

        var exit = await CloudScriptClassicCommandFactory.RunListVersionsAsync(
            client, OutputOptions.Json, stdout, new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("/Admin/GetCloudScriptVersions", handler.LastRequestUri);
        Assert.Contains("Versions", stdout.ToString());
    }

    // ----- classic revision create -----

    [Fact]
    public async Task RunUpdateAsync_LocalFileMissing_ReturnsOneWithoutCallingApi()
    {
        var handler = new FileCommandMockHandler(Ok("""{"code":200,"status":"OK","data":{}}"""));
        using var client = CreateClientWithHandler(handler);
        var stderr = new StringWriter();
        var missing = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.js");

        var exit = await CloudScriptClassicCommandFactory.RunUpdateAsync(
            client, missing, publish: false, OutputOptions.Json, new StringWriter(), stderr, default);

        Assert.Equal(1, exit);
        Assert.Contains("File not found", stderr.ToString());
        Assert.Null(handler.LastRequestUri);
    }

    [Fact]
    public async Task RunUpdateAsync_Success_UploadsFileContentsAndPublishFlag()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var filePath = Path.Combine(tempDir, "handlers.js");
            await File.WriteAllTextAsync(filePath, "handlers.foo = function() {};");
            var responseJson = """{"code":200,"status":"OK","data":{"Version":9,"Revision":1}}""";
            var handler = new FileCommandMockHandler(Ok(responseJson));
            using var client = CreateClientWithHandler(handler);
            var stdout = new StringWriter();

            var exit = await CloudScriptClassicCommandFactory.RunUpdateAsync(
                client, filePath, publish: true, OutputOptions.Json, stdout, new StringWriter(), default);

            Assert.Equal(0, exit);
            Assert.Contains("/Admin/UpdateCloudScript", handler.LastRequestUri);
            Assert.NotNull(handler.CapturedBody);
            using var body = JsonDocument.Parse(handler.CapturedBody!);
            Assert.True(body.RootElement.GetProperty("Publish").GetBoolean());
            var file = body.RootElement.GetProperty("Files").EnumerateArray().Single();
            Assert.Equal("handlers.js", file.GetProperty("Filename").GetString());
            Assert.Equal("handlers.foo = function() {};", file.GetProperty("FileContents").GetString());
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // ----- classic revision publish -----

    [Fact]
    public async Task RunPublishAsync_Success_SendsVersionAndRevision()
    {
        var responseJson = """{"code":200,"status":"OK","data":{"Version":4}}""";
        var handler = new FileCommandMockHandler(Ok(responseJson));
        using var client = CreateClientWithHandler(handler);
        var stdout = new StringWriter();

        var exit = await CloudScriptClassicCommandFactory.RunPublishAsync(
            client, 4, 7, OutputOptions.Json, stdout, new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("/Admin/SetPublishedRevision", handler.LastRequestUri);
        Assert.NotNull(handler.CapturedBody);
        using var body = JsonDocument.Parse(handler.CapturedBody!);
        Assert.Equal(4, body.RootElement.GetProperty("Version").GetInt32());
        Assert.Equal(7, body.RootElement.GetProperty("Revision").GetInt32());
    }

    // ----- functions list -----

    [Theory]
    [InlineData(null, "/CloudScript/ListFunctions")]
    [InlineData("http", "/CloudScript/ListHttpFunctions")]
    [InlineData("eventhub", "/CloudScript/ListEventHubFunctions")]
    public async Task RunListAsync_SelectsEndpointByType(string? type, string expectedPath)
    {
        var responseJson = """{"code":200,"status":"OK","data":{"Functions":[]}}""";
        var handler = new FileCommandMockHandler(Ok(responseJson));
        using var client = CreateClientWithHandler(handler);

        var exit = await CloudScriptFunctionCommandFactory.RunListAsync(
            client, type, OutputOptions.Json, new StringWriter(), new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains(expectedPath, handler.LastRequestUri);
    }

    // ----- function register --type http -----

    [Theory]
    [InlineData(null, "https://nodpublishers.azurewebsites.net/api/MatchWebhook")]
    [InlineData("abc/DEF==", "https://nodpublishers.azurewebsites.net/api/MatchWebhook?code=abc/DEF==")]
    public async Task RunRegisterHttpAsync_Success_SendsNameAndCompleteUrl(string? functionKey, string expectedUrl)
    {
        var handler = new FileCommandMockHandler(Ok("""{"code":200,"status":"OK","data":{}}"""));
        using var client = CreateClientWithHandler(handler);

        var exit = await CloudScriptFunctionCommandFactory.RunRegisterHttpAsync(
            client, "MatchWebhook", "https://nodpublishers.azurewebsites.net/api/MatchWebhook", functionKey,
            OutputOptions.Json, new StringWriter(), new StringWriter(), CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("/CloudScript/RegisterHttpFunction", handler.LastRequestUri);
        Assert.NotNull(handler.CapturedBody);
        using var body = JsonDocument.Parse(handler.CapturedBody!);
        Assert.Equal("MatchWebhook", body.RootElement.GetProperty("FunctionName").GetString());
        Assert.Equal(expectedUrl, body.RootElement.GetProperty("FunctionUrl").GetString());
    }

    // ----- functions unregister -----

    [Fact]
    public async Task RunUnregisterAsync_Success_SendsName()
    {
        var handler = new FileCommandMockHandler(Ok("""{"code":200,"status":"OK","data":{}}"""));
        using var client = CreateClientWithHandler(handler);

        var exit = await CloudScriptFunctionCommandFactory.RunUnregisterAsync(
            client, "OldFunc", OutputOptions.Json, new StringWriter(), new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("/CloudScript/UnregisterFunction", handler.LastRequestUri);
        Assert.NotNull(handler.CapturedBody);
        using var body = JsonDocument.Parse(handler.CapturedBody!);
        Assert.Equal("OldFunc", body.RootElement.GetProperty("FunctionName").GetString());
    }

    [Fact]
    public async Task RunUnregisterAsync_ApiError_ReturnsOneAndWritesStderr()
    {
        var errorJson = """{"code":404,"status":"NotFound","error":"NotFound","errorCode":1,"errorMessage":"missing"}""";
        var handler = new FileCommandMockHandler(
            new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent(errorJson) });
        using var client = CreateClientWithHandler(handler);
        var stderr = new StringWriter();

        var exit = await CloudScriptFunctionCommandFactory.RunUnregisterAsync(
            client, "Ghost", OutputOptions.Json, new StringWriter(), stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.NotEqual(string.Empty, stderr.ToString());
    }

    #endregion
}
