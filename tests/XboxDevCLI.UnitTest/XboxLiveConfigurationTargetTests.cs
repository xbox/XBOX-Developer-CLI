// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Commands;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

[Collection("ConsoleRedirect")]
public sealed class XboxLiveConfigurationTargetTests : IDisposable
{
    private const string Scid = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private static readonly SignedInIdentity Identity = new("tenant", "user");
    private static readonly string[] Variables =
        ["XBOX_TITLE_ID", "XBOX_SANDBOX_ID", "XBOX_SELLER_ID", "XBOX_PROFILE", "XBOX_XBOXPARTNER__MAXRESPONSEBYTES"];
    private readonly Dictionary<string, string?> _original = Variables.ToDictionary(
        name => name, Environment.GetEnvironmentVariable);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"xbox-target-{Guid.NewGuid():N}");

    public XboxLiveConfigurationTargetTests()
    {
        foreach (var name in Variables)
        {
            Environment.SetEnvironmentVariable(name, null);
        }

        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "nsal.xml"), "<ServiceNsal />");
        ContextStore.DefaultDirectory = () => Path.Combine(_directory, "state");
        ContextResolver.ReadIdentityAsync = _ => Task.FromResult<SignedInIdentity?>(Identity);
    }

    public void Dispose()
    {
        foreach (var (name, value) in _original)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        Program.XboxPartnerClientFactory = null;
        TestEnvironment.Reset();
        Directory.Delete(_directory, recursive: true);
    }

    public static TheoryData<string, string> Commands => new()
    {
        { "config", "list" }, { "config", "status" },
        { "config", "download" }, { "config", "save" },
        { "config", "publish" },
    };

    [Theory]
    [MemberData(nameof(Commands))]
    public async Task BoundTitle_AllCommandsSendOneOperationAndReportEffectiveResponseTarget(string family, string action)
    {
        var handler = Handler(action, Target(Operation(family, action), bound: true));
        var result = await InvokeAsync(handler, [.. Arguments(family, action),
            "--title-id", "0x42", "--seller-id", "1234567", "--verbose", "--output", "table"]);

        Assert.True(result.ExitCode == 0, result.Stderr);
        AssertTitleOperation(OperationRequest(handler, action), family, action, "00000042", 1234567);
        Assert.Contains("Effective configuration target:", result.Stderr);
        Assert.Contains("title 00000042, primary title 00000043, SCID " + Scid, result.Stderr);
        Assert.DoesNotContain("resolvedTitleId", result.Stdout);
        if (action is "download" or "save")
        {
            Assert.True(File.Exists(Path.Combine(_directory, "XDKS.1.etag")));
        }
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public async Task EnvironmentTitle_AllCommandsSendTitleWithoutInventingSellerOrScid(string family, string action)
    {
        Environment.SetEnvironmentVariable("XBOX_TITLE_ID", "42");
        var handler = Handler(action, Target(Operation(family, action)));

        var result = await InvokeAsync(handler, Arguments(family, action));

        Assert.True(result.ExitCode == 0, result.Stderr);
        AssertTitleOperation(OperationRequest(handler, action), family, action, "00000042", null);
        if (action is "save" or "publish")
        {
            Assert.Contains("XBOX_TITLE_ID", result.Stderr);
            Assert.Contains("only authorized seller", result.Stderr);
        }
        else
        {
            Assert.DoesNotContain("SCID", result.Stderr);
        }
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public async Task SavedTitle_AllCommandsUseContextWithoutRewritingIt(string family, string action)
    {
        string gateway = TestSettings.BuiltIn("XboxPartner:BaseUrl")!;
        await new ContextStore().UpdateAsync(Identity, [new ContextChange("title-id", gateway, "42")], "default");
        var handler = Handler(action, Target(Operation(family, action), bound: true));

        var result = await InvokeAsync(handler, Arguments(family, action));

        Assert.True(result.ExitCode == 0, result.Stderr);
        AssertTitleOperation(OperationRequest(handler, action), family, action, "00000042", null);
        Assert.Equal("42", await new ContextStore().GetAsync(Identity, "title-id", gateway, "default", CancellationToken.None));
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public async Task ExplicitTitle_AllCommandsOverrideEnvironmentAndKeepSelectedSeller(string family, string action)
    {
        Environment.SetEnvironmentVariable("XBOX_TITLE_ID", "invalid");
        Environment.SetEnvironmentVariable("XBOX_SELLER_ID", "1234567");
        var handler = Handler(action, Target(Operation(family, action)));

        var result = await InvokeAsync(handler, [.. Arguments(family, action), "--title-id", "42"]);

        Assert.True(result.ExitCode == 0, result.Stderr);
        AssertTitleOperation(OperationRequest(handler, action), family, action, "00000042", 1234567);
        if (action is "save" or "publish")
        {
            Assert.Contains("XBOX_SELLER_ID", result.Stderr);
            Assert.DoesNotContain("only authorized seller", result.Stderr);
        }
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public async Task DirectScid_AllCommandsBypassInvalidTitleContext(string family, string action)
    {
        Environment.SetEnvironmentVariable("XBOX_TITLE_ID", "not-a-title");
        var handler = Handler(action, Operation(family, action));

        var result = await InvokeAsync(handler, [.. Arguments(family, action), "--scid", Scid]);

        Assert.True(result.ExitCode == 0, result.Stderr);
        var request = OperationRequest(handler, action);
        Assert.DoesNotContain("XBOX_TITLE_ID", result.Stderr);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal(Scid, body.RootElement.GetProperty("scid").GetString());
        AssertNullOrAbsent(body.RootElement, "titleId");
        AssertNullOrAbsent(body.RootElement, "sellerId");
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public async Task ConflictingSelectors_AllCommandsRejectBeforeNetwork(string family, string action)
    {
        var handler = new XboxPartnerHttpMessageHandler(Ok("{}"));
        var result = await InvokeAsync(handler, [.. Arguments(family, action),
            "--scid", Scid, "--title-id", "42"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("cannot be combined", result.Stderr);
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public async Task MissingTarget_AllCommandsFailBeforeNetwork(string family, string action)
    {
        var handler = new XboxPartnerHttpMessageHandler(Ok("{}"));
        var result = await InvokeAsync(handler, Arguments(family, action));
        Assert.Equal(1, result.ExitCode);
        Assert.Empty(handler.Requests);
        Assert.Empty(result.Stdout);
        Assert.Contains("No title-id is set. Pass --title-id, set XBOX_TITLE_ID", result.Stderr);
        Assert.Contains("'xbox context set title-id <value>'", result.Stderr);
    }

    [Fact]
    public async Task MissingTarget_SuggestsSettingTitleOnSelectedProfile()
    {
        await new ContextStore().UpdateAsync(
            Identity, [new ContextChange("seller-id", "https://other.example", "1234567")], "staging");
        var handler = new XboxPartnerHttpMessageHandler(Ok("{}"));

        var result = await InvokeAsync(handler, ["--profile", "staging", "xbl", "config", "list"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("'xbox --profile staging context set title-id <value>'", result.Stderr);
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public async Task InvalidTitles_AllCommandsRejectWithoutAnyRequest(string family, string action)
    {
        foreach (string title in new[] { "", " ", "xyz", "-1", "0", "80000000", "FFFFFFFF" })
        {
            var handler = new XboxPartnerHttpMessageHandler(Operation(family, action));

            var result = await InvokeAsync(handler, [.. Arguments(family, action), "--title-id", title]);

            Assert.Equal(1, result.ExitCode);
            Assert.Empty(handler.Requests);
            Assert.NotEmpty(result.Stderr);
        }
    }

    [Theory]
    [InlineData("json")]
    [InlineData("json-compact")]
    [InlineData("table")]
    public async Task OutputFormats_ContainOnlyFinalOperationResult(string format)
    {
        var handler = Handler("status", Target(Operation("config", "status"), bound: true));
        var result = await InvokeAsync(handler, [.. Arguments("config", "status"),
            "--title-id", "42", "--output", format]);
        Assert.True(result.ExitCode == 0, result.Stderr);
        Assert.Contains("Pending", result.Stdout);
        Assert.DoesNotContain("resolvedTitleId", result.Stdout);
        Assert.DoesNotContain("primary title", result.Stdout);
        if (format != "table")
        {
            using var body = JsonDocument.Parse(result.Stdout);
            Assert.Equal("Pending", body.RootElement.GetProperty("lastPublish").GetProperty("status").GetString());
        }
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public async Task OperationFailure_AllCommandsPreserveCorrelationAndNeverRetry(string family, string action)
    {
        var error = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("""{"code":"denied","detail":"Access denied"}"""),
        };
        error.Headers.Add("x-ms-correlation-id", "lookup-correlation");
        var handler = new XboxPartnerHttpMessageHandler(error);

        var result = await InvokeAsync(handler, [.. Arguments(family, action), "--title-id", "42"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        AssertTitleOperation(Assert.Single(handler.Requests), family, action, "00000042", null);
        Assert.Contains("lookup-correlation", result.Stderr);
        Assert.Contains("denied", result.Stderr);
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public async Task MissingTargetHeaders_AllCommandsPreserveCorrelationAndNeverReplay(string family, string action)
    {
        var response = Operation(family, action);
        response.Headers.Add("x-ms-correlation-id", "malformed-target");
        var handler = new XboxPartnerHttpMessageHandler(response);
        var result = await InvokeAsync(handler, [.. Arguments(family, action), "--title-id", "42"]);

        Assert.Equal(WriteFailureExitCode(family, action), result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Single(handler.Requests);
        Assert.Contains("malformed-target", result.Stderr);
        Assert.DoesNotContain("Effective configuration target:", result.Stderr);
        if (action is "download" or "save")
        {
            Assert.False(File.Exists(Path.Combine(_directory, "XDKS.1.etag")));
            Assert.Equal("<ServiceNsal />", await File.ReadAllTextAsync(Path.Combine(_directory, "nsal.xml")));
        }
    }

    [Theory]
    [InlineData("X-XboxPartner-Scid", "bad")]
    [InlineData("X-XboxPartner-Scid", "00000000-0000-0000-0000-000000000000")]
    public async Task InvalidScidHeader_PreservesCorrelationAndNeverReplaysWrite(string header, string value)
    {
        var response = Target(Operation("config", "publish"));
        response.Headers.Remove(header);
        response.Headers.TryAddWithoutValidation(header, value);
        response.Headers.Add("x-ms-correlation-id", "bad-target");
        var handler = new XboxPartnerHttpMessageHandler(response);
        var result = await InvokeAsync(handler, [.. Arguments("config", "publish"), "--title-id", "42"]);

        Assert.Equal(PublishCommandFactory.OutcomeUnknownExitCode, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Single(handler.Requests);
        Assert.Contains("bad-target", result.Stderr);
    }

    [Theory]
    [InlineData("X-XboxPartner-Seller-Id", "0")]
    [InlineData("X-XboxPartner-Seller-Id", "-1")]
    [InlineData("X-XboxPartner-Seller-Id", "seller")]
    [InlineData("X-XboxPartner-Resolved-Title-Id", "00000000")]
    [InlineData("X-XboxPartner-Resolved-Title-Id", "FFFFFFFF")]
    [InlineData("X-XboxPartner-Resolved-Title-Id", "42")]
    public async Task InvalidDisplayHeaders_AreIgnored(string header, string value)
    {
        var response = Target(Operation("config", "publish"));
        response.Headers.Remove(header);
        response.Headers.TryAddWithoutValidation(header, value);
        var handler = new XboxPartnerHttpMessageHandler(response);
        var result = await InvokeAsync(handler, [.. Arguments("config", "publish"), "--title-id", "42"]);

        Assert.True(result.ExitCode == 0, result.Stderr);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ReturnedSellerCannotOverrideSelectedSeller()
    {
        var handler = new XboxPartnerHttpMessageHandler(Target(Operation("config", "publish")));
        var result = await InvokeAsync(handler, [.. Arguments("config", "publish"),
            "--title-id", "42", "--seller-id", "7654321", "--verbose"]);
        Assert.True(result.ExitCode == 0, result.Stderr);
        AssertTitleOperation(Assert.Single(handler.Requests), "config", "publish", "00000042", 7654321);
        Assert.Contains("seller 7654321", result.Stderr);
        Assert.DoesNotContain("seller 1234567", result.Stderr);
    }

    [Fact]
    public void Describe_WithoutSellerUsesSameTermAsResolvedOutput()
    {
        var target = new XboxLiveConfigurationTarget.SelectedTarget(null, null, "0000002A");

        Assert.Equal("seller (only authorized seller), title 0000002A", target.Describe());
    }

    [Fact]
    public async Task ConfigurationList_LaterPagesKeepFirstPageTargetWithoutMetadata()
    {
        var handler = new XboxPartnerHttpMessageHandler(
            Target(Ok("""{"configurations":[{"sandbox":"XDKS.1"}],"continuationToken":"next"}""")),
            Ok("""{"configurations":[{"sandbox":"TESTSB.42"}],"continuationToken":"third"}"""),
            Ok("""{"configurations":[{"sandbox":"RETAIL"}]}"""));

        var result = await InvokeAsync(handler, [.. Arguments("config", "list"), "--title-id", "42"]);

        Assert.True(result.ExitCode == 0, result.Stderr);
        Assert.Equal(3, handler.Requests.Count);
        AssertOperationTarget(handler.Requests[1], sandbox: false);
        AssertOperationTarget(handler.Requests[2], sandbox: false);
        using var output = JsonDocument.Parse(result.Stdout);
        Assert.Equal(3, output.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task ConfigurationList_FirstPageSendsTitleThenPinsScidAndActualSeller()
    {
        var handler = new XboxPartnerHttpMessageHandler(
            Target(Ok("""{"configurations":[{"sandbox":"XDKS.1"}],"continuationToken":"next"}"""), bound: true),
            ScidTarget(Ok("""{"configurations":[{"sandbox":"TESTSB.42"}]}""")));

        var result = await InvokeAsync(handler, [.. Arguments("config", "list"), "--title-id", "42"]);

        Assert.True(result.ExitCode == 0, result.Stderr);
        Assert.Equal(2, handler.Requests.Count);
        AssertTitleOperation(handler.Requests[0], "config", "list", "00000042", null);
        AssertOperationTarget(handler.Requests[1], sandbox: false);
        using var first = JsonDocument.Parse(handler.Requests[0].Body!);
        Assert.Equal(XboxPartnerClient.ListPageSize, first.RootElement.GetProperty("pageSize").GetInt32());
        Assert.False(first.RootElement.TryGetProperty("continuationToken", out var firstToken)
            && firstToken.ValueKind != JsonValueKind.Null);
        using var second = JsonDocument.Parse(handler.Requests[1].Body!);
        AssertNullOrAbsent(second.RootElement, "titleId");
        AssertNullOrAbsent(second.RootElement, "pageSize");
        Assert.Equal("next", second.RootElement.GetProperty("continuationToken").GetString());
        Assert.Equal(Assert.Single(handler.Requests[0].Headers["x-ms-correlation-id"]),
            Assert.Single(handler.Requests[1].Headers["x-ms-correlation-id"]));
        using var output = JsonDocument.Parse(result.Stdout);
        Assert.Equal(2, output.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task ConfigurationList_InvalidFirstPageMetadataNeverFollowsContinuation()
    {
        var handler = new XboxPartnerHttpMessageHandler(
            Ok("""{"configurations":[{"sandbox":"XDKS.1"}],"continuationToken":"next"}"""),
            Operation("config", "list"));

        var result = await InvokeAsync(handler, [.. Arguments("config", "list"), "--title-id", "42"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Single(handler.Requests);
        Assert.Empty(result.Stdout);
    }

    [Fact]
    public async Task ConfigurationList_ContinuationDenialNeverResolvesAgainOrPrintsPartialOutput()
    {
        var denied = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("""{"code":"denied","detail":"Access denied"}"""),
        };
        denied.Headers.Add("x-ms-correlation-id", "page-denied");
        var handler = new XboxPartnerHttpMessageHandler(
            Target(Ok("""{"configurations":[{"sandbox":"XDKS.1"}],"continuationToken":"next"}""")),
            denied);

        var result = await InvokeAsync(handler, [.. Arguments("config", "list"), "--title-id", "42"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(2, handler.Requests.Count);
        AssertOperationTarget(handler.Requests[1], sandbox: false);
        Assert.Empty(result.Stdout);
        Assert.Contains("page-denied", result.Stderr);
    }

    [Fact]
    public async Task ConfigurationList_LegacyScidPagingDoesNotRequireTargetHeaders()
    {
        Environment.SetEnvironmentVariable("XBOX_TITLE_ID", "invalid");
        var handler = new XboxPartnerHttpMessageHandler(
            Ok("""{"configurations":[{"sandbox":"XDKS.1"}],"continuationToken":"next"}"""),
            Operation("config", "list"));

        var result = await InvokeAsync(handler, [.. Arguments("config", "list"), "--scid", Scid]);

        Assert.True(result.ExitCode == 0, result.Stderr);
        Assert.Equal(2, handler.Requests.Count);
        foreach (var request in handler.Requests)
        {
            using var body = JsonDocument.Parse(request.Body!);
            Assert.Equal(Scid, body.RootElement.GetProperty("scid").GetString());
            AssertNullOrAbsent(body.RootElement, "titleId");
        }
        using var second = JsonDocument.Parse(handler.Requests[1].Body!);
        Assert.Equal("next", second.RootElement.GetProperty("continuationToken").GetString());
        Assert.Equal(Assert.Single(handler.Requests[0].Headers["x-ms-correlation-id"]),
            Assert.Single(handler.Requests[1].Headers["x-ms-correlation-id"]));
        using var output = JsonDocument.Parse(result.Stdout);
        Assert.Equal(2, output.RootElement.GetArrayLength());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ConfigSave_PreservesValidateForceEtagMessageAndDocumentBytes(bool validateOnly, bool force)
    {
        var handler = new XboxPartnerHttpMessageHandler(Target(Operation("config", "save"), bound: true));
        string etagPath = Path.Combine(_directory, "XDKS.1.etag");
        await File.WriteAllTextAsync(etagPath, "\"saved\"");
        var args = Arguments("config", "save").Concat(
            new[] { "--title-id", "42", "--message", "Update configuration", "--verbose" }).ToList();
        if (validateOnly) args.Add("--validate-only");
        if (force) args.Add("--force");

        var result = await InvokeAsync(handler, [.. args]);

        Assert.True(result.ExitCode == 0, result.Stderr);
        AssertTitleOperation(Assert.Single(handler.Requests), "config", "save", "00000042", null);
        using var body = JsonDocument.Parse(handler.Requests[0].Body!);
        Assert.Equal(validateOnly, body.RootElement.GetProperty("validateOnly").GetBoolean());
        Assert.Equal(force, body.RootElement.GetProperty("force").GetBoolean());
        Assert.Equal(force ? null : "\"saved\"", body.RootElement.GetProperty("eTag").GetString());
        Assert.Equal("Update configuration", body.RootElement.GetProperty("message").GetString());
        Assert.Equal("<ServiceNsal />", System.Text.Encoding.UTF8.GetString(
            body.RootElement.GetProperty("documents")[0].GetProperty("content").GetBytesFromBase64()));
        Assert.Contains(validateOnly ? "Validate" : "Save", result.Stderr);
        Assert.Contains("primary title 00000043", result.Stderr);
        Assert.Equal("\"etag\"", await File.ReadAllTextAsync(etagPath));
    }

    [Fact]
    public async Task ConfigSave_InvalidMetadataDoesNotReplaceSavedEtag()
    {
        string etagPath = Path.Combine(_directory, "XDKS.1.etag");
        await File.WriteAllTextAsync(etagPath, "\"saved\"");
        var handler = new XboxPartnerHttpMessageHandler(Operation("config", "save"));

        var result = await InvokeAsync(handler, [.. Arguments("config", "save"), "--title-id", "42"]);

        Assert.Equal(CommitDocumentsCommandFactory.OutcomeUnknownExitCode, result.ExitCode);
        Assert.Single(handler.Requests);
        Assert.Empty(result.Stdout);
        Assert.Equal("\"saved\"", await File.ReadAllTextAsync(etagPath));
    }

    [Fact]
    public async Task ConfigDownload_InvalidMetadataDoesNotExtractFilesOrEtag()
    {
        string destination = Path.Combine(_directory, "download");
        var handler = new XboxPartnerHttpMessageHandler(Operation("config", "download"));

        var result = await InvokeAsync(handler, "xbl", "config", "download",
            "--title-id", "42", "--sandbox-id", "XDKS.1", "--view", "draft", "--dest", destination);

        Assert.Equal(1, result.ExitCode);
        Assert.Single(handler.Requests);
        Assert.Empty(result.Stdout);
        Assert.False(File.Exists(Path.Combine(destination, "nsal.xml")));
        Assert.False(File.Exists(Path.Combine(destination, "XDKS.1.etag")));
    }

    [Fact]
    public async Task ConfigDownload_PreservesPublishedViewVersionAndFilters()
    {
        var handler = new XboxPartnerHttpMessageHandler(Target(Operation("config", "download"), bound: true));
        var result = await InvokeAsync(handler, [.. Arguments("config", "download", view: "published"), "--title-id", "42",
            "--version", "7", "--document", "nsal.xml"]);

        Assert.True(result.ExitCode == 0, result.Stderr);
        AssertTitleOperation(Assert.Single(handler.Requests), "config", "download", "00000042", null);
        using var body = JsonDocument.Parse(handler.Requests[0].Body!);
        Assert.Equal("published", body.RootElement.GetProperty("view").GetString());
        Assert.Equal(7, body.RootElement.GetProperty("version").GetInt32());
        Assert.Equal("nsal.xml", body.RootElement.GetProperty("documents")[0].GetString());
        Assert.Equal("<ServiceNsal />", await File.ReadAllTextAsync(Path.Combine(_directory, "nsal.xml")));
        Assert.Equal("\"etag\"", await File.ReadAllTextAsync(Path.Combine(_directory, "XDKS.1.etag")));
        using var output = JsonDocument.Parse(result.Stdout);
        Assert.Contains("published", output.RootElement.GetRawText());
    }

    [Theory]
    [InlineData("download")]
    [InlineData("save")]
    public async Task AccountDocuments_IgnoreAmbientTitleAndNeverResolve(string action)
    {
        Environment.SetEnvironmentVariable("XBOX_TITLE_ID", "invalid");
        var handler = new XboxPartnerHttpMessageHandler(Operation("config", action));
        var args = Arguments("config", action).ToList();
        int sandbox = args.IndexOf("--sandbox-id");
        args.RemoveRange(sandbox, 2);

        var result = await InvokeAsync(handler, [.. args, "--type", "ACCOUNT"]);

        Assert.True(result.ExitCode == 0, result.Stderr);
        var request = Assert.Single(handler.Requests);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("scid").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("sandbox").ValueKind);
        AssertNullOrAbsent(body.RootElement, "titleId");
        Assert.DoesNotContain("XBOX_TITLE_ID", result.Stderr);
    }

    [Theory]
    [InlineData("download")]
    [InlineData("save")]
    public async Task AccountDocuments_RejectExplicitTitle(string action)
    {
        var args = Arguments("config", action).ToList();
        args.RemoveRange(args.IndexOf("--sandbox-id"), 2);
        var handler = new XboxPartnerHttpMessageHandler(Ok("{}"));
        var result = await InvokeAsync(handler, [.. args, "--type", "account", "--title-id", "42"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("--title-id cannot be used with account", result.Stderr);
    }

    [Theory]
    [InlineData("download")]
    [InlineData("save")]
    public async Task AccountDocuments_RejectExplicitScid(string action)
    {
        var args = Arguments("config", action).ToList();
        args.RemoveRange(args.IndexOf("--sandbox-id"), 2);
        var handler = new XboxPartnerHttpMessageHandler(Operation("config", action));

        var result = await InvokeAsync(handler, [.. args, "--type", "account", "--scid", Scid]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("--scid", result.Stderr);
    }

    [Fact]
    public async Task Schemas_IgnoreAmbientTitle()
    {
        Environment.SetEnvironmentVariable("XBOX_TITLE_ID", "invalid");
        var handler = new XboxPartnerHttpMessageHandler(Ok("[]"));
        var result = await InvokeAsync(handler, "xbl", "schema", "list");
        Assert.True(result.ExitCode == 0, result.Stderr);
        Assert.EndsWith("/schemas", Assert.Single(handler.Requests).RequestUri!.AbsolutePath);
    }

    [Theory]
    [InlineData("1", "00000001")]
    [InlineData("42", "00000042")]
    [InlineData("0xabcdef", "00ABCDEF")]
    [InlineData("7fffffff", "7FFFFFFF")]
    public async Task HexadecimalTitle_IsNormalizedInOperationRequest(string input, string normalized)
    {
        var handler = Handler("status", Target(Operation("config", "status"), title: normalized));
        var result = await InvokeAsync(handler, [.. Arguments("config", "status"), "--title-id", input]);
        Assert.True(result.ExitCode == 0, result.Stderr);
        AssertTitleOperation(OperationRequest(handler, "status"), "config", "status", normalized, null);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("80000000")]
    [InlineData("FFFFFFFF")]
    public async Task UnsupportedLookupRange_DoesNotSendOrWrap(string title)
    {
        var handler = new XboxPartnerHttpMessageHandler(Ok("{}"));
        var result = await InvokeAsync(handler, [.. Arguments("config", "publish"), "--title-id", title]);
        Assert.Equal(1, result.ExitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("7FFFFFFF", result.Stderr);
        Assert.Contains("--scid", result.Stderr);
    }

    [Theory]
    [InlineData("xbl", "config", "publish", "--title-id", "")]
    [InlineData("xbl", "config", "status", "--title-id", "xyz", "--sandbox-id", "XDKS.1")]
    [InlineData("xbl", "config", "publish", "--title-id", "42")]
    [InlineData("xbl", "config", "status", "--sandbox-id", "XDKS.1")]
    [InlineData("xbl", "config", "list", "--scid", "00000000-0000-0000-0000-000000000000")]
    public async Task InvalidInput_NeverSendsRequests(params string[] args)
    {
        var handler = new XboxPartnerHttpMessageHandler(Ok("{}"));
        var result = await InvokeAsync(handler, args);
        Assert.Equal(1, result.ExitCode);
        Assert.Empty(handler.Requests);
        Assert.NotEmpty(result.Stderr);
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public async Task Help_DoesNotResolveTargets(string family, string action)
    {
        Environment.SetEnvironmentVariable("XBOX_TITLE_ID", "invalid");
        var handler = new XboxPartnerHttpMessageHandler(Ok("{}"));
        var result = await InvokeAsync(handler, "xbl", family, action, "--help");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("--title-id", result.Stdout);
        Assert.Contains("bound title",
            string.Join(" ", result.Stdout.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task OperationAuthorizationDenial_IsNotRetriedWithOtherSelectors()
    {
        var handler = new XboxPartnerHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("""{"code":"denied"}""") });
        var result = await InvokeAsync(handler, [.. Arguments("config", "publish"), "--title-id", "42"]);
        Assert.Equal(1, result.ExitCode);
        AssertTitleOperation(Assert.Single(handler.Requests), "config", "publish", "00000042", null);
        Assert.Empty(result.Stdout);
        Assert.Contains("denied", result.Stderr);
    }

    [Fact]
    public async Task CrossSandboxPublish_PreservesDefaultsAndValidation()
    {
        var handler = new XboxPartnerHttpMessageHandler(Target(Operation("config", "publish"), bound: true));
        var result = await InvokeAsync(handler, "xbl", "config", "publish", "--title-id", "42",
            "--source-sandbox-id", "TESTSB.42", "--sandbox-id", "RETAIL", "--validate-only", "--configset", "42",
            "--verbose");

        Assert.True(result.ExitCode == 0, result.Stderr);
        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body!);
        Assert.False(body.RootElement.TryGetProperty("sourceView", out _));
        Assert.Equal("TESTSB.42", body.RootElement.GetProperty("sourceSandbox").GetString());
        Assert.True(body.RootElement.GetProperty("validateOnly").GetBoolean());
        Assert.Equal(42UL, body.RootElement.GetProperty("configSetVersion").GetUInt64());
        Assert.Contains("from TESTSB.42 (working version 42) to RETAIL", result.Stderr);
    }

    [Fact]
    public async Task SameSandboxPublish_PreservesWorkingLatestDefaults()
    {
        var handler = new XboxPartnerHttpMessageHandler(Target(Operation("config", "publish")));

        var result = await InvokeAsync(handler, [.. Arguments("config", "publish"), "--title-id", "42"]);

        Assert.True(result.ExitCode == 0, result.Stderr);
        AssertTitleOperation(Assert.Single(handler.Requests), "config", "publish", "00000042", null);
        using var body = JsonDocument.Parse(handler.Requests[0].Body!);
        Assert.Equal("XDKS.1", body.RootElement.GetProperty("sourceSandbox").GetString());
        Assert.Equal("XDKS.1", body.RootElement.GetProperty("sandbox").GetString());
        Assert.False(body.RootElement.TryGetProperty("sourceView", out _));
        Assert.False(body.RootElement.GetProperty("validateOnly").GetBoolean());
        AssertNullOrAbsent(body.RootElement, "configSetVersion");
    }

    [Theory]
    [InlineData("config", "publish")]
    [InlineData("config", "save")]
    public async Task Mutations_SharedConfigurationRefusalWithoutTerminalSuggestsOptIn(string family, string action)
    {
        var handler = new XboxPartnerHttpMessageHandler(SharedConfigurationConflict());

        var result = await InvokeAsync(handler, [.. Arguments(family, action), "--scid", Scid, "--seller-id", "1234567"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Single(handler.Requests);
        Assert.Empty(result.Stdout);
        Assert.Contains($"This configuration (SCID {Scid}, title 00000043) is shared by 2 bound titles: 00000042, 00000044.", result.Stderr);
        Assert.Contains(XboxLiveConfigurationTarget.AllowSharedConfigurationHint, result.Stderr);
        Assert.Contains("409", result.Stderr);
        Assert.Contains(XboxPartnerCommandErrorFormatter.SharedConfigurationRequiresConfirmationCode, result.Stderr);
        Assert.Contains(ConflictCorrelationId, result.Stderr);
        Assert.DoesNotContain("outcome is unknown", result.Stderr);
        Assert.False(File.Exists(Path.Combine(_directory, "XDKS.1.etag")));
        using var body = JsonDocument.Parse(handler.Requests[0].Body!);
        AssertNullOrAbsent(body.RootElement, "allowSharedConfiguration");
    }

    [Theory]
    [InlineData("config", "publish")]
    [InlineData("config", "save")]
    public async Task Mutations_AllowSharedConfigurationOptsInOnTheFirstRequest(string family, string action)
    {
        var handler = new XboxPartnerHttpMessageHandler(ScidTarget(Operation(family, action)));

        var result = await InvokeAsync(handler,
            [.. Arguments(family, action), "--scid", Scid, "--seller-id", "1234567", "--allow-shared-configuration"]);

        Assert.True(result.ExitCode == 0, result.Stderr);
        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body!);
        Assert.True(body.RootElement.GetProperty("allowSharedConfiguration").GetBoolean());
    }

    [Theory]
    [InlineData("config", "publish", "y", 0, 2)]
    [InlineData("config", "save", "y", 0, 2)]
    [InlineData("config", "publish", "n", 1, 1)]
    [InlineData("config", "save", "n", 1, 1)]
    [InlineData("config", "publish", null, 1, 1)]
    public async Task Mutations_SharedConfigurationPromptResendsOnlyWhenConfirmed(
        string family, string action, string? answer, int exitCode, int requests)
    {
        var handler = new XboxPartnerHttpMessageHandler(
            SharedConfigurationConflict(), ScidTarget(Operation(family, action)));
        int reads = 0;
        var prompt = new PublishConfirmationDependencies(
            () => true,
            _ => { reads++; return Task.FromResult(answer); });

        var result = await InvokeWithPromptAsync(handler, prompt,
            [.. Arguments(family, action)[1..], "--scid", Scid, "--seller-id", "1234567",
                .. action == "publish" ? new[] { "--yes" } : []]);

        Assert.True(result.ExitCode == exitCode, result.Stderr);
        Assert.Equal(1, reads);
        Assert.Equal(requests, handler.Requests.Count);
        Assert.Contains("is shared by 2 bound titles", result.Stderr);
        Assert.Contains("Change the shared configuration? [y/N]:", result.Stderr);
        using var first = JsonDocument.Parse(handler.Requests[0].Body!);
        AssertNullOrAbsent(first.RootElement, "allowSharedConfiguration");
        if (requests == 2)
        {
            using var second = JsonDocument.Parse(handler.Requests[1].Body!);
            Assert.True(second.RootElement.GetProperty("allowSharedConfiguration").GetBoolean());
        }
        else
        {
            Assert.Contains("Canceled. Nothing was changed.", result.Stderr);
            Assert.Contains($"{XboxPartnerClient.CorrelationIdHeader}: {ConflictCorrelationId}", result.Stderr);
        }
    }

    [Theory]
    [InlineData("config", "publish")]
    [InlineData("config", "save")]
    public async Task Mutations_SharedConfigurationPromptSaysValidateOnlySavesNothing(string family, string action)
    {
        var handler = new XboxPartnerHttpMessageHandler(
            SharedConfigurationConflict(), ScidTarget(Operation(family, action)));
        var prompt = new PublishConfirmationDependencies(() => true, _ => Task.FromResult<string?>("n"));

        var result = await InvokeWithPromptAsync(handler, prompt,
            [.. Arguments(family, action)[1..], "--scid", Scid, "--seller-id", "1234567", "--validate-only",
                .. action == "publish" ? new[] { "--yes" } : []]);

        Assert.True(result.ExitCode == 1, result.Stderr);
        Assert.Single(handler.Requests);
        Assert.Contains("Validation checks it against all of them; nothing is saved.", result.Stderr);
        Assert.Contains("Validate the shared configuration? [y/N]:", result.Stderr);
        Assert.Contains("Canceled. Nothing was validated.", result.Stderr);
        Assert.DoesNotContain("Changes apply to all of them", result.Stderr);
    }

    [Fact]
    public async Task AccountDocuments_RejectAllowSharedConfiguration()
    {
        var handler = new XboxPartnerHttpMessageHandler(Ok("{}"));

        var result = await InvokeAsync(handler,
            "xbl", "config", "save", "--type", "account", "--seller-id", "1234567",
            "--file", Path.Combine(_directory, "nsal.xml"), "--allow-shared-configuration");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("--allow-shared-configuration cannot be used with account documents.", result.Stderr);
    }

    [Fact]
    public void SharedConfiguration_IgnoresMalformedConflicts()
    {
        Assert.Null(XboxLiveConfigurationTarget.ReadSharedConfiguration(Conflict(
            """{"status":409,"code":"shared_configuration_requires_confirmation","scid":"not-a-guid","boundTitleIds":["00000042"]}""")));
        Assert.Null(XboxLiveConfigurationTarget.ReadSharedConfiguration(Conflict(
            $$"""{"status":409,"code":"shared_configuration_requires_confirmation","scid":"{{Scid}}","boundTitleIds":[]}""")));
        Assert.Null(XboxLiveConfigurationTarget.ReadSharedConfiguration(Conflict(
            $$"""{"status":409,"code":"other","scid":"{{Scid}}","boundTitleIds":["00000042"]}""")));
        Assert.NotNull(XboxLiveConfigurationTarget.ReadSharedConfiguration(Conflict(
            $$"""{"status":409,"code":"shared_configuration_requires_confirmation","scid":"{{Scid}}","boundTitleIds":["00000042"]}""")));
    }
    [Theory]
    [InlineData("config", "publish")]
    [InlineData("config", "save")]
    public async Task Mutations_ServiceRefusalOfBoundTitleSuggestsScid(string family, string action)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent(
                $$"""{"status":409,"code":"bound_title_requires_scid","title":"Bound title","detail":"Title 00000042 is bound to 00000043.","titleId":"00000042","boundTitleId":"00000043","scid":"{{Scid}}"}"""),
        };
        var handler = new XboxPartnerHttpMessageHandler(response);

        var result = await InvokeAsync(handler, [.. Arguments(family, action), "--title-id", "42"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Single(handler.Requests);
        Assert.Empty(result.Stdout);
        Assert.Contains("bound_title_requires_scid", result.Stderr);
        Assert.Contains($"Rerun with --scid {Scid} to change the shared configuration.", result.Stderr);
        Assert.DoesNotContain("outcome is unknown", result.Stderr);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reads_ReportEffectiveTargetOnlyWhenVerbose(bool verbose)
    {
        var handler = Handler("status", Target(Operation("config", "status"), bound: true));
        var args = Arguments("config", "status").Concat(["--title-id", "42"]).ToList();
        if (verbose) args.Add("--verbose");

        var result = await InvokeAsync(handler, [.. args]);

        Assert.True(result.ExitCode == 0, result.Stderr);
        if (verbose)
        {
            Assert.Contains("Effective configuration target:", result.Stderr);
            Assert.Contains("primary title 00000043", result.Stderr);
        }
        else
        {
            Assert.DoesNotContain("Effective configuration target:", result.Stderr);
            Assert.DoesNotContain(Scid, result.Stderr);
        }
        Assert.DoesNotContain("Effective configuration target:", result.Stdout);
    }

    [Theory]
    [InlineData("config", "publish")]
    [InlineData("config", "save")]
    public async Task WriteTimeout_IsNeverRetried(string family, string action)
    {
        var handler = new TimeoutHandler();
        var result = await InvokeAsync(handler, [.. Arguments(family, action), "--title-id", "42"]);
        Assert.Equal(WriteFailureExitCode(family, action), result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("timed out", result.Stderr);
        Assert.Equal(1, handler.RequestCount);
        Assert.False(string.IsNullOrEmpty(handler.CorrelationId));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(
            result.Stderr, System.Text.RegularExpressions.Regex.Escape(handler.CorrelationId!)));
        Assert.DoesNotContain("Effective configuration target:", result.Stderr);
    }

    [Theory]
    [InlineData("config", "publish")]
    [InlineData("config", "save")]
    public async Task MalformedWritePayload_IsNeverReplayed(string family, string action)
    {
        var handler = new XboxPartnerHttpMessageHandler(Target(Ok("{")));

        var result = await InvokeAsync(handler, [.. Arguments(family, action), "--title-id", "42"]);

        Assert.Equal(WriteFailureExitCode(family, action), result.ExitCode);
        AssertTitleOperation(Assert.Single(handler.Requests), family, action, "00000042", null);
        Assert.Empty(result.Stdout);
        if (action is "download" or "save")
        {
            Assert.False(File.Exists(Path.Combine(_directory, "XDKS.1.etag")));
        }
    }

    [Theory]
    [InlineData("config", "publish", HttpStatusCode.InternalServerError)]
    [InlineData("config", "publish", HttpStatusCode.GatewayTimeout)]
    [InlineData("config", "save", HttpStatusCode.InternalServerError)]
    [InlineData("config", "save", HttpStatusCode.GatewayTimeout)]
    [InlineData("config", "publish", HttpStatusCode.RequestTimeout)]
    [InlineData("config", "save", HttpStatusCode.RequestTimeout)]
    public async Task WriteServerFailure_PreservesCorrelationAndNeverReplays(
        string family, string action, HttpStatusCode status)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent("""{"code":"operation-failed","detail":"The operation failed"}"""),
        };
        response.Headers.Add("x-ms-correlation-id", "uncertain-write");
        var handler = new XboxPartnerHttpMessageHandler(response);

        var result = await InvokeAsync(handler, [.. Arguments(family, action), "--title-id", "42"]);

        Assert.Equal(WriteFailureExitCode(family, action), result.ExitCode);
        AssertTitleOperation(Assert.Single(handler.Requests), family, action, "00000042", null);
        Assert.Empty(result.Stdout);
        Assert.Contains("uncertain-write", result.Stderr);
        Assert.Contains(CheckCommand(action, "--title-id 00000042"), result.Stderr);
    }

    [Theory]
    [InlineData("config", "publish", "not json")]
    [InlineData("config", "publish", "{}")]
    [InlineData("config", "save", "not json")]
    [InlineData("config", "save", """{"ETag":"\"etag\""}""")]
    public async Task WriteMalformedSuccess_PreservesCorrelationAndNeverReplays(
        string family, string action, string body)
    {
        var response = Target(Ok(body));
        response.Headers.Add("x-ms-correlation-id", "malformed-write");
        var handler = new XboxPartnerHttpMessageHandler(response);

        var result = await InvokeAsync(handler, [.. Arguments(family, action), "--title-id", "42"]);

        Assert.Equal(WriteFailureExitCode(family, action), result.ExitCode);
        Assert.Single(handler.Requests);
        Assert.Empty(result.Stdout);
        Assert.Contains("malformed-write", result.Stderr);
        Assert.Contains(CheckCommand(action, $"--scid {Scid} --seller-id 1234567"), result.Stderr);
        Assert.False(File.Exists(Path.Combine(_directory, "XDKS.1.etag")));
    }

    [Theory]
    [InlineData("config", "publish")]
    [InlineData("config", "save")]
    public async Task WriteUnreadableSuccess_PreservesCorrelationAndNeverReplays(
        string family, string action)
    {
        Environment.SetEnvironmentVariable("XBOX_XBOXPARTNER__MAXRESPONSEBYTES", "4");
        var response = Target(Operation(family, action));
        response.Headers.Add("x-ms-correlation-id", "unreadable-write");
        var handler = new XboxPartnerHttpMessageHandler(response);

        var result = await InvokeAsync(handler, [.. Arguments(family, action), "--title-id", "42"]);

        Assert.Equal(WriteFailureExitCode(family, action), result.ExitCode);
        Assert.Single(handler.Requests);
        Assert.Empty(result.Stdout);
        Assert.Contains("unreadable-write", result.Stderr);
        Assert.Contains("exceeded the configured maximum size", result.Stderr);
    }

    /// <summary>A sent write that fails without a definite refusal leaves its outcome unknown.</summary>
    private static int WriteFailureExitCode(string family, string action) => (family, action) switch
    {
        ("config", "publish") => PublishCommandFactory.OutcomeUnknownExitCode,
        ("config", "save") => CommitDocumentsCommandFactory.OutcomeUnknownExitCode,
        _ => 1,
    };

    private static string CheckCommand(string action, string target) => action == "publish"
        ? $"Check with: xbox xbl config status {target} --sandbox-id XDKS.1"
        : $"Check with: xbox xbl config download {target} --sandbox-id XDKS.1 --view draft --dest <directory>";

    private string[] Arguments(string family, string action, string view = "draft")
    {
        List<string> args = ["xbl", family, action];
        if (action != "list") args.AddRange(["--sandbox-id", "XDKS.1"]);
        if (action == "download") args.AddRange(["--view", view, "--dest", _directory]);
        if (action == "save") args.AddRange(["--file", Path.Combine(_directory, "nsal.xml")]);
        return [.. args];
    }

    private static void AssertTitleOperation(
        CapturedHttpRequest request, string family, string action, string title, long? seller)
    {
        Assert.Equal(HttpMethod.Post, request.Method);
        string path = (family, action) switch
        {
            ("config", "list") => "configurations",
            ("config", "status") => "configurations/get",
            ("config", "download") => "documents",
            ("config", "save") => "documents/commit",
            ("config", "publish") => "publish",
            ("config", "last-publish") => "publish/status",
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
        Assert.Equal("/xboxpartner/v1/xboxlive/" + path, request.RequestUri!.AbsolutePath);
        Assert.Equal("application/json", request.ContentType);
        Assert.Contains("Bearer", Assert.Single(request.Headers["Authorization"]));
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal(title, body.RootElement.GetProperty("titleId").GetString());
        AssertNullOrAbsent(body.RootElement, "scid");
        if (seller.HasValue) Assert.Equal(seller, body.RootElement.GetProperty("sellerId").GetInt64());
        else AssertNullOrAbsent(body.RootElement, "sellerId");
    }

    private static void AssertOperationTarget(CapturedHttpRequest request, bool sandbox = true)
    {
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal(Scid, body.RootElement.GetProperty("scid").GetString());
        Assert.Equal(1234567, body.RootElement.GetProperty("sellerId").GetInt64());
        if (sandbox && body.RootElement.TryGetProperty("sandbox", out var value)) Assert.Equal("XDKS.1", value.GetString());
    }

    /// <summary>
    /// A handler for one command. config status also reads the last publish, so it gets a second
    /// response carrying the same target headers as the first.
    /// </summary>
    private static XboxPartnerHttpMessageHandler Handler(string action, HttpResponseMessage response)
    {
        if (action != "status")
        {
            return new XboxPartnerHttpMessageHandler(response);
        }

        var lastPublish = Ok("""{"Status":"Pending"}""");
        foreach (var header in response.Headers.Where(header => header.Key.StartsWith("X-XboxPartner-", StringComparison.Ordinal)))
        {
            lastPublish.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return new XboxPartnerHttpMessageHandler(response, lastPublish);
    }

    /// <summary>
    /// The command's operation request. config status sends its last-publish read after it,
    /// pinned to the SCID the first read resolved rather than looking the title up again.
    /// </summary>
    private static CapturedHttpRequest OperationRequest(XboxPartnerHttpMessageHandler handler, string action)
    {
        Assert.Equal(action == "status" ? 2 : 1, handler.Requests.Count);
        if (action == "status")
        {
            Assert.EndsWith("/publish/status", handler.Requests[1].RequestUri!.AbsolutePath);
            using var first = JsonDocument.Parse(handler.Requests[0].Body!);
            using var second = JsonDocument.Parse(handler.Requests[1].Body!);
            Assert.Equal(Scid, second.RootElement.GetProperty("scid").GetString());
            AssertNullOrAbsent(second.RootElement, "titleId");
            Assert.Equal(
                first.RootElement.GetProperty("sandbox").GetString(),
                second.RootElement.GetProperty("sandbox").GetString());
            if (first.RootElement.TryGetProperty("sellerId", out var seller) && seller.ValueKind != JsonValueKind.Null)
            {
                Assert.Equal(seller.GetInt64(), second.RootElement.GetProperty("sellerId").GetInt64());
            }
        }

        return handler.Requests[0];
    }

    private static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };

    private static void AssertNullOrAbsent(JsonElement body, string property) =>
        Assert.True(!body.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null,
            $"Expected {property} to be absent or null, but got {body.GetRawText()}.");

    private static HttpResponseMessage ScidTarget(HttpResponseMessage response)
    {
        response.Headers.Add("X-XboxPartner-Seller-Id", "1234567");
        return response;
    }

    private static HttpResponseMessage Target(
        HttpResponseMessage response, bool bound = false, string title = "00000042")
    {
        ScidTarget(response);
        response.Headers.Add("X-XboxPartner-Scid", Scid);
        response.Headers.Add("X-XboxPartner-Resolved-Title-Id", bound ? "00000043" : title);
        return response;
    }

    internal static HttpResponseMessage Operation(string family, string action)
    {
        if (action == "download")
        {
            using var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                using var writer = new StreamWriter(zip.CreateEntry("nsal.xml").Open());
                writer.Write("<ServiceNsal />");
            }
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(stream.ToArray()) };
            response.Headers.TryAddWithoutValidation("ETag", "\"etag\"");
            response.Headers.Add("X-Version", "7");
            response.Headers.Add("X-Document-View", "published");
            return response;
        }
        return Ok(action switch
        {
            "list" => """{"configurations":[{"sandbox":"XDKS.1"}]}""",
            "status" => """{"sandbox":"XDKS.1"}""",
            "save" => """{"ValidationInfo":[],"ETag":"\"etag\"","CanCommit":true,"Committed":true}""",
            _ => """{"status":"Pending"}""",
        });
    }

    private const string ConflictCorrelationId = "conflict-correlation-id";

    private static HttpResponseMessage SharedConfigurationConflict()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent(
                $$"""{"status":409,"code":"shared_configuration_requires_confirmation","title":"Shared configuration","detail":"Shared.","scid":"{{Scid}}","titleId":"00000043","boundTitleIds":["00000042","00000044"]}"""),
        };
        response.Headers.Add(XboxPartnerClient.CorrelationIdHeader, ConflictCorrelationId);
        return response;
    }

    private static XboxPartnerApiException Conflict(string body) =>
        new(HttpStatusCode.Conflict, "Conflict", body, new Uri("http://localhost/xboxpartner/v1/xboxlive/publish"));

    private async Task<(int ExitCode, string Stdout, string Stderr)> InvokeWithPromptAsync(
        HttpMessageHandler handler, PublishConfirmationDependencies prompt, params string[] args)
    {
        RootCommand root = Program.BuildRootCommand();
        Command xbl = root.Subcommands.Single(command => command.Name == "xbl");
        Command config = xbl.Subcommands.Single(command => command.Name == "config");
        config.Subcommands.Remove(config.Subcommands.Single(command => command.Name == "publish"));
        config.Subcommands.Add(PublishCommandFactory.Create(prompt));
        config.Subcommands.Remove(config.Subcommands.Single(command => command.Name == "save"));
        config.Subcommands.Add(CommitDocumentsCommandFactory.Create(prompt));
        CommandErrorBoundary.Install(root);

        using var http = new HttpClient(handler);
        Program.XboxPartnerClientFactory = configuration =>
            new XboxPartnerClient(configuration, http,
                new FakeAuthenticationService(FakeAuthenticationService.CreateToken(), configuration));
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            int exitCode = await root.Parse(["xbl", .. args], Program.NoResponseFileConfig).InvokeAsync();
            return (exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Program.XboxPartnerClientFactory = null;
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }
    private Task<(int ExitCode, string Stdout, string Stderr)> InvokeAsync(
        HttpMessageHandler handler, params string[] args) =>
        InvokeAsync(handler, null, args);

    private async Task<(int ExitCode, string Stdout, string Stderr)> InvokeAsync(
        HttpMessageHandler handler, StringWriter? errorCapture, params string[] args)
    {
        using var http = new HttpClient(handler);
        Program.XboxPartnerClientFactory = configuration =>
            new XboxPartnerClient(configuration, http,
                new FakeAuthenticationService(FakeAuthenticationService.CreateToken(), configuration));
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = errorCapture ?? new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            int exitCode = await Program.Main(args);
            return (exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private sealed class TimeoutHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public string? CorrelationId { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            CorrelationId = request.Headers.TryGetValues(XboxPartnerClient.CorrelationIdHeader, out var values)
                ? values.Single()
                : null;
            throw new TaskCanceledException("The XboxPartner request timed out.");
        }
    }

    private sealed class ObservingHandler(Action beforeSend, HttpResponseMessage response)
        : XboxPartnerHttpMessageHandler(response)
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            beforeSend();
            return base.SendAsync(request, cancellationToken);
        }
    }

}
