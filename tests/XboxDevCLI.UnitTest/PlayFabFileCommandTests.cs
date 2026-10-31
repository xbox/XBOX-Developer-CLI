// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Identity.Client;
using Moq;
using XboxDevCLI.CommandLine;
using XboxDevCLI.PlayFab.Commands;
using DataModels = XboxDevCLI.PlayFab.Generated.Data;
using XboxDevCLI.Safety;
using XboxDevCLI.Services;
using XboxDevCLI.Output;
using Xunit;

namespace XboxDevCLI.UnitTest;

public class PlayFabFileCommandTests
{
    private const string ListCommand =
        "xbox --playfab-title-id ABC123 playfab file list --entity-id E1 --entity-type title";

    #region Command Structure Tests

    [Fact]
    public void FilesCommand_HasExpectedSubcommands()
    {
        var command = FileCommandFactory.Create();

        Assert.Equal(4, command.Subcommands.Count);
        Assert.Contains(command.Subcommands, c => c.Name == "list");
        Assert.Contains(command.Subcommands, c => c.Name == "download");
        Assert.Contains(command.Subcommands, c => c.Name == "upload");
        Assert.Contains(command.Subcommands, c => c.Name == "delete");
    }

    #endregion

    #region Files Build Method Tests

    [Fact]
    public void FilesBuildListCall_MapsEntityCorrectly()
    {
        var (apiPath, request) = FileCommandFactory.BuildListCall("E1", "title_player_account");

        Assert.Equal("/File/GetFiles", apiPath);
        Assert.Equal("E1", request.Entity!.Id);
        Assert.Equal("title_player_account", request.Entity!.Type);
    }

    [Fact]
    public void FilesBuildDeleteCall_MapsEntityAndFileNames()
    {
        var (apiPath, request) = FileCommandFactory.BuildDeleteCall("E2", "title", new[] { "file1.json", "file2.txt" });

        Assert.Equal("/File/DeleteFiles", apiPath);
        Assert.Equal("E2", request.Entity!.Id);
        Assert.Equal("title", request.Entity!.Type);
        Assert.Equal(new List<string> { "file1.json", "file2.txt" }, request.FileNames);
    }

    #endregion

    #region Integration Tests with PlayFabApiClient

    private static IConfiguration CreateTestConfiguration(
        string titleId = "TEST123",
        string baseUrlTemplate = "https://{0}.playfabapi.com")
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PlayFab:TitleId"] = titleId,
                ["PlayFab:BaseUrlTemplate"] = baseUrlTemplate,
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

