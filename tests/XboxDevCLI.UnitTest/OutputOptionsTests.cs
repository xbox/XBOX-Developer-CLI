// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Identity.Client;
using Moq;
using XboxDevCLI.Services;
using XboxDevCLI.Output;
using Xunit;

namespace XboxDevCLI.UnitTest;

public class OutputOptionsTests
{
    // ---------------------------------------------------------------------------
    // FormatJson compact tests
    // ---------------------------------------------------------------------------

    [Fact]
    public void FormatJson_DefaultIsPrettyPrinted()
    {
        var json = """{"code":200,"data":{"Key":"val"}}""";
        var result = PlayFabApiClient.FormatJson(json);

        Assert.Contains("\n", result);
        Assert.Contains("  ", result);
    }

    [Fact]
    public void FormatJson_CompactReturnsSingleLine()
    {
        var json = """{"code":200,"data":{"Key":"val"}}""";
        var result = PlayFabApiClient.FormatJson(json, compact: true);

        Assert.DoesNotContain("\n", result);
        Assert.Contains("\"code\"", result);
        Assert.Contains("\"Key\"", result);
    }

    [Fact]
    public void FormatJson_CompactFalse_PrettyPrints()
    {
        var json = """{"code":200,"data":{"Key":"val"}}""";
        var result = PlayFabApiClient.FormatJson(json, compact: false);

        Assert.Contains("\n", result);
        Assert.Contains("  ", result);
    }

    [Fact]
    public void FormatJson_CompactTrue_ReturnsOriginalForInvalidJson()
    {
        var notJson = "not valid json";
        var result = PlayFabApiClient.FormatJson(notJson, compact: true);

        Assert.Equal(notJson, result);
    }

    // ---------------------------------------------------------------------------
    // FormatOutput tests
    // ---------------------------------------------------------------------------

    [Fact]
    public void FormatOutput_FullMode_ReturnsFullResponse()
    {
        var json = """{"code":200,"status":"OK","data":{"Key":"val"}}""";
        var result = Program.FormatOutput(json, compact: false, dataOnly: false);

        Assert.Contains("\"code\"", result);
        Assert.Contains("\"status\"", result);
        Assert.Contains("\"data\"", result);
    }

    [Fact]
    public void FormatOutput_DataMode_ExtractsDataField()
    {
        var json = """{"code":200,"status":"OK","data":{"Key":"val","Other":"x"}}""";
        var result = Program.FormatOutput(json, compact: false, dataOnly: true);

        Assert.Contains("\"Key\"", result);
        Assert.Contains("\"Other\"", result);
        Assert.DoesNotContain("\"code\"", result);
        Assert.DoesNotContain("\"status\"", result);
    }

    [Fact]
    public void FormatOutput_DataMode_NoDataField_ReturnsFullResponse()
    {
        // When there's no "data" field, fall back to the full response
        var json = """{"code":200,"status":"OK"}""";
        var result = Program.FormatOutput(json, compact: false, dataOnly: true);

        Assert.Contains("\"code\"", result);
        Assert.Contains("\"status\"", result);
    }

    [Fact]
    public void FormatOutput_CompactMode_ReturnsSingleLine()
    {
        var json = """{"code":200,"status":"OK","data":{"Key":"val"}}""";
        var result = Program.FormatOutput(json, compact: true, dataOnly: false);

        Assert.DoesNotContain("\n", result);
        Assert.Contains("\"code\"", result);
    }

    [Fact]
    public void FormatOutput_DataModeAndCompact_ExtractsDataAndSingleLine()
    {
        var json = """{"code":200,"status":"OK","data":{"Key":"val"}}""";
        var result = Program.FormatOutput(json, compact: true, dataOnly: true);

        Assert.DoesNotContain("\n", result);
        Assert.Contains("\"Key\"", result);
        Assert.DoesNotContain("\"code\"", result);
    }

    [Fact]
    public void FormatOutput_InvalidJson_ReturnedAsIs()
    {
        var notJson = "not valid json";
        var result = Program.FormatOutput(notJson, compact: false, dataOnly: false);

        Assert.Equal(notJson, result);
    }

    // ---------------------------------------------------------------------------
    // FormatErrorOutput tests
    // ---------------------------------------------------------------------------

    [Fact]
    public void FormatErrorOutput_ValidJsonBody_ReturnsFormattedJson()
    {
        var rawBody = """{"code":400,"status":"BadRequest","error":"InvalidParams","errorCode":1000,"errorMessage":"Bad input"}""";
        var ex = new PlayFabApiException(400, "BadRequest", rawBody);

        var result = Program.FormatErrorOutput(ex, compact: false);

        Assert.Contains("\n", result);
        Assert.Contains("\"code\"", result);
        Assert.Contains("\"errorMessage\"", result);
    }

