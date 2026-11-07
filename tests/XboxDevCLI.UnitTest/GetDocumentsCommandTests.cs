// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using XboxDevCLI.Commands;
using XboxDevCLI.Output;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

[Collection("ConsoleRedirect")]
public class GetDocumentsCommandTests
{
    [Fact]
    public async Task ApiClient_ReturnsArchiveAndMetadata()
    {
        byte[] content = CreateZipArchive(("nsal.xml", "<ServiceNsal />"));
        using var response = CreateResponse(content, "\"etag-value\"", 42, "published");
        var handler = new MockHttpMessageHandler(response);
        using var client = CreateClient(handler);
        var scid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        XboxPartnerDocumentArchive result = await client.GetXboxLiveDocumentsAsync(
            12345,
            new XboxLiveGetDocumentsRequest(
                "sandbox",
                scid,
                "AbCdEf.0",
                "published",
                42,
                ["nsal.xml"]),
            CancellationToken.None);

        Assert.Equal(content, result.Content);
        Assert.Equal("\"etag-value\"", result.ETag);
        Assert.Equal(42UL, result.Version);
        Assert.Equal("published", result.View);
        Assert.Equal(
            "http://localhost:8080/xboxpartner/v1/xboxlive/documents",
            handler.LastRequest?.RequestUri?.AbsoluteUri);
        Assert.Equal("application/zip", handler.LastRequest?.Headers.Accept.Single().MediaType);
        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("sandbox", body.RootElement.GetProperty("documentType").GetString());
        Assert.Equal(scid, body.RootElement.GetProperty("scid").GetGuid());
        Assert.Equal("AbCdEf.0", body.RootElement.GetProperty("sandbox").GetString());
        Assert.Equal("nsal.xml", body.RootElement.GetProperty("documents")[0].GetString());
    }

