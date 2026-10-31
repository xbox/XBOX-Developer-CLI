// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text;
using System.Text.Json;
using XboxDevCLI.Commands;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

[Collection("ConsoleRedirect")]
public class TestAccountGetTests
{
    private const string Email = "ABCD-tester00007@xboxtest.com";
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("ABCD-tester00007@xboxtest.com", "ABCD-tester", "-tester")]
    [InlineData("abcd-tester00007@XBOXTEST.COM", "abcd-tester", "-tester")]
    [InlineData(" ABCD-tester00007@xboxtest.com ", "ABCD-tester", "-tester")]
    public void TryParse_StripsTheFiveDigitIndex(string value, string template, string withoutGroup)
    {
        Assert.Null(TestAccountLookup.TryParse(value, out TestAccountEmail? email));
        Assert.Equal(template, email!.Templates[0]);
        Assert.Contains(value.Trim().Split('@')[0], email.Templates);
        Assert.Contains(withoutGroup, email.Templates);
    }

    [Fact]
    public void TryParse_ConsoleStyleAddressKeepsTheWholeLocalPart()
    {
        Assert.Null(TestAccountLookup.TryParse("nod-console@xboxtest.com", out TestAccountEmail? email));
        Assert.Equal(["nod-console", "-console"], email!.Templates);
    }

    [Fact]
    public void TryParse_ShortDigitTailIsNotAnIndex()
    {
        Assert.Null(TestAccountLookup.TryParse("nod1234@xboxtest.com", out TestAccountEmail? email));
        Assert.Equal(["nod1234"], email!.Templates);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("noatsign")]
    [InlineData("@xboxtest.com")]
    [InlineData("a@b@xboxtest.com")]
    [InlineData("a b@xboxtest.com")]
    [InlineData("tester00001@example.com")]
    [InlineData("a<b@xboxtest.com")]
    [InlineData("ABCD-tester.@xboxtest.com")]
    [InlineData(".ABCD-tester@xboxtest.com")]
    [InlineData("-tester00000@xboxtest.com")]
    [InlineData("ABCD..tester@xboxtest.com")]
    [InlineData("ABCD-tést00000@xboxtest.com")]
    [InlineData("ABCD+tester@xboxtest.com")]
    public void TryParse_RejectsBadAddresses(string value)
    {
        Assert.NotNull(TestAccountLookup.TryParse(value, out TestAccountEmail? email));
        Assert.Null(email);
    }

    [Fact]
    public async Task Get_ActiveBatchPrintsJsonAndExitsZero()
    {
        var handler = Respond(ListJson("2027-01-01T00:00:00Z"), DetailJson(42, "2027-01-01T00:00:00Z"));

        Invocation result = await InvokeAsync(["test-account", "get", Email, "--seller-id", "1234567"], handler);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Stderr);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("?sellerId=1234567&pageSize=100", handler.Requests[0].RequestUri?.Query);
        Assert.Equal("/xboxpartner/v1/testaccounts/batches/42", handler.Requests[1].RequestUri?.AbsolutePath);
        using JsonDocument json = JsonDocument.Parse(result.Stdout);
        Assert.False(json.RootElement.GetProperty("expired").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("expiredOn").ValueKind);
        Assert.Equal(42, json.RootElement.GetProperty("batch").GetProperty("id").GetInt64());
        Assert.Equal(Email, json.RootElement.GetProperty("account").GetProperty("email").GetString());
    }

    [Fact]
    public async Task Get_ExpiredBatchIsFlagged()
    {
        var handler = Respond(ListJson("2026-01-01T00:00:00Z"), DetailJson(42, "2026-01-01T00:00:00Z"));

        Invocation result = await InvokeAsync(["test-account", "get", Email.ToLowerInvariant()], handler);

        Assert.Equal(0, result.ExitCode);
        using JsonDocument json = JsonDocument.Parse(result.Stdout);
        Assert.True(json.RootElement.GetProperty("expired").GetBoolean());
        Assert.StartsWith("2026-01-01", json.RootElement.GetProperty("expiredOn").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_BatchWithoutExpiryIsNotExpired()
    {
        var handler = Respond(ListJson(null), DetailJson(42, null));

        Invocation result = await InvokeAsync(["test-account", "get", Email], handler);

        Assert.Equal(0, result.ExitCode);
        using JsonDocument json = JsonDocument.Parse(result.Stdout);
        Assert.False(json.RootElement.GetProperty("expired").GetBoolean());
    }

    [Fact]
    public async Task Get_TableShowsOneRow()
    {
        var handler = Respond(ListJson("2026-01-01T00:00:00Z"), DetailJson(42, "2026-01-01T00:00:00Z"));

        Invocation result = await InvokeAsync(["test-account", "get", Email, "--output", "table"], handler);

        Assert.Equal(0, result.ExitCode);
        string[] lines = result.Stdout.TrimEnd().Split(Environment.NewLine);
        Assert.Equal(
            ["EMAIL", "GAMERTAG", "BATCH ID", "EMAIL TEMPLATE", "EXPIRES (UTC)", "EXPIRED"],
            System.Text.RegularExpressions.Regex.Split(lines[0].Trim(), @"\s{2,}"));
        string row = lines[^1].TrimEnd();
        Assert.StartsWith(Email, row, StringComparison.Ordinal);
        Assert.Contains("2026-01-01 00:00", row, StringComparison.Ordinal);
        Assert.EndsWith(" yes", row, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_DeletedAccountIsStillFound()
    {
        var handler = Respond(
            ListJson("2026-01-01T00:00:00Z"),
            DetailJson(42, "2026-01-01T00:00:00Z", isDeleted: true));

        Invocation result = await InvokeAsync(["test-account", "get", Email], handler);

        Assert.Equal(0, result.ExitCode);
        using JsonDocument json = JsonDocument.Parse(result.Stdout);
        Assert.True(json.RootElement.GetProperty("account").GetProperty("isDeleted").GetBoolean());
        Assert.Contains($"{Email} is deleted; its batch still lists it.", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_NoMatchingTemplateExitsOneWithoutReadingBatches()
    {
        var handler = Respond("""{"batches":[]}""");

        Invocation result = await InvokeAsync(["test-account", "get", Email], handler);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains(
            "was not found among the batches this seller can list",
            result.Stderr,
            StringComparison.Ordinal);
        Assert.Contains("--seller-id", result.Stderr, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Get_TemplateMatchesButAccountMissingExitsOne()
    {
        var handler = Respond(ListJson("2027-01-01T00:00:00Z"), DetailJson(42, "2027-01-01T00:00:00Z", "other@xboxtest.com"));

        Invocation result = await InvokeAsync(["test-account", "get", Email], handler);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("was not found", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_PrefixMatchesOnlyAreIgnored()
    {
        const string list = """
            {"batches":[{"id":9,"emailTemplate":"ABCD-tester-extra","batchSize":1,"createdBy":1,"isComplete":true}]}
            """;
        var handler = Respond(list);

        Invocation result = await InvokeAsync(["test-account", "get", Email], handler);

        Assert.Equal(1, result.ExitCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Get_AccountInTwoBatchesListsThemAndExitsOne()
    {
        const string list = """
            {"batches":[
              {"id":43,"emailTemplate":"ABCD-tester","batchSize":10,"createdBy":1,"isComplete":true},
              {"id":42,"emailTemplate":"abcd-tester","batchSize":10,"createdBy":1,"isComplete":true}]}
            """;
        var handler = Respond(list, DetailJson(43, null), DetailJson(42, null));

        Invocation result = await InvokeAsync(["test-account", "get", Email], handler);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("appears in more than one batch", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("xbox test-account batch get 43", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("xbox test-account batch get 42", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_AmbiguityCommandsKeepAnExplicitSeller()
    {
        const string list = """
            {"batches":[
              {"id":43,"emailTemplate":"ABCD-tester","batchSize":10,"createdBy":1,"isComplete":true},
              {"id":42,"emailTemplate":"abcd-tester","batchSize":10,"createdBy":1,"isComplete":true}]}
            """;
        var handler = Respond(list, DetailJson(43, null), DetailJson(42, null));

        Invocation result = await InvokeAsync(
            ["test-account", "get", Email, "--seller-id", "1234567"],
            handler);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("batch get 43 --seller-id 1234567", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_ReadsAtMostTheCandidateCapAndSaysSo()
    {
        int count = TestAccountLookup.MaxCandidates + 3;
        string batches = string.Join(
            ",",
            Enumerable.Range(1, count).Select(id =>
                $$"""{"id":{{id}},"emailTemplate":"ABCD-tester","batchSize":1,"createdBy":1,"isComplete":true}"""));
        var responses = new List<HttpResponseMessage> { Json($$"""{"batches":[{{batches}}]}""") };
        responses.AddRange(Enumerable.Range(1, TestAccountLookup.MaxCandidates)
            .Select(id => Json(DetailJson(id, null, "other@xboxtest.com"))));
        var handler = new XboxPartnerHttpMessageHandler([.. responses]);

        Invocation result = await InvokeAsync(["test-account", "get", Email], handler);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(1 + TestAccountLookup.MaxCandidates, handler.Requests.Count);
        Assert.Contains($"newest {TestAccountLookup.MaxCandidates} of {count}", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("the older ones were not searched", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("xbox test-account batch list" + Environment.NewLine, result.Stderr, StringComparison.Ordinal);
        Assert.Contains("xbox test-account batch get <batch-id>", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("--seller-id", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_TruncatedNotFoundKeepsTheSellerInTheNextSteps()
    {
        int count = TestAccountLookup.MaxCandidates + 1;
        string batches = string.Join(
            ",",
            Enumerable.Range(1, count).Select(id =>
                $$"""{"id":{{id}},"emailTemplate":"ABCD-tester","batchSize":1,"createdBy":1,"isComplete":true}"""));
        var responses = new List<HttpResponseMessage> { Json($$"""{"batches":[{{batches}}]}""") };
        responses.AddRange(Enumerable.Range(1, TestAccountLookup.MaxCandidates)
            .Select(id => Json(DetailJson(id, null, "other@xboxtest.com"))));
        var handler = new XboxPartnerHttpMessageHandler([.. responses]);

        Invocation result = await InvokeAsync(["test-account", "get", Email, "--seller-id", "1234567"], handler);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("xbox test-account batch list --seller-id 1234567", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("xbox test-account batch get <batch-id> --seller-id 1234567", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_FindsABatchOnALaterPage()
    {
        var handler = Respond(
            """{"batches":[{"id":50,"emailTemplate":"other","batchSize":1,"createdBy":1,"isComplete":true}],"continuationToken":"t1"}""",
            ListJson("2026-01-01T00:00:00Z"),
            DetailJson(42, "2026-01-01T00:00:00Z"));

        Invocation result = await InvokeAsync(["test-account", "get", Email], handler);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Contains("continuationToken=t1", handler.Requests[1].RequestUri?.Query, StringComparison.Ordinal);
        using JsonDocument json = JsonDocument.Parse(result.Stdout);
        Assert.Equal(42, json.RootElement.GetProperty("batch").GetProperty("id").GetInt64());
        Assert.True(json.RootElement.GetProperty("expired").GetBoolean());
    }

    [Fact]
    public async Task Get_ExpiryEqualToNowIsNotExpired()
    {
        const string now = "2026-06-01T00:00:00Z";
        var handler = Respond(ListJson(now), DetailJson(42, now));

        Invocation result = await InvokeAsync(["test-account", "get", Email], handler);

        Assert.Equal(0, result.ExitCode);
        using JsonDocument json = JsonDocument.Parse(result.Stdout);
        Assert.False(json.RootElement.GetProperty("expired").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("expiredOn").ValueKind);
    }

    [Fact]
    public void FilterExpired_KeepsABatchOnlyOnceItsExpiryHasPassed()
    {
        const string body = """
            {"batches":[{"id":2,"accessExpirationDate":"2026-06-01T00:00:00Z"},
                        {"id":1,"accessExpirationDate":"2026-05-31T23:59:59Z"}]}
            """;

        using JsonDocument json = JsonDocument.Parse(TestAccountLookup.FilterExpired(body, Now, null));

        JsonElement batches = json.RootElement.GetProperty("batches");
        Assert.Equal(1, batches.GetArrayLength());
        Assert.Equal(1, batches[0].GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task Get_GatewayErrorOnListUsesLookupHints()
    {
        var handler = new XboxPartnerHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });

        Invocation result = await InvokeAsync(["test-account", "get", Email], handler);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("--seller-id", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Check the batch ID", result.Stderr, StringComparison.Ordinal);
        Assert.StartsWith(
            $"x-ms-correlation-id: {Assert.Single(Assert.Single(handler.Requests).Headers["x-ms-correlation-id"])}",
            result.Stderr,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_UnreadableListPageWritesTheCorrelationId()
    {
        var handler = Respond("not-json");

        Invocation result = await InvokeAsync(["test-account", "get", Email], handler);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Equal(
            [
                $"x-ms-correlation-id: {Assert.Single(Assert.Single(handler.Requests).Headers["x-ms-correlation-id"])}",
                "Request failed: The test account batch list response was not valid JSON.",
            ],
            result.Stderr.TrimEnd().Split(Environment.NewLine));
    }

    [Fact]
    public async Task Get_GatewayErrorWhileReadingABatchExitsOne()
    {
        var handler = new XboxPartnerHttpMessageHandler(
            Json(ListJson(null)),
            new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });

        Invocation result = await InvokeAsync(["test-account", "get", Email], handler);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.StartsWith(
            $"x-ms-correlation-id: {Assert.Single(handler.Requests[1].Headers["x-ms-correlation-id"])}{Environment.NewLine}",
            result.Stderr,
            StringComparison.Ordinal);
        Assert.Contains("deleted while it was being read", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_GatewayErrorWhileReadingABatchWritesTheGatewayCorrelationId()
    {
        var failure = new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        failure.Headers.Add("x-ms-correlation-id", "server-correlation");
        var handler = new XboxPartnerHttpMessageHandler(Json(ListJson(null)), failure);

        Invocation result = await InvokeAsync(["test-account", "get", Email], handler);

        Assert.Equal(1, result.ExitCode);
        Assert.StartsWith(
            $"x-ms-correlation-id: server-correlation{Environment.NewLine}",
            result.Stderr,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"batches":[null]}""")]
    public async Task Get_MalformedListExitsOne(string body)
    {
        Invocation result = await InvokeAsync(["test-account", "get", Email], Respond(body));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Request failed", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"batch":{"id":42,"emailTemplate":"ABCD-tester"},"accounts":[null]}""")]
    [InlineData("""{"batch":null,"accounts":[]}""")]
    [InlineData("""{"batch":{"id":42,"emailTemplate":"ABCD-tester"},"accounts":null}""")]
    public async Task Get_MalformedDetailBodiesExitOne(string detail)
    {
        Invocation result = await InvokeAsync(["test-account", "get", Email], Respond(ListJson(null), detail));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Request failed", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_LiveAccountWinsOverADeletedCopyInAnotherBatch()
    {
        const string list = """
            {"batches":[
              {"id":43,"emailTemplate":"ABCD-tester","batchSize":10,"createdBy":1,"isComplete":true},
              {"id":42,"emailTemplate":"ABCD-tester","batchSize":10,"createdBy":1,"isComplete":true}]}
            """;
        var handler = Respond(list, DetailJson(43, null, isDeleted: true), DetailJson(42, "2026-01-01T00:00:00Z"));

        Invocation result = await InvokeAsync(["test-account", "get", Email], handler);

        Assert.Equal(0, result.ExitCode);
        using JsonDocument json = JsonDocument.Parse(result.Stdout);
        Assert.Equal(42, json.RootElement.GetProperty("batch").GetProperty("id").GetInt64());
        Assert.False(json.RootElement.GetProperty("account").GetProperty("isDeleted").GetBoolean());
        Assert.DoesNotContain("more than one", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_MatchInALaterCandidateIsFound()
    {
        const string list = """
            {"batches":[
              {"id":43,"emailTemplate":"ABCD-tester","batchSize":10,"createdBy":1,"isComplete":true},
              {"id":42,"emailTemplate":"ABCD-tester","batchSize":10,"createdBy":1,"isComplete":true}]}
            """;
        var handler = Respond(list, DetailJson(43, null, "other@xboxtest.com"), DetailJson(42, null));

        Invocation result = await InvokeAsync(["test-account", "get", Email], handler);

        Assert.Equal(0, result.ExitCode);
        using JsonDocument json = JsonDocument.Parse(result.Stdout);
        Assert.Equal(42, json.RootElement.GetProperty("batch").GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task Get_MatchWithTruncatedCandidatesStillSaysSo()
    {
        int count = TestAccountLookup.MaxCandidates + 1;
        string batches = string.Join(
            ",",
            Enumerable.Range(1, count).Select(id =>
                $$"""{"id":{{id}},"emailTemplate":"ABCD-tester","batchSize":1,"createdBy":1,"isComplete":true}"""));
        var responses = new List<HttpResponseMessage> { Json($$"""{"batches":[{{batches}}]}""") };
        responses.Add(Json(DetailJson(1, null)));
        responses.AddRange(Enumerable.Range(2, TestAccountLookup.MaxCandidates - 1)
            .Select(id => Json(DetailJson(id, null, "other@xboxtest.com"))));
        var handler = new XboxPartnerHttpMessageHandler([.. responses]);

        Invocation result = await InvokeAsync(["test-account", "get", Email], handler);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains($"newest {TestAccountLookup.MaxCandidates} of {count}", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_AmbiguousWithTruncatedCandidatesSaysBoth()
    {
        int count = TestAccountLookup.MaxCandidates + 1;
        string batches = string.Join(
            ",",
            Enumerable.Range(1, count).Select(id =>
                $$"""{"id":{{id}},"emailTemplate":"ABCD-tester","batchSize":1,"createdBy":1,"isComplete":true}"""));
        var responses = new List<HttpResponseMessage>
        {
            Json($$"""{"batches":[{{batches}}]}"""),
            Json(DetailJson(1, null)),
            Json(DetailJson(2, null)),
        };
        responses.AddRange(Enumerable.Range(3, TestAccountLookup.MaxCandidates - 2)
            .Select(id => Json(DetailJson(id, null, "other@xboxtest.com"))));
        var handler = new XboxPartnerHttpMessageHandler([.. responses]);

        Invocation result = await InvokeAsync(["test-account", "get", Email], handler);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains($"newest {TestAccountLookup.MaxCandidates} of {count}", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("appears in more than one batch", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("xbox test-account batch get 1", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("xbox test-account batch get 2", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_ConsoleStyleAddressMatchesTheWholeLocalPartTemplate()
    {
        const string list = """
            {"batches":[{"id":7,"emailTemplate":"nod-console","batchSize":1,"createdBy":1,"isComplete":true}]}
            """;
        const string detail = """
            {"batch":{"id":7,"emailTemplate":"nod-console","batchSize":1,"createdBy":1,"isComplete":true},
             "accounts":[{"xuid":"1","email":"nod-console@xboxtest.com","gamertag":"Console","isDeleted":false}]}
            """;
        var handler = Respond(list, detail);

        Invocation result = await InvokeAsync(["test-account", "get", "nod-console@xboxtest.com"], handler);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("?pageSize=100", handler.Requests[0].RequestUri?.Query);
    }

    [Fact]
    public async Task Get_FindsABatchWhoseTemplateLacksTheGroup()
    {
        const string list = """
            {"batches":[
              {"id":9,"emailTemplate":"NOD-other","batchSize":1,"createdBy":1,"isComplete":true},
              {"id":8,"emailTemplate":"-tester","batchSize":1,"createdBy":1,"isComplete":true}]}
            """;
        const string detail = """
            {"batch":{"id":8,"emailTemplate":"-tester","batchSize":1,"createdBy":1,"isComplete":true},
             "accounts":[{"xuid":"1","email":"ABCD-tester00007@xboxtest.com","gamertag":"Seven","isDeleted":false}]}
            """;
        var handler = Respond(list, detail);

        Invocation result = await InvokeAsync(["test-account", "get", Email], handler);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("/xboxpartner/v1/testaccounts/batches/8", handler.Requests[1].RequestUri?.AbsolutePath);
        using JsonDocument json = JsonDocument.Parse(result.Stdout);
        Assert.Equal(8, json.RootElement.GetProperty("batch").GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task Get_TableForABatchWithoutExpiryShowsNo()
    {
        var handler = Respond(ListJson(null), DetailJson(42, null));

        Invocation result = await InvokeAsync(["test-account", "get", Email, "--output", "table"], handler);

        Assert.Equal(0, result.ExitCode);
        Assert.EndsWith(" no", result.Stdout.TrimEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BatchList_ExpiredAndIncludeSandboxesCannotBeCombined()
    {
        var handler = Respond(ListJson("2026-01-01T00:00:00Z"));

        Invocation result = await InvokeAsync(
            ["test-account", "batch", "list", "--expired", "--include-sandboxes"],
            handler);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("--expired and --include-sandboxes cannot be combined", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("'xbox test-account batch get <batch-id>'", result.Stderr, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.BadGateway)]
    public void DescribeFailure_LookupNeverMentionsABatchId(HttpStatusCode status)
    {
        string? hint = TestAccountCommandFactory.DescribeFailure(
            status,
            "{}",
            TestAccountCommandFactory.TestAccountRequest.Lookup);

        Assert.NotNull(hint);
        Assert.DoesNotContain("The batch ", hint, StringComparison.Ordinal);
        Assert.DoesNotContain("batch ID", hint, StringComparison.Ordinal);
        Assert.DoesNotContain("another developer", hint, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeFailure_LookupForbiddenNamesTheRoleFirst()
    {
        string? hint = TestAccountCommandFactory.DescribeFailure(
            HttpStatusCode.Forbidden,
            "{}",
            TestAccountCommandFactory.TestAccountRequest.Lookup);

        Assert.NotNull(hint);
        Assert.StartsWith("You are not a member of this seller, or lack the Partner Center test account role", hint, StringComparison.Ordinal);
        Assert.Contains("--seller-id", hint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_BadDomainIsAParseError()
    {
        var handler = Respond("{}");

        Invocation result = await InvokeAsync(["test-account", "get", "tester00001@example.com"], handler);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("@xboxtest.com", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a<b@xboxtest.com")]
    [InlineData("ABCD-tester.@xboxtest.com")]
    public async Task Get_BadLocalPartIsAParseError(string email)
    {
        var handler = Respond("{}");

        Invocation result = await InvokeAsync(["test-account", "get", email], handler);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("the part before @xboxtest.com", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void TryParse_AcceptsDotsAndUnderscoresAndALongestLocalPart()
    {
        Assert.Null(TestAccountLookup.TryParse("nod.team_a-console@xboxtest.com", out _));
        Assert.Null(TestAccountLookup.TryParse(new string('a', TestAccountLookup.MaxLocalLength) + "@xboxtest.com", out _));
        string? problem = TestAccountLookup.TryParse(new string('a', TestAccountLookup.MaxLocalLength + 1) + "@xboxtest.com", out _);
        Assert.Contains($"at most {TestAccountLookup.MaxLocalLength} characters", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_MalformedDetailExitsOne()
    {
        var handler = Respond(ListJson(null), "{}");

        Invocation result = await InvokeAsync(["test-account", "get", Email], handler);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Request failed", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BatchList_ExpiredKeepsOnlyExpiredAndAppliesLimitAfterwards()
    {
        const string list = """
            {"batches":[
              {"id":4,"emailTemplate":"a","accessExpirationDate":"2027-01-01T00:00:00Z"},
              {"id":3,"emailTemplate":"b","accessExpirationDate":"2026-03-01T00:00:00Z"},
              {"id":2,"emailTemplate":"c"},
              {"id":1,"emailTemplate":"d","accessExpirationDate":"2026-01-01T00:00:00Z"}]}
            """;
        var handler = Respond(list);

        Invocation result = await InvokeAsync(["test-account", "batch", "list", "--expired", "--limit", "1"], handler);

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("top=", handler.Requests[0].RequestUri?.Query, StringComparison.Ordinal);
        using JsonDocument json = JsonDocument.Parse(result.Stdout);
        JsonElement batches = json.RootElement.GetProperty("batches");
        Assert.Equal(1, batches.GetArrayLength());
        Assert.Equal(3, batches[0].GetProperty("id").GetInt64());
        Assert.False(json.RootElement.TryGetProperty("returned", out _));
    }

    [Fact]
    public async Task BatchList_ExpiredReadsLaterPagesBeforeApplyingTheLimit()
    {
        var handler = Respond(
            """{"batches":[{"id":9,"emailTemplate":"a","accessExpirationDate":"2027-01-01T00:00:00Z"}],"continuationToken":"t1"}""",
            """{"batches":[{"id":5,"emailTemplate":"b","accessExpirationDate":"2026-03-01T00:00:00Z"},{"id":4,"emailTemplate":"c","accessExpirationDate":"2026-01-01T00:00:00Z"}]}""");

        Invocation result = await InvokeAsync(["test-account", "batch", "list", "--expired", "--limit", "1"], handler);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("continuationToken=t1", handler.Requests[1].RequestUri?.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("top=", handler.Requests[1].RequestUri?.Query, StringComparison.Ordinal);
        using JsonDocument json = JsonDocument.Parse(result.Stdout);
        JsonElement batches = json.RootElement.GetProperty("batches");
        Assert.Equal(1, batches.GetArrayLength());
        Assert.Equal(5, batches[0].GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task BatchList_ExpiredTableNoteNamesTheFilter()
    {
        var handler = Respond(ListJson("2026-01-01T00:00:00Z"));

        Invocation result = await InvokeAsync(["test-account", "batch", "list", "--expired", "--output", "table"], handler);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Returned 1 expired batches.", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterExpired_RejectsABodyWithoutBatches()
    {
        Assert.Throws<InvalidDataException>(() => TestAccountLookup.FilterExpired("{}", Now, null));
        Assert.Throws<InvalidDataException>(() => TestAccountLookup.FilterExpired("not json", Now, null));
    }

    [Theory]
    [InlineData("""{"batches":[{"id":1,"accessExpirationDate":"soon"}]}""")]
    [InlineData("""{"batches":[{"id":1,"accessExpirationDate":5}]}""")]
    [InlineData("""{"batches":[7]}""")]
    public void FilterExpired_RejectsUnreadableEntries(string body)
    {
        Assert.Throws<InvalidDataException>(() => TestAccountLookup.FilterExpired(body, Now, null));
    }

    [Fact]
    public void FilterExpired_SkipsBatchesWithNullOrMissingExpiry()
    {
        const string body = """
            {"batches":[{"id":1,"accessExpirationDate":null},{"id":2},{"id":3,"accessExpirationDate":"2026-01-01T00:00:00Z"}]}
            """;

        using JsonDocument json = JsonDocument.Parse(TestAccountLookup.FilterExpired(body, Now, null));

        Assert.Equal(1, json.RootElement.GetProperty("batches").GetArrayLength());
        Assert.Equal(3, json.RootElement.GetProperty("batches")[0].GetProperty("id").GetInt64());
    }

    private static string ListJson(string? expiry) =>
        $$"""
        {"batches":[{"id":42,"emailTemplate":"ABCD-tester","batchSize":10,"createdBy":1,"isComplete":true,
          "accessExpirationDate":{{Quote(expiry)}}}]}
        """;

    private static string DetailJson(long id, string? expiry, string email = Email, bool isDeleted = false) =>
        $$"""
        {"batch":{"id":{{id}},"emailTemplate":"ABCD-tester","batchSize":10,"createdBy":1,"isComplete":true,
          "accessExpirationDate":{{Quote(expiry)}}},
         "accounts":[{"xuid":"2814600000000001","email":"{{email}}","gamertag":"Alpha","isDeleted":{{(isDeleted ? "true" : "false")}}}]}
        """;

    private static string Quote(string? value) => value is null ? "null" : $"\"{value}\"";

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static XboxPartnerHttpMessageHandler Respond(params string[] bodies) =>
        new([.. bodies.Select(Json)]);

    private static async Task<Invocation> InvokeAsync(string[] args, HttpMessageHandler handler)
    {
        TextWriter originalOut = Console.Out;
        TextWriter originalError = Console.Error;
        var originalDependencies = TestAccountCommandFactory.Dependencies;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        TestAccountCommandFactory.Dependencies = originalDependencies with { GetUtcNow = () => Now };
        Program.XboxPartnerClientFactory = configuration => new XboxPartnerClient(
            configuration,
            new HttpClient(handler),
            () => Task.FromResult<string?>("caller-token"));

        try
        {
            int exitCode = await Program.Main(args);
            return new Invocation(exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Program.XboxPartnerClientFactory = null;
            TestAccountCommandFactory.Dependencies = originalDependencies;
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private sealed record Invocation(int ExitCode, string Stdout, string Stderr);
}