    [Fact]
    public void FormatErrorOutput_ValidJsonBody_CompactReturnsSingleLine()
    {
        var rawBody = """{"code":400,"status":"BadRequest","error":"InvalidParams","errorCode":1000,"errorMessage":"Bad input"}""";
        var ex = new PlayFabApiException(400, "BadRequest", rawBody);

        var result = Program.FormatErrorOutput(ex, compact: true);

        Assert.DoesNotContain("\n", result);
        Assert.Contains("\"errorCode\"", result);
    }

    [Fact]
    public void FormatErrorOutput_NonJsonBody_WithApiErrorMessage_ReturnsHumanReadable()
    {
        // When ApiErrorMessage is set (PlayFab JSON error parsed from body), but the raw body
        // itself is not JSON (e.g., a truncated or malformed response), fall back to ex.Message.
        // This is a contrived case but tests the fallback path.
        var rawBody = "not valid json";
        var ex = new PlayFabApiException(503, "ServiceUnavailable", rawBody);

        // ApiErrorMessage will be null because rawBody is not valid JSON
        Assert.Null(ex.ApiErrorMessage);

        var result = Program.FormatErrorOutput(ex, compact: false);

        // Should contain both the human-readable message and the raw body
        Assert.Contains(ex.Message, result);
        Assert.Contains(rawBody, result);
    }

    [Fact]
    public void FormatErrorOutput_ValidJsonPlayFabError_FormatsAsJson()
    {
        // Simulate a PlayFab error where the raw body is valid JSON with errorMessage
        var rawBody = """{"code":401,"status":"Unauthorized","error":"NotAuthorized","errorCode":1089,"errorMessage":"Developer does not have admin permission for this title."}""";
        var ex = new PlayFabApiException(401, "Unauthorized", rawBody);

        Assert.NotNull(ex.ApiErrorMessage);

        // Even though the body is JSON, the FormatErrorOutput should format it as JSON
        var result = Program.FormatErrorOutput(ex, compact: false);

        Assert.Contains("\"error\"", result);
        Assert.Contains("\"errorMessage\"", result);
    }

    [Fact]
    public void FormatErrorOutput_UnknownOutcome_AddsTheWarningToTheErrorJson()
    {
        var rawBody = """{"code":500,"status":"InternalServerError","error":"InternalServerError","errorCode":1110,"errorMessage":"oops"}""";
        var ex = new PlayFabApiException("/Admin/SetTitleData", 500, "InternalServerError", rawBody, bodyFailure: null, innerException: null);

        var result = Program.FormatErrorOutput(ex, compact: true);

        using var doc = System.Text.Json.JsonDocument.Parse(result);
        Assert.Equal("oops", doc.RootElement.GetProperty("errorMessage").GetString());
        Assert.Equal(ex.OutcomeWarning, doc.RootElement.GetProperty("outcomeUnknown").GetString());
    }

    [Theory]
    [InlineData("upstream timed out")]
    [InlineData("[1]")]
    public void FormatErrorOutput_UnknownOutcomeWithoutAnErrorObject_StillWarns(string rawBody)
    {
        var ex = new PlayFabApiException("/Admin/SetTitleData", 504, "GatewayTimeout", rawBody, bodyFailure: null, innerException: null);

        var result = Program.FormatErrorOutput(ex, compact: true);

        Assert.Contains(rawBody, result);
        Assert.Contains("outcome is unknown", result);
    }

    // ---------------------------------------------------------------------------
    // Invalid UTF-16 handling (regression tests for defensive JSON parsing).
    // JsonDocument.Parse throws ArgumentException (not JsonException) when a string
    // contains an unpaired UTF-16 surrogate that cannot be transcoded to UTF-8.
    // These paths must fall back gracefully rather than crash. Found via fuzzing.
    // ---------------------------------------------------------------------------

    // A lone high surrogate (U+D800) is a valid .NET string but not transcodable to UTF-8.
    private const string InvalidUtf16 = "\uD800";

    [Fact]
    public void FormatJson_InvalidUtf16_ReturnsOriginal()
    {
        var result = PlayFabApiClient.FormatJson(InvalidUtf16, compact: false);
        Assert.Equal(InvalidUtf16, result);
    }

    [Fact]
    public void FormatOutput_InvalidUtf16_DoesNotThrow_AndReturnsOriginal()
    {
        var full = Program.FormatOutput(InvalidUtf16, compact: false, dataOnly: false);
        var dataOnly = Program.FormatOutput(InvalidUtf16, compact: true, dataOnly: true);

        Assert.Equal(InvalidUtf16, full);
        Assert.Equal(InvalidUtf16, dataOnly);
    }