    [Fact]
    public async Task ApiClient_RejectsArchiveLargerThanConfiguredDocumentLimit()
    {
        using var response = CreateResponse([1, 2, 3, 4], "\"etag-value\"", 42, "published");
        var handler = new MockHttpMessageHandler(response);
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["XboxPartner:BaseUrl"] = "http://localhost:8080",
                ["XboxPartner:MaxDocumentArchiveBytes"] = "3",
            })
            .Build();
        using var client = new XboxPartnerClient(
            configuration,
            new HttpClient(handler),
            () => Task.FromResult<string?>("caller-token"));

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetXboxLiveDocumentsAsync(
                12345,
                new XboxLiveGetDocumentsRequest(
                    "sandbox",
                    Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
                    "ALL",
                    "published",
                    42,
                    ["nsal.xml"]),
                CancellationToken.None));

        Assert.Contains(
            "XboxPartner:MaxDocumentArchiveBytes",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Command_ValidatesScopeSpecificOptions()
    {
        ParseResult validSandbox = Program.BuildRootCommand().Parse(
            "xbl document download --seller-id 2 " +
            "--scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id XDKS.1 " +
            "--dest output");
        ParseResult validAccount = Program.BuildRootCommand().Parse(
            "xbl document download --seller-id 2 " +
            "--type account --dest output");
        ParseResult missingScid = Program.BuildRootCommand().Parse(
            "xbl document download --seller-id 2 " +
            "--sandbox-id XDKS.1 --dest output");
        ParseResult mixedScope = Program.BuildRootCommand().Parse(
            "xbl document download --seller-id 2 " +
            "--type account --sandbox-id XDKS.1 --dest output");
        ParseResult unsafeSandbox = Program.BuildRootCommand().Parse(
            "xbl document download --seller-id 2 " +
            "--scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id ../outside " +
            "--dest output");
        ParseResult removedAccountId = Program.BuildRootCommand().Parse(
            "xbl document download --seller-id 2 " +
            "--type account --account-id 11111111-2222-3333-4444-555555555555 --dest output");

        Assert.Empty(validSandbox.Errors);
        Assert.Empty(validAccount.Errors);
        Assert.NotEmpty(missingScid.Errors);
        Assert.NotEmpty(mixedScope.Errors);
        Assert.NotEmpty(unsafeSandbox.Errors);
        Assert.NotEmpty(removedAccountId.Errors);
    }

    [Fact]
    public void Command_ReportsAnScidThatIsntAGuidAsAParseError()
    {
        ParseResult result = Program.BuildRootCommand().Parse(
            "xbl document download --seller-id 2 --scid notaguid --sandbox-id XDKS.1 --dest output");

        Assert.Contains(result.Errors, error => error.Message.Contains("notaguid", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_ExtractsFilesAndSavesEtag()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"XboxDevCLI-{Guid.NewGuid():N}");
        try
        {
            byte[] content = CreateZipArchive(
                ("nsal.xml", "<ServiceNsal />"),
                ("titlepriv.xml", "<TitlePrivileges />"));
            Directory.CreateDirectory(directory);
            string existingEtagPath = Path.Combine(directory, "ALL.etag");
            await File.WriteAllTextAsync(existingEtagPath, "\"old-etag\"");
            if (OperatingSystem.IsWindows())
            {
                File.SetAttributes(
                    existingEtagPath,
                    File.GetAttributes(existingEtagPath) | FileAttributes.Hidden);
            }
            using var response = CreateResponse(content, "\"etag-value\"", 42, "working");
            using var client = CreateClient(new MockHttpMessageHandler(response));
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            int exitCode = await GetDocumentsCommandFactory.RunAsync(
                client,
                12345,
                new XboxLiveGetDocumentsRequest(
                    "sandbox",
                    Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
                    "ALL",
                    "working",
                    null,
                    []),
                directory,
                OutputOptions.JsonCompact,
                stdout,
                stderr,
                CancellationToken.None);

            Assert.Equal(0, exitCode);
            Assert.Equal("<ServiceNsal />", await File.ReadAllTextAsync(
                Path.Combine(directory, "nsal.xml")));
            Assert.Equal("<TitlePrivileges />", await File.ReadAllTextAsync(
                Path.Combine(directory, "titlepriv.xml")));
            Assert.Equal("\"etag-value\"", await File.ReadAllTextAsync(
                Path.Combine(directory, "ALL.etag")));
            if (OperatingSystem.IsWindows())
            {
                Assert.True(
                    (File.GetAttributes(Path.Combine(directory, "ALL.etag")) &
                        FileAttributes.Hidden) != 0);
            }
            Assert.Contains("\"version\":42", stdout.ToString(), StringComparison.Ordinal);
            Assert.Empty(stderr.ToString());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RunAsync_WritesExtractionSummaryAsTable()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"XboxDevCLI-{Guid.NewGuid():N}");
        try
        {
            byte[] content = CreateZipArchive(
                ("nsal.xml", "<ServiceNsal />"),
                ("titlepriv.xml", "<TitlePrivileges />"));
            using var response = CreateResponse(content, "\"etag-value\"", 42, "working");
            using var client = CreateClient(new MockHttpMessageHandler(response));
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            int exitCode = await GetDocumentsCommandFactory.RunAsync(
                client,
                12345,
                new XboxLiveGetDocumentsRequest(
                    "sandbox",
                    Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
                    "XDKS.1",
                    "working",
                    null,
                    []),
                directory,
                OutputOptions.Table(XboxPartnerCommandSupport.DocumentDownloadTableView),
                stdout,
                stderr,
                CancellationToken.None);

            Assert.Equal(0, exitCode);
            Assert.Contains("TYPE", stdout.ToString(), StringComparison.Ordinal);
            Assert.Contains("SANDBOX", stdout.ToString(), StringComparison.Ordinal);
            Assert.Contains("FILES", stdout.ToString(), StringComparison.Ordinal);
            Assert.Contains("XDKS.1", stdout.ToString(), StringComparison.Ordinal);
            Assert.Contains("working", stdout.ToString(), StringComparison.Ordinal);
            Assert.Contains("2", stdout.ToString(), StringComparison.Ordinal);
            Assert.Empty(stderr.ToString());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RunAsync_RejectsUnsafeArchiveEntry()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"XboxDevCLI-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            string existingEtagPath = Path.Combine(directory, "Account.etag");
            await File.WriteAllTextAsync(existingEtagPath, "\"old-etag\"");
            byte[] content = CreateZipArchive(("../escape.xml", "unsafe"));
            using var response = CreateResponse(content, "\"etag-value\"", 42, "working");
            using var client = CreateClient(new MockHttpMessageHandler(response));
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            int exitCode = await GetDocumentsCommandFactory.RunAsync(
                client,
                12345,
                new XboxLiveGetDocumentsRequest(
                    "account",
                    null,
                    null,
                    "working",
                    null,
                    []),
                directory,
                OutputOptions.Json,
                stdout,
                stderr,
                CancellationToken.None);

            Assert.Equal(1, exitCode);
            Assert.Empty(stdout.ToString());
            Assert.Contains("invalid or duplicate", stderr.ToString(), StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(Path.GetTempPath(), "escape.xml")));
            Assert.Equal("\"old-etag\"", await File.ReadAllTextAsync(existingEtagPath));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExtractArchiveAsync_RejectsCumulativeExpandedSize()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"XboxDevCLI-{Guid.NewGuid():N}");
        try
        {
            byte[] content = CreateZipArchive(("large.xml", "12345"));

            InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => GetDocumentsCommandFactory.ExtractArchiveAsync(
                    content,
                    directory,
                    maxExtractedBytes: 4,
                    maxEntries: 10,
                    CancellationToken.None));

            Assert.Contains("expands beyond", exception.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExtractArchiveAsync_RejectsExcessiveEntryCount()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"XboxDevCLI-{Guid.NewGuid():N}");
        try
        {
            byte[] content = CreateZipArchive(("one.xml", "1"), ("two.xml", "2"));

            InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => GetDocumentsCommandFactory.ExtractArchiveAsync(
                    content,
                    directory,
                    maxExtractedBytes: 100,
                    maxEntries: 1,
                    CancellationToken.None));

            Assert.Contains("more than 1 documents", exception.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(directory));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExtractArchiveAsync_PreflightsAllDestinationsBeforeReplacingFiles()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"XboxDevCLI-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            string existingFile = Path.Combine(directory, "a.xml");
            await File.WriteAllTextAsync(existingFile, "existing");
            Directory.CreateDirectory(Path.Combine(directory, "b.xml"));
            byte[] content = CreateZipArchive(("a.xml", "replacement"), ("b.xml", "content"));

            IOException exception = await Assert.ThrowsAsync<IOException>(
                () => GetDocumentsCommandFactory.ExtractArchiveAsync(
                    content,
                    directory,
                    maxExtractedBytes: 100,
                    maxEntries: 10,
                    CancellationToken.None));

            Assert.Contains(
                "destination is a directory",
                exception.Message,
                StringComparison.Ordinal);
            Assert.Equal("existing", await File.ReadAllTextAsync(existingFile));
            Assert.True(Directory.Exists(Path.Combine(directory, "b.xml")));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExtractArchiveAsync_RejectsDestinationDirectoryReparsePoint()
    {
        string targetDirectory = Path.Combine(
            Path.GetTempPath(),
            $"XboxDevCLI-{Guid.NewGuid():N}");
        string linkedDirectory = Path.Combine(
            Path.GetTempPath(),
            $"XboxDevCLI-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(targetDirectory);
            if (!TryCreateDirectorySymbolicLink(linkedDirectory, targetDirectory))
            {
                return;
            }

            byte[] content = CreateZipArchive(("document.xml", "content"));
            string destination = Path.Combine(linkedDirectory, "output");

            IOException exception = await Assert.ThrowsAsync<IOException>(
                () => GetDocumentsCommandFactory.ExtractArchiveAsync(
                    content,
                    destination,
                    maxExtractedBytes: 100,
                    maxEntries: 10,
                    CancellationToken.None));

            Assert.Contains("reparse point", exception.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.GetFileSystemEntries(targetDirectory));
        }
        finally
        {
            if (Directory.Exists(linkedDirectory))
            {
                Directory.Delete(linkedDirectory);
            }

            if (Directory.Exists(targetDirectory))
            {
                Directory.Delete(targetDirectory, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("payload.xml:Zone.Identifier")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("COM1.log")]
    [InlineData("report.")]
    [InlineData("report ")]
    public async Task ExtractArchiveAsync_RejectsInvalidLocalFileNames(string fileName)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"XboxDevCLI-{Guid.NewGuid():N}");
        try
        {
            byte[] content = CreateZipArchive((fileName, "content"));

            InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => GetDocumentsCommandFactory.ExtractArchiveAsync(
                    content,
                    directory,
                    maxExtractedBytes: 100,
                    maxEntries: 10,
                    CancellationToken.None));

            Assert.Contains(
                "invalid or duplicate document name",
                exception.Message,
                StringComparison.Ordinal);
            Assert.False(Directory.Exists(directory));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Command_NormalizesTypeViewAndSandboxBeforeRequest()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"XboxDevCLI-{Guid.NewGuid():N}");
        TextWriter originalOut = Console.Out;
        TextWriter originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var handler = new MockHttpMessageHandler(CreateResponse(
            CreateZipArchive(("nsal.xml", "<ServiceNsal />")),
            "\"etag-value\"",
            42,
            "published"));
        Console.SetOut(stdout);
        Console.SetError(stderr);
        Program.XboxPartnerClientFactory = configuration => new XboxPartnerClient(
            configuration,
            new HttpClient(handler),
            () => Task.FromResult<string?>("caller-token"));
        try
        {
            int exitCode = await Program.Main(
            [
                "xbl",
                "document",
                "download",
                "--seller-id",
                "2",
                "--type",
                "SANDBOX",
                "--scid",
                "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
                "--sandbox-id",
                " XDKS.1 ",
                "--view",
                "PUBLISHED",
                "--dest",
                directory,
                "--output",
                "json-compact",
            ]);

            Assert.Equal(0, exitCode);
            using var body = JsonDocument.Parse(handler.LastRequestBody!);
            Assert.Equal("sandbox", body.RootElement.GetProperty("documentType").GetString());
            Assert.Equal("published", body.RootElement.GetProperty("view").GetString());
            Assert.Equal("XDKS.1", body.RootElement.GetProperty("sandbox").GetString());
            Assert.True(File.Exists(Path.Combine(directory, "XDKS.1.etag")));
        }
        finally
        {
            Program.XboxPartnerClientFactory = null;
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static XboxPartnerClient CreateClient(HttpMessageHandler handler) =>
        new(
            CreateConfiguration(),
            new HttpClient(handler),
            () => Task.FromResult<string?>("caller-token"));

    private static IConfiguration CreateConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["XboxPartner:BaseUrl"] = "http://localhost:8080",
            })
            .Build();

    private static HttpResponseMessage CreateResponse(
        byte[] content,
        string eTag,
        ulong version,
        string view)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(content),
        };
        response.Headers.ETag = new EntityTagHeaderValue(eTag);
        response.Headers.Add("X-Version", version.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        response.Headers.Add("X-Document-View", view);
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        return response;
    }

    private static byte[] CreateZipArchive(params (string Name, string Content)[] files)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, string content) in files)
            {
                ZipArchiveEntry entry = archive.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(content);
            }
        }

        return stream.ToArray();
    }

    private static bool TryCreateDirectorySymbolicLink(string path, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(path, targetPath);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
    }
}
