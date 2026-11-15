// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Identity.Client;
using Moq;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Commands;
using XboxDevCLI.Output;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

[Collection("ConsoleRedirect")]
public class PlaytestCommandTests
{
    private const string Store = "9N0000000000";

    private const string Playtest = "11111111-2222-3333-4444-555555555555";

    private const string Other = "22222222-3333-4444-5555-666666666666";

    private const string GamesJson =
        """{"games":[{"xboxTitleId":"1234ABCD","storeId":"9N0000000000","name":"Game"}]}""";

    private const string Detail =
        """{"details":{"playtestId":"p1","name":"Alpha","status":"Live","gameArtUri":"https://art/1"},"packages":{"packages":[{"packageId":"a","platform":"PC"}]},"etag":"\"v1\""}""";

    [Fact]
    public async Task List_FollowsEveryPageAndSendsSellerInTheQuery()
    {
        var handler = Handler(
            """{"playtests":[{"playtestId":"p1","name":"A"}],"continuationToken":"t1"}""",
            """{"playtests":[{"playtestId":"p2","name":"B"}]}""");

        var result = await InvokeAsync(handler, "playtest", "list", "--store-id", Store, "--output", "json-compact");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            """{"playtests":[{"playtestId":"p1","name":"A"},{"playtestId":"p2","name":"B"}]}""",
            result.Stdout.Trim());
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal($"/xboxpartner/v1/games/{Store}/playtests", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("sellerId=1234567", handler.Requests[0].RequestUri!.Query, StringComparison.Ordinal);
        Assert.Contains("pageSize=100", handler.Requests[0].RequestUri!.Query, StringComparison.Ordinal);
        Assert.Contains("continuationToken=t1", handler.Requests[1].RequestUri!.Query, StringComparison.Ordinal);
        Assert.All(handler.Requests, request => Assert.False(request.Headers.ContainsKey("X-DeveloperAccountId")));
        Assert.Single(handler.Requests.Select(request => Assert.Single(request.Headers["x-ms-correlation-id"])).Distinct());
    }

