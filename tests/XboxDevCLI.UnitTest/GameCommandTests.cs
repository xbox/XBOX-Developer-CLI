// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Net;
using System.Text.Json;
using Microsoft.Identity.Client;
using XboxDevCLI.Commands;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

[Collection("ConsoleRedirect")]
public class GameCommandTests
{
    [Theory]
    [InlineData("list", true)]
    [InlineData("list", false)]
    [InlineData("get", true)]
    [InlineData("get", false)]
    public async Task GameCommands_UnexpectedFailurePrintsOnlyTheMessage(string command, bool authenticationFailure)
    {
        Exception exception = authenticationFailure
            ? new MsalServiceException("invalid_client", "AADSTS7000215: Invalid client secret.")
            : new NotSupportedException("The credential provider is unavailable.");
        var client = new FakeXboxPartnerClient { Exception = exception };
        string[] args = command == "get" ? ["game", command, "1234ABCD"] : ["game", command];

        var result = await InvokeAsync(client, args);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.EndsWith(exception.Message + Environment.NewLine, result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(exception.GetType().FullName!, result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", result.Stderr, StringComparison.Ordinal);
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task GameList_MakesOneRequestAndPreservesTheGamesEnvelope()
    {
        var client = new FakeXboxPartnerClient
        {
            GamesResponse =
                """{"games":[{"productId":"abc","name":"Nod Arena"},{"productId":"def","name":"Lucerne Rally"}]}""",
        };

        var result = await InvokeAsync(client, "game", "list", "--output", "json-compact");

        Assert.Equal(0, result.ExitCode);
        Assert.Single(client.Requests);
        // stderr is redirected here, so no progress is drawn.
        Assert.NotNull(client.LastPageRead);
        Assert.DoesNotContain("Reading games", result.Stderr, StringComparison.Ordinal);
        using var output = JsonDocument.Parse(result.Stdout);
        Assert.Equal(2, output.RootElement.GetProperty("games").GetArrayLength());
        Assert.False(output.RootElement.TryGetProperty("continuationToken", out _));
        Assert.False(output.RootElement.TryGetProperty("products", out _));
    }

    [Fact]
    public async Task GameList_ServiceTimeoutUsesGatewayErrorHandlingWithoutRetry()
    {
        var client = new FakeXboxPartnerClient
        {
            Exception = new XboxPartnerApiException(
                HttpStatusCode.GatewayTimeout,
                "Gateway Timeout",
                """{"detail":"The complete game catalog could not be retrieved within two minutes."}""",
                new Uri("https://gateway.example/xboxpartner/v1/games"),
                "listing-correlation"),
        };

        var result = await InvokeAsync(client, "game", "list");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("x-ms-correlation-id: listing-correlation", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("timed out", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("within two minutes", result.Stderr, StringComparison.Ordinal);
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task GameList_InvalidPagePrintsTheListingCorrelationId()
    {
        var client = new FakeXboxPartnerClient
        {
            Exception = new XboxPartnerResponseException(
                """{"games":[],"continuationToken":"t1"}""",
                "listing-correlation",
                new InvalidDataException("The XboxPartner game response repeated a continuation token.")),
        };

        var result = await InvokeAsync(client, "game", "list");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("x-ms-correlation-id: listing-correlation", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("repeated a continuation token", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GameList_TableGroupsStoreIdsByHexadecimalTitleId()
    {
        var client = new FakeXboxPartnerClient
        {
            GamesResponse =
                """
                {
                  "games": [
                    {"productId":"abc","storeId":"9N0000000000","name":"Nod Arena","xboxTitleId":"12345678","packageFamilyName":"Nod.Arena_abc123","msaAppId":"0000000040ABCDEF"},
                    {"productId":"def","storeId":"9N0000000001","name":"Nod Arena","xboxTitleId":"12345678","packageFamilyName":"Nod.Arena_abc123","msaAppId":"0000000040ABCDEF"},
                    {"productId":"ghi","storeId":"9N0000000002","name":"Lucerne Rally","xboxTitleId":"1234ABCD","packageFamilyName":"Lucerne.Rally_def456","msaAppId":"0000000040FEDCBA"}
                  ]
                }
                """,
        };

        var result = await InvokeAsync(client, "game", "list", "--output", "table");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("12345678", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("9N0000000000, 9N0000000001", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("PACKAGE FAMILY NAME", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("Nod.Arena_abc123", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("MSA APP ID", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("0000000040ABCDEF", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("results found", result.Stdout, StringComparison.Ordinal);
        Assert.Matches(@"3 results found in \d+\.\d{3}s\r?\n?$", result.Stderr);
    }

    [Fact]
    public async Task GameList_DetailsShowsNumberedProductsWithinATitle()
    {
        var client = new FakeXboxPartnerClient
        {
            GamesResponse =
                """
                {
                  "games": [
                    {"productId":"abc","storeId":"9N0000000000","name":"Nod Arena","xboxTitleId":"12345678"},
                    {"productId":"def","storeId":"9N0000000001","name":"Nod Rally","xboxTitleId":"12345678"}
                  ]
                }
                """,
        };

        var result = await InvokeAsync(client, "game", "list", "--details");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("TITLE ID: 12345678", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("1. Nod Arena", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("2. Nod Rally", result.Stdout, StringComparison.Ordinal);
        Assert.Matches(@"2 results found in \d+\.\d{3}s\r?\n?$", result.Stderr);
    }

    [Fact]
    public async Task GameGet_ForwardsStoreIdWithoutCatalogScan()
    {
        var client = new FakeXboxPartnerClient
        {
            GamesResponse =
                """{"games":[{"productId":"abc","storeId":"9N0000000042"}]}""",
        };

        var result = await InvokeAsync(
            client,
            "game",
            "get",
            "--store-id",
            "9N0000000042",
            "--output",
            "json-compact");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("9N0000000042", client.LastStoreId);
        Assert.Null(client.LastTitleId);
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task GameGet_DetailsShortAliasOmitsNumberingForOneProduct()
    {
        var client = new FakeXboxPartnerClient
        {
            GamesResponse =
                """{"games":[{"productId":"abc","storeId":"9N0000000042","name":"Nod Arena","xboxTitleId":"12345678"}]}""",
        };

        var result = await InvokeAsync(client, "game", "get", "12345678", "-d");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("TITLE ID: 12345678", result.Stdout, StringComparison.Ordinal);
        Assert.Contains(
            $"{Environment.NewLine}Nod Arena{Environment.NewLine}   Store ID: 9N0000000042",
            result.Stdout,
            StringComparison.Ordinal);
        Assert.DoesNotContain("1. Nod Arena", result.Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("json-compact")]
    [InlineData("table")]
    public async Task GameDetails_RejectsExplicitOutputFormat(string output)
    {
        var client = new FakeXboxPartnerClient();

        var result = await InvokeAsync(
            client,
            "game",
            "list",
            "--details",
            "--output",
            output);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("cannot be combined", result.Stderr, StringComparison.Ordinal);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task GameGet_ForwardsPositionalTitleId()
    {
        var client = new FakeXboxPartnerClient
        {
            GamesResponse = """{"games":[{"productId":"abc","name":"Nod Arena"}]}""",
        };

        var result = await InvokeAsync(
            client,
            "game",
            "get",
            "1234ABCD",
            "--output",
            "json-compact");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("1234ABCD", client.LastTitleId);
        using var output = JsonDocument.Parse(result.Stdout);
        Assert.Single(output.RootElement.GetProperty("games").EnumerateArray());
        Assert.False(output.RootElement.TryGetProperty("continuationToken", out _));
        Assert.Single(client.Requests);
    }

    [Theory]
    [InlineData("1234ABCD", "1234ABCD")]
    [InlineData("42", "00000042")]
    [InlineData("0x42", "00000042")]
    [InlineData("1", "00000001")]
    [InlineData("abcdef", "00ABCDEF")]
    public async Task GameGet_NormalizesAcceptedTitleIdForms(
        string titleId,
        string expected)
    {
        var client = new FakeXboxPartnerClient();

        var result = await InvokeAsync(client, "game", "get", titleId);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, client.LastTitleId);
        Assert.Null(client.LastStoreId);
        Assert.Single(client.Requests);
    }

    [Theory]
    [InlineData("not-an-id")]
    [InlineData("0x")]
    [InlineData("123456789")]
    [InlineData("0x100000000")]
    public async Task GameGet_RejectsInvalidTitleIdBeforeMakingARequest(string titleId)
    {
        var client = new FakeXboxPartnerClient();

        var result = await InvokeAsync(client, "game", "get", titleId);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("one to eight hexadecimal digits", result.Stderr, StringComparison.Ordinal);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task GameGet_AllowsBothSelectors()
    {
        var client = new FakeXboxPartnerClient();

        var result = await InvokeAsync(
            client,
            "game",
            "get",
            "1234ABCD",
            "--store-id",
            "9N0000000000");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("1234ABCD", client.LastTitleId);
        Assert.Equal("9N0000000000", client.LastStoreId);
    }

    [Fact]
    public async Task GameGet_ForwardsKeyword()
    {
        var client = new FakeXboxPartnerClient();

        var result = await InvokeAsync(
            client,
            "game",
            "get",
            "--keyword",
            "headless");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("headless", client.LastKeyword);
        Assert.Null(client.LastTitleId);
        Assert.Null(client.LastStoreId);
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task GameGet_RejectsKeywordCombinedWithStoreId()
    {
        var client = new FakeXboxPartnerClient();

        var result = await InvokeAsync(
            client,
            "game",
            "get",
            "--keyword",
            "headless",
            "--store-id",
            "9N0000000000");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("cannot be combined", result.Stderr, StringComparison.Ordinal);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task GameGet_RejectsKeywordCombinedWithTitleId()
    {
        var client = new FakeXboxPartnerClient();

        var result = await InvokeAsync(
            client,
            "game",
            "get",
            "1234ABCD",
            "--keyword",
            "headless");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("cannot be combined", result.Stderr, StringComparison.Ordinal);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task GameGet_RejectsWhitespaceOnlyKeyword()
    {
        var client = new FakeXboxPartnerClient();

        var result = await InvokeAsync(client, "game", "get", "--keyword", "   ");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("non-whitespace", result.Stderr, StringComparison.Ordinal);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task GameGet_RejectsWhitespaceOnlyStoreId()
    {
        var client = new FakeXboxPartnerClient();

        var result = await InvokeAsync(
            client,
            "game",
            "get",
            "1234ABCD",
            "--store-id",
            "   ");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("--store-id", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("non-whitespace", result.Stderr, StringComparison.Ordinal);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task GameList_RejectsWhitespaceOnlySellerOverride()
    {
        var client = new FakeXboxPartnerClient();

        var result = await InvokeAsync(client, "game", "list", "--seller-id", "   ");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("--seller-id", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("non-whitespace", result.Stderr, StringComparison.Ordinal);
        Assert.Empty(client.Requests);
    }

    /// <summary>
    /// With no --seller-id, a read uses the seller saved for the signed-in account, quietly.
    /// </summary>
    [Fact]
    public async Task GameList_UsesTheSavedSeller()
    {
        var client = new FakeXboxPartnerClient();

        var result = await InvokeWithSavedSellerAsync(client, ["game", "list", "--output", "json-compact"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("7654321", client.LastSellerId);
        Assert.Equal(1, result.IdentityReads);
        using var output = JsonDocument.Parse(result.Stdout);
        Assert.Empty(output.RootElement.GetProperty("games").EnumerateArray());
        Assert.DoesNotContain("saved context", result.Stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>--seller-id</c>, then <c>XBOX_SELLER_ID</c>, names the seller instead of the saved one,
    /// without a sign-in lookup. A read doesn't say where its seller came from.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GameList_FlagOrEnvironmentSellerIsUsedWithoutIdentityLookup(bool fromFlag)
    {
        var client = new FakeXboxPartnerClient();

        var result = fromFlag
            ? await InvokeWithSavedSellerAsync(
                client, ["game", "list", "--seller-id", "2345678"], environmentSeller: "1111111")
            : await InvokeWithSavedSellerAsync(client, ["game", "list"], environmentSeller: "2345678");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("2345678", client.LastSellerId);
        Assert.Equal(0, result.IdentityReads);
        Assert.DoesNotContain("Using --seller-id", result.Stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// With no seller from anywhere, signed out the fix is to sign in; signed in, it's every way
    /// to name one. Either way no request is made.
    /// </summary>
    [Theory]
    [InlineData(false, "Not signed in. Run 'xbox auth login' first.")]
    [InlineData(true, "No seller-id is set. Pass --seller-id, set XBOX_SELLER_ID, or run 'xbox context set seller-id <value>'.")]
    public async Task GameList_WithoutASellerSaysHowToNameOne(bool signedIn, string expected)
    {
        var client = new FakeXboxPartnerClient();

        var result = await InvokeWithSavedSellerAsync(
            client, ["game", "list"], identity: new SignedInIdentity("other", "user"), signedIn: signedIn);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(expected, result.Stderr, StringComparison.Ordinal);
        Assert.Empty(result.Stdout);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task GameGet_RequiresAtLeastOneSelector()
    {
        var client = new FakeXboxPartnerClient();

        var result = await InvokeAsync(client, "game", "get");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("At least one", result.Stderr, StringComparison.Ordinal);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task GameGet_PreservesMultipleTitleMatches()
    {
        var client = new FakeXboxPartnerClient
        {
            GamesResponse =
                """
                {
                  "games": [
                    {"productId":"abc","storeId":"9N0000000000","xboxTitleId":"1234ABCD","name":"Nod Arena"},
                    {"productId":"def","storeId":"9N0000000001","xboxTitleId":"1234ABCD","name":"Lucerne Rally"}
                  ]
                }
                """,
        };

        var result = await InvokeAsync(
            client,
            "game",
            "get",
            "1234ABCD",
            "--output",
            "json-compact");

        Assert.Equal(0, result.ExitCode);
        using var output = JsonDocument.Parse(result.Stdout);
        Assert.Equal(2, output.RootElement.GetProperty("games").GetArrayLength());
    }

    [Theory]
    [InlineData("list", "json")]
    [InlineData("list", "json-compact")]
    [InlineData("get", "json")]
    [InlineData("get", "json-compact")]
    public async Task GameCommands_PreserveAllGameFieldsInJsonOutput(string command, string format)
    {
        var client = new FakeXboxPartnerClient
        {
            GamesResponse =
                """
                {
                  "games": [
                    {
                      "productId": "00000000000000000042",
                      "storeId": "9N0000000042",
                      "xboxTitleId": "ABCD1234",
                      "SCID": "00000000-0000-0000-0000-0000ABCD1234",
                      "primaryDevelopmentSandbox": "TESTSB.42",
                      "deviceFamilies": ["xbox", "desktop"],
                      "name": "Nod Arena",
                      "packageIdentityName": "Nod.Arena",
                      "packageIdentityPublisher": "CN=Nod Publishers",
                      "packageFamilyName": "Nod.Arena_abc123",
                      "msaAppId": "0000000040ABCDEF"
                    }
                  ]
                }
                """,
        };

        string[] args = command == "get"
            ? ["game", command, "ABCD1234", "--output", format]
            : ["game", command, "--output", format];
        var result = await InvokeAsync(client, args);

        Assert.Equal(0, result.ExitCode);
        using var output = JsonDocument.Parse(result.Stdout);
        var game = Assert.Single(output.RootElement.GetProperty("games").EnumerateArray());
        Assert.Equal("00000000000000000042", game.GetProperty("productId").GetString());
        Assert.Equal("9N0000000042", game.GetProperty("storeId").GetString());
        Assert.Equal("ABCD1234", game.GetProperty("xboxTitleId").GetString());
        Assert.Equal("00000000-0000-0000-0000-0000ABCD1234", game.GetProperty("SCID").GetString());
        Assert.Equal("TESTSB.42", game.GetProperty("primaryDevelopmentSandbox").GetString());
        Assert.Equal(["xbox", "desktop"],
            game.GetProperty("deviceFamilies").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal("Nod Arena", game.GetProperty("name").GetString());
        Assert.Equal("Nod.Arena", game.GetProperty("packageIdentityName").GetString());
        Assert.Equal("CN=Nod Publishers", game.GetProperty("packageIdentityPublisher").GetString());
        Assert.Equal("Nod.Arena_abc123", game.GetProperty("packageFamilyName").GetString());
        Assert.Equal("0000000040ABCDEF", game.GetProperty("msaAppId").GetString());
    }

    [Fact]
    public async Task GameGet_NoMatchIsSuccessful()
    {
        var client = new FakeXboxPartnerClient();

        var result = await InvokeAsync(
            client,
            "game",
            "get",
            "--store-id",
            "9N0000000000",
            "--output",
            "json-compact");

        Assert.Equal(0, result.ExitCode);
        using var output = JsonDocument.Parse(result.Stdout);
        Assert.Empty(output.RootElement.GetProperty("games").EnumerateArray());
    }

    [Theory]
    [InlineData("json")]
    [InlineData("json-compact")]
    public async Task GameList_PreservesNullSummaryFields(string format)
    {
        var client = new FakeXboxPartnerClient
        {
            GamesResponse =
                """{"games":[{"name":"Nod Arena","xboxTitleId":null,"SCID":null,"primaryDevelopmentSandbox":null}]}""",
        };

        var result = await InvokeAsync(client, "game", "list", "--output", format);

        Assert.Equal(0, result.ExitCode);
        using var output = JsonDocument.Parse(result.Stdout);
        var game = Assert.Single(output.RootElement.GetProperty("games").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, game.GetProperty("xboxTitleId").ValueKind);
        Assert.Equal(JsonValueKind.Null, game.GetProperty("SCID").ValueKind);
        Assert.Equal(JsonValueKind.Null, game.GetProperty("primaryDevelopmentSandbox").ValueKind);
        Assert.False(game.TryGetProperty("scid", out _));
    }

    [Theory]
    [InlineData("list", "json")]
    [InlineData("list", "json-compact")]
    [InlineData("get", "json")]
    [InlineData("get", "json-compact")]
    public async Task GameCommands_PreserveDeviceFamilies(string command, string format)
    {
        foreach (var fields in new[]
        {
            "",
            "\"deviceFamilies\":null",
            "\"deviceFamilies\":[]",
            "\"deviceFamilies\":[\"xbox\",\"desktop\"]",
            "\"deviceFamilies\":[\"FutureDevice\",\"Desktop\",\"xbox\",\"FutureDevice\"]",
        })
        {
            var client = new FakeXboxPartnerClient { GamesResponse = $$"""{"games":[{ {{fields}} }]}""" };
            string[] args = command == "get"
                ? ["game", command, "1234ABCD", "--output", format]
                : ["game", command, "--output", format];

            var result = await InvokeAsync(client, args);

            Assert.Equal(0, result.ExitCode);
            using var output = JsonDocument.Parse(result.Stdout);
            using var expected = JsonDocument.Parse(client.GamesResponse);
            Assert.True(JsonElement.DeepEquals(expected.RootElement, output.RootElement));
        }
    }

    [Fact]
    public async Task GameGet_UsesExistingGatewayErrorHandling()
    {
        var client = new FakeXboxPartnerClient
        {
            Exception = new XboxPartnerApiException(
                HttpStatusCode.BadRequest,
                "Bad Request",
                """{"title":"Game identifiers do not match."}""",
                new Uri("https://gateway.example/xboxpartner/v1/games")),
        };

        var result = await InvokeAsync(
            client,
            "game",
            "get",
            "1234ABCD",
            "--store-id",
            "9N0000000000");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("request was rejected", result.Stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// The gateway rejects a title ID and Store ID that don't identify the same game with a 400,
    /// which can follow earlier pages, so the listing fails with its correlation ID and the detail.
    /// </summary>
    [Fact]
    public async Task GameGet_MismatchOnALaterPagePrintsTheGatewayDetail()
    {
        var handler = new XboxPartnerHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"games":[],"continuationToken":"t1"}"""),
            },
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(
                    """{"title":"Game identifiers do not match.","status":400,"detail":"'titleId' 305441741 and 'storeId' '9N0000000000' do not identify the same game."}"""),
            });
        using var http = new HttpClient(handler);
        using var client = XboxPartnerClientTests.CreateClient(http);

        var result = await InvokeAsync(client, "game", "get", "1234ABCD", "--store-id", "9N0000000000");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Equal(2, handler.Requests.Count);
        var correlationId = Assert.Single(handler.Requests[1].Headers["x-ms-correlation-id"]);
        Assert.Contains($"x-ms-correlation-id: {correlationId}", result.Stderr, StringComparison.Ordinal);
        Assert.Contains(
            "Error detail: 'titleId' 305441741 and 'storeId' '9N0000000000' do not identify the same game.",
            result.Stderr,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("""{"games":{}}""")]
    [InlineData("""{"games":[123]}""")]
    [InlineData("""{"games":[{"productId":"abc","xboxTitleId":"305441741"}]}""")]
    public async Task GameCommands_RejectInvalidSuccessfulResponseShapes(string response)
    {
        var client = new FakeXboxPartnerClient { GamesResponse = response };

        var result = await InvokeAsync(client, "game", "list");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.NotEmpty(result.Stderr);
    }

    private static Task<CapturedInvocation> InvokeAsync(
        IXboxPartnerClient client,
        params string[] commandArgs) =>
        RunAsync(client, commandArgs.Contains("--seller-id") ? commandArgs : [.. commandArgs, "--seller-id", "1234567"]);

    /// <summary>
    /// Runs a game command with seller 7654321 saved for tenant/user on the loopback gateway,
    /// <paramref name="identity"/> signed in (tenant/user by default; <c>null</c> when
    /// <paramref name="signedIn"/> is <c>false</c>), and <c>XBOX_SELLER_ID</c> set to
    /// <paramref name="environmentSeller"/> (unset by default). It adds no <c>--seller-id</c>.
    /// </summary>
    private static async Task<CapturedInvocation> InvokeWithSavedSellerAsync(
        FakeXboxPartnerClient client,
        string[] commandArgs,
        string? environmentSeller = null,
        SignedInIdentity? identity = null,
        bool signedIn = true)
    {
        string[] variables = ["XBOX_SELLER_ID", ContextProfiles.EnvironmentVariable];
        var previous = variables.ToDictionary(name => name, name => Environment.GetEnvironmentVariable(name));
        var directory = Path.Combine(Path.GetTempPath(), $"xbox-game-{Guid.NewGuid():N}");
        var identityReads = 0;
        try
        {
            Environment.SetEnvironmentVariable(ContextProfiles.EnvironmentVariable, null);
            Environment.SetEnvironmentVariable("XBOX_SELLER_ID", environmentSeller);
            ContextStore.DefaultDirectory = () => directory;
            await new ContextStore().UpdateAsync(
                new SignedInIdentity("tenant", "user"),
                [new ContextChange("seller-id", "http://localhost:54848", "7654321")]);
            ContextResolver.ReadIdentityAsync = _ =>
            {
                identityReads++;
                return Task.FromResult(signedIn ? identity ?? new SignedInIdentity("tenant", "user") : null);
            };

            return await RunAsync(client, commandArgs) with { IdentityReads = identityReads };
        }
        finally
        {
            foreach (var (name, value) in previous)
            {
                Environment.SetEnvironmentVariable(name, value);
            }

            TestEnvironment.Reset();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>Runs a game command on the loopback gateway, filling in context values first, as Main does.</summary>
    private static async Task<CapturedInvocation> RunAsync(IXboxPartnerClient client, string[] commandArgs)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);

        try
        {
            var root = Program.BuildRootCommand(gameCommandFactory: () => GameCommandFactory.Create(_ => client));
            string[] args = [.. commandArgs, "--base-url", "http://localhost:54848"];
            var parseResult = await Program.ResolveContextAsync(root.Parse(args, Program.NoResponseFileConfig), args);
            var exitCode = parseResult is null ? 1 : await parseResult.InvokeAsync();
            return new CapturedInvocation(exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private sealed record CapturedInvocation(int ExitCode, string Stdout, string Stderr, int IdentityReads = 0);

    private sealed class FakeXboxPartnerClient : IXboxPartnerClient
    {
        public Task<XboxPartnerGameCreationJob> CreateGameAsync(
            string baseUrl, string sellerId, CreateXboxGameRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<XboxPartnerGameCreationJob> GetGameCreationJobAsync(
            string baseUrl, string sellerId, string jobId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public string GamesResponse { get; init; } =
            """{"games":[]}""";

        public Exception? Exception { get; init; }

        public string? LastTitleId { get; private set; }

        public string? LastStoreId { get; private set; }

        public string? LastSellerId { get; private set; }

        public string? LastKeyword { get; private set; }

        public Action<int>? LastPageRead { get; private set; }

        public List<string> Requests { get; } = [];

        public Task<string> GetGamesAsync(
            string baseUrl,
            string sellerId,
            string? titleId = null,
            string? storeId = null,
            string? keyword = null,
            CancellationToken cancellationToken = default,
            Action<int>? pageRead = null)
        {
            Requests.Add(baseUrl);
            if (Exception != null)
            {
                throw Exception;
            }

            LastTitleId = titleId;
            LastStoreId = storeId;
            LastSellerId = sellerId;
            LastKeyword = keyword;
            LastPageRead = pageRead;
            pageRead?.Invoke(2);
            return Task.FromResult(GamesResponse);
        }

        public Task<XboxPartnerSellersResult> GetSellersAsync(
            string baseUrl,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}