    [Fact]
    public async Task GetFiles_SendsCorrectEntityInRequest()
    {
        var config = CreateTestConfiguration();
        var mockAuth = new Mock<AuthenticationService>(config) { CallBase = false };
        mockAuth.Setup(a => a.AcquireTokenSilentAsync(It.IsAny<CancellationToken>())).ReturnsAsync(CreateMockAuthResult("token"));

        var responseJson = """{"code":200,"status":"OK","data":{"Entity":{"Id":"E1","Type":"title"},"Metadata":null,"ProfileVersion":1}}""";
        var handler = new FileCommandMockHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseJson) });

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://TEST123.playfabapi.com/") };
        using var client = new PlayFabApiClient(config, mockAuth.Object, httpClient);

        var (apiPath, request) = FileCommandFactory.BuildListCall("E1", "title_player_account");
        await client.CallApiAsync<DataModels.GetFilesRequest, DataModels.GetFilesResponse>(apiPath, request, CancellationToken.None);

        Assert.NotNull(handler.CapturedBody);
        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        var entity = doc.RootElement.GetProperty("Entity");
        Assert.Equal("E1", entity.GetProperty("Id").GetString());
        Assert.Equal("title_player_account", entity.GetProperty("Type").GetString());
        Assert.Contains("/File/GetFiles", handler.LastRequestUri);
    }

    [Fact]
    public async Task DeleteFiles_WithoutConfirmation_IsBlockedByDestructiveGuard()
    {
        var config = CreateTestConfiguration();
        var mockAuth = new Mock<AuthenticationService>(config) { CallBase = false };
        mockAuth.Setup(a => a.AcquireTokenSilentAsync(It.IsAny<CancellationToken>())).ReturnsAsync(CreateMockAuthResult("token"));

        var handler = new FileCommandMockHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"code":200,"status":"OK","data":{}}""") });

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://TEST123.playfabapi.com/") };
        using var client = new PlayFabApiClient(config, mockAuth.Object, httpClient);

        var (apiPath, request) = FileCommandFactory.BuildDeleteCall("E1", "title_player_account", new[] { "file1.json" });

        // /File/DeleteFiles is destructive; without confirmedDestructive the client must refuse
        // and never issue the HTTP request.
        await Assert.ThrowsAsync<DestructiveOperationNotConfirmedException>(
            () => client.CallApiAsync(apiPath, JsonSerializer.Serialize(request), CancellationToken.None));
        Assert.Null(handler.LastRequestUri);
    }

    [Fact]
    public async Task DeleteFiles_WithConfirmation_SendsCorrectRequest()
    {
        var config = CreateTestConfiguration();
        var mockAuth = new Mock<AuthenticationService>(config) { CallBase = false };
        mockAuth.Setup(a => a.AcquireTokenSilentAsync(It.IsAny<CancellationToken>())).ReturnsAsync(CreateMockAuthResult("token"));

        var handler = new FileCommandMockHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"code":200,"status":"OK","data":{}}""") });

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://TEST123.playfabapi.com/") };
        using var client = new PlayFabApiClient(config, mockAuth.Object, httpClient);

        var (apiPath, request) = FileCommandFactory.BuildDeleteCall("E1", "title_player_account", new[] { "file1.json", "file2.txt" });
        await client.CallApiAsync(apiPath, JsonSerializer.Serialize(request), CancellationToken.None, confirmedDestructive: true);

        Assert.NotNull(handler.CapturedBody);
        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        var fileNames = doc.RootElement.GetProperty("FileNames").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(new List<string?> { "file1.json", "file2.txt" }, fileNames);
        Assert.Contains("/File/DeleteFiles", handler.LastRequestUri);
    }

    #endregion

    #region Run Method Tests (SetAction lambda logic)

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

    // ----- list -----

    // Table output for this command is covered in TableOutputWiringTests, which drives every
    // command that declares a view through the same assertions. Keeping that coverage together
    // is what makes a command missing from it obvious; the list tests below are JSON-only by
    // design, not by omission.

    [Fact]
    public async Task RunListAsync_Success_PrettyPrintsMetadata()
    {
        var responseJson = """{"code":200,"status":"OK","data":{"Metadata":{"save.json":{"FileName":"save.json","Size":42}},"ProfileVersion":3}}""";
        using var client = CreateClientWithHandler(new FileCommandMockHandler(Ok(responseJson)));
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await FileCommandFactory.RunListAsync(client, "E1", "title_player_account", OutputOptions.Json, stdout, stderr, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("save.json", stdout.ToString());
        // Pretty-printed output is multi-line/indented.
        Assert.Contains("\n", stdout.ToString().TrimEnd());
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public async Task RunListAsync_JsonCompact_EmitsSingleLine()
    {
        var responseJson = """{"code":200,"status":"OK","data":{"Metadata":{"save.json":{"FileName":"save.json"}},"ProfileVersion":3}}""";
        using var client = CreateClientWithHandler(new FileCommandMockHandler(Ok(responseJson)));
        var stdout = new StringWriter();

        var exit = await FileCommandFactory.RunListAsync(client, "E1", "title", OutputOptions.JsonCompact, stdout, new StringWriter(), CancellationToken.None);

        Assert.Equal(0, exit);
        var output = stdout.ToString().TrimEnd();
        Assert.DoesNotContain("\n", output);
        Assert.Contains("save.json", output);
    }

    [Fact]
    public async Task RunListAsync_NoMetadata_PrintsEmptyObject()
    {
        var responseJson = """{"code":200,"status":"OK","data":{"ProfileVersion":1}}""";
        using var client = CreateClientWithHandler(new FileCommandMockHandler(Ok(responseJson)));
        var stdout = new StringWriter();

        var exit = await FileCommandFactory.RunListAsync(client, "E1", "title", OutputOptions.Json, stdout, new StringWriter(), CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal("{}", stdout.ToString().Trim());
    }

    [Fact]
    public async Task RunListAsync_ApiError_ReturnsOneAndWritesError()
    {
        var errorJson = """{"code":400,"status":"BadRequest","error":"InvalidParams","errorCode":1000,"errorMessage":"bad entity"}""";
        var handler = new FileCommandMockHandler(
            new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(errorJson) });
        using var client = CreateClientWithHandler(handler);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await FileCommandFactory.RunListAsync(client, "E1", "title", OutputOptions.Json, stdout, stderr, CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.NotEqual(string.Empty, stderr.ToString());
    }

    // ----- download -----

    [Fact]
    public async Task RunDownloadAsync_Success_WritesFileAndReportsBytes()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var outputPath = Path.Combine(tempDir, "nested", "out.bin");
            var content = "hello-world-bytes";
            var blobUrl = "https://nodpublishers.blob.core.windows.net/download/save";
            var responseJson = """{"code":200,"status":"OK","data":{"Metadata":{"save.bin":{"FileName":"save.bin","DownloadUrl":"__URL__"}}}}""".Replace("__URL__", blobUrl);

            using var client = CreateClientWithHandler(new FileCommandMockHandler(Ok(responseJson)));
            var blobHandler = new RoutingMockHandler((_, _) => Ok(content));
            using var blobClient = new HttpClient(blobHandler);
            var stderr = new StringWriter();

            var exit = await FileCommandFactory.RunDownloadAsync(
                client, blobClient, "E1", "title", "save.bin", outputPath, ListCommand, "json", stderr, default);

            Assert.Equal(0, exit);
            Assert.True(File.Exists(outputPath));
            Assert.Equal(content, await File.ReadAllTextAsync(outputPath));
            Assert.Contains($"({content.Length} bytes)", stderr.ToString());
            Assert.Equal(blobUrl, blobHandler.Requests.Single().Uri);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task RunDownloadAsync_DefaultOutputPath_UsesFileName()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;
        var previousCwd = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(tempDir);
            var responseJson = """{"code":200,"status":"OK","data":{"Metadata":{"data.txt":{"FileName":"data.txt","DownloadUrl":"https://nodpublishers.blob.core.windows.net/d"}}}}""";
            using var client = CreateClientWithHandler(new FileCommandMockHandler(Ok(responseJson)));
            using var blobClient = new HttpClient(new RoutingMockHandler((_, _) => Ok("payload")));
            var stderr = new StringWriter();

            var exit = await FileCommandFactory.RunDownloadAsync(
                client, blobClient, "E1", "title", "data.txt", null, ListCommand, "json", stderr, default);

            Assert.Equal(0, exit);
            Assert.True(File.Exists(Path.Combine(tempDir, "data.txt")));
            // The working directory, not tempDir: macOS reports /var/... as /private/var/...
            Assert.Contains($"to '{Path.Combine(Directory.GetCurrentDirectory(), "data.txt")}'", stderr.ToString());
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task RunDownloadAsync_DefaultOutputPath_RejectsInvalidLocalFileName()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;
        var previousCwd = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(tempDir);
            var responseJson = """{"code":200,"status":"OK","data":{"Metadata":{"../nested/data.txt":{"FileName":"../nested/data.txt","DownloadUrl":"https://nodpublishers.blob.core.windows.net/d"}}}}""";
            using var client = CreateClientWithHandler(new FileCommandMockHandler(Ok(responseJson)));
            using var blobClient = new HttpClient(new RoutingMockHandler((_, _) => Ok("payload")));
            var stderr = new StringWriter();

            var exit = await FileCommandFactory.RunDownloadAsync(
                client, blobClient, "E1", "title", "../nested/data.txt", null, ListCommand, "json", stderr, default);

            Assert.Equal(1, exit);
            Assert.Contains("is not a valid local file name", stderr.ToString());
            Assert.Contains("--dest", stderr.ToString());
            Assert.Empty(Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task RunDownloadAsync_InvalidDefaultFileName_ExplicitDestinationWritesFile()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var responseJson = """{"code":200,"status":"OK","data":{"Metadata":{"../nested/data.txt":{"FileName":"../nested/data.txt","DownloadUrl":"https://nodpublishers.blob.core.windows.net/d"}}}}""";
            using var client = CreateClientWithHandler(new FileCommandMockHandler(Ok(responseJson)));
            using var blobClient = new HttpClient(new RoutingMockHandler((_, _) => Ok("payload")));
            var outputPath = Path.Combine(tempDir, "data.txt");

            var exit = await FileCommandFactory.RunDownloadAsync(
                client, blobClient, "E1", "title", "../nested/data.txt", outputPath, ListCommand, "json", new StringWriter(), default);

            Assert.Equal(0, exit);
            Assert.True(File.Exists(outputPath));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Theory]
    [InlineData(".")]
    [InlineData("nested/..")]
    [InlineData("nested/")]
    public void ResolveDownloadOutputPath_DestinationWithoutFileName_Throws(string outputPath)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => FileCommandFactory.ResolveDownloadOutputPath(outputPath, "data.txt"));

        Assert.Contains("must include a file name", exception.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void ResolveDownloadOutputPath_EmptyDestination_Throws(string outputPath)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => FileCommandFactory.ResolveDownloadOutputPath(outputPath, "data.txt"));

        Assert.Contains("resolves to an empty path", exception.Message);
    }

    [Fact]
    public async Task RunDownloadAsync_StdoutDestination_FailsBeforeCallingPlayFab()
    {
        var handler = new FileCommandMockHandler(Ok("""{"code":200,"status":"OK","data":{}}"""));
        using var client = CreateClientWithHandler(handler);
        using var blobClient = new HttpClient(new RoutingMockHandler((_, _) => Ok("x")));
        var stderr = new StringWriter();

        var exit = await FileCommandFactory.RunDownloadAsync(
            client, blobClient, "E1", "title", "save.bin", "-", ListCommand, "json", stderr, default);

        Assert.Equal(1, exit);
        Assert.Equal(
            "Download failed: Downloading to stdout with '--dest -' isn't implemented yet. Pass a file path to --dest."
            + Environment.NewLine,
            stderr.ToString());
        Assert.Null(handler.LastRequestUri);
    }

    [Fact]
    public void ResolveDownloadOutputPath_ExistingDirectoryDestination_Throws()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(
                () => FileCommandFactory.ResolveDownloadOutputPath(tempDir, "data.txt"));

            Assert.Contains("must include a file name", exception.Message);
        }
        finally
        {
            Directory.Delete(tempDir);
        }
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("COM1.log")]
    [InlineData("lpt9")]
    [InlineData("name.")]
    [InlineData("name ")]
    public void HasInvalidWindowsFileNameSyntax_InvalidName_ReturnsTrue(string fileName)
    {
        Assert.True(FileCommandFactory.HasInvalidWindowsFileNameSyntax(fileName));
    }

    [Theory]
    [InlineData("data.txt")]
    [InlineData("COM0")]
    [InlineData("LPT10")]
    [InlineData(".gitignore")]
    public void HasInvalidWindowsFileNameSyntax_ValidName_ReturnsFalse(string fileName)
    {
        Assert.False(FileCommandFactory.HasInvalidWindowsFileNameSyntax(fileName));
    }

    [Theory]
    [InlineData("nodpublishers.blob.core.windows.net")]
    [InlineData("nodpublishers.privatelink.blob.core.windows.net")]
    [InlineData("nodpublishers.blob.core.usgovcloudapi.net")]
    [InlineData("nodpublishers.blob.core.chinacloudapi.cn")]
    [InlineData("nodpublishers.blob.core.cloudapi.de")]
    public void IsAllowedAzureBlobHost_AllowedHost_ReturnsTrue(string host)
    {
        Assert.True(FileCommandFactory.IsAllowedAzureBlobHost(host));
    }

    [Theory]
    [InlineData("example.com")]
    [InlineData("nodpublishers.blob.core.windows.net.evil.com")]
    [InlineData("blob.example.net")]
    public void IsAllowedAzureBlobHost_DisallowedHost_ReturnsFalse(string host)
    {
        Assert.False(FileCommandFactory.IsAllowedAzureBlobHost(host));
    }

    [Fact]
    public async Task RunDownloadAsync_RejectsRelativeTraversalDestination()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;
        var previousCwd = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(tempDir);
            var responseJson = """{"code":200,"status":"OK","data":{"Metadata":{"data.txt":{"FileName":"data.txt","DownloadUrl":"https://nodpublishers.blob.core.windows.net/d"}}}}""";
            using var client = CreateClientWithHandler(new FileCommandMockHandler(Ok(responseJson)));
            using var blobClient = new HttpClient(new RoutingMockHandler((_, _) => Ok("payload")));
            var stderr = new StringWriter();
            var outsidePath = Path.GetFullPath(Path.Combine(tempDir, "..", "escape.txt"));

            var exit = await FileCommandFactory.RunDownloadAsync(
                client, blobClient, "E1", "title", "data.txt", "../escape.txt", ListCommand, "json", stderr, default);

            Assert.Equal(1, exit);
            Assert.Contains("must stay within the expected directory", stderr.ToString());
            Assert.False(File.Exists(outsidePath));
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task RunDownloadAsync_ExplicitAbsoluteDestination_WritesFile()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var responseJson = """{"code":200,"status":"OK","data":{"Metadata":{"data.txt":{"FileName":"data.txt","DownloadUrl":"https://nodpublishers.blob.core.windows.net/d"}}}}""";
            using var client = CreateClientWithHandler(new FileCommandMockHandler(Ok(responseJson)));
            using var blobClient = new HttpClient(new RoutingMockHandler((_, _) => Ok("payload")));
            var stderr = new StringWriter();
            var absolutePath = Path.Combine(tempDir, "escape.txt");

            var exit = await FileCommandFactory.RunDownloadAsync(
                client, blobClient, "E1", "title", "data.txt", absolutePath, ListCommand, "json", stderr, default);

            Assert.Equal(0, exit);
            Assert.Equal(
                $"Downloaded 'data.txt' to '{absolutePath}' (7 bytes).{Environment.NewLine}",
                stderr.ToString());
            Assert.True(File.Exists(absolutePath));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task RunDownloadAsync_NoMetadata_ReturnsOne()
    {
        var responseJson = """{"code":200,"status":"OK","data":{"ProfileVersion":1}}""";
        using var client = CreateClientWithHandler(new FileCommandMockHandler(Ok(responseJson)));
        using var blobClient = new HttpClient(new RoutingMockHandler((_, _) => Ok("x")));
        var stderr = new StringWriter();

        var exit = await FileCommandFactory.RunDownloadAsync(
            client, blobClient, "E1", "title", "save.bin", "out.bin", ListCommand, "json", stderr, default);

        Assert.Equal(1, exit);
        Assert.Contains("No files found", stderr.ToString());
    }

    [Fact]
    public async Task RunDownloadAsync_FileNotFound_SuggestsFileListWithoutNamingOtherFiles()
    {
        var responseJson = """{"code":200,"status":"OK","data":{"Metadata":{"other.bin":{"FileName":"other.bin","DownloadUrl":"https://nodpublishers.blob.core.windows.net/d"}}}}""";
        using var client = CreateClientWithHandler(new FileCommandMockHandler(Ok(responseJson)));
        using var blobClient = new HttpClient(new RoutingMockHandler((_, _) => Ok("x")));
        var stderr = new StringWriter();

        var exit = await FileCommandFactory.RunDownloadAsync(
            client, blobClient, "E1", "title", "missing.bin", "out.bin", ListCommand, "json", stderr, default);

        Assert.Equal(1, exit);
        Assert.Equal(
            "Error: File 'missing.bin' not found on this entity. To see its files, run:" + Environment.NewLine
            + $"  {ListCommand}" + Environment.NewLine,
            stderr.ToString());
    }

    [Fact]
    public void BuildListCommandSuggestion_NamesTitleEntityAndProfile()
    {
        var parseResult = Program.BuildRootCommand().Parse(
            ["--profile", "nod", "--playfab-title-id", "ABC123", "playfab", "file", "download",
             "--entity-id", "PLAYER123", "--entity-type", "title_player_account", "--name", "save.json"],
            Program.NoResponseFileConfig);

        Assert.Equal(
            "xbox --profile nod --playfab-title-id ABC123 playfab file list --entity-id PLAYER123 --entity-type title_player_account",
            FileCommandFactory.BuildListCommandSuggestion(parseResult, "PLAYER123", "title_player_account"));
    }

    [Fact]
    public void BuildListCommandSuggestion_QuotesArgumentsForTheShell()
    {
        var parseResult = Program.BuildRootCommand().Parse(
            ["--playfab-title-id", "ABC123", "playfab", "file", "download",
             "--entity-id", "a b", "--entity-type", "title", "--name", "save.json"],
            Program.NoResponseFileConfig);

        Assert.EndsWith(
            $"--entity-id {CommandLine.ShellQuoting.Quote("a b")} --entity-type title",
            FileCommandFactory.BuildListCommandSuggestion(parseResult, "a b", "title"));
    }

    [Fact]
    public async Task RunDownloadAsync_MissingDownloadUrl_ReturnsOne()
    {
        var responseJson = """{"code":200,"status":"OK","data":{"Metadata":{"save.bin":{"FileName":"save.bin"}}}}""";
        using var client = CreateClientWithHandler(new FileCommandMockHandler(Ok(responseJson)));
        using var blobClient = new HttpClient(new RoutingMockHandler((_, _) => Ok("x")));
        var stderr = new StringWriter();

        var exit = await FileCommandFactory.RunDownloadAsync(
            client, blobClient, "E1", "title", "save.bin", "out.bin", ListCommand, "json", stderr, default);

        Assert.Equal(1, exit);
        Assert.Contains("No download URL", stderr.ToString());
    }

    [Fact]
    public async Task RunDownloadAsync_BlobHttpFailure_ReturnsOne()
    {
        var responseJson = """{"code":200,"status":"OK","data":{"Metadata":{"save.bin":{"FileName":"save.bin","DownloadUrl":"https://nodpublishers.blob.core.windows.net/d"}}}}""";
        using var client = CreateClientWithHandler(new FileCommandMockHandler(Ok(responseJson)));
        using var blobClient = new HttpClient(new RoutingMockHandler(
            (_, _) => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("denied") }));
        var stderr = new StringWriter();

        var exit = await FileCommandFactory.RunDownloadAsync(
            client, blobClient, "E1", "title", "save.bin", "out.bin", ListCommand, "json", stderr, default);

        Assert.Equal(1, exit);
        Assert.Contains("Download failed with HTTP 403", stderr.ToString());
    }

    [Fact]
    public async Task RunDownloadAsync_NonAzureBlobHost_ReturnsOneWithoutBlobRequest()
    {
        var responseJson = """{"code":200,"status":"OK","data":{"Metadata":{"save.bin":{"FileName":"save.bin","DownloadUrl":"https://attacker.example.com/d"}}}}""";
        using var client = CreateClientWithHandler(new FileCommandMockHandler(Ok(responseJson)));
        var blobHandler = new RoutingMockHandler((_, _) => Ok("x"));
        using var blobClient = new HttpClient(blobHandler);
        var stderr = new StringWriter();

        var exit = await FileCommandFactory.RunDownloadAsync(
            client, blobClient, "E1", "title", "save.bin", "out.bin", ListCommand, "json", stderr, default);

        Assert.Equal(1, exit);
        Assert.Contains("Rejected download URL host", stderr.ToString());
        Assert.Empty(blobHandler.Requests);
    }

    [Fact]
    public async Task RunDownloadAsync_ApiError_ReturnsOne()
    {
        var errorJson = """{"code":403,"status":"Forbidden","error":"NotAuthorized","errorCode":1100,"errorMessage":"no"}""";
        var handler = new FileCommandMockHandler(
            new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent(errorJson) });
        using var client = CreateClientWithHandler(handler);
        using var blobClient = new HttpClient(new RoutingMockHandler((_, _) => Ok("x")));
        var stderr = new StringWriter();

        var exit = await FileCommandFactory.RunDownloadAsync(
            client, blobClient, "E1", "title", "save.bin", "out.bin", ListCommand, "json", stderr, default);

        Assert.Equal(1, exit);
        Assert.NotEqual(string.Empty, stderr.ToString());
    }

    // ----- upload -----

    [Fact]
    public async Task RunUploadAsync_StdinSource_FailsWithoutUploadingAFileNamedDash()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;
        var previousCwd = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(tempDir);
            await File.WriteAllTextAsync("-", "x");
            var handler = new FileCommandMockHandler(Ok("""{"code":200,"status":"OK","data":{}}"""));
            using var client = CreateClientWithHandler(handler);
            using var blobClient = new HttpClient(new RoutingMockHandler((_, _) => Ok("x")));
            var stderr = new StringWriter();

            var exit = await FileCommandFactory.RunUploadAsync(
                client, blobClient, "E1", "title", "-", "r.bin", "json", "xbox", stderr, default);

            Assert.Equal(1, exit);
            Assert.Equal(
                "Error: Uploading from stdin with '--file -' isn't implemented yet. Pass a file path to --file."
                + Environment.NewLine,
                stderr.ToString());
            Assert.Null(handler.LastRequestUri);
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task RunUploadAsync_LocalFileMissing_ReturnsOne()
    {
        using var client = CreateClientWithHandler(new FileCommandMockHandler(Ok("""{"code":200,"status":"OK","data":{}}""")));
        using var blobClient = new HttpClient(new RoutingMockHandler((_, _) => Ok("x")));
        var stderr = new StringWriter();
        var missing = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.bin");

        var exit = await FileCommandFactory.RunUploadAsync(
            client, blobClient, "E1", "title", missing, null, "json", "xbox", stderr, default);

        Assert.Equal(1, exit);
        Assert.Contains("File not found", stderr.ToString());
    }

    [Fact]
    public async Task RunUploadAsync_Success_InitiatesPutsAndFinalizes()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, "file-content");
            var initiateJson = """{"code":200,"status":"OK","data":{"ProfileVersion":7,"UploadDetails":[{"FileName":"remote.bin","UploadUrl":"https://nodpublishers.blob.core.windows.net/put"}]}}""";
            var finalizeJson = """{"code":200,"status":"OK","data":{"ProfileVersion":8}}""";

            var apiHandler = new RoutingMockHandler((req, _) =>
                req.RequestUri!.ToString().Contains("Initiate", StringComparison.OrdinalIgnoreCase)
                    ? Ok(initiateJson)
                    : Ok(finalizeJson));
            using var client = CreateClientWithHandler(apiHandler);

            var blobHandler = new RoutingMockHandler((_, _) => Ok(""));
            using var blobClient = new HttpClient(blobHandler);
            var stderr = new StringWriter();
            var relativeSegmentPath = Path.Combine(Path.GetDirectoryName(tempFile)!, ".", Path.GetFileName(tempFile));

            var exit = await FileCommandFactory.RunUploadAsync(
                client, blobClient, "E1", "title", relativeSegmentPath, "remote.bin", "json", "xbox", stderr, default);

            Assert.Equal(0, exit);
            Assert.Contains("Uploaded 'remote.bin'", stderr.ToString());
            Assert.Contains($"Uploading source file: {Path.GetFullPath(relativeSegmentPath)}", stderr.ToString());
            Assert.Contains(apiHandler.Requests, r => r.Uri.Contains("InitiateFileUploads"));
            Assert.Contains(apiHandler.Requests, r => r.Uri.Contains("FinalizeFileUploads"));
            Assert.DoesNotContain(apiHandler.Requests, r => r.Uri.Contains("AbortFileUploads"));
            var put = Assert.Single(blobHandler.Requests);
            Assert.Equal("PUT", put.Method);
            Assert.Equal("https://nodpublishers.blob.core.windows.net/put", put.Uri);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task RunUploadAsync_DefaultsRemoteNameToLocalFileName()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var tempFile = Path.Combine(tempDir, "local-name.dat");
            await File.WriteAllTextAsync(tempFile, "abc");
            var initiateJson = """{"code":200,"status":"OK","data":{"ProfileVersion":1,"UploadDetails":[{"FileName":"local-name.dat","UploadUrl":"https://nodpublishers.blob.core.windows.net/put"}]}}""";

            var apiHandler = new RoutingMockHandler((req, _) =>
                req.RequestUri!.ToString().Contains("Initiate", StringComparison.OrdinalIgnoreCase)
                    ? Ok(initiateJson)
                    : Ok("""{"code":200,"status":"OK","data":{"ProfileVersion":2}}"""));
            using var client = CreateClientWithHandler(apiHandler);
            using var blobClient = new HttpClient(new RoutingMockHandler((_, _) => Ok("")));
            var stderr = new StringWriter();

            var exit = await FileCommandFactory.RunUploadAsync(
                client, blobClient, "E1", "title", tempFile, null, "json", "xbox", stderr, default);

            Assert.Equal(0, exit);
            Assert.Contains("Uploaded 'local-name.dat'", stderr.ToString());
            var initiateBody = apiHandler.Requests.First(r => r.Uri.Contains("Initiate")).Body!;
            Assert.Contains("local-name.dat", initiateBody);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private const string InitiateOkJson =
        """{"code":200,"status":"OK","data":{"ProfileVersion":7,"UploadDetails":[{"FileName":"r.bin","UploadUrl":"https://nodpublishers.blob.core.windows.net/put"}]}}""";

    private const string ProfileVersionOkJson = """{"code":200,"status":"OK","data":{"ProfileVersion":8}}""";

    private static Task<HttpResponseMessage> OkAsync(string json) => Task.FromResult(Ok(json));

    private static Task<HttpResponseMessage> PlayFabErrorAsync(HttpStatusCode status, string error) =>
        Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(
                $$"""{"code":{{(int)status}},"status":"{{status}}","error":"{{error}}","errorCode":1000,"errorMessage":"{{error}}"}"""),
        });

    private sealed record UploadRun(int Exit, string Stderr, AsyncRoutingHandler Api, AsyncRoutingHandler Blob)
    {
        public string? AbortBody =>
            Api.Requests.SingleOrDefault(r => r.Uri.Contains("AbortFileUploads", StringComparison.Ordinal)).Body;

        public bool Finalized => Api.Requests.Any(r => r.Uri.Contains("FinalizeFileUploads", StringComparison.Ordinal));
    }

    /// <summary>
    /// Uploads a one-byte file to <c>r.bin</c> on <c>PLAYER123</c>, with PlayFab's Initiate answering
    /// <paramref name="initiateJson"/> and each later step answered by its responder, which
    /// defaults to success.
    /// </summary>
    private static async Task<UploadRun> RunUploadScenarioAsync(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? put = null,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? finalize = null,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? abort = null,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? initiate = null,
        string initiateJson = InitiateOkJson,
        TimeSpan? blobTimeout = null,
        CancellationToken cancellationToken = default)
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, "x", CancellationToken.None);
            var api = new AsyncRoutingHandler((request, token) =>
            {
                var uri = request.RequestUri!.ToString();
                if (uri.Contains("InitiateFileUploads", StringComparison.Ordinal))
                {
                    return initiate?.Invoke(request, token) ?? OkAsync(initiateJson);
                }

                return uri.Contains("FinalizeFileUploads", StringComparison.Ordinal)
                    ? finalize?.Invoke(request, token) ?? OkAsync(ProfileVersionOkJson)
                    : abort?.Invoke(request, token) ?? OkAsync(ProfileVersionOkJson);
            });
            using var client = CreateClientWithHandler(api);
            var blob = new AsyncRoutingHandler(put ?? ((_, _) => OkAsync(string.Empty)));
            using var blobClient = new HttpClient(blob) { Timeout = blobTimeout ?? TimeSpan.FromSeconds(100) };
            var stderr = new StringWriter();

            var exit = await FileCommandFactory.RunUploadAsync(
                client, blobClient, "PLAYER123", "title_player_account", tempFile, "r.bin", "json",
                "xbox --playfab-title-id ABC123", stderr, cancellationToken);

            return new UploadRun(exit, stderr.ToString(), api, blob);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task RunUploadAsync_InitiateFails_DoesNotAbort()
    {
        var run = await RunUploadScenarioAsync(
            initiate: (_, _) => PlayFabErrorAsync(HttpStatusCode.BadRequest, "EntityFileOperationPending"));

        Assert.Equal(1, run.Exit);
        Assert.Contains("EntityFileOperationPending", run.Stderr);
        Assert.Single(run.Api.Requests);
        Assert.Empty(run.Blob.Requests);
        Assert.DoesNotContain("--body-file", run.Stderr);
    }

    [Fact]
    public async Task RunUploadAsync_InitiateOutcomeUnknown_WarnsOfTheLockWithoutAborting()
    {
        var run = await RunUploadScenarioAsync(
            initiate: (_, _) => PlayFabErrorAsync(HttpStatusCode.ServiceUnavailable, "ServiceUnavailable"));

        Assert.Equal(1, run.Exit);
        Assert.Single(run.Api.Requests);
        Assert.Empty(run.Blob.Requests);
        Assert.Contains("PlayFab may have started the upload of 'r.bin'", run.Stderr);
        Assert.Contains("playfab api /File/AbortFileUploads --body-file -", run.Stderr);
    }

    [Theory]
    [InlineData("""{"code":200,"status":"OK","data":{"ProfileVersion":7,"UploadDetails":[]}}""", "No upload URL")]
    [InlineData("""{"code":200,"status":"OK","data":{"ProfileVersion":7,"UploadDetails":[{"FileName":"r.bin","UploadUrl":""}]}}""", "Empty upload URL")]
    [InlineData("""{"code":200,"status":"OK","data":{"ProfileVersion":7,"UploadDetails":[{"FileName":"r.bin","UploadUrl":"https://attacker.example.com/put"}]}}""", "Rejected upload URL host")]
    public async Task RunUploadAsync_UnusableUploadUrl_AbortsWithoutPutOrFinalize(string initiateJson, string expectedError)
    {
        var run = await RunUploadScenarioAsync(initiateJson: initiateJson);

        Assert.Equal(1, run.Exit);
        Assert.Contains(expectedError, run.Stderr);
        Assert.Empty(run.Blob.Requests);
        Assert.False(run.Finalized);
        Assert.NotNull(run.AbortBody);
        Assert.Contains("Canceled the pending upload of 'r.bin'", run.Stderr);
    }

    [Fact]
    public async Task RunUploadAsync_BlobPutFailure_AbortsThePendingUploadAtItsProfileVersion()
    {
        var run = await RunUploadScenarioAsync(
            put: (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("blob-rejected") }));

        Assert.Equal(1, run.Exit);
        Assert.Contains("upload failed with HTTP 400", run.Stderr);
        Assert.Contains("blob-rejected", run.Stderr);
        Assert.False(run.Finalized);
        Assert.Equal(
            """{"Entity":{"Id":"PLAYER123","Type":"title_player_account"},"FileNames":["r.bin"],"ProfileVersion":7}""",
            run.AbortBody);
        Assert.Contains("Canceled the pending upload of 'r.bin'. The entity's files are unchanged.", run.Stderr);
    }

    [Fact]
    public async Task RunUploadAsync_BlobPutTimesOut_Aborts()
    {
        var run = await RunUploadScenarioAsync(
            put: async (_, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                throw new InvalidOperationException("unreachable");
            },
            blobTimeout: TimeSpan.FromMilliseconds(50));

        Assert.Equal(1, run.Exit);
        Assert.Contains("the file transfer timed out", run.Stderr);
        Assert.False(run.Finalized);
        Assert.Contains("Canceled the pending upload of 'r.bin'", run.Stderr);
    }

    [Fact]
    public async Task RunUploadAsync_CtrlCDuringPut_StillAborts()
    {
        using var ctrlC = new CancellationTokenSource();
        var run = await RunUploadScenarioAsync(
            put: async (_, token) =>
            {
                ctrlC.Cancel();
                await Task.Delay(Timeout.Infinite, token);
                throw new InvalidOperationException("unreachable");
            },
            cancellationToken: ctrlC.Token);

        Assert.Equal(1, run.Exit);
        Assert.Contains("Upload canceled.", run.Stderr);
        Assert.False(run.Finalized);
        Assert.Contains("Canceled the pending upload of 'r.bin'", run.Stderr);
    }

    [Fact]
    public async Task RunUploadAsync_FinalizeRejected_Aborts()
    {
        var run = await RunUploadScenarioAsync(
            finalize: (_, _) => PlayFabErrorAsync(HttpStatusCode.BadRequest, "FileNotFound"));

        Assert.Equal(1, run.Exit);
        Assert.Contains("FileNotFound", run.Stderr);
        Assert.Contains("Canceled the pending upload of 'r.bin'", run.Stderr);
        Assert.DoesNotContain("finalize may have applied", run.Stderr);
    }

    [Fact]
    public async Task RunUploadAsync_CtrlCDuringFinalize_AbortsAndReportsTheFinalizeDidNotApply()
    {
        using var ctrlC = new CancellationTokenSource();
        var run = await RunUploadScenarioAsync(
            finalize: async (_, token) =>
            {
                ctrlC.Cancel();
                await Task.Delay(Timeout.Infinite, token);
                throw new InvalidOperationException("unreachable");
            },
            cancellationToken: ctrlC.Token);

        Assert.Equal(1, run.Exit);
        Assert.Contains("the request was canceled", run.Stderr);
        Assert.Contains("\"ProfileVersion\":7", run.AbortBody);
        Assert.Contains("Canceled the pending upload of 'r.bin'. The entity's files are unchanged.", run.Stderr);
    }

    [Fact]
    public async Task RunUploadAsync_FinalizeOutcomeUnknownAndProfileChanged_LeavesItAloneAndSaysHowToCheck()
    {
        var run = await RunUploadScenarioAsync(
            finalize: (_, _) => PlayFabErrorAsync(HttpStatusCode.ServiceUnavailable, "ServiceUnavailable"),
            abort: (_, _) => PlayFabErrorAsync(HttpStatusCode.BadRequest, "EntityProfileVersionMismatch"));

        Assert.Equal(1, run.Exit);
        Assert.Contains("Didn't cancel the upload of 'r.bin'", run.Stderr);
        Assert.Contains("most likely because the finalize applied", run.Stderr);
        Assert.Contains(
            "xbox --playfab-title-id ABC123 playfab file list --entity-id PLAYER123 --entity-type title_player_account",
            run.Stderr);
    }

    [Fact]
    public async Task RunUploadAsync_FinalizeAnsweredSuccessWithUnreadableBody_DoesNotAbort()
    {
        var run = await RunUploadScenarioAsync(finalize: (_, _) => OkAsync("not json"));

        Assert.Equal(1, run.Exit);
        Assert.True(run.Finalized);
        Assert.Null(run.AbortBody);
    }

    [Fact]
    public async Task RunUploadAsync_AbortFails_SaysTheFileMayStayLockedAndHowToCancelIt()
    {
        var run = await RunUploadScenarioAsync(
            put: (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)),
            abort: (_, _) => PlayFabErrorAsync(HttpStatusCode.Forbidden, "NotAuthorized"));

        Assert.Equal(1, run.Exit);
        Assert.Contains("Couldn't cancel the pending upload of 'r.bin'", run.Stderr);
        Assert.Contains("NotAuthorized", run.Stderr);
        Assert.Contains("EntityFileOperationPending", run.Stderr);
        var body = """{"Entity":{"Id":"PLAYER123","Type":"title_player_account"},"FileNames":["r.bin"]}""";
        Assert.Contains(
            $"{ShellQuoting.PrintLine(body)} | xbox --playfab-title-id ABC123 playfab api /File/AbortFileUploads --body-file -",
            run.Stderr);
        Assert.DoesNotContain("finalize may have applied", run.Stderr);
    }

    [Fact]
    public async Task RunUploadAsync_AbortGetsNoAnswer_SaysItCantTellAndHowToCancelIt()
    {
        var run = await RunUploadScenarioAsync(
            put: (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)),
            abort: (_, _) => throw new HttpRequestException("connection reset"));

        Assert.Equal(1, run.Exit);
        Assert.Contains("Couldn't tell whether the pending upload of 'r.bin' was canceled", run.Stderr);
        Assert.Contains("--body-file -", run.Stderr);
    }

    [Fact]
    public async Task RunUploadAsync_AbortAnsweredSuccessWithUnreadableBody_ReportsItCanceled()
    {
        var run = await RunUploadScenarioAsync(
            put: (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)),
            abort: (_, _) => OkAsync("not json"));

        Assert.Equal(1, run.Exit);
        Assert.Contains("Canceled the pending upload of 'r.bin'. The entity's files are unchanged.", run.Stderr);
        Assert.DoesNotContain("--body-file", run.Stderr);
    }

    [Fact]
    public async Task RunUploadAsync_KeepsTheProcessAliveFromInitiateOn()
    {
        var putStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePut = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runTask = RunUploadScenarioAsync(put: async (_, _) =>
        {
            putStarted.SetResult();
            await releasePut.Task;
            return Ok(string.Empty);
        });

        await putStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // After Ctrl-C, Main waits on this; a transfer still in flight must hold it.
        var exitWait = ExitCleanup.Default.WaitAsync(TimeSpan.FromSeconds(30));
        await Task.Delay(100);
        Assert.False(exitWait.IsCompleted);

        releasePut.SetResult();
        var run = await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        await exitWait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, run.Exit);
    }

    [Theory]
    [InlineData("NoEntityFileOperationPending")]
    [InlineData("FileNotFound")]
    public async Task RunUploadAsync_NothingPendingToAbort_SaysNothingIsLocked(string abortError)
    {
        var run = await RunUploadScenarioAsync(
            put: (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)),
            abort: (_, _) => PlayFabErrorAsync(HttpStatusCode.BadRequest, abortError));

        Assert.Equal(1, run.Exit);
        Assert.Contains("PlayFab has no pending upload of 'r.bin' to cancel, so nothing is locked.", run.Stderr);
        Assert.DoesNotContain("playfab api /File/AbortFileUploads", run.Stderr);
    }

    // ----- delete -----

    [Fact]
    public async Task RunDeleteAsync_NotConfirmed_ReturnsOneWithoutCallingApi()
    {
        var handler = new FileCommandMockHandler(Ok("""{"code":200,"status":"OK","data":{}}"""));
        using var client = CreateClientWithHandler(handler);

        var exit = await FileCommandFactory.RunDeleteAsync(
            client, "E1", "title", new[] { "f.bin" }, confirmedDestructive: false, "json", new StringWriter(), cancellationToken: CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Null(handler.LastRequestUri);
    }

    [Fact]
    public async Task RunDeleteAsync_Confirmed_SingleFile_UsesSingularMessage()
    {
        var handler = new FileCommandMockHandler(Ok("""{"code":200,"status":"OK","data":{}}"""));
        using var client = CreateClientWithHandler(handler);
        var stderr = new StringWriter();

        var exit = await FileCommandFactory.RunDeleteAsync(
            client, "E1", "title", new[] { "f.bin" }, confirmedDestructive: true, "json", stderr, cancellationToken: CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("/File/DeleteFiles", handler.LastRequestUri);
        Assert.Contains("Deleted file: 'f.bin'", stderr.ToString());
    }

    [Fact]
    public async Task RunDeleteAsync_Confirmed_MultipleFiles_UsesPluralMessage()
    {
        var handler = new FileCommandMockHandler(Ok("""{"code":200,"status":"OK","data":{}}"""));
        using var client = CreateClientWithHandler(handler);
        var stderr = new StringWriter();

        var exit = await FileCommandFactory.RunDeleteAsync(
            client, "E1", "title", new[] { "a.bin", "b.bin" }, confirmedDestructive: true, "json", stderr, cancellationToken: CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("Deleted files: 'a.bin', 'b.bin'", stderr.ToString());
        Assert.NotNull(handler.CapturedBody);
        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        var fileNames = doc.RootElement.GetProperty("FileNames").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(new List<string?> { "a.bin", "b.bin" }, fileNames);
    }

    [Fact]
    public async Task RunDeleteAsync_ApiError_ReturnsOne()
    {
        var errorJson = """{"code":500,"status":"InternalServerError","error":"Internal","errorCode":1,"errorMessage":"boom"}""";
        var handler = new FileCommandMockHandler(
            new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent(errorJson) });
        using var client = CreateClientWithHandler(handler);
        var stderr = new StringWriter();

        var exit = await FileCommandFactory.RunDeleteAsync(
            client, "E1", "title", new[] { "f.bin" }, confirmedDestructive: true, "json", stderr, cancellationToken: CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.NotEqual(string.Empty, stderr.ToString());
    }

    #endregion
}

/// <summary>
/// Mock handler that records each request (method, URI, body) and returns a
/// response produced by a caller-supplied responder. Supports multi-call flows
/// such as upload (initiate + finalize) and routing by request URI.
/// </summary>
internal sealed class RoutingMockHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, string, HttpResponseMessage> _responder;

    public List<(string Method, string Uri, string? Body)> Requests { get; } = new();

    public RoutingMockHandler(Func<HttpRequestMessage, string, HttpResponseMessage> responder)
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
        return _responder(request, body ?? string.Empty);
    }
}

/// <summary>
/// Mock handler that captures the request body and URI for assertion.
/// </summary>
internal class FileCommandMockHandler : HttpMessageHandler
{
    private readonly HttpResponseMessage _response;
    public string? CapturedBody { get; private set; }
    public string? LastRequestUri { get; private set; }

    public FileCommandMockHandler(HttpResponseMessage response)
    {
        _response = response;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        LastRequestUri = request.RequestUri?.ToString();
        if (request.Content != null)
        {
            CapturedBody = await request.Content.ReadAsStringAsync(cancellationToken);
        }

        return _response;
    }
}

/// <summary>
/// Mock handler that records each request (method, URI, body) and answers through an
/// asynchronous responder, which can wait on the request's cancellation token to stand in for a
/// request that hangs until it times out or is canceled.
/// </summary>
internal sealed class AsyncRoutingHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

    public List<(string Method, string Uri, string? Body)> Requests { get; } = new();

    public AsyncRoutingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
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
        return await _responder(request, cancellationToken);
    }
}