    [Fact]
    public async Task List_RepeatedContinuationToken_Fails()
    {
        var handler = Handler(
            """{"playtests":[],"continuationToken":"t1"}""",
            """{"playtests":[],"continuationToken":"t1"}""");

        var result = await InvokeAsync(handler, "playtest", "list", "--store-id", Store);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("repeated a continuation token", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_TableOutput_ShowsOneRowPerPlaytest()
    {
        var handler = Handler("""{"playtests":[{"playtestId":"p1","name":"Alpha","status":"Live"}]}""");

        var result = await InvokeAsync(handler, "playtest", "list", "--store-id", Store, "--output", "table");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("PLAYTEST ID", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("Alpha", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TitleId_IsResolvedThroughTheGameLookup()
    {
        var handler = Handler(GamesJson, """{"playtests":[]}""");

        var result = await InvokeAsync(handler, "playtest", "list", "--xbox-title-id", "1234abcd");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("/xboxpartner/v1/games", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("titleId=1234ABCD", handler.Requests[0].RequestUri!.Query, StringComparison.Ordinal);
        Assert.Equal($"/xboxpartner/v1/games/{Store}/playtests", handler.Requests[1].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task TitleId_MatchingNoGame_FailsBeforeAskingForPlaytests()
    {
        var handler = Handler("""{"games":[]}""");

        var result = await InvokeAsync(handler, "playtest", "list", "--xbox-title-id", "1234ABCD");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("No game with XBOX title ID 1234ABCD", result.Stderr, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task TitleId_MatchingSeveralGames_NamesThemAndFails()
    {
        var handler = Handler(
            """{"games":[{"xboxTitleId":"1234ABCD","storeId":"9N0000000000"},{"xboxTitleId":"1234ABCD","storeId":"9N0000000001"}]}""");

                    var result = await InvokeAsync(handler, "playtest", "list", "--xbox-title-id", "1234ABCD");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("9N0000000000, 9N0000000001", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("--store-id", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Pass exactly one of", "playtest", "list")]
    [InlineData("Pass exactly one of", "playtest", "list", "--store-id", Store, "--xbox-title-id", "1234ABCD")]
    [InlineData("Invalid Store ID", "playtest", "list", "--store-id", "short")]
    [InlineData("Invalid XBOX title ID", "playtest", "list", "--xbox-title-id", "zzz")]
    [InlineData("Pass exactly one of", "playtest", "get", Playtest)]
    [InlineData("Invalid --compare-to", "playtest", "diff", Playtest, "--store-id", Store, "--compare-to", " ")]
    [InlineData("Invalid playtest-id", "playtest", "get", "p1", "--store-id", Store)]
    [InlineData("Invalid playtest-id", "playtest", "get", "00000000-0000-0000-0000-000000000000", "--store-id", Store)]
    [InlineData("Invalid --compare-to", "playtest", "diff", Playtest, "--store-id", Store, "--compare-to", "p0")]
    public async Task InvalidGameOptions_AreRejectedWithoutARequest(string expected, params string[] arguments)
    {
        var handler = Handler("""{}""");

        var result = await InvokeAsync(handler, arguments);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(expected, result.Stderr, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task StoreId_IsTrimmedAndUppercasedBeforeTheRequest()
    {
        var handler = Handler("""{"playtests":[]}""");

        var result = await InvokeAsync(handler, "playtest", "list", "--store-id", " 9n0000000000 ");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"/xboxpartner/v1/games/{Store}/playtests", handler.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Get_PrintsTheResponseWithItsEtag()
    {
        var handler = Handler(Detail);

        var result = await InvokeAsync(handler, "playtest", "get", Playtest, "--store-id", Store, "--output", "json-compact");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Detail, result.Stdout.Trim());
        Assert.Equal($"/xboxpartner/v1/games/{Store}/playtests/{Playtest}", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("sellerId=1234567", handler.Requests[0].RequestUri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_TableOutput_FlattensFieldsAndSkipsArtUris()
    {
        var handler = Handler(Detail);

        var result = await InvokeAsync(handler, "playtest", "get", Playtest, "--store-id", Store, "--output", "table");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("details.name", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("packages.packages[PC].packageId", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("etag", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("gameArtUri", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_NotFound_ShowsTheGatewayErrorAndExitsWithOne()
    {
        var handler = new XboxPartnerHttpMessageHandler(
            Response("""{"title":"Not found","detail":"No such playtest."}""", HttpStatusCode.NotFound));

        var result = await InvokeAsync(handler, "playtest", "get", Playtest, "--store-id", Store);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("No such playtest.", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diff_SendsTheCompareTargetAndShowsOnlyDifferences()
    {
        var handler = Handler(
            """{"hasDiffData":true,"current":{"details":{"name":"New","status":"Live"}},"compared":{"details":{"name":"Old","status":"Live"}}}""");

        var result = await InvokeAsync(
            handler, "playtest", "diff", Playtest, "--store-id", Store, "--compare-to", Other, "--output", "table");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            $"/xboxpartner/v1/games/{Store}/playtests/{Playtest}/diff", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains($"comparePlaytestId={Other}", handler.Requests[0].RequestUri!.Query, StringComparison.Ordinal);
        Assert.Contains("details.name", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("New", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("Old", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("details.status", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diff_WithoutAPublishedVersion_SaysSoOnStderrAndKeepsJsonOnStdout()
    {
        var handler = Handler("""{"hasDiffData":false,"current":{"details":{"name":"New"}}}""");

        var result = await InvokeAsync(
            handler, "playtest", "diff", Playtest, "--store-id", Store, "--output", "json-compact");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("nothing to compare", result.Stderr, StringComparison.Ordinal);
        Assert.Equal("""{"hasDiffData":false,"current":{"details":{"name":"New"}}}""", result.Stdout.Trim());
        Assert.DoesNotContain("comparePlaytestId", handler.Requests[0].RequestUri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diff_WithoutAPublishedVersion_TableHasNoRows()
    {
        var handler = Handler("""{"hasDiffData":false,"current":{"details":{"name":"New"}}}""");

        var result = await InvokeAsync(
            handler, "playtest", "diff", Playtest, "--store-id", Store, "--output", "table");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("nothing to compare", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("details.name", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("New", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diff_WhereOnlyArtUrisDiffer_TableHasNoRows()
    {
        var handler = Handler(
            """{"hasDiffData":true,"current":{"details":{"name":"Same","gameArtUri":"https://art/1"}},"compared":{"details":{"name":"Same","gameArtUri":"https://art/2"}}}""");

        var result = await InvokeAsync(
            handler, "playtest", "diff", Playtest, "--store-id", Store, "--output", "table");

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("gameArtUri", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("details.name", result.Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"current":{}}""", "hasDiffData as true or false")]
    [InlineData("""{"hasDiffData":"true","current":{}}""", "hasDiffData as true or false")]
    [InlineData("""{"hasDiffData":null,"current":{}}""", "hasDiffData as true or false")]
    [InlineData("""{"hasDiffData":false}""", "current as an object")]
    [InlineData("""{"hasDiffData":true,"current":{"details":{}}}""", "compared as an object")]
    [InlineData("""{"hasDiffData":true,"current":{},"compared":null}""", "compared as an object")]
    public async Task Diff_AMalformedResponse_Fails(string response, string expected)
    {
        var handler = Handler(response);

        var result = await InvokeAsync(
            handler, "playtest", "diff", Playtest, "--store-id", Store, "--output", "table");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains(expected, result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Flatten_SkipsNullValues_AndUsesPositions_WhenAnItemHasNoPlatform()
    {
        using var document = JsonDocument.Parse(
            """{"a":null,"p":[{"platform":"PC","packageId":"a"},{"packageId":"b"}]}""");

        var fields = PlaytestFields.Flatten(document.RootElement).Select(row => row.Key).ToArray();

        Assert.Equal(["p[0].platform", "p[0].packageId", "p[1].packageId"], fields);
    }

    [Fact]
    public void ToDiffFields_ListsFieldsMissingOnOneSide()
    {
        var json = PlaytestFields.ToDiffFields(
            """{"hasDiffData":true,"current":{"a":"1","b":"2"},"compared":{"a":"1","c":"3"}}""");

        using var document = JsonDocument.Parse(json);
        var fields = document.RootElement.GetProperty("fields").EnumerateArray()
            .Select(row => (row.GetProperty("field").GetString(), row.GetProperty("current").GetString(),
                row.GetProperty("compared").GetString()))
            .ToArray();
        Assert.Equal([("b", "2", ""), ("c", "", "3")], fields);
    }

    [Fact]
    public void ToDiffFields_LinesUpPackagesByPlatform_WhenOneIsAdded()
    {
        var json = PlaytestFields.ToDiffFields(
            """{"hasDiffData":true,"current":{"p":[{"platform":"PC","packageId":"a"},{"platform":"Xbox","packageId":"b"}]},"compared":{"p":[{"platform":"Xbox","packageId":"b"}]}}""");

        using var document = JsonDocument.Parse(json);
        var fields = document.RootElement.GetProperty("fields").EnumerateArray()
            .Select(row => row.GetProperty("field").GetString()!)
            .ToArray();
        Assert.Equal(["p[PC].packageId", "p[PC].platform"], fields);
    }

    [Fact]
    public void Flatten_UsesPositions_WhenTwoItemsShareAPlatform()
    {
        using var document = JsonDocument.Parse(
            """[{"platform":"PC","packageId":"a"},{"platform":"pc","packageId":"b"}]""");

        var fields = PlaytestFields.Flatten(document.RootElement).Select(row => row.Key).ToArray();

        Assert.Equal(["[0].platform", "[0].packageId", "[1].platform", "[1].packageId"], fields);
    }

    [Fact]
    public async Task List_EscapesAContinuationTokenWithPlusSlashAndEquals()
    {
        var handler = Handler(
            """{"playtests":[],"continuationToken":"a+b/c="}""",
            """{"playtests":[]}""");

        var result = await InvokeAsync(handler, "playtest", "list", "--store-id", Store);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("continuationToken=a%2Bb%2Fc%3D", handler.Requests[1].RequestUri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_StopsAtThePageCap_WhenTheGatewayNeverEndsTheList()
    {
        var handler = Handler(Enumerable.Range(0, XboxPartnerClient.MaxPlaytestListPages)
            .Select(page => $$"""{"playtests":[],"continuationToken":"t{{page}}"}""")
            .ToArray());

        var result = await InvokeAsync(handler, "playtest", "list", "--store-id", Store);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(
            $"did not end after {XboxPartnerClient.MaxPlaytestListPages} pages", result.Stderr, StringComparison.Ordinal);
        Assert.Equal(XboxPartnerClient.MaxPlaytestListPages, handler.Requests.Count);
    }

    [Fact]
    public async Task List_EndsOnTheGatewaysTerminalPage_AfterAThousandFullPages()
    {
        var pages = Enumerable.Range(0, 1000)
            .Select(page => $$"""{"playtests":[],"continuationToken":"t{{page}}"}""")
            .Append("""{"playtests":[]}""")
            .ToArray();
        var handler = Handler(pages);

        var result = await InvokeAsync(handler, "playtest", "list", "--store-id", Store);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1001, handler.Requests.Count);
    }

    [Theory]
    [InlineData("null", "json")]
    [InlineData("[]", "json")]
    [InlineData("null", "table")]
    [InlineData("[]", "table")]
    public async Task Get_AResponseThatIsNotAnObject_Fails(string response, string format)
    {
        var handler = Handler(response);

        var result = await InvokeAsync(
            handler, "playtest", "get", Playtest, "--store-id", Store, "--output", format);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("must be a JSON object", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallerCancellation_IsNotReportedAsARequestFailure()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var handler = Handler("""{"playtests":[]}""");

        var result = await InvokeAsync(handler, canceled.Token, "playtest", "list", "--store-id", Store);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(handler.Requests);
        Assert.DoesNotContain("Request canceled", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(typeof(ArgumentException))]
    [InlineData(typeof(ObjectDisposedException))]
    public async Task BugsAreNotReportedAsRequestFailures(Type exceptionType)
    {
        const string Message = "defect in the CLI";
        var handler = new ThrowingHandler(exceptionType == typeof(ArgumentException)
            ? new ArgumentException(Message)
            : new ObjectDisposedException(Message));

        var result = await InvokeAsync(handler, "playtest", "list", "--store-id", Store);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains($"{exceptionType.FullName}: ", result.Stderr, StringComparison.Ordinal);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task TitleIdLookup_RefusesAMalformedStoreIdWithoutUsingIt()
    {
        var games = """{"games":[{"xboxTitleId":"1234ABCD","storeId":"9N0000000000; printf x"}]}""";
        var handler = new XboxPartnerHttpMessageHandler(Response(games));

        var result = await InvokeAsync(handler, "playtest", "get", Playtest, "--xbox-title-id", "1234ABCD");

        Assert.Equal(1, result.ExitCode);
        Assert.Single(handler.Requests);
        Assert.Contains("has a Store ID that isn't valid", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Pass --store-id instead", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TitleIdLookup_UppercasesTheStoreIdItFinds()
    {
        var games = """{"games":[{"xboxTitleId":"1234ABCD","storeId":"9n0000000000"}]}""";
        var handler = new XboxPartnerHttpMessageHandler(Response(games), Response(Created));

        var result = await InvokeAsync(handler, "playtest", "get", PlaytestId, "--xbox-title-id", "1234ABCD");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            $"/xboxpartner/v1/games/9N0000000000/playtests/{PlaytestId}", handler.Requests[1].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task List_AResponseWithoutThePlaytestsArray_Fails()
    {
        var handler = Handler("""{"items":[]}""");

        var result = await InvokeAsync(handler, "playtest", "list", "--store-id", Store);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("must contain the playtests array", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("package_permission_denied", false, "Packages Read permission")]
    [InlineData("playtest_not_enabled", false, "Playtests aren't enabled for this game and seller")]
    [InlineData("playtest_not_found", false, "no playtest with that ID. To see its playtests, run: xbox ")]
    [InlineData("playtest_not_found", true, "no playtest with one of these IDs. To see its playtests, run: xbox ")]
    [InlineData("game_not_found", false, "No game with Store ID " + Store + " was found for seller ")]
    [InlineData("game_not_found", true, "Check --store-id and --seller-id.")]
    public async Task PlaytestRefusals_ExplainThemselvesInsteadOfShowingTheServiceDetail(
        string code, bool comparing, string expected)
    {
        var status = code.EndsWith("_not_found", StringComparison.Ordinal) ? HttpStatusCode.NotFound : HttpStatusCode.Forbidden;
        var body = JsonSerializer.Serialize(new { title = "Refused", status = (int)status, code, detail = "Upstream detail." });
        string[] arguments = comparing
            ? ["playtest", "diff", Playtest, "--store-id", Store, "--compare-to", Other]
            : ["playtest", "get", Playtest, "--store-id", Store];

        var result = await InvokeAsync(new XboxPartnerHttpMessageHandler(Response(body, status)), arguments);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains(expected, result.Stderr, StringComparison.Ordinal);
        Assert.Contains($"HTTP {(int)status}.", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("response-correlation", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Upstream detail.", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("not authorized", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlaytestRefusal_WithVerbose_AlsoShowsTheResponseBody()
    {
        var body = JsonSerializer.Serialize(new { status = 403, code = "playtest_not_enabled", detail = "Upstream detail." });

        var result = await InvokeAsync(
            new XboxPartnerHttpMessageHandler(Response(body, HttpStatusCode.Forbidden)),
            "playtest", "get", Playtest, "--store-id", Store, "--verbose");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Playtests aren't enabled", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("HTTP 403.", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Upstream detail.", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlaytestNotFound_PointsToTheListCommandForTheResolvedGame()
    {
        var body = JsonSerializer.Serialize(new { status = 404, code = "playtest_not_found" });
        var handler = new XboxPartnerHttpMessageHandler(
            Response(GamesJson), Response(body, HttpStatusCode.NotFound));

        var result = await InvokeAsync(handler, "playtest", "diff", Playtest, "--xbox-title-id", "1234ABCD");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("no playtest with that ID", result.Stderr, StringComparison.Ordinal);
        Assert.Contains($"playtest list --store-id {Store}", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("--seller-id 1234567", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("--base-url https://gateway.example", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "identity_override_not_authorized", "Not one of your sellers.")]
    [InlineData(HttpStatusCode.TooManyRequests, null, "Partner Center is busy.")]
    public async Task OtherGatewayRefusals_ShowTheGatewayMessageAndExitWithOne(
        HttpStatusCode status, string? code, string detail)
    {
        var body = JsonSerializer.Serialize(new { title = "Refused", status = (int)status, code, detail });
        var result = await InvokeAsync(
            new XboxPartnerHttpMessageHandler(Response(body, status)), "playtest", "get", Playtest, "--store-id", Store);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains(detail, result.Stderr, StringComparison.Ordinal);
        Assert.Contains(((int)status).ToString(CultureInfo.InvariantCulture), result.Stderr, StringComparison.Ordinal);
    }

    private const string Created =
        """{"details":{"playtestId":"11111111-2222-3333-4444-555555555555","name":"Beta","status":"Draft"},"packages":null,"images":null,"etag":"\"v1\""}""";

    private const string PlaytestId = "11111111-2222-3333-4444-555555555555";

    [Fact]
    public async Task Create_PostsTheDetailsWithAGuidIdempotencyKey()
    {
        var handler = Handler(Created);

        var result = await InvokeAsync(
            handler, "playtest", "create", "--store-id", Store, "--name", " Beta ", "--description", "Short",
            "--start", "2026-11-01", "--end", "2026-11-15T17:00:00-07:00", "--confirm", "--output", "json-compact");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Created, result.Stdout.Trim());
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"/xboxpartner/v1/games/{Store}/playtests", request.RequestUri!.AbsolutePath);
        Assert.Equal("?sellerId=1234567", request.RequestUri!.Query);
        Assert.Equal("application/json", request.ContentType);
        Assert.True(Guid.TryParse(Assert.Single(request.Headers["Idempotency-Key"]), out _));
        Assert.False(request.Headers.ContainsKey("If-Match"));
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal(
            ["name", "description", "startDate", "endDate"],
            body.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal("Beta", body.RootElement.GetProperty("name").GetString());
        Assert.Equal("Short", body.RootElement.GetProperty("description").GetString());
        Assert.Equal("2026-11-01T00:00:00.0000000+00:00", body.RootElement.GetProperty("startDate").GetString());
        Assert.Equal("2026-11-16T00:00:00.0000000+00:00", body.RootElement.GetProperty("endDate").GetString());
    }

    [Fact]
    public async Task Create_SendsThePackageIdsAndNamesThemInThePrompt()
    {
        var handler = Handler(Created);
        string[] arguments =
        [
            "playtest", "create", "--store-id", Store, "--name", "Beta", "--start", "2026-11-01", "--end", "2026-11-02",
            "--pc-package-id", " 22222222-3333-4444-5555-666666666666 ", "--console-package-id", "33333333-4444-5555-6666-777777777777",
        ];

        var prompt = await InvokeAsync(handler, arguments);
        var result = await InvokeAsync(handler, [.. arguments, "--confirm"]);

        Assert.Equal(1, prompt.ExitCode);
        Assert.Contains(
            ", with PC package 22222222-3333-4444-5555-666666666666 and console package 33333333-4444-5555-6666-777777777777.", prompt.Stderr, StringComparison.Ordinal);
        Assert.Equal(0, result.ExitCode);
        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body!);
        Assert.Equal("22222222-3333-4444-5555-666666666666", body.RootElement.GetProperty("pcPackageId").GetString());
        Assert.Equal("33333333-4444-5555-6666-777777777777", body.RootElement.GetProperty("consolePackageId").GetString());
    }

    [Fact]
    public async Task Create_SendsANewIdempotencyKeyEachRun()
    {
        var handler = Handler(Created, Created);
        string[] arguments =
            ["playtest", "create", "--store-id", Store, "--name", "Beta", "--start", "2026-11-01", "--end", "2026-11-02", "--yes"];

        var first = await InvokeAsync(handler, arguments);
        var second = await InvokeAsync(handler, arguments);

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(0, second.ExitCode);
        Assert.NotEqual(handler.Requests[0].Headers["Idempotency-Key"][0], handler.Requests[1].Headers["Idempotency-Key"][0]);
    }

    [Fact]
    public async Task Create_WithIdempotencyKey_SendsItTrimmed()
    {
        var key = new string('k', 50);
        var handler = Handler(Created);

        var result = await InvokeAsync(
            handler, "playtest", "create", "--store-id", Store, "--name", "Beta",
            "--start", "2026-11-01", "--end", "2026-11-02", "--idempotency-key", $" {key} ", "--confirm");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(key, Assert.Single(Assert.Single(handler.Requests).Headers["Idempotency-Key"]));
    }

    [Theory]
    [InlineData("list", true)]
    [InlineData("get", true)]
    [InlineData("diff", true)]
    [InlineData("create", false)]
    [InlineData("update", false)]
    public void TableOutput_IsDeclaredOnlyForReads(string name, bool supported)
    {
        var command = Program.BuildRootCommand().Subcommands
            .Single(command => command.Name == "playtest").Subcommands
            .Single(command => command.Name == name);

        Assert.Equal(supported, Program.SupportsTableOutput(command));
    }

    [Theory]
    [InlineData("--name", "--start", "2026-11-01", "--end", "2026-11-02")]
    [InlineData("--end must be after --start", "--name", "Beta", "--start", "2026-11-02", "--end", "2026-11-02")]
    [InlineData("next week", "--name", "Beta", "--start", "next week", "--end", "2026-11-02")]
    [InlineData("--idempotency-key must be", "--name", "Beta", "--start", "2026-11-01", "--end", "2026-11-02", "--idempotency-key", " ")]
    [InlineData("--idempotency-key must be", "--name", "Beta", "--start", "2026-11-01", "--end", "2026-11-02", "--idempotency-key", "123456789012345678901234567890123456789012345678901")]
    [InlineData("--idempotency-key must be", "--name", "Beta", "--start", "2026-11-01", "--end", "2026-11-02", "--idempotency-key", "clé")]
    public async Task Create_InvalidDetails_AreRejectedWithoutARequest(string expected, params string[] options)
    {
        var handler = Handler(Created);

        var result = await InvokeAsync(
            handler, ["playtest", "create", "--store-id", Store, "--confirm", .. options]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains(expected, result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2026-11-01T10:00:00", "2026-11-01T10:00:00.0000000+00:00")]
    [InlineData("2026-11-01T10:00:00Z", "2026-11-01T10:00:00.0000000+00:00")]
    [InlineData("2026-11-01T12:00:00+02:00", "2026-11-01T10:00:00.0000000+00:00")]
    [InlineData("2026-11-01T10:00:00.5Z", "2026-11-01T10:00:00.5000000+00:00")]
    public async Task Create_IsoDates_AreSentAsUtc(string start, string expected)
    {
        var handler = Handler(Created);

        var result = await InvokeAsync(
            handler, "playtest", "create", "--store-id", Store, "--name", "Beta",
            "--start", start, "--end", "2026-11-15", "--confirm");

        Assert.Equal(0, result.ExitCode);
        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body!);
        Assert.Equal(expected, body.RootElement.GetProperty("startDate").GetString());
    }

    [Fact]
    public async Task Create_EndNotAfterStart_SaysSo()
    {
        var result = await InvokeAsync(
            Handler(Created), "playtest", "create", "--store-id", Store, "--name", "Beta",
            "--start", "2026-11-02T10:00:00Z", "--end", "2026-11-02T09:00:00Z", "--confirm");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("--end must be after --start.", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_WithoutConfirmationWhenNotInteractive_SendsNothing()
    {
        var handler = Handler(Created);

        var result = await InvokeAsync(
            handler, "playtest", "create", "--store-id", Store, "--name", "Beta",
            "--start", "2026-11-01", "--end", "2026-11-02");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("--confirm", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("list", nameof(CommandStage.Stable))]
    [InlineData("get", nameof(CommandStage.Stable))]
    [InlineData("diff", nameof(CommandStage.Stable))]
    [InlineData("create", nameof(CommandStage.Experimental))]
    [InlineData("update", nameof(CommandStage.Experimental))]
    [InlineData("publish", nameof(CommandStage.Experimental))]
    [InlineData("delete", nameof(CommandStage.Experimental))]
    public void Stage_MarksOnlyTheWritesExperimental(string verb, string stage)
    {
        var playtest = Program.BuildRootCommand().Subcommands.Single(command => command.Name == "playtest");
        var command = playtest.Subcommands.Single(command => command.Name == verb);

        Assert.Equal(stage, CommandMetadataRegistry.GetMetadata(command).Stage.ToString());
    }

    [Theory]
    [InlineData("create", false, "", false)]
    [InlineData("create", true, "y\n", true)]
    [InlineData("update", false, "", false)]
    [InlineData("update", true, "\n", false)]
    [InlineData("update", true, " YES \n", true)]
    public async Task WithoutConfirm_FailsClosedOrPromptsOnStderr(
        string verb, bool interactive, string input, bool accepted)
    {
        var handler = Handler(Created);
        string[] arguments = verb == "create"
            ? ["playtest", "create", "--store-id", Store, "--name", "Beta", "--start", "2026-11-01", "--end", "2026-11-02"]
            : ["playtest", "update", PlaytestId, "--store-id", Store, "--name", "Beta"];
        var (question, canceled) = verb == "create"
            ? ("Create this playtest? [y/N]: ", "Playtest creation canceled.")
            : ("Update this playtest? [y/N]: ", "Playtest update canceled.");

        var result = await InvokeAsync(handler, interactive,         new StringReader(input), CancellationToken.None, arguments);

        Assert.Equal(accepted ? 0 : 1, result.ExitCode);
        Assert.Equal(accepted ? 1 : 0, handler.Requests.Count);
        Assert.Contains(verb == "create" ? "Creating the playtest 'Beta'" : $"Updating playtest {PlaytestId}", result.Stderr);
        if (verb == "create")
        {
            Assert.Contains("from 2026-11-01 00:00 UTC to 2026-11-02 00:00 UTC", result.Stderr, StringComparison.Ordinal);
            Assert.Contains("Remove it later with 'xbox playtest delete'.", result.Stderr, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("name to 'Beta'", result.Stderr, StringComparison.Ordinal);
            Assert.Contains("A published playtest returns to draft", result.Stderr, StringComparison.Ordinal);
        }

        Assert.Equal(interactive, result.Stderr.Contains(question, StringComparison.Ordinal));
        if (!accepted)
        {
            Assert.Empty(result.Stdout);
            Assert.Contains(interactive ? canceled : "Run again with --confirm", result.Stderr);
        }
    }

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    public async Task CancellationWhilePromptIsWaiting_EndsWithoutSending(string verb)
    {
        using var readerStarted = new ManualResetEventSlim();
        using var releaseReader = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var handler = Handler(Created);
        string[] arguments = verb == "create"
            ? ["playtest", "create", "--store-id", Store, "--name", "Beta", "--start", "2026-11-01", "--end", "2026-11-02"]
            : ["playtest", "update", PlaytestId, "--store-id", Store, "--name", "Beta"];

        try
        {
            var pending = InvokeAsync(
                handler, interactive: true, new BlockingReader(readerStarted, releaseReader), cancellation.Token, arguments);
            Assert.True(readerStarted.Wait(TimeSpan.FromSeconds(10)));
            cancellation.Cancel();

            var completed = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.Same(pending, completed);
            var result = await pending;
            Assert.NotEqual(0, result.ExitCode);
            Assert.Empty(handler.Requests);
            Assert.Empty(result.Stdout);
        }
        finally
        {
            releaseReader.Set();
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task Create_ServerErrorAfterSending_ExitsWithOutcomeUnknown(HttpStatusCode status)
    {
        var handler = new XboxPartnerHttpMessageHandler(Response("""{"detail":"Upstream failed."}""", status));

        var result = await InvokeAsync(
            handler, "playtest", "create", "--store-id", Store, "--name", "Beta",
            "--start", "2026-11-01", "--end", "2026-11-02", "--confirm");

        Assert.Equal(PlaytestCommandFactory.OutcomeUnknownExitCode, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("unknown whether the playtest 'Beta' was created", result.Stderr, StringComparison.Ordinal);
        var key = Assert.Single(handler.Requests[0].Headers["Idempotency-Key"]);
        Assert.Contains(
            $"send the same idempotency key: xbox playtest create --store-id {Store} --name Beta "
                + $"--start 2026-11-01 --end 2026-11-02 --idempotency-key {key} --seller-id 1234567 --base-url https://gateway.example",
            result.Stderr,
            StringComparison.Ordinal);
        Assert.Contains($"playtest list --store-id {Store}", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_OutcomeUnknown_RetryKeepsTheGivenKeyAndOptions()
    {
        var handler = new XboxPartnerHttpMessageHandler(
            Response(GamesJson), Response("""{"status":500}""", HttpStatusCode.InternalServerError));

        var result = await InvokeAsync(
            handler, "playtest", "create", "--xbox-title-id", "1234ABCD", "--name", "My sample playtest",
            "--description", "", "--start", "2026-11-01T17:00:00+02:00", "--end", "2026-11-15",
            "--idempotency-key", PlaytestId, "--confirm");

        Assert.Equal(PlaytestCommandFactory.OutcomeUnknownExitCode, result.ExitCode);
        Assert.Equal(PlaytestId, Assert.Single(handler.Requests[1].Headers["Idempotency-Key"]));
        Assert.Contains(
            $"xbox playtest create --store-id {Store} --name {ShellQuoting.Quote("My sample playtest")} "
                + $"--description {ShellQuoting.Quote("")} --start {ShellQuoting.Quote("2026-11-01T17:00:00+02:00")} "
                + $"--end 2026-11-15 --idempotency-key {PlaytestId} --seller-id 1234567 --base-url https://gateway.example",
            result.Stderr,
            StringComparison.Ordinal);
        Assert.Contains(
            $"If the retry is refused, check whether the playtest exists before creating it with a new key: xbox playtest list --store-id {Store}",
            result.Stderr,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_OutcomeUnknown_WithAMultiLineValue_PrintsOnlyTheKey()
    {
        var handler = new XboxPartnerHttpMessageHandler(
            Response("""{"status":500}""", HttpStatusCode.InternalServerError));

        var result = await InvokeAsync(
            handler, "playtest", "create", "--store-id", Store, "--name", "Beta",
            "--long-description", "Try the new arena.\r\nReport bugs in the forum.\r\n",
            "--start", "2026-11-01", "--end", "2026-11-02", "--confirm");

        Assert.Equal(PlaytestCommandFactory.OutcomeUnknownExitCode, result.ExitCode);
        var key = Assert.Single(handler.Requests[0].Headers["Idempotency-Key"]);
        Assert.Contains(
            $"To retry without creating a second playtest, run the same command again with --idempotency-key {key}.",
            result.Stderr,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Report bugs", result.Stderr, StringComparison.Ordinal);
        Assert.Contains($"playtest list --store-id {Store}", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_ConnectionLostAfterSending_ExitsWithOutcomeUnknown()
    {
        var handler = new ThrowingHandler(new HttpRequestException("The connection was reset."));
        var result = await InvokeAsync(
            handler,
            "playtest", "create", "--store-id", Store, "--name", "Beta",
            "--start", "2026-11-01", "--end", "2026-11-02", "--confirm");

        Assert.Equal(PlaytestCommandFactory.OutcomeUnknownExitCode, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("unknown whether the playtest 'Beta' was created", result.Stderr, StringComparison.Ordinal);
        Assert.Contains(handler.CorrelationId!, result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_ConnectionLostAfterSending_ExitsWithOutcomeUnknownAndTheSentCorrelationId()
    {
        var handler = new ThrowingHandler(new HttpRequestException("The connection was reset."));
        var result = await InvokeAsync(
            handler,
            "playtest", "update", PlaytestId, "--store-id", Store, "--name", "Beta", "--confirm");

        Assert.Equal(PlaytestCommandFactory.OutcomeUnknownExitCode, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("unknown whether it was applied", result.Stderr, StringComparison.Ordinal);
        Assert.Contains(handler.CorrelationId!, result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_ServerErrorAfterSending_PrintsTheCorrelationIdOnce()
    {
        var handler = new XboxPartnerHttpMessageHandler(
            Response("""{"status":500}""", HttpStatusCode.InternalServerError));

        var result = await InvokeAsync(
            handler, "playtest", "create", "--store-id", Store, "--name", "Beta",
            "--start", "2026-11-01", "--end", "2026-11-02", "--confirm");

        Assert.Equal(PlaytestCommandFactory.OutcomeUnknownExitCode, result.ExitCode);
        var sent = Assert.Single(handler.Requests[0].Headers["x-ms-correlation-id"]);
        Assert.Contains("response-correlation", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(sent, result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("not json")]
    public async Task Create_UnreadableSuccessBody_ExitsWithOutcomeUnknown(string responseBody)
    {
        var result = await InvokeAsync(
            Handler(responseBody), "playtest", "create", "--store-id", Store, "--name", "Beta",
            "--start", "2026-11-01", "--end", "2026-11-02", "--confirm");

        Assert.Equal(PlaytestCommandFactory.OutcomeUnknownExitCode, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains($"playtest list --store-id {Store}", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("response-correlation", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("create", "--name", "Beta", "--start", "2026-11-01", "--end", "2026-11-02")]
    [InlineData("update", Playtest, "--name", "Beta")]
    public async Task Writes_PermissionDenied_AskForPackagesWrite(string verb, params string[] options)
    {
        var body = JsonSerializer.Serialize(new { status = 403, code = "package_permission_denied", detail = "Upstream detail." });

        var result = await InvokeAsync(
            new XboxPartnerHttpMessageHandler(Response(body, HttpStatusCode.Forbidden)),
            ["playtest", verb, .. options, "--store-id", Store, "--confirm"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("permission to change this game's playtests", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Packages Read", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Upstream detail.", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_Rejection_ExitsWithOne()
    {
        var body = JsonSerializer.Serialize(
            new { status = 400, code = "playtest_request_rejected", detail = "The end date is in the past." });
        var handler = new XboxPartnerHttpMessageHandler(Response(body, HttpStatusCode.BadRequest));

        var result = await InvokeAsync(
            handler, "playtest", "create", "--store-id", Store, "--name", "Beta",
            "--start", "2020-11-01", "--end", "2020-11-02", "--confirm");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("The end date is in the past.", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_PatchesOnlyTheGivenFieldsAndSendsNoIfMatch()
    {
        var handler = Handler(Created);

        var result = await InvokeAsync(
            handler, "playtest", "update", PlaytestId, "--store-id", Store, "--description", "",
            "--end", "2026-11-30", "--confirm", "--output", "json-compact");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Created, result.Stdout.Trim());
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Patch, request.Method);
        Assert.Equal($"/xboxpartner/v1/games/{Store}/playtests/{PlaytestId}", request.RequestUri!.AbsolutePath);
        Assert.Equal("?sellerId=1234567", request.RequestUri!.Query);
        Assert.False(request.Headers.ContainsKey("If-Match"));
        Assert.False(request.Headers.ContainsKey("Idempotency-Key"));
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal(
            ["description", "endDate"],
            body.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal("", body.RootElement.GetProperty("description").GetString());
        Assert.Equal("2026-11-30T00:00:00.0000000+00:00", body.RootElement.GetProperty("endDate").GetString());
    }

    [Theory]
    [InlineData("\"v1\"")]
    [InlineData("v1")]
    [InlineData(" v1 ")]
    public async Task Update_SendsIfMatchAsAQuotedEtag(string etag)
    {
        var handler = Handler(Created);

        var result = await InvokeAsync(
            handler, "playtest", "update", PlaytestId, "--store-id", Store, "--name", "Beta 2",
            "--if-match", etag, "--confirm");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["\"v1\""], handler.Requests[0].Headers["If-Match"]);
        Assert.Equal("""{"name":"Beta 2"}""", handler.Requests[0].Body);
    }

    [Theory]
    [InlineData("Pass at least one of --name", "--confirm")]
    [InlineData("--if-match must be one etag", "--if-match", "W/\"v1\"", "--name", "Beta", "--confirm")]
    [InlineData("--if-match cannot be empty", "--if-match", " ", "--name", "Beta", "--confirm")]
    [InlineData("--if-match must be one etag", "--if-match", "W/v1", "--name", "Beta", "--confirm")]
    [InlineData("--if-match must be one etag", "--if-match", "a,b", "--name", "Beta", "--confirm")]
    [InlineData("--if-match must be one etag", "--if-match", "\"v\u00e9\"", "--name", "Beta", "--confirm")]
    [InlineData("--if-match must be one etag", "--if-match", "*", "--name", "Beta", "--confirm")]
    [InlineData("--end must be after --start", "--start", "2026-11-03", "--end", "2026-11-02", "--confirm")]
    [InlineData("--pc-package-id cannot be only whitespace", "--pc-package-id", " ", "--confirm")]
    public async Task Update_InvalidOptions_AreRejectedWithoutARequest(string expected, params string[] options)
    {
        var handler = Handler(Created);

        var result = await InvokeAsync(
            handler, ["playtest", "update", PlaytestId, "--store-id", Store, .. options]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains(expected, result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Update_InvalidPlaytestId_IsRejectedWithoutARequest(string id)
    {
        var handler = Handler(Created);

        var result = await InvokeAsync(
            handler, "playtest", "update", id, "--store-id", Store, "--name", "Beta", "--confirm");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains($"Invalid playtest-id '{id}'", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_WithOnlyAStartDate_LeavesTheMergedCheckToTheService()
    {
        var handler = Handler(Created);

        var result = await InvokeAsync(
            handler, "playtest", "update", PlaytestId, "--store-id", Store, "--start", "2026-11-01", "--confirm");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            """{"startDate":"2026-11-01T00:00:00.0000000\u002B00:00"}""", Assert.Single(handler.Requests).Body);
    }

    [Fact]
    public async Task Update_WithOnlyAnEndDate_SendsOnlyTheEndDate()
    {
        var handler = Handler(Created);

        var result = await InvokeAsync(
            handler, "playtest", "update", PlaytestId, "--store-id", Store, "--end", "2026-11-30", "--confirm");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            """{"endDate":"2026-11-30T00:00:00.0000000\u002B00:00"}""", Assert.Single(handler.Requests).Body);
    }

    [Theory]
    [InlineData("playtest_precondition_failed", true, "The etag in --if-match is no longer current")]
    [InlineData("playtest_precondition_failed", false, "The playtest changed while this update ran")]
    [InlineData(null, true, "The etag in --if-match is no longer current")]
    [InlineData(null, false, "The playtest changed while this update ran")]
    public async Task Update_PreconditionFailed_ExplainsAndSaysHowToRetry(string? code, bool withIfMatch, string expected)
    {
        var body = code is null
            ? JsonSerializer.Serialize(new { status = 412 })
            : JsonSerializer.Serialize(new { status = 412, code, detail = "The playtest changed." });
        var handler = new XboxPartnerHttpMessageHandler(Response(body, HttpStatusCode.PreconditionFailed));
        string[] ifMatch = withIfMatch ? ["--if-match", "\"v0\""] : [];

        var result = await InvokeAsync(
            handler, ["playtest", "update", PlaytestId, "--store-id", Store, "--name", "Beta", .. ifMatch, "--confirm"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains(expected, result.Stderr, StringComparison.Ordinal);
        Assert.Contains($"playtest get {PlaytestId} --store-id {Store}", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("unknown whether", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_Ended_AsksForAFutureEndDate()
    {
        var body = JsonSerializer.Serialize(new { status = 409, code = "playtest_ended", detail = "Ended." });

        var result = await InvokeAsync(
            new XboxPartnerHttpMessageHandler(Response(body, HttpStatusCode.Conflict)),
            "playtest", "update", PlaytestId, "--store-id", Store, "--name", "Beta", "--confirm");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("unless you also pass a future --end", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("may already exist", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_Rejection_ExitsWithOne()
    {
        var body = JsonSerializer.Serialize(new { status = 400, code = "invalid_playtest_request", detail = "Bad." });

        var result = await InvokeAsync(
            new XboxPartnerHttpMessageHandler(Response(body, HttpStatusCode.BadRequest)),
            "playtest", "update", PlaytestId, "--store-id", Store, "--name", "Beta", "--confirm");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.DoesNotContain("unknown whether", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task Update_ServerErrorAfterSending_ExitsWithOutcomeUnknown(HttpStatusCode status)
    {
        var body = JsonSerializer.Serialize(new { status = (int)status });

        var result = await InvokeAsync(
            new XboxPartnerHttpMessageHandler(Response(body, status)),
            "playtest", "update", PlaytestId, "--store-id", Store, "--name", "Beta", "--confirm");

        Assert.Equal(PlaytestCommandFactory.OutcomeUnknownExitCode, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("unknown whether it was applied", result.Stderr, StringComparison.Ordinal);
        Assert.Contains($"playtest get {PlaytestId} --store-id {Store}", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    public async Task Update_UnreadableSuccessBody_ExitsWithOutcomeUnknown(string responseBody)
    {
        var result = await InvokeAsync(
            Handler(responseBody), "playtest", "update", PlaytestId, "--store-id", Store, "--name", "Beta", "--confirm");

        Assert.Equal(PlaytestCommandFactory.OutcomeUnknownExitCode, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains($"playtest get {PlaytestId} --store-id {Store}", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("response-correlation", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("create", "--name", "Beta", "--start", "2026-11-01", "--end", "2026-11-02")]
    [InlineData("update", PlaytestId, "--name", "Beta")]
    public async Task Writes_RefuseAMalformedStoreIdFromTheLookupWithoutSending(string verb, params string[] options)
    {
        var games = """{"games":[{"xboxTitleId":"1234ABCD","storeId":"9N0000000000; printf x"}]}""";
        var handler = new XboxPartnerHttpMessageHandler(
            Response(games), Response("""{"status":500}""", HttpStatusCode.InternalServerError));

        var result = await InvokeAsync(
            handler, ["playtest", verb, .. options, "--xbox-title-id", "1234ABCD", "--confirm"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Single(handler.Requests);
        Assert.Contains("has a Store ID that isn't valid", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("unknown whether", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_NotEditable_PointsToPlaytestGet()
    {
        var body = JsonSerializer.Serialize(
            new { status = 409, code = "playtest_not_editable", detail = "The playtest is publishing." });
        var handler = new XboxPartnerHttpMessageHandler(Response(body, HttpStatusCode.Conflict));

        var result = await InvokeAsync(
            handler, "playtest", "update", PlaytestId, "--store-id", Store, "--name", "Beta", "--confirm");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("The playtest's status doesn't allow changes right now", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("playtest get", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("may already exist", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_PackagePlatformMismatch_NamesThePackageOptions()
    {
        var body = JsonSerializer.Serialize(new
        {
            status = 400,
            code = "playtest_package_platform_mismatch",
            detail = "The package is a console package. Send it as consolePackageId.",
        });
        var handler = new XboxPartnerHttpMessageHandler(Response(body, HttpStatusCode.BadRequest));

        var result = await InvokeAsync(
            handler, "playtest", "update", PlaytestId, "--store-id", Store,
            "--pc-package-id", "33333333-4444-5555-6666-777777777777", "--confirm");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("--console-package-id", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("consolePackageId", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_WithoutConfirmationWhenNotInteractive_SendsNothing()
    {
        var handler = Handler(Created);

        var result = await InvokeAsync(
            handler, "playtest", "update", PlaytestId, "--store-id", Store, "--name", "Beta");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("returns to draft", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_PromptDescribesClearedTextAndUtcEnd()
    {
        var handler = Handler(Created);

        var result = await InvokeAsync(
            handler, "playtest", "update", PlaytestId, "--store-id", Store,
            "--description", "", "--long-description", "x", "--end", "2026-11-15T17:00:00-07:00");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains(
            $"Updating playtest {PlaytestId} of game {Store} under seller 1234567: clear the description, long description, end to 2026-11-16 00:00 UTC.",
            result.Stderr,
            StringComparison.Ordinal);
    }

    private const string Audience = "1152921504606846976";

    private const string OtherAudience = "1152921504606846977";

    [Fact]
    public async Task Create_SendsEachAudienceIdOnceInTheOrderGiven()
    {
        var handler = Handler(Created);

        var result = await InvokeAsync(
            handler, "playtest", "create", "--store-id", Store, "--name", "Beta", "--start", "2026-11-01",
            "--end", "2026-11-02", "--audience-id", $" {OtherAudience} ", "--audience-id", Audience,
            "--audience-id", OtherAudience, "--confirm");

        Assert.Equal(0, result.ExitCode);
        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body!);
        Assert.Equal(
            $$"""["{{OtherAudience}}","{{Audience}}"]""",
            body.RootElement.GetProperty("audienceIds").GetRawText());
    }

    [Theory]
    [InlineData(new[] { Audience, OtherAudience }, $$"""{"audienceIds":["{{Audience}}","{{OtherAudience}}"]}""")]
    [InlineData(new[] { "" }, """{"audienceIds":[]}""")]
    [InlineData(new[] { " " }, """{"audienceIds":[]}""")]
    public async Task Update_AudienceIds_ReplaceOrClearTheAudiences(string[] ids, string expected)
    {
        var handler = Handler(Created);
        var options = ids.SelectMany(id => new[] { "--audience-id", id });

        var result = await InvokeAsync(
            handler, ["playtest", "update", PlaytestId, "--store-id", Store, .. options, "--confirm"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, Assert.Single(handler.Requests).Body);
    }

    [Theory]
    [InlineData("update", "alone to clear the audiences", "--audience-id", "", "--audience-id", Audience)]
    [InlineData("update", "Invalid --audience-id 'a b'", "--audience-id", "a b")]
    [InlineData("update", "Invalid --audience-id 'x/y'", "--audience-id", Audience, "--audience-id", "x/y")]
    [InlineData("update", "Invalid --audience-id '..'", "--audience-id", "..")]
    [InlineData("create", "--audience-id cannot be empty", "--audience-id", "")]
    [InlineData("create", "Invalid --audience-id '.'", "--audience-id", ".")]
    public async Task InvalidAudienceIds_AreRejectedWithoutARequest(string verb, string expected, params string[] options)
    {
        var handler = Handler(Created);
        string[] target = verb == "create"
            ? ["--name", "Beta", "--start", "2026-11-01", "--end", "2026-11-02"]
            : [PlaytestId];

        var result = await InvokeAsync(
            handler, ["playtest", verb, .. target, "--store-id", Store, .. options, "--confirm"]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains(expected, result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AudienceId_LongerThan128Characters_IsRejected()
    {
        var handler = Handler(Created, Created);

        var accepted = await InvokeAsync(
            handler, "playtest", "update", PlaytestId, "--store-id", Store, "--audience-id", new string('a', 128), "--confirm");
        var refused = await InvokeAsync(
            handler, "playtest", "update", PlaytestId, "--store-id", Store, "--audience-id", new string('a', 129), "--confirm");

        Assert.Equal(0, accepted.ExitCode);
        Assert.NotEqual(0, refused.ExitCode);
        Assert.Single(handler.Requests);
        Assert.Contains("up to 128", refused.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new[] { Audience, OtherAudience }, $"audiences to {Audience}, {OtherAudience}.")]
    [InlineData(new[] { "" }, "clear the audiences, so it can't be published until it has one.")]
    public async Task Update_PromptDescribesTheAudiences(string[] ids, string expected)
    {
        var handler = Handler(Created);
        var options = ids.SelectMany(id => new[] { "--audience-id", id });

        var result = await InvokeAsync(handler, ["playtest", "update", PlaytestId, "--store-id", Store, .. options]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains(expected, result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_PromptNamesTheAudiences()
    {
        var handler = Handler(Created);

        var result = await InvokeAsync(
            handler, "playtest", "create", "--store-id", Store, "--name", "Beta", "--start", "2026-11-01",
            "--end", "2026-11-02", "--audience-id", Audience, "--audience-id", OtherAudience);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains(
            $"to 2026-11-02 00:00 UTC, for audiences {Audience}, {OtherAudience}.", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("create", "--name", "Beta", "--start", "2026-11-01", "--end", "2026-11-02")]
    [InlineData("update", PlaytestId)]
    public async Task UnknownAudience_PointsToTheAudienceList(string verb, params string[] options)
    {
        var body = JsonSerializer.Serialize(
            new { status = 400, code = "playtest_audience_not_found", detail = "Upstream detail." });
        var handler = new XboxPartnerHttpMessageHandler(Response(body, HttpStatusCode.BadRequest));

        var result = await InvokeAsync(
            handler, ["playtest", verb, .. options, "--store-id", Store, "--audience-id", Audience, "--confirm"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("isn't one of the seller's audiences", result.Stderr, StringComparison.Ordinal);
        Assert.Contains(
            "audience list --seller-id 1234567 --base-url https://gateway.example", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Upstream detail.", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("unknown whether", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_PackagesAloneReplaceOrRemoveOnlyTheGivenSlots()
    {
        var handler = Handler(Created);
        string[] arguments =
            ["playtest", "update", PlaytestId, "--store-id", Store, "--pc-package-id", "22222222-3333-4444-5555-666666666666", "--console-package-id", ""];

        var prompt = await InvokeAsync(handler, arguments);
        var result = await InvokeAsync(handler, [.. arguments, "--confirm"]);

        Assert.Equal(1, prompt.ExitCode);
        Assert.Contains(": PC package to 22222222-3333-4444-5555-666666666666, remove the console package.", prompt.Stderr, StringComparison.Ordinal);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("""{"pcPackageId":"22222222-3333-4444-5555-666666666666","consolePackageId":""}""", Assert.Single(handler.Requests).Body);
    }

    private static string WithStatus(string status, string etag = "\"v1\"") =>
        JsonSerializer.Serialize(new { details = new { playtestId = Playtest, name = "Beta", status }, etag });

    private const string Queued = """{"publishJobId":"job1","warnings":[]}""";

    private const string Ready = """{"ready":true,"checks":[]}""";

    [Fact]
    public async Task Publish_SendsIfMatchFromAFreshReadAndWaitsUntilLive()
    {
        var handler = Handler(WithStatus("Draft", "\"v7\""), Ready, Queued, WithStatus("Publishing", "\"v8\""), WithStatus("Live", "\"v9\""));

        var result = await InvokeAsync(
            handler, "playtest", "publish", Playtest, "--store-id", Store, "--confirm", "--output", "json-compact");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(5, handler.Requests.Count);
        var publish = handler.Requests[2];
        Assert.Equal(HttpMethod.Post, publish.Method);
        Assert.Equal($"/xboxpartner/v1/games/{Store}/playtests/{Playtest}/publish", publish.RequestUri!.AbsolutePath);
        Assert.Equal(["\"v7\""], publish.Headers["If-Match"]);
        Assert.Null(publish.Body);
        Assert.False(publish.Headers.ContainsKey("Idempotency-Key"));
        Assert.Contains("\"status\":\"Live\"", result.Stdout, StringComparison.Ordinal);
        Assert.Contains($"Playtest {Playtest} is published and Live.", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publish_Blocked_ListsEachCheckAndExplainsTheAgeRatingRule()
    {
        var body = JsonSerializer.Serialize(new
        {
            status = 422,
            code = "playtest_publish_blocked",
            validation = new object[]
            {
                new { severity = "Error", error = "PlaytestMissingAgeRatings", message = "The game has no age ratings." },
                new { severity = "Warning", documentName = "doc", error = "PlaytestNoDescription", message = "No description." },
            },
        });
        var handler = new XboxPartnerHttpMessageHandler(
            Response(WithStatus("Draft")), Response(Ready), Response(body, HttpStatusCode.UnprocessableEntity));

        var result = await InvokeAsync(handler, "playtest", "publish", Playtest, "--store-id", Store, "--confirm");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("these checks failed", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Error: The game has no age ratings. (PlaytestMissingAgeRatings)", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("complete the age-rating questionnaire in Partner Center", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Warning: doc: No description. (PlaytestNoDescription)", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publish_NoWait_WritesTheResponseAndWarnings()
    {
        var queued = """{"publishJobId":"job1","warnings":[{"severity":"Warning","error":"W1","message":"Heads up."}]}""";
        var handler = Handler(WithStatus("Draft"), Ready, queued);

        var result = await InvokeAsync(
            handler, "playtest", "publish", Playtest, "--store-id", Store, "--no-wait", "--confirm", "--output", "json-compact");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(queued, result.Stdout.Trim());
        Assert.Contains("queued with these warnings", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Warning: Heads up. (W1)", result.Stderr, StringComparison.Ordinal);
    }

    private const string NotReady = """
        {"ready":false,"checks":[
          {"name":"xboxServicesConfigured","status":"checkFailed","code":"xbox_services_check_failed","message":"Couldn't check."},
          {"name":"embargoCoverage","status":"checkFailed","code":"xbox_services_check_failed","message":"Couldn't check."},
          {"name":"other","status":"warning","code":"embargo_not_set","message":"Set an embargo.\r\nBefore it ends."},
          {"name":"skipped","status":"notApplicable","message":"Not shown."},
          {"name":"fine","status":"passed"}]}
        """;

    [Theory]
    [InlineData("y\n", true)]
    [InlineData("n\n", false)]
    public async Task Publish_NotReady_ListsEachWarningOnceAndAsksToPublishAnyway(string input, bool accepted)
    {
        var handler = Handler(WithStatus("Draft"), NotReady, Queued);

        var result = await InvokeAsync(
            handler, true, new StringReader(input), CancellationToken.None,
            "playtest", "publish", Playtest, "--store-id", Store, "--no-wait", "--output", "json-compact");

        Assert.Equal(accepted ? 0 : 1, result.ExitCode);
        Assert.Equal(accepted ? 3 : 2, handler.Requests.Count);
        var readiness = handler.Requests[1];
        Assert.Equal(HttpMethod.Get, readiness.Method);
        Assert.Equal($"/xboxpartner/v1/games/{Store}/playtests/{Playtest}/readiness", readiness.RequestUri!.AbsolutePath);
        Assert.Contains("sellerId=", readiness.RequestUri.Query, StringComparison.Ordinal);
        Assert.Equal(accepted ? Queued : "", result.Stdout.Trim());
        Assert.Contains("Review these before you publish:\n  Couldn't check.\n  Set an embargo.\n  Before it ends.\n",
            result.Stderr.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Equal(2, result.Stderr.Split("Couldn't check.").Length);
        Assert.DoesNotContain("Not shown.", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Publish this playtest anyway?", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publish_NotReadyWithConfirm_ListsTheWarningsAndPublishesWithoutAsking()
    {
        var handler = Handler(WithStatus("Draft"), NotReady, Queued);

        var result = await InvokeAsync(
            handler, "playtest", "publish", Playtest, "--store-id", Store, "--no-wait", "--confirm", "--output", "json-compact");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(HttpMethod.Post, handler.Requests[2].Method);
        Assert.Equal(Queued, result.Stdout.Trim());
        Assert.Contains("Set an embargo.", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("anyway?", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"status":404,"code":"not_found"}""", HttpStatusCode.NotFound)]
    [InlineData("""{"status":502}""", HttpStatusCode.BadGateway)]
    [InlineData("not json", HttpStatusCode.OK)]
    public async Task Publish_ChecksThatCannotRun_AreSkippedAndThePublishGoesAhead(string body, HttpStatusCode status)
    {
        var handler = new XboxPartnerHttpMessageHandler(
            Response(WithStatus("Draft")), Response(body, status), Response(Queued));

        var result = await InvokeAsync(
            handler, "playtest", "publish", Playtest, "--store-id", Store, "--no-wait", "--confirm", "--output", "json-compact");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(Queued, result.Stdout.Trim());
        Assert.Contains(
            "Couldn't run the checks before publishing, so they were skipped.", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publish_NoWaitWithTimeout_IsRejectedBeforeSending()
    {
        var handler = Handler(WithStatus("Draft"));

        var result = await InvokeAsync(
            handler, "playtest", "publish", Playtest, "--store-id", Store, "--no-wait", "--timeout", "1m", "--confirm");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("--no-wait and --timeout cannot be combined.", result.Stderr, StringComparison.Ordinal);
    }

    // A PublishFailed read with the old etag is the previous failure, so the wait goes on.
    // With a new etag it's this publish failing.
    [Theory]
    [InlineData("\"v1\"", 0, 5)]
    [InlineData("\"v2\"", PlaytestCommandFactory.PublishFailedExitCode, 4)]
    public async Task Publish_PublishFailed_FailsOnlyWithANewEtag(string etag, int exitCode, int requests)
    {
        var handler = Handler(
            WithStatus("PublishFailed", "\"v1\""), Ready, Queued, WithStatus("PublishFailed", etag), WithStatus("Live", "\"v3\""));

        var result = await InvokeAsync(handler, "playtest", "publish", Playtest, "--store-id", Store, "--confirm");

        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(requests, handler.Requests.Count);
    }
    [Fact]
    public async Task Publish_PublishFailed_NamesTheErrorCode()
    {
        var failed = JsonSerializer.Serialize(new
        {
            details = new { playtestId = Playtest, name = "Beta", status = "PublishFailed", error = new { code = "IngestionFailed" } },
            etag = "\"v2\"",
        });
        var handler = Handler(WithStatus("Draft"), Ready, Queued, failed);

        var result = await InvokeAsync(handler, "playtest", "publish", Playtest, "--store-id", Store, "--confirm");

        Assert.Equal(PlaytestCommandFactory.PublishFailedExitCode, result.ExitCode);
        Assert.Contains(
            $"Publishing playtest {Playtest} failed. Its error code is IngestionFailed.", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Live", true)]
    [InlineData("Draft", false)]
    public async Task Delete_PromptNamesThePlaytestAndWarnsWhenItIsLive(string status, bool warns)
    {
        var handler = Handler(WithStatus(status));

        var result = await InvokeAsync(
            handler, true, new StringReader("n\n"), CancellationToken.None, "playtest", "delete", Playtest, "--store-id", Store);

        Assert.Equal(1, result.ExitCode);
        Assert.Single(handler.Requests);
        Assert.Contains($"Deleting playtest {Playtest} 'Beta' (status {status},", result.Stderr, StringComparison.Ordinal);
        Assert.Equal(warns, result.Stderr.Contains("Its players lose access to it.", StringComparison.Ordinal));
    }
    [Fact]
    public async Task Publish_ARetryThatFailsAgainBeforeTheFirstPoll_IsReportedAsFailed()
    {
        var handler = Handler(
            WithStatus("PublishFailed", "\"v1\""), Ready, Queued, WithStatus("PublishFailed", "\"v2\""));

        var result = await InvokeAsync(handler, "playtest", "publish", Playtest, "--store-id", Store, "--confirm");

        Assert.Equal(PlaytestCommandFactory.PublishFailedExitCode, result.ExitCode);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task Publish_AnUnquotedEtagThatDidNotChange_KeepsWaitingOnTheOldFailure()
    {
        var handler = Handler(
            WithStatus("PublishFailed", "v1"), Ready, Queued, WithStatus("PublishFailed", "v1"), WithStatus("Live", "v2"));

        var result = await InvokeAsync(handler, "playtest", "publish", Playtest, "--store-id", Store, "--confirm");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(5, handler.Requests.Count);
    }

    [Fact]
    public async Task Publish_ThrottledRead_WaitsForRetryAfterBeforeReadingAgain()
    {
        var now = DateTimeOffset.UnixEpoch;
        var delays = new List<TimeSpan>();
        var dependencies = new XboxPartnerJobDependencies(
            (delay, _) => { delays.Add(delay); now += delay; return Task.CompletedTask; }, () => now, () => false);
        var throttled = Response("""{"status":429}""", HttpStatusCode.TooManyRequests);
        throttled.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
        var handler = new XboxPartnerHttpMessageHandler(
            Response(WithStatus("Draft")), Response(Ready), Response(Queued), throttled, Response(WithStatus("Live", "\"v2\"")));

        var result = await InvokeAsync(
            handler, dependencies, null, CancellationToken.None,
            "playtest", "publish", Playtest, "--store-id", Store, "--confirm");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60)], delays);
    }

    [Fact]
    public async Task Publish_ReportsEachStatusChangeAndThePublishJob()
    {
        var handler = Handler(WithStatus("Draft"), Ready, Queued, WithStatus("Publishing", "\"v2\""), WithStatus("Live", "\"v3\""));

        var result = await InvokeAsync(handler, "playtest", "publish", Playtest, "--store-id", Store, "--confirm");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Publish job: job1", result.Stderr);
        Assert.Contains($"Playtest {Playtest} is now Publishing.", result.Stderr);
        Assert.Contains("is published and Live", result.Stderr);
    }

    [Fact]
    public async Task Publish_CanceledWhileWaiting_ExitsWithFour()
    {
        using var cts = new CancellationTokenSource();
        var dependencies = new XboxPartnerJobDependencies(
            (_, _) => { cts.Cancel(); return Task.CompletedTask; }, () => DateTimeOffset.UnixEpoch, () => false);
        var handler = Handler(WithStatus("Draft"), Ready, Queued, WithStatus("Publishing", "\"v2\""));

        var result = await InvokeAsync(
            handler, dependencies, null, cts.Token, "playtest", "publish", Playtest, "--store-id", Store, "--confirm");

        Assert.Equal(4, result.ExitCode);
    }
    [Fact]
    public async Task Publish_WaitEndsAtTheTimeout_ExitsWithFour()
    {
        var now = DateTimeOffset.UnixEpoch;
        var dependencies = new XboxPartnerJobDependencies(
            (delay, _) => { now += delay; return Task.CompletedTask; }, () => now, () => false);
        var handler = Handler(WithStatus("Draft"), Ready, Queued, WithStatus("Publishing", "\"v2\""), WithStatus("Publishing", "\"v3\""));

        // Reads at 10s and 20s; the wait ends at 25s without a third read.
        var result = await InvokeAsync(
            handler, dependencies, null, CancellationToken.None,
            "playtest", "publish", Playtest, "--store-id", Store, "--timeout", "25s", "--confirm");

        Assert.Equal(PlaytestCommandFactory.PublishNotFinishedExitCode, result.ExitCode);
        Assert.Equal(5, handler.Requests.Count);
        Assert.Empty(result.Stdout);
        Assert.Contains("wasn't published within 25 seconds; its status is Publishing.", result.Stderr, StringComparison.Ordinal);
        Assert.Contains($"playtest get {Playtest} --store-id {Store}", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("publish", "it wasn't published")]
    [InlineData("delete", "a delete is already queued")]
    public async Task PreconditionFailed_ExplainsAndPointsToARead(string verb, string expected)
    {
        var body = JsonSerializer.Serialize(new { status = 412, code = "playtest_precondition_failed" });
        var handler = new XboxPartnerHttpMessageHandler(
            [Response(WithStatus("Draft")), .. ReadinessFor(verb), Response(body, HttpStatusCode.PreconditionFailed)]);

        var result = await InvokeAsync(handler, "playtest", verb, Playtest, "--store-id", Store, "--confirm");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains(expected, result.Stderr, StringComparison.Ordinal);
        Assert.Contains($"playtest get {Playtest} --store-id {Store}", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("publish")]
    [InlineData("delete")]
    public async Task ServerErrorAfterSending_ExitsWithOutcomeUnknown(string verb)
    {
        var handler = new XboxPartnerHttpMessageHandler(
            [Response(WithStatus("Draft")), .. ReadinessFor(verb), Response("""{"status":502}""", HttpStatusCode.BadGateway)]);

        var result = await InvokeAsync(handler, "playtest", verb, Playtest, "--store-id", Store, "--confirm");

        Assert.Equal(PlaytestCommandFactory.OutcomeUnknownExitCode, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("unknown whether it was queued", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("response-correlation", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("publish", "not json")]
    [InlineData("publish", "[]")]
    [InlineData("delete", "not json")]
    [InlineData("delete", "[]")]
    [InlineData("delete", """{"deleteJobId":"job1"}""")]
    [InlineData("delete", """{"deleteJobId":"job1","status":""}""")]
    public async Task MalformedSuccessResponse_ExitsWithOutcomeUnknown(string verb, string body)
    {
        var handler = Handler([WithStatus("Draft"), .. verb == "publish" ? [Ready] : Array.Empty<string>(), body]);
        string[] arguments = ["playtest", verb, Playtest, "--store-id", Store, "--confirm"];
        if (verb == "publish")
        {
            arguments = [.. arguments, "--no-wait"];
        }

        var result = await InvokeAsync(handler, arguments);

        Assert.Equal(PlaytestCommandFactory.OutcomeUnknownExitCode, result.ExitCode);
        Assert.Equal(verb == "publish" ? 3 : 2, handler.Requests.Count);
        Assert.Empty(result.Stdout);
        Assert.Contains("response-correlation", result.Stderr, StringComparison.Ordinal);
    }

    private static string WithoutEtag(string status) =>
        JsonSerializer.Serialize(new { details = new { playtestId = Playtest, name = "Beta", status } });

    // Republishing a PublishFailed playtest: a read without an etag is new only once its status differs
    // from the one read before publishing, and every read after that counts.
    [Fact]
    public async Task Publish_ReadsWithoutAnEtag_AreNewOnlyOnceTheStatusChanges()
    {
        var handler = Handler(
            WithStatus("PublishFailed", "\"v1\""), Ready, Queued,
            WithoutEtag("PublishFailed"), WithoutEtag("Publishing"), WithoutEtag("PublishFailed"));

        var result = await InvokeAsync(handler, "playtest", "publish", Playtest, "--store-id", Store, "--confirm");

        Assert.Equal(PlaytestCommandFactory.PublishFailedExitCode, result.ExitCode);
        Assert.Equal(6, handler.Requests.Count);
        Assert.Contains($"Playtest {Playtest} is now Publishing.", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publish_AReadWithoutAnEtagAndTheOldFailure_KeepsWaiting()
    {
        var handler = Handler(
            WithStatus("PublishFailed", "\"v1\""), Ready, Queued, WithoutEtag("PublishFailed"), WithoutEtag("Live"));

        var result = await InvokeAsync(handler, "playtest", "publish", Playtest, "--store-id", Store, "--confirm");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(5, handler.Requests.Count);
    }

    [Theory]
    [InlineData("publish", null)]
    public async Task ReadWithoutAUsableEtag_SendsNothing(string verb, string? etag)
    {
        var read = etag is null
            ? JsonSerializer.Serialize(new { details = new { playtestId = Playtest, status = "Draft" } })
            : WithStatus("Draft", etag);
        var handler = Handler(read);

        var result = await InvokeAsync(handler, "playtest", verb, Playtest, "--store-id", Store, "--confirm");

        Assert.Equal(1, result.ExitCode);
        Assert.Single(handler.Requests);
        Assert.Contains("without a usable etag", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("publish", false, "", false)]
    [InlineData("publish", true, "n\n", false)]
    [InlineData("publish", true, "y\n", true)]
    [InlineData("delete", false, "", false)]
    [InlineData("delete", true, "n\n", false)]
    [InlineData("delete", true, "y\n", true)]
    public async Task PublishAndDelete_WithoutConfirm_FailsClosedOrPrompts(
        string verb, bool interactive, string input, bool accepted)
    {
        var handler = verb == "publish"
            ? Handler(WithStatus("Draft"), Ready, Queued)
            : Handler(WithStatus("Draft"), """{"status":"Deleted"}""");
        string[] arguments = ["playtest", verb, Playtest, "--store-id", Store, "--no-wait"];
        if (verb == "delete")
        {
            arguments = arguments[..^1];
        }

        var result = await InvokeAsync(handler, interactive, new StringReader(input), CancellationToken.None, arguments);

        Assert.Equal(accepted ? 0 : 1, result.ExitCode);
        Assert.Equal((accepted ? 2 : 1) + (verb == "publish" ? 1 : 0), handler.Requests.Count);
        Assert.Contains(
            verb == "publish" ? $"Publishing playtest {Playtest}" : "This can't be undone.",
            result.Stderr,
            StringComparison.Ordinal);
        if (!accepted)
        {
            Assert.Empty(result.Stdout);
            Assert.Contains(interactive ? $"Playtest {verb} canceled." : "Run again with --confirm", result.Stderr);
        }
    }

    [Theory]
    [InlineData("Deleted", "deleted.")]
    [InlineData("Deleting", "was queued. Check it with:")]
    public async Task Delete_SendsIfMatchAndSaysWhetherItIsDone(string status, string expected)
    {
        var response = JsonSerializer.Serialize(new { deleteJobId = "job1", status });
        var handler = Handler(WithStatus("Draft", "\"v3\""), response);

        var result = await InvokeAsync(
            handler, "playtest", "delete", Playtest, "--store-id", Store, "--no-wait", "--confirm", "--output", "json-compact");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(2, handler.Requests.Count);
        var delete = handler.Requests[1];
        Assert.Equal(HttpMethod.Delete, delete.Method);
        Assert.Equal($"/xboxpartner/v1/games/{Store}/playtests/{Playtest}", delete.RequestUri!.AbsolutePath);
        Assert.Equal(["\"v3\""], delete.Headers["If-Match"]);
        Assert.Equal(response, result.Stdout.Trim());
        Assert.Contains(expected, result.Stderr, StringComparison.Ordinal);
    }

    private const string DeleteQueued = """{"deleteJobId":"job1","status":"Deleting"}""";

    // Reads with the etag from before the delete are the old version and are skipped.
    [Theory]
    [InlineData("Deleted")]
    [InlineData(null)]
    public async Task Delete_WaitsUntilDeletedOrNotFound_AndWritesTheDeleteResponse(string? finalStatus)
    {
        var final = finalStatus is null
            ? Response("""{"status":404,"code":"playtest_not_found"}""", HttpStatusCode.NotFound)
            : Response(WithStatus(finalStatus, "\"v3\""));
        var handler = new XboxPartnerHttpMessageHandler(
            Response(WithStatus("Draft", "\"v1\"")), Response(DeleteQueued),
            Response(WithStatus("Draft", "\"v1\"")), Response(WithStatus("Deleting", "\"v2\"")), final);

        var result = await InvokeAsync(
            handler, "playtest", "delete", Playtest, "--store-id", Store, "--confirm", "--output", "json-compact");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(5, handler.Requests.Count);
        Assert.Equal(DeleteQueued, result.Stdout.Trim());
        Assert.Contains($"The delete of playtest {Playtest} was queued.", result.Stderr, StringComparison.Ordinal);
        Assert.Contains($"Playtest {Playtest} is now Deleting.", result.Stderr, StringComparison.Ordinal);
        Assert.Contains($"Playtest {Playtest} deleted.", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_AGameNotFoundWhileWaiting_IsNotADeletion()
    {
        var handler = new XboxPartnerHttpMessageHandler(
            Response(WithStatus("Draft", "\"v1\"")), Response(DeleteQueued),
            Response("""{"status":404,"code":"game_not_found"}""", HttpStatusCode.NotFound));

        var result = await InvokeAsync(
            handler, "playtest", "delete", Playtest, "--store-id", Store, "--confirm", "--output", "json-compact");

        Assert.Equal(PlaytestCommandFactory.PublishNotFinishedExitCode, result.ExitCode);
        Assert.Contains($"No game with Store ID {Store} was found for seller ", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("HTTP 404.", result.Stderr, StringComparison.Ordinal);
        Assert.Contains($"Could not read playtest {Playtest}.", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain($"Playtest {Playtest} deleted.", result.Stderr, StringComparison.Ordinal);
    }

    // No status means a delete failed, and a queued delete can keep the old status or the old etag for a
    // while, so only Deleted ends the wait, whichever of the two has changed.
    [Fact]
    public async Task Delete_AnyStatusButDeleted_KeepsWaiting()
    {
        var handler = Handler(
            WithStatus("Live", "\"v1\""), DeleteQueued,
            WithStatus("Live", "\"v2\""), WithoutEtag("Live"), WithStatus("Deleted", "\"v1\""));

        var result = await InvokeAsync(handler, "playtest", "delete", Playtest, "--store-id", Store, "--confirm");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(5, handler.Requests.Count);
        Assert.Contains($"Playtest {Playtest} is now Deleted.", result.Stderr, StringComparison.Ordinal);
        Assert.Contains($"Playtest {Playtest} deleted.", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_DeletedAtOnce_DoesNotWait()
    {
        var handler = Handler(WithStatus("Draft"), """{"status":"Deleted"}""");

        var result = await InvokeAsync(handler, "playtest", "delete", Playtest, "--store-id", Store, "--confirm");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(2, handler.Requests.Count);
        Assert.DoesNotContain("Waiting", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_WaitEndsAtTheTimeout_ExitsWithFour()
    {
        var now = DateTimeOffset.UnixEpoch;
        var dependencies = new XboxPartnerJobDependencies(
            (delay, _) => { now += delay; return Task.CompletedTask; }, () => now, () => false);
        var handler = Handler(WithStatus("Draft"), DeleteQueued, WithStatus("Deleting", "\"v2\""));

        var result = await InvokeAsync(
            handler, dependencies, null, CancellationToken.None,
            "playtest", "delete", Playtest, "--store-id", Store, "--timeout", "15s", "--confirm");

        Assert.Equal(PlaytestCommandFactory.DeleteNotFinishedExitCode, result.ExitCode);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Empty(result.Stdout);
        Assert.Contains("wasn't deleted within 15 seconds; its status is Deleting.", result.Stderr, StringComparison.Ordinal);
        Assert.Contains($"playtest get {Playtest} --store-id {Store}", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_NoWaitWithTimeout_IsRejectedBeforeSending()
    {
        var handler = Handler(WithStatus("Draft"));

        var result = await InvokeAsync(
            handler, "playtest", "delete", Playtest, "--store-id", Store, "--no-wait", "--timeout", "1m", "--confirm");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("--no-wait and --timeout cannot be combined.", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("publish", "playtest_not_publishable")]
    [InlineData("delete", "playtest_not_deletable")]
    public async Task NotAllowedInItsStatus_ExplainsTheRefusal(string verb, string code)
    {
        var body = JsonSerializer.Serialize(new { status = 409, code, detail = "Upstream detail." });
        var handler = new XboxPartnerHttpMessageHandler(
            [Response(WithStatus("Live")), .. ReadinessFor(verb), Response(body, HttpStatusCode.Conflict)]);

        var result = await InvokeAsync(handler, "playtest", verb, Playtest, "--store-id", Store, "--confirm");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Check its status with 'xbox playtest get'.", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("HTTP 409.", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"status":422,"code":"playtest_publish_blocked"}""")]
    [InlineData("""{"status":422,"code":"playtest_publish_blocked","validation":"bad"}""")]
    public async Task Publish_BlockedWithoutReadableChecks_StillFails(string body)
    {
        var handler = new XboxPartnerHttpMessageHandler(
            Response(WithStatus("Draft")), Response(Ready), Response(body, HttpStatusCode.UnprocessableEntity));

        var result = await InvokeAsync(handler, "playtest", "publish", Playtest, "--store-id", Store, "--confirm");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("422", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publish_AFailedReadWhileWaiting_KeepsWaiting()
    {
        var handler = new XboxPartnerHttpMessageHandler(
            Response(WithStatus("Draft")), Response(Ready), Response(Queued),
            Response("""{"status":502}""", HttpStatusCode.BadGateway), Response(WithStatus("Live", "\"v2\"")));

        var result = await InvokeAsync(handler, "playtest", "publish", Playtest, "--store-id", Store, "--confirm");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(5, handler.Requests.Count);
        Assert.Contains("\"Live\"", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publish_ReadsKeepFailingWhileWaiting_ExitsWithFour()
    {
        var failures = Enumerable.Range(0, TestAccountJobWaiter.MaxConsecutiveReadFailures)
            .Select(_ => Response("""{"status":502}""", HttpStatusCode.BadGateway));
        var handler = new XboxPartnerHttpMessageHandler(
            [Response(WithStatus("Draft")), Response(Ready), Response(Queued), .. failures]);

        var result = await InvokeAsync(handler, "playtest", "publish", Playtest, "--store-id", Store, "--confirm");

        Assert.Equal(PlaytestCommandFactory.PublishNotFinishedExitCode, result.ExitCode);
        Assert.Equal(3 + TestAccountJobWaiter.MaxConsecutiveReadFailures, handler.Requests.Count);
        Assert.Empty(result.Stdout);
        Assert.Contains($"Could not read playtest {Playtest}.", result.Stderr, StringComparison.Ordinal);
        Assert.Contains($"playtest get {Playtest} --store-id {Store}", result.Stderr, StringComparison.Ordinal);
    }

    // Publish reads its checks before asking; delete doesn't.
    private static HttpResponseMessage[] ReadinessFor(string verb) => verb == "publish" ? [Response(Ready)] : [];

    private static HttpResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
        response.Headers.Add("x-ms-correlation-id", "response-correlation");
        return response;
    }

    private static XboxPartnerHttpMessageHandler Handler(params string[] responses) =>
        new(responses.Select(response => Response(response)).ToArray());

    private static Mock<AuthenticationService> CreateAuth()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["AzureAd:ClientId"] = "test-client",
                ["AzureAd:TenantId"] = "test-tenant",
                ["AzureAd:Scopes"] = "api://test/.default",
            }).Build();
        var account = Mock.Of<IAccount>(
            account => account.HomeAccountId == new AccountId("id", "user", "test-tenant"));
        var auth = new Mock<AuthenticationService>(configuration);
        auth.Setup(service => service.GetCachedAccountAsync(It.IsAny<CancellationToken>())).ReturnsAsync(account);
        auth.Setup(service => service.AcquireTokenSilentAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
            new AuthenticationResult(
                "access-token", false, "user", DateTimeOffset.UtcNow.AddHours(1),
                DateTimeOffset.UtcNow.AddHours(1), "test-tenant", account,
                "id-token", ["api://test/.default"], Guid.NewGuid()));
        return auth;
    }

    private static Task<CapturedInvocation> InvokeAsync(
        HttpMessageHandler handler, params string[] arguments) =>
        InvokeAsync(handler, CancellationToken.None, arguments);

    private static Task<CapturedInvocation> InvokeAsync(
        HttpMessageHandler handler, CancellationToken cancellationToken, params string[] arguments) =>
        InvokeAsync(handler, interactive: false, input: null, cancellationToken, arguments);

    private static Task<CapturedInvocation> InvokeAsync(
        HttpMessageHandler handler,
        bool interactive,
        TextReader? input,
        CancellationToken cancellationToken,
        params string[] arguments) =>
        InvokeAsync(
            handler,
            new XboxPartnerJobDependencies((_, _) => Task.CompletedTask, () => DateTimeOffset.UtcNow, () => interactive),
            input,
            cancellationToken,
            arguments);

    private static async Task<CapturedInvocation> InvokeAsync(
        HttpMessageHandler handler,
        XboxPartnerJobDependencies dependencies,
        TextReader? input,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        var auth = CreateAuth();
        using var httpClient = new HttpClient(handler);
        var root = Program.BuildRootCommand(playtestCommandFactory: () => PlaytestCommandFactory.Create(
            configuration => new XboxPartnerClient(configuration, httpClient, auth.Object),
            dependencies));
        var args = new List<string>(arguments) { "--base-url", "https://gateway.example", "--seller-id", "1234567" };

        var oldOut = Console.Out;
        var oldError = Console.Error;
        var oldIn = Console.In;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            if (input is not null)
            {
                Console.SetIn(input);
            }

            ContextResolver.ReadIdentityAsync = _ => SignedInIdentity.FromCacheAsync(auth.Object, CancellationToken.None);
            ContextStore.DefaultDirectory = () => Path.Combine(Path.GetTempPath(), $"playtest-tests-{Guid.NewGuid():N}");
            var parseResult = await Program.ResolveContextAsync(
                root.Parse(args.ToArray(), Program.NoResponseFileConfig), args);
            var exitCode = parseResult is null ? 1 : await parseResult.InvokeAsync(cancellationToken: cancellationToken);
            return new CapturedInvocation(exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldError);
            Console.SetIn(oldIn);
            TestEnvironment.Reset();
        }
    }

    private sealed record CapturedInvocation(int ExitCode, string Stdout, string Stderr);

    private sealed class BlockingReader(ManualResetEventSlim started, ManualResetEventSlim release) : TextReader
    {
        public override string? ReadLine()
        {
            started.Set();
            release.Wait(TimeSpan.FromSeconds(30));
            return "y";
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        public string? CorrelationId { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            CorrelationId = request.Headers.TryGetValues("x-ms-correlation-id", out var ids) ? ids.Single() : null;
            throw exception;
        }
    }
}
