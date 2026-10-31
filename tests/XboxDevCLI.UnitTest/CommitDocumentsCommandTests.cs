// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using XboxDevCLI.Commands;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

public class CommitDocumentsCommandTests
{
    [Fact]
    public void Command_ReportsAnScidThatIsntAGuidAsAParseError()
    {
        ParseResult result = Program.BuildRootCommand().Parse(
            ["xbl", "document", "commit", "--scid", "notaguid", "--sandbox", "XDKS.1", "--file", "a.json"],
            Program.NoResponseFileConfig);

        Assert.Contains(result.Errors, error => error.Message.Contains("notaguid", StringComparison.Ordinal));
    }

    [Fact]
    public void Command_CollectsRepeatedFileOptionsWithoutSplittingPaths()
    {
        string[] paths = ["folder with spaces/nsal.xml", "title,priv.xml", "config/*.xml"];
        ParseResult result = Program.BuildRootCommand().Parse(
            ["xbl", "document", "commit", "--type", "account",
                "--file", paths[0], "--file", paths[1], "--file", paths[2]],
            Program.NoResponseFileConfig);

        Assert.Empty(result.Errors);
        var option = Assert.IsType<Option<string[]>>(
            Assert.Single(result.CommandResult.Command.Options, o => o.Name == "--file"));
        Assert.Equal(paths, result.GetValue(option));
    }