    [Fact]
    public void FormatErrorOutput_InvalidUtf16Body_FallsBackToHumanReadable()
    {
        var ex = new PlayFabApiException(400, "BadRequest", InvalidUtf16);

        // The exception constructor must not throw while parsing the body...
        Assert.Null(ex.ApiErrorMessage);

        // ...and formatting the error must fall back to the human-readable message.
        var result = Program.FormatErrorOutput(ex, compact: false);
        Assert.Contains(ex.Message, result);
    }

    [Fact]
    public void PlayFabApiException_WrongTypedErrorFields_DoesNotThrow()
    {
        // Valid JSON, but errorCode is a string rather than a number: GetInt32 would
        // throw InvalidOperationException. BuildMessage must swallow it and fall back.
        var rawBody = """{"errorCode":"not-a-number","error":"X","errorMessage":"boom"}""";

        var ex = new PlayFabApiException(400, "BadRequest", rawBody);

        Assert.Null(ex.ApiErrorCode);
        Assert.Contains("HTTP 400", ex.Message);
    }

    // ---------------------------------------------------------------------------
    // Global option parsing tests
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task GlobalOption_Output_IsAccepted()
    {
        var rootCommand = new RootCommand();
        rootCommand.Options.Add(Program.OutputOption);
        var dummyCommand = new Command("test");
        string? capturedOutput = null;
        dummyCommand.SetAction((ParseResult pr) =>
        {
            capturedOutput = pr.GetValue(Program.OutputOption);
            return 0;
        });
        rootCommand.Subcommands.Add(dummyCommand);

        await rootCommand.Parse("--output json-compact test").InvokeAsync();

        Assert.Equal("json-compact", capturedOutput);
    }

    [Fact]
    public async Task GlobalOption_Output_ShortAlias_IsAccepted()
    {
        var rootCommand = new RootCommand();
        rootCommand.Options.Add(Program.OutputOption);
        var dummyCommand = new Command("test");
        string? capturedOutput = null;
        dummyCommand.SetAction((ParseResult pr) =>
        {
            capturedOutput = pr.GetValue(Program.OutputOption);
            return 0;
        });
        rootCommand.Subcommands.Add(dummyCommand);

        await rootCommand.Parse("-o json-compact test").InvokeAsync();

        Assert.Equal("json-compact", capturedOutput);
    }

    [Fact]
    public void GlobalOption_Table_IsAcceptedOnlyForSupportedCommandFamilies()
    {
        var root = Program.BuildRootCommand();
        var gameList = root.Parse(["game", "list", "--output", "table"]);
        var gameGet = root.Parse(["game", "get", "--store-id", "9N0000000000", "--output", "table"]);
        var sellerList = root.Parse(["seller", "list", "--output", "table"]);
        var contextGet = root.Parse(["context", "get", "--output", "table"]);
        var contextSet = root.Parse(["context", "set", "seller-id", "1234567", "--output", "table"]);
        var contextUnset = root.Parse(["context", "unset", "seller-id", "--output", "table"]);
        var playFab = root.Parse(["playfab", "api", "/Admin/GetTitleData", "--output", "table"]);
        var batches = root.Parse(["test-account", "batch", "list", "-o", "table"]);
        var batch = root.Parse(["test-account", "batch", "get", "42", "-o", "table"]);

        Assert.Empty(gameList.Errors);
        Assert.Empty(gameGet.Errors);
        Assert.Empty(sellerList.Errors);
        Assert.Empty(contextGet.Errors);
        Assert.Empty(contextSet.Errors);
        Assert.Empty(contextUnset.Errors);
        Assert.True(Program.SupportsTableOutput(gameList.CommandResult.Command));
        Assert.True(Program.SupportsTableOutput(gameGet.CommandResult.Command));
        Assert.True(Program.SupportsTableOutput(sellerList.CommandResult.Command));
        Assert.True(Program.SupportsTableOutput(contextGet.CommandResult.Command));
        Assert.True(Program.SupportsTableOutput(contextSet.CommandResult.Command));
        Assert.True(Program.SupportsTableOutput(contextUnset.CommandResult.Command));
        Assert.False(Program.SupportsTableOutput(playFab.CommandResult.Command));
        Assert.Empty(batches.Errors);
        Assert.Empty(batch.Errors);
        Assert.True(Program.SupportsTableOutput(batches.CommandResult.Command));
        Assert.True(Program.SupportsTableOutput(batch.CommandResult.Command));
    }

    [Fact]
    public async Task GlobalOption_DataOnly_IsAccepted()
    {
        var rootCommand = new RootCommand();
        rootCommand.Options.Add(Program.DataOnlyOption);
        var dummyCommand = new Command("test");
        var captured = false;
        dummyCommand.SetAction((ParseResult pr) =>
        {
            captured = pr.GetValue(Program.DataOnlyOption);
            return 0;
        });
        rootCommand.Subcommands.Add(dummyCommand);

        await rootCommand.Parse("--data-only test").InvokeAsync();

        Assert.True(captured);
    }

    [Fact]
    public async Task GlobalOption_Verbose_IsAccepted()
    {
        var rootCommand = new RootCommand();
        rootCommand.Options.Add(Program.VerboseOption);
        var dummyCommand = new Command("test");
        var captured = false;
        dummyCommand.SetAction((ParseResult pr) =>
        {
            captured = pr.GetValue(Program.VerboseOption);
            return 0;
        });
        rootCommand.Subcommands.Add(dummyCommand);

        await rootCommand.Parse("--verbose test").InvokeAsync();

        Assert.True(captured);
    }

    [Theory]
    [InlineData("--confirm")]
    [InlineData("--yes")]
    [InlineData("-y")]
    public async Task GlobalOption_Confirm_AliasesAccepted(string flag)
    {
        var rootCommand = new RootCommand();
        rootCommand.Options.Add(Program.ConfirmOption);
        var dummyCommand = new Command("test");
        var captured = false;
        dummyCommand.SetAction((ParseResult pr) =>
        {
            captured = pr.GetValue(Program.ConfirmOption);
            return 0;
        });
        rootCommand.Subcommands.Add(dummyCommand);

        await rootCommand.Parse($"{flag} test").InvokeAsync();

        Assert.True(captured);
    }

    [Fact]
    public async Task GlobalOption_Output_DefaultIsNull_TreatedAsJson()
    {
        var rootCommand = new RootCommand();
        rootCommand.Options.Add(Program.OutputOption);
        var dummyCommand = new Command("test");
        string? capturedOutput = null;
        dummyCommand.SetAction((ParseResult pr) =>
        {
            capturedOutput = pr.GetValue(Program.OutputOption);
            return 0;
        });
        rootCommand.Subcommands.Add(dummyCommand);

        await rootCommand.Parse("test").InvokeAsync();

        // Null means "use the default" (json)
        Assert.Null(capturedOutput);
    }

    [Fact]
    public async Task RawApi_InvalidOutputValue_ReturnsOne()
    {
        // Validation of --output occurs before PlayFabApiClient is constructed (which
        // requires a TitleId), so Program.Main exits with 1 from the validation check.
        var result = await Program.Main(new[] { "--output", "invalid-value", "playfab", "api", "/Admin/GetTitleData" });
        Assert.Equal(1, result);
    }

    [Fact]
    public async Task RawApi_InvalidOutputValue_ShortAlias_ReturnsOne()
    {
        // The -o alias is validated the same way as --output, before any network call.
        var result = await Program.Main(new[] { "-o", "xml", "playfab", "api", "/Admin/GetTitleData" });
        Assert.Equal(1, result);
    }

    [Theory]
    [InlineData("--output")]
    [InlineData("--data-only")]
    [InlineData("--verbose")]
    [InlineData("--confirm")]
    public void BuildRootCommand_RegistersGlobalOption_AsRecursive(string optionName)
    {
        // The standardized global options must be registered once on the root command
        // and be recursive so every subcommand inherits them uniformly.
        var root = Program.BuildRootCommand();

        var option = Assert.Single(root.Options, o => o.Name == optionName);
        Assert.True(option.Recursive, $"'{optionName}' must be a recursive global option.");
    }

    [Fact]
    public void BuildRootCommand_OutputOption_HasShortAlias()
    {
        var root = Program.BuildRootCommand();

        var output = Assert.Single(root.Options, o => o.Name == "--output");
        Assert.Contains("-o", output.Aliases);
    }

    [Fact]
    public void FilesDownload_UsesDestOption_NotOutputPath()
    {
        // The file-download destination was renamed --output-path -> --dest to avoid
        // visual collision with the global --output format option.
        var root = Program.BuildRootCommand();
        var playfab = Assert.Single(root.Subcommands, c => c.Name == "playfab");
        var files = Assert.Single(playfab.Subcommands, c => c.Name == "file");
        var download = Assert.Single(files.Subcommands, c => c.Name == "download");

        Assert.Single(download.Options, o => o.Name == "--dest");
        Assert.DoesNotContain(download.Options, o => o.Name == "--output-path");
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static IConfiguration CreateTestConfiguration(string titleId = "ABC123")
    {
        var settings = new Dictionary<string, string?>
        {
            ["PlayFab:TitleId"] = titleId,
            ["PlayFab:BaseUrlTemplate"] = "https://{0}.playfabapi.com",
            ["AzureAd:ClientId"] = "test-client-id",
            ["AzureAd:TenantId"] = "test-tenant-id",
            ["AzureAd:Scopes"] = "api://test/.default",
        };
        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
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
}