    [Theory]
    [InlineData("--file")]
    [InlineData("--files", "nsal.xml")]
    [InlineData("--file", "nsal.xml", "titlepriv.xml")]
    [InlineData("--file", "nsal.xml", "--file")]
    [InlineData("--file", "--file", "nsal.xml")]
    [InlineData("--file", "")]
    [InlineData("--file", " ")]
    [InlineData("--file", "nsal.xml", "--file", "", "--file", "titlepriv.xml")]
    public void Command_RejectsMissingFileValuesAndLegacySyntax(params string[] arguments)
    {
        ParseResult result = Program.BuildRootCommand().Parse(
            ["xbl", "document", "commit", "--type", "account", .. arguments],
            Program.NoResponseFileConfig);

        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Command_AcceptsRepeatedFileOptionsWithEqualsSyntax()
    {
        ParseResult result = Program.BuildRootCommand().Parse(
            ["xbl", "document", "commit", "--type", "account",
                "--file=nsal.xml", "--file=titlepriv.xml"],
            Program.NoResponseFileConfig);

        Assert.Empty(result.Errors);
        var option = Assert.IsType<Option<string[]>>(
            Assert.Single(result.CommandResult.Command.Options, o => o.Name == "--file"));
        Assert.Equal(new[] { "nsal.xml", "titlepriv.xml" }, result.GetValue(option));
    }

    [Fact]
    public void Command_ValidatesScopeSpecificOptionsAndFiles()
    {
        ParseResult validSandbox = Program.BuildRootCommand().Parse(
            "xbl document commit --seller-id 2 " +
            "--scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox XDKS.1 " +
            "--file nsal.xml --file titlepriv.xml --validate-only");
        ParseResult validAccount = Program.BuildRootCommand().Parse(
            "xbl document commit --seller-id 2 " +
            "--type account --file accountconfig.xml");
        ParseResult missingFiles = Program.BuildRootCommand().Parse(
            "xbl document commit --seller-id 2 " +
            "--scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox XDKS.1");
        ParseResult missingScid = Program.BuildRootCommand().Parse(
            "xbl document commit --seller-id 2 " +
            "--sandbox XDKS.1 --file nsal.xml");
        ParseResult mixedScope = Program.BuildRootCommand().Parse(
            "xbl document commit --seller-id 2 " +
            "--type account --sandbox XDKS.1 --file accountconfig.xml");
        ParseResult unsafeSandbox = Program.BuildRootCommand().Parse(
            "xbl document commit --seller-id 2 " +
            "--scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox ../outside " +
            "--file nsal.xml");
        ParseResult removedAccountId = Program.BuildRootCommand().Parse(
            "xbl document commit --seller-id 2 " +
            "--type account --account-id 11111111-2222-3333-4444-555555555555 " +
            "--file accountconfig.xml");
        ParseResult invalidEtagFileName = Program.BuildRootCommand().Parse(
            "xbl document commit --seller-id 2 " +
            "--scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox CON " +
            "--file nsal.xml");

        Assert.Empty(validSandbox.Errors);
        Assert.Empty(validAccount.Errors);
        Assert.NotEmpty(missingFiles.Errors);
        Assert.NotEmpty(missingScid.Errors);
        Assert.NotEmpty(mixedScope.Errors);
        Assert.NotEmpty(unsafeSandbox.Errors);
        Assert.NotEmpty(removedAccountId.Errors);
        if (OperatingSystem.IsWindows())
        {
            Assert.NotEmpty(invalidEtagFileName.Errors);
        }
    }

    [Fact]
    public async Task RunAsync_ExpandsFilesLoadsEtagAndSavesReturnedEtag()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string firstPath = Path.Combine(directory, "nsal.xml");
            string secondPath = Path.Combine(directory, "titlepriv.xml");
            await File.WriteAllTextAsync(firstPath, "<ServiceNsal />");
            await File.WriteAllTextAsync(secondPath, "<TitlePrivileges />");
            await File.WriteAllTextAsync(Path.Combine(directory, "ALL.etag"), "\"old-etag\"");
            using var response = CreateResponse(
                """{"ValidationInfo":[],"Version":42,"ETag":"\"new-etag\"","CanCommit":true,"Committed":true}""");
            var handler = new MockHttpMessageHandler(response);
            using var client = CreateClient(handler);
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            var scid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

            int exitCode = await CommitDocumentsCommandFactory.RunAsync(
                client,
                12345,
                " SandBox ",
                scid,
                " ALL ",
                [firstPath, Path.Combine(directory, "*.xml"), firstPath],
                validateOnly: false,
                force: false,
                eTag: null,
                message: "Test commit",
                "json-compact",
                stdout,
                stderr,
                CancellationToken.None);

            Assert.Equal(0, exitCode);
            Assert.Empty(stderr.ToString());
            Assert.Equal(
                "http://localhost:8080/xboxpartner/v1/xboxlive/documents/commit",
                handler.LastRequest?.RequestUri?.AbsoluteUri);
            using JsonDocument body = JsonDocument.Parse(handler.LastRequestBody!);
            Assert.Equal("sandbox", body.RootElement.GetProperty("documentType").GetString());
            Assert.Equal(scid, body.RootElement.GetProperty("scid").GetGuid());
            Assert.Equal("ALL", body.RootElement.GetProperty("sandbox").GetString());
            Assert.Equal("\"old-etag\"", body.RootElement.GetProperty("eTag").GetString());
            Assert.Equal("Test commit", body.RootElement.GetProperty("message").GetString());
            Assert.False(body.RootElement.GetProperty("validateOnly").GetBoolean());
            Assert.False(body.RootElement.GetProperty("force").GetBoolean());

            JsonElement documents = body.RootElement.GetProperty("documents");
            Assert.Equal(2, documents.GetArrayLength());
            Assert.Equal("nsal.xml", documents[0].GetProperty("name").GetString());
            Assert.Equal(
                "<ServiceNsal />",
                DecodeContent(documents[0].GetProperty("content").GetString()));
            Assert.Equal("titlepriv.xml", documents[1].GetProperty("name").GetString());
            Assert.Equal(
                "<TitlePrivileges />",
                DecodeContent(documents[1].GetProperty("content").GetString()));

            string eTagPath = Path.Combine(directory, "ALL.etag");
            Assert.Equal("\"new-etag\"", await File.ReadAllTextAsync(eTagPath));
            if (OperatingSystem.IsWindows())
            {
                Assert.True((File.GetAttributes(eTagPath) & FileAttributes.Hidden) != 0);
            }

            Assert.Contains("\"Committed\":true", stdout.ToString(), StringComparison.Ordinal);
            Assert.Contains("\"etagFile\":", stdout.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_RepeatedFileOptionsPreserveOrderAndContent()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string firstPath = Path.Combine(directory, "title priv.xml");
            string secondPath = Path.Combine(directory, "nsal,custom.xml");
            await File.WriteAllTextAsync(firstPath, "<TitlePrivileges />");
            await File.WriteAllTextAsync(secondPath, "<ServiceNsal />");
            ParseResult parsed = Program.BuildRootCommand().Parse(
                ["xbl", "document", "commit", "--type", "account",
                    "--file", firstPath, "--file", secondPath, "--validate-only"],
                Program.NoResponseFileConfig);
            Assert.Empty(parsed.Errors);
            var fileOption = Assert.IsType<Option<string[]>>(
                Assert.Single(parsed.CommandResult.Command.Options, o => o.Name == "--file"));
            using var response = CreateResponse(
                """{"ValidationInfo":[],"CanCommit":true,"Committed":false}""");
            var handler = new MockHttpMessageHandler(response);
            using var client = CreateClient(handler);
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            int exitCode = await CommitDocumentsCommandFactory.RunAsync(
                client, 12345, "account", null, null, parsed.GetValue(fileOption)!,
                validateOnly: true, force: false, eTag: null, message: null,
                "json-compact", stdout, stderr, CancellationToken.None);

            Assert.Equal(0, exitCode);
            Assert.Empty(stderr.ToString());
            using JsonDocument body = JsonDocument.Parse(handler.LastRequestBody!);
            JsonElement documents = body.RootElement.GetProperty("documents");
            Assert.Equal(2, documents.GetArrayLength());
            Assert.Equal("title priv.xml", documents[0].GetProperty("name").GetString());
            Assert.Equal("<TitlePrivileges />", DecodeContent(documents[0].GetProperty("content").GetString()));
            Assert.Equal("nsal,custom.xml", documents[1].GetProperty("name").GetString());
            Assert.Equal("<ServiceNsal />", DecodeContent(documents[1].GetProperty("content").GetString()));
            Assert.True(body.RootElement.GetProperty("validateOnly").GetBoolean());
            using JsonDocument output = JsonDocument.Parse(stdout.ToString());
            Assert.Equal(
                new[] { firstPath, secondPath },
                output.RootElement.GetProperty("files").EnumerateArray().Select(file => file.GetString()));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_RejectsAnyUnmatchedFilePattern()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string validPath = Path.Combine(directory, "valid.xml");
            string missingPath = Path.Combine(directory, "missing.xml");
            await File.WriteAllTextAsync(validPath, "<Valid />");
            using var response = CreateResponse(
                """{"ValidationInfo":[],"CanCommit":true,"Committed":true}""");
            var handler = new MockHttpMessageHandler(response);
            using var client = CreateClient(handler);
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            int exitCode = await CommitDocumentsCommandFactory.RunAsync(
                client,
                12345,
                "sandbox",
                Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
                "XDKS.1",
                [validPath, missingPath],
                validateOnly: false,
                force: false,
                eTag: null,
                message: null,
                "json",
                stdout,
                stderr,
                CancellationToken.None);

            Assert.Equal(1, exitCode);
            Assert.Empty(stdout.ToString());
            Assert.Contains("No files matched --file value", stderr.ToString(), StringComparison.Ordinal);
            Assert.Contains("missing.xml", stderr.ToString(), StringComparison.Ordinal);
            Assert.Null(handler.LastRequest);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_ForceIgnoresExplicitAndSavedEtags()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string documentPath = Path.Combine(directory, "accountconfig.xml");
            await File.WriteAllTextAsync(documentPath, "<AccountConfig />");
            string eTagPath = Path.Combine(directory, "Account.etag");
            await File.WriteAllTextAsync(eTagPath, "\"saved-etag\"");
            using var response = CreateResponse(
                """{"ValidationInfo":[],"Version":7,"CanCommit":true,"Committed":true}""");
            var handler = new MockHttpMessageHandler(response);
            using var client = CreateClient(handler);
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            int exitCode = await CommitDocumentsCommandFactory.RunAsync(
                client,
                12345,
                "account",
                null,
                null,
                [documentPath],
                validateOnly: false,
                force: true,
                eTag: "\"explicit-etag\"",
                message: null,
                "json",
                stdout,
                stderr,
                CancellationToken.None);

            Assert.Equal(0, exitCode);
            using JsonDocument body = JsonDocument.Parse(handler.LastRequestBody!);
            Assert.True(body.RootElement.GetProperty("force").GetBoolean());
            Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("eTag").ValueKind);
            Assert.Equal("\"saved-etag\"", await File.ReadAllTextAsync(eTagPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("bad?.xml")]
    [InlineData("CON.xml")]
    [InlineData("folder/name.xml")]
    public void ValidateDocumentFileName_RejectsNonPortableNames(string name)
    {
        var exception = Assert.Throws<InvalidDataException>(
            () => CommitDocumentsCommandFactory.ValidateDocumentFileName(name));

        Assert.Contains("not supported by XCon", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WritesCommitSummaryAsTable()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string documentPath = Path.Combine(directory, "nsal.xml");
            await File.WriteAllTextAsync(documentPath, "<ServiceNsal />");
            using var response = CreateResponse(
                """{"ValidationInfo":[],"CanCommit":true,"Committed":false}""");
            using var client = CreateClient(new MockHttpMessageHandler(response));
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            int exitCode = await CommitDocumentsCommandFactory.RunAsync(
                client,
                12345,
                "sandbox",
                Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
                "XDKS.1",
                [documentPath],
                validateOnly: true,
                force: false,
                eTag: null,
                message: null,
                "table",
                stdout,
                stderr,
                CancellationToken.None);

            Assert.Equal(0, exitCode);
            Assert.Contains("TYPE", stdout.ToString(), StringComparison.Ordinal);
            Assert.Contains("VALIDATE ONLY", stdout.ToString(), StringComparison.Ordinal);
            Assert.Contains("CAN COMMIT", stdout.ToString(), StringComparison.Ordinal);
            Assert.Contains("XDKS.1", stdout.ToString(), StringComparison.Ordinal);
            Assert.Contains("True", stdout.ToString(), StringComparison.Ordinal);
            Assert.Empty(stderr.ToString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_RejectsDuplicateDocumentNamesBeforeCallingApi()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string firstDirectory = Path.Combine(directory, "first");
            string secondDirectory = Path.Combine(directory, "second");
            Directory.CreateDirectory(firstDirectory);
            Directory.CreateDirectory(secondDirectory);
            string firstPath = Path.Combine(firstDirectory, "nsal.xml");
            string secondPath = Path.Combine(secondDirectory, "nsal.xml");
            await File.WriteAllTextAsync(firstPath, "first");
            await File.WriteAllTextAsync(secondPath, "second");
            var handler = new MockHttpMessageHandler(CreateResponse("{}"));
            using var client = CreateClient(handler);
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            int exitCode = await CommitDocumentsCommandFactory.RunAsync(
                client,
                12345,
                "sandbox",
                Guid.NewGuid(),
                "ALL",
                [firstPath, secondPath],
                validateOnly: true,
                force: false,
                eTag: null,
                message: null,
                "json",
                stdout,
                stderr,
                CancellationToken.None);

            Assert.Equal(1, exitCode);
            Assert.Null(handler.LastRequest);
            Assert.Contains("Multiple selected files", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_ExplainsHowToIsolateInvalidXconDocumentName()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string firstPath = Path.Combine(directory, "accesspolicies.xml");
            string secondPath = Path.Combine(directory, "achievements2017.xml");
            await File.WriteAllTextAsync(firstPath, "<AccessPolicies />");
            await File.WriteAllTextAsync(secondPath, "<Achievements />");
            using var response = CreateResponse(
                """
                {
                  "status": 400,
                  "title": "Xbox downstream request failed.",
                  "detail": "XCon returned HTTP 400: Invalid Document Name."
                }
                """,
                HttpStatusCode.BadRequest);
            using var client = CreateClient(new MockHttpMessageHandler(response));
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            int exitCode = await CommitDocumentsCommandFactory.RunAsync(
                client,
                12345,
                "sandbox",
                Guid.NewGuid(),
                "ALL",
                [firstPath, secondPath],
                validateOnly: true,
                force: false,
                eTag: null,
                message: null,
                "json",
                stdout,
                stderr,
                CancellationToken.None);

            Assert.Equal(1, exitCode);
            Assert.Empty(stdout.ToString());
            using JsonDocument error = JsonDocument.Parse(stderr.ToString());
            string detail = error.RootElement.GetProperty("detail").GetString()!;
            Assert.Contains("'accesspolicies.xml'", detail, StringComparison.Ordinal);
            Assert.Contains("'achievements2017.xml'", detail, StringComparison.Ordinal);
            Assert.Contains("--validate-only", detail, StringComparison.Ordinal);
            Assert.Contains("--file <path>", detail, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_NamesSingleInvalidXconDocument()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string documentPath = Path.Combine(directory, "accesspolicies.xml");
            await File.WriteAllTextAsync(documentPath, "<AccessPolicies />");
            using var response = CreateResponse(
                """
                {
                  "status": 400,
                  "title": "Xbox downstream request failed.",
                  "detail": "XCon returned HTTP 400: Invalid Document Name."
                }
                """,
                HttpStatusCode.BadRequest);
            using var client = CreateClient(new MockHttpMessageHandler(response));
            using var stderr = new StringWriter();

            int exitCode = await CommitDocumentsCommandFactory.RunAsync(
                client,
                12345,
                "sandbox",
                Guid.NewGuid(),
                "ALL",
                [documentPath],
                validateOnly: true,
                force: false,
                eTag: null,
                message: null,
                "json",
                TextWriter.Null,
                stderr,
                CancellationToken.None);

            Assert.Equal(1, exitCode);
            using JsonDocument error = JsonDocument.Parse(stderr.ToString());
            string detail = error.RootElement.GetProperty("detail").GetString()!;
            Assert.Contains(
                "XCon rejected the selected document name 'accesspolicies.xml'",
                detail,
                StringComparison.Ordinal);
            Assert.Contains("--file <path>", detail, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_RejectsOversizedRawContentBeforeCallingApi()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string documentPath = Path.Combine(directory, "oversized.bin");
            await using (var stream = new FileStream(
                documentPath,
                FileMode.CreateNew,
                FileAccess.Write))
            {
                stream.SetLength(XboxUploadPolicy.MaxDocumentContentBytes + 1);
            }

            var handler = new MockHttpMessageHandler(CreateResponse("{}"));
            using var client = CreateClient(handler);
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            int exitCode = await CommitDocumentsCommandFactory.RunAsync(
                client,
                12345,
                "sandbox",
                Guid.NewGuid(),
                "ALL",
                [documentPath],
                validateOnly: true,
                force: false,
                eTag: null,
                message: null,
                "json",
                stdout,
                stderr,
                CancellationToken.None);

            Assert.Equal(1, exitCode);
            Assert.Null(handler.LastRequest);
            Assert.Contains("50 MiB", stderr.ToString(), StringComparison.Ordinal);
            Assert.Contains("generated XCon ZIP", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_PreflightsEtagDestinationBeforeCallingApi()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string documentPath = Path.Combine(directory, "nsal.xml");
            await File.WriteAllTextAsync(documentPath, "<ServiceNsal />");
            Directory.CreateDirectory(Path.Combine(directory, "ALL.etag"));
            var handler = new MockHttpMessageHandler(CreateResponse(
                """{"ValidationInfo":[],"CanCommit":true,"Committed":true}"""));
            using var client = CreateClient(handler);
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            int exitCode = await CommitDocumentsCommandFactory.RunAsync(
                client,
                12345,
                "sandbox",
                Guid.NewGuid(),
                "ALL",
                [documentPath],
                validateOnly: false,
                force: true,
                eTag: null,
                message: null,
                "json",
                stdout,
                stderr,
                CancellationToken.None);

            Assert.Equal(1, exitCode);
            Assert.Null(handler.LastRequest);
            Assert.Empty(stdout.ToString());
            Assert.Contains("existing directory", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_PreservesExistingEtagWhenReplacementFails()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = CreateTemporaryDirectory();
        try
        {
            string documentPath = Path.Combine(directory, "nsal.xml");
            await File.WriteAllTextAsync(documentPath, "<ServiceNsal />");
            string eTagPath = Path.Combine(directory, "ALL.etag");
            await File.WriteAllTextAsync(eTagPath, "\"old-etag\"");
            await using var lockStream = new FileStream(
                eTagPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            var handler = new MockHttpMessageHandler(CreateResponse(
                """{"ValidationInfo":[],"ETag":"\"new-etag\"","CanCommit":true,"Committed":true}"""));
            using var client = CreateClient(handler);
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            int exitCode = await CommitDocumentsCommandFactory.RunAsync(
                client,
                12345,
                "sandbox",
                Guid.NewGuid(),
                "ALL",
                [documentPath],
                validateOnly: false,
                force: true,
                eTag: null,
                message: null,
                "json",
                stdout,
                stderr,
                CancellationToken.None);

            Assert.Equal(1, exitCode);
            Assert.NotNull(handler.LastRequest);
            Assert.Empty(stdout.ToString());
            Assert.Equal("\"old-etag\"", await File.ReadAllTextAsync(eTagPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("not-json", "not valid JSON")]
    [InlineData("{}", "missing required fields")]
    [InlineData(
        """{"ValidationInfo":[],"CanCommit":true}""",
        "missing required fields")]
    public async Task RunAsync_RejectsMalformedSuccessResponse(
        string responseBody,
        string expectedError)
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string documentPath = Path.Combine(directory, "nsal.xml");
            await File.WriteAllTextAsync(documentPath, "<ServiceNsal />");
            using var client = CreateClient(
                new MockHttpMessageHandler(CreateResponse(responseBody)));
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            int exitCode = await CommitDocumentsCommandFactory.RunAsync(
                client,
                12345,
                "sandbox",
                Guid.NewGuid(),
                "ALL",
                [documentPath],
                validateOnly: true,
                force: false,
                eTag: null,
                message: null,
                "json",
                stdout,
                stderr,
                CancellationToken.None);

            Assert.Equal(1, exitCode);
            Assert.Empty(stdout.ToString());
            Assert.Contains(expectedError, stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(true, """{"ValidationInfo":[{"Severity":"Error","DocumentName":"achievements2017.xml","Error":"NamespaceDocTypeMismatch","Message":"The root namespace doesn't match the document type."},{"Severity":"Warning","Message":"Check the title."}],"CanCommit":false,"Committed":false}""", "XBOX Live can't commit these documents:")]
    [InlineData(false, """{"validationInfo":[{"severity":"Error","documentName":"achievements2017.xml","error":"NamespaceDocTypeMismatch","message":"The root namespace doesn't match the document type."},{"severity":"Warning","message":"Check the title."}],"canCommit":false,"committed":false}""", "XBOX Live didn't commit these documents:")]
    public async Task RunAsync_WritesValidationMessagesToStderrAndFails_WhenXconRefusesTheDocuments(
        bool validateOnly,
        string responseBody,
        string expectedSummary)
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string documentPath = Path.Combine(directory, "achievements2017.xml");
            await File.WriteAllTextAsync(documentPath, "<Achievements />");
            using var client = CreateClient(
                new MockHttpMessageHandler(CreateResponse(responseBody)));
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            int exitCode = await CommitDocumentsCommandFactory.RunAsync(
                client,
                12345,
                "sandbox",
                Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
                "XDKS.1",
                [documentPath],
                validateOnly,
                force: true,
                eTag: null,
                message: null,
                "json",
                stdout,
                stderr,
                CancellationToken.None);

            Assert.Equal(1, exitCode);
            using JsonDocument result = JsonDocument.Parse(stdout.ToString());
            Assert.False(result.RootElement.GetProperty("result").GetProperty("CanCommit").GetBoolean());
            Assert.Equal(
                [
                    expectedSummary,
                    "Error: achievements2017.xml: The root namespace doesn't match the document type. (NamespaceDocTypeMismatch)",
                    "Warning: Check the title.",
                ],
                stderr.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_KeepsTheSavedEtag_WhenXconRefusesTheDocuments()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string documentPath = Path.Combine(directory, "achievements2017.xml");
            await File.WriteAllTextAsync(documentPath, "<Achievements />");
            string eTagPath = Path.Combine(directory, "XDKS.1.etag");
            await File.WriteAllTextAsync(eTagPath, "\"saved-etag\"");
            using var client = CreateClient(new MockHttpMessageHandler(CreateResponse(
                """{"ValidationInfo":[{"Severity":"Error","Error":"NamespaceDocTypeMismatch","Message":"Mismatch."}],"CanCommit":false,"Committed":false,"ETag":"\"server-etag\""}""")));
            using var stdout = new StringWriter();

            int exitCode = await CommitDocumentsCommandFactory.RunAsync(
                client,
                12345,
                "sandbox",
                Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
                "XDKS.1",
                [documentPath],
                validateOnly: false,
                force: false,
                eTag: null,
                message: null,
                "json",
                stdout,
                TextWriter.Null,
                CancellationToken.None);

            Assert.Equal(1, exitCode);
            Assert.Equal("\"saved-etag\"", await File.ReadAllTextAsync(eTagPath));
            using JsonDocument result = JsonDocument.Parse(stdout.ToString());
            Assert.Equal(JsonValueKind.Null, result.RootElement.GetProperty("etagFile").ValueKind);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(true, """{"ValidationInfo":[],"CanCommit":false,"Committed":false}""", "XBOX Live can't commit these documents.")]
    [InlineData(false, """{"ValidationInfo":[],"CanCommit":true,"Committed":false}""", "XBOX Live didn't commit these documents.")]
    [InlineData(false, """{"ValidationInfo":[{"Severity":"Error","Error":"FilesNotChangedCommit","Message":"Unchanged."}],"CanCommit":false,"Committed":false}""", "XBOX Live didn't commit these documents:")]
    [InlineData(false, """{"ValidationInfo":[{"Severity":"Warning","Error":"FilesNotChangedCommit","Message":"No files changed."},{"Severity":"Error","Error":"SchemaError"}],"CanCommit":false,"Committed":false}""", "XBOX Live didn't commit these documents:")]
    [InlineData(true, """{"ValidationInfo":[{"Severity":"Error","Error":"SchemaError"}],"CanCommit":false,"Committed":false}""", "XBOX Live can't commit these documents:\nError: SchemaError")]
    [InlineData(true, """{"ValidationInfo":[{"Severity":"Error","Message":" "}],"CanCommit":false,"Committed":false}""", "XBOX Live can't commit these documents.")]
    public async Task RunAsync_FailsWithASummary_WhenXconRefusesTheDocumentsWithoutAnError(
        bool validateOnly,
        string responseBody,
        string expectedSummary)
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string documentPath = Path.Combine(directory, "nsal.xml");
            await File.WriteAllTextAsync(documentPath, "<ServiceNsal />");
            using var client = CreateClient(
                new MockHttpMessageHandler(CreateResponse(responseBody)));
            using var stderr = new StringWriter();

            int exitCode = await CommitDocumentsCommandFactory.RunAsync(
                client,
                12345,
                "sandbox",
                Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
                "XDKS.1",
                [documentPath],
                validateOnly,
                force: true,
                eTag: null,
                message: null,
                "table",
                TextWriter.Null,
                stderr,
                CancellationToken.None);

            Assert.Equal(1, exitCode);
            Assert.StartsWith(expectedSummary, stderr.ToString().ReplaceLineEndings("\n"), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_NeutralizesControlCharactersInValidationMessages()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string documentPath = Path.Combine(directory, "nsal.xml");
            await File.WriteAllTextAsync(documentPath, "<ServiceNsal />");
            using var client = CreateClient(new MockHttpMessageHandler(CreateResponse(
                """{"ValidationInfo":[{"Severity":"Error","DocumentName":"nsal\u001b[2J.xml","Error":"Bad\u202eKey","Message":"Line one\r\nWarning: forged line"}],"CanCommit":false,"Committed":false}""")));
            using var stderr = new StringWriter();

            int exitCode = await CommitDocumentsCommandFactory.RunAsync(
                client,
                12345,
                "sandbox",
                Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
                "XDKS.1",
                [documentPath],
                validateOnly: true,
                force: true,
                eTag: null,
                message: null,
                "json",
                TextWriter.Null,
                stderr,
                CancellationToken.None);

            Assert.Equal(1, exitCode);
            Assert.Equal(
                [
                    "XBOX Live can't commit these documents:",
                    "Error: nsal [2J.xml: Line one  Warning: forged line (Bad Key)",
                ],
                stderr.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(true, """{"ValidationInfo":[{"Severity":"Warning","Error":"FilesNotChangedCommit","Message":"No files changed."}],"CanCommit":false,"Committed":false}""")]
    [InlineData(false, """{"ValidationInfo":[{"Severity":"Warning","Error":"FilesNotChangedCommit","Message":"No files changed."}],"CanCommit":false,"Committed":false}""")]
    [InlineData(false, """{"ValidationInfo":[{"Severity":"Warning","Error":"FilesNotChangedCommit"}],"CanCommit":false,"Committed":false}""")]
    public async Task RunAsync_Succeeds_WhenTheDocumentsMatchTheLatestOnes(bool validateOnly, string responseBody)
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string documentPath = Path.Combine(directory, "nsal.xml");
            await File.WriteAllTextAsync(documentPath, "<ServiceNsal />");
            using var client = CreateClient(new MockHttpMessageHandler(CreateResponse(responseBody)));
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            int exitCode = await CommitDocumentsCommandFactory.RunAsync(
                client,
                12345,
                "sandbox",
                Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
                "XDKS.1",
                [documentPath],
                validateOnly,
                force: true,
                eTag: null,
                message: null,
                "json",
                stdout,
                stderr,
                CancellationToken.None);

            Assert.Equal(0, exitCode);
            Assert.Contains("FilesNotChangedCommit", stdout.ToString(), StringComparison.Ordinal);
            Assert.Empty(stderr.ToString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static XboxPartnerClient CreateClient(HttpMessageHandler handler) =>
        new(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["XboxPartner:BaseUrl"] = "http://localhost:8080",
                })
                .Build(),
            new HttpClient(handler),
            () => Task.FromResult<string?>("caller-token"));

    private static HttpResponseMessage CreateResponse(
        string body,
        HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(statusCode)
        {
            Content = new StringContent(body),
        };

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"XboxDevCLI-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string DecodeContent(string? base64) =>
        Encoding.UTF8.GetString(Convert.FromBase64String(base64!));
}
