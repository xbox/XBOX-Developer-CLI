// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Identity.Client;
using Moq;
using XboxDevCLI.Commands;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

[Collection("ConsoleRedirect")]
public class AudienceCommandTests
{
    private const string Id = "1152921504606846976";
    private const string SecondId = "1152921504606846977";

    private const string Testers =
        $$"""{"name":"Nod Arena testers","id":"{{Id}}","memberCount":2,"members":["dev@nodpublishers.com","Tester@NodPublishers.com"],"dnaTags":["AppFlighting","MetadataFlighting"],"sellerId":"1234567","groupType":"User","groupUsage":"ForPackageFlight, ForLimitedDistribution"}""";

    private const string Devices =
        $$"""{"name":null,"id":"{{SecondId}}","memberCount":0,"members":[],"dnaTags":[],"sellerId":"1234567","groupType":null,"groupUsage":null}""";

    // list writes neither memberCount nor members, and get doesn't write memberCount.
    private static string AsListed(string audience) => Without(audience, "memberCount", "members");

    private static string AsGot(string audience) => Without(audience, "memberCount");

    private static string Without(string audience, params string[] fields)
    {
        var json = JsonNode.Parse(audience)!.AsObject();
        foreach (var field in fields)
        {
            Assert.True(json.Remove(field));
        }

        return json.ToJsonString();
    }

    [Fact]
    public async Task List_WrapsTheArrayAndOmitsMembersAndMemberCount()
    {
        var listed = Testers.Replace("\"memberCount\":2", "\"memberCount\":0").Replace(
            "[\"dev@nodpublishers.com\",\"Tester@NodPublishers.com\"]", "[]");
        var xboxDevice = Devices.Replace("\"groupType\":null", "\"groupType\":\"XboxDevice\"");
        var handler = Handler($"[{listed},{xboxDevice},{Devices}]");
        var result = await InvokeAsync(handler, ["audience", "list", "--output", "json-compact"], sellerId: "1234567");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal($$"""{"audiences":[{{AsListed(listed)}},{{AsListed(xboxDevice)}},{{AsListed(Devices)}}]}""", result.Stdout.Trim());
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/" + XboxPartnerClient.AudiencesPath, request.RequestUri!.AbsolutePath);
        Assert.Equal("?sellerId=1234567", request.RequestUri!.Query);
        Assert.DoesNotContain(request.Headers.Keys, key => key.Equals("X-DeveloperAccountId", StringComparison.OrdinalIgnoreCase));
        Assert.Null(request.Body);
    }

    [Fact]
    public async Task List_PassesOtherFieldsAndValuesThrough()
    {
        var unusual = """{"uniqueGroupId":"u","name":5,"id":"1","memberCount":0,"members":[],"dnaTags":[null],"updatedAt":"2026-01-15T18:30:00Z"}""";
        var result = await InvokeAsync(Handler($"[{unusual},{{\"id\":\"2\"}}]"), ["audience", "list", "--output", "json-compact"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            """{"audiences":[{"uniqueGroupId":"u","name":5,"id":"1","dnaTags":[null],"updatedAt":"2026-01-15T18:30:00Z"},{"id":"2"}]}""",
            result.Stdout.Trim());
    }

    [Theory]
    [InlineData("json-compact", """{"audiences":[]}""")]
    [InlineData("table", "No results.")]
    public async Task List_EmptyIsSuccess(string format, string expected)
    {
        var result = await InvokeAsync(Handler("[]"), ["audience", "list", "--output", format]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.Stdout.Trim());
    }

    [Theory]
    [InlineData("testers", true, false)]
    [InlineData("NOD ARENA", true, false)]
    [InlineData("rally", false, true)]
    [InlineData("missing", false, false)]
    public async Task List_NameKeepsAudiencesWhoseNameContainsTheTextIgnoringCase(string filter, bool arena, bool rally)
    {
        var rallyTesters = Devices.Replace("\"name\":null", "\"name\":\"Nod Rally\"").Replace(SecondId, "3");
        var handler = Handler($"[{Testers},{Devices},{rallyTesters},{{\"id\":\"4\",\"name\":5}}]");
        var result = await InvokeAsync(handler, ["audience", "list", "--name", filter, "--output", "json-compact"]);

        Assert.Equal(0, result.ExitCode);
        var expected = new List<string>();
        if (arena)
        {
            expected.Add(AsListed(Testers));
        }

        if (rally)
        {
            expected.Add(AsListed(rallyTesters));
        }

        Assert.Equal($$"""{"audiences":[{{string.Join(",", expected)}}]}""", result.Stdout.Trim());
        Assert.Equal("?sellerId=seller-1", Assert.Single(handler.Requests).RequestUri!.Query);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task List_RejectsAnEmptyName(string filter)
    {
        var handler = Handler($"[{Testers}]");
        var result = await InvokeAsync(handler, ["audience", "list", "--name", filter]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.DoesNotContain("Nod Arena testers", result.Stdout);
        Assert.Contains("Invalid --name", result.Stderr);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task List_RendersTheDeclaredTable()
    {
        var handler = Handler($"[{Testers.Replace("Nod Arena testers", "Nod\\nTesters").Replace("\"User\"", "\"PreCreated\"")}]");
        var result = await InvokeAsync(handler, ["audience", "list", "--output", "table"]);

        Assert.Equal(0, result.ExitCode);
        var lines = result.Stdout.TrimEnd().Split(Environment.NewLine);
        Assert.Equal(3, lines.Length);
        Assert.Contains("ID", lines[0]);
        Assert.Contains("NAME", lines[0]);
        Assert.Contains("TYPE", lines[0]);
        Assert.DoesNotContain("UPDATED", lines[0]);
        Assert.Contains(Id, lines[2]);
        Assert.Contains("Nod Testers", lines[2]);
        Assert.Contains("PreCreated", lines[2]);
        Assert.DoesNotContain("dev@nodpublishers.com", result.Stdout);
        Assert.All(lines, line => Assert.DoesNotContain(line, char.IsControl));
    }

    [Theory]
    [InlineData("""{}""", "must be a JSON array")]
    [InlineData("""{"audiences":[]}""", "must be a JSON array")]
    [InlineData("""null""", "must be a JSON array")]
    [InlineData("""[null]""", "not a JSON object")]
    [InlineData("""["x"]""", "not a JSON object")]
    [InlineData("""[{"name":"A"}]""", "no 'id'")]
    [InlineData("""[{"id":""}]""", "no 'id'")]
    [InlineData("""[{"id":" "}]""", "no 'id'")]
    [InlineData("""[{"id":null}]""", "no 'id'")]
    [InlineData("""[{"id":5}]""", "no 'id'")]
    [InlineData("""[{"id":"\uD800"}]""", "not valid JSON")]
    [InlineData("<html></html>", "not valid JSON")]
    [InlineData("""[{"id":"1","id":"2"}]""", "not valid JSON")]
    public async Task List_RejectsAMalformedResponse(string body, string message)
    {
        var result = await InvokeAsync(Handler(body), ["audience", "list"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains(message, result.Stderr);
        Assert.Contains("x-ms-correlation-id: response-correlation", result.Stderr);
    }

    // The list is wrapped in one more object for output, so it can be one level less deep than
    // the parser's default limit of 64.
    private static string NestedList(int depth) =>
        "[{\"id\":\"1\",\"nested\":" + new string('[', depth - 2) + new string(']', depth - 2) + "}]";

    [Fact]
    public async Task List_RejectsAListNestedTooDeepToWrap()
    {
        var result = await InvokeAsync(Handler(NestedList(64)), ["audience", "list", "--output", "table"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("not valid JSON", result.Stderr);
        Assert.Contains("x-ms-correlation-id: response-correlation", result.Stderr);
    }

    [Theory]
    [InlineData("table")]
    [InlineData("json")]
    public async Task List_WritesTheDeepestListItAccepts(string format)
    {
        var result = await InvokeAsync(Handler(NestedList(63)), ["audience", "list", "--output", format]);

        Assert.True(result.ExitCode == 0, result.Stderr);
        if (format == "json")
        {
            Assert.Contains(Environment.NewLine + "  \"audiences\": [", result.Stdout);
        }
        else
        {
            Assert.Contains("ID", result.Stdout);
        }
    }

    [Fact]
    public async Task List_AMalformedLaterAudienceWritesNothing()
    {
        var result = await InvokeAsync(Handler($"[{Testers},{{\"name\":\"B\"}}]"), ["audience", "list"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("no 'id'", result.Stderr);
    }

    [Fact]
    public async Task Get_PassesTheOpaqueIdThroughAndOutputsTheAudience()
    {
        var handler = Handler(Testers);
        var result = await InvokeAsync(handler, ["audience", "get", Id, "--output", "json-compact"], sellerId: "1234567");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(AsGot(Testers), result.Stdout.Trim());
        var request = Assert.Single(handler.Requests);
        Assert.Equal($"/{XboxPartnerClient.AudiencesPath}/{Id}", request.RequestUri!.AbsolutePath);
        Assert.Equal("?sellerId=1234567", request.RequestUri!.Query);
        Assert.DoesNotContain(request.Headers.Keys, key => key.Equals("X-DeveloperAccountId", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Get_NullAndEmptyValuesAreOutputAsReturned()
    {
        var result = await InvokeAsync(Handler(Devices), ["audience", "get", SecondId, "--output", "json-compact"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(AsGot(Devices), result.Stdout.Trim());
    }

    [Fact]
    public async Task Get_EscapesTheIdAndPreservesItExactly()
    {
        const string opaque = "Group A/b?c";
        var handler = Handler(Devices.Replace(SecondId, opaque));
        var result = await InvokeAsync(handler, ["audience", "get", opaque, "--output", "json-compact"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains($"\"id\":\"{opaque}\"", result.Stdout);
        var uri = Assert.Single(handler.Requests).RequestUri!;
        Assert.Equal($"/{XboxPartnerClient.AudiencesPath}/Group%20A%2Fb%3Fc", uri.AbsolutePath);
        Assert.Equal("?sellerId=seller-1", uri.Query);
    }

    [Fact]
    public async Task Get_RendersTheDeclaredRecordTable()
    {
        var result = await InvokeAsync(Handler(Testers), ["audience", "get", Id, "--output", "table"]);

        Assert.Equal(0, result.ExitCode);
        var lines = result.Stdout.TrimEnd().Split(Environment.NewLine);
        Assert.Contains("TYPE", lines[0]);
        Assert.DoesNotContain("UPDATED", lines[0]);
        Assert.Contains(Id, lines[2]);
        Assert.Contains("User", lines[2]);
        Assert.DoesNotContain("dev@nodpublishers.com", result.Stdout);
    }

    [Theory]
    [InlineData("""{"name":"A","members":[]}""", "no 'id'")]
    [InlineData("""{"id":null}""", "no 'id'")]
    [InlineData("""[{"id":"1152921504606846976"}]""", "not a JSON object")]
    [InlineData("""null""", "not a JSON object")]
    [InlineData("<html></html>", "not valid JSON")]
    public async Task Get_RejectsAMalformedResponse(string body, string message)
    {
        var result = await InvokeAsync(Handler(body), ["audience", "get", Id]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains(message, result.Stderr);
        Assert.Contains("x-ms-correlation-id: response-correlation", result.Stderr);
    }

    [Fact]
    public async Task Get_OutputsTheAudienceAsReturned()
    {
        var returned = Devices.Replace(SecondId, "abc").Replace("{\"name\"", "{\"updatedAt\":\"2026-01-15T18:30:00Z\",\"name\"");
        var result = await InvokeAsync(Handler(returned), ["audience", "get", "ABC", "--output", "json-compact"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(AsGot(returned), result.Stdout.Trim());
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "audience_not_found", "HTTP 404")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "audience_authorization_unavailable", "HTTP 503")]
    public async Task Get_GatewayErrorWritesStderrOnly(HttpStatusCode status, string code, string message)
    {
        var handler = new XboxPartnerHttpMessageHandler(
            Response($$"""{"title":"Problem","code":"{{code}}","detail":"The audience request failed."}""", status));
        var result = await InvokeAsync(handler, ["audience", "get", Id]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains(message, result.Stderr);
        Assert.Contains("The audience request failed.", result.Stderr);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Get_RejectsAnEmptyIdBeforeSigningIn(string audienceId)
    {
        var auth = CreateAuth();
        var handler = Handler(Testers);
        var result = await InvokeAsync(handler, ["audience", "get", audienceId], auth: auth);

        Assert.NotEqual(0, result.ExitCode);
        Assert.DoesNotContain("Nod Arena testers", result.Stdout);
        Assert.Contains("Invalid audience ID", result.Stderr);
        Assert.Empty(handler.Requests);
        auth.Verify(service => service.AcquireTokenSilentAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public async Task Get_RejectsADotSegmentIdBeforeSigningIn(string audienceId)
    {
        var auth = CreateAuth();
        var handler = Handler(Testers);
        var result = await InvokeAsync(handler, ["audience", "get", audienceId], auth: auth);

        Assert.NotEqual(0, result.ExitCode);
        Assert.DoesNotContain("Nod Arena testers", result.Stdout);
        Assert.Contains("can't be '.' or '..'", result.Stderr);
        Assert.Empty(handler.Requests);
        auth.Verify(service => service.AcquireTokenSilentAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public async Task GetAudienceAsync_RefusesADotSegmentId(string audienceId)
    {
        var handler = Handler(Testers);
        using var httpClient = new HttpClient(handler);
        using var client = new XboxPartnerClient(
            new ConfigurationBuilder().Build(), httpClient, CreateAuth().Object);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.GetAudienceAsync("https://gateway.example", "1234567", audienceId, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(" .", "%20.")]
    [InlineData("...", "...")]
    public async Task Get_SendsOtherDotIdsAsOneSegment(string audienceId, string segment)
    {
        var handler = Handler(Devices.Replace(SecondId, audienceId));
        var result = await InvokeAsync(handler, ["audience", "get", audienceId, "--output", "json-compact"]);

        Assert.Equal(0, result.ExitCode);
        Assert.EndsWith($"/xboxpartner/v1/audiences/{segment}", handler.Requests.Single().RequestUri!.AbsolutePath);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("get")]
    public async Task ALoneSurrogateIsAResponseError(string command)
    {
        var body = Testers.Replace("\"Nod Arena testers\"", "\"\\uD800\"");
        string[] arguments = command == "list" ? ["audience", "list"] : ["audience", "get", Id];
        var result = await InvokeAsync(Handler(command == "list" ? $"[{body}]" : body), arguments);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("not valid JSON", result.Stderr);
        Assert.Contains("x-ms-correlation-id: response-correlation", result.Stderr);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("get")]
    public async Task ALoneSurrogatePropertyNameIsAResponseError(string command)
    {
        var body = "{\"\\uD800\":0," + Testers.TrimStart()[1..];
        string[] arguments = command == "list" ? ["audience", "list"] : ["audience", "get", Id];
        var result = await InvokeAsync(Handler(command == "list" ? $"[{body}]" : body), arguments);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("not valid JSON", result.Stderr);
        Assert.Contains("x-ms-correlation-id: response-correlation", result.Stderr);
    }

    [Theory]
    [InlineData(typeof(ArgumentException))]
    [InlineData(typeof(ObjectDisposedException))]
    public async Task BugsAreNotReportedAsRequestFailures(Type exceptionType)
    {
        var handler = new ThrowingHandler(() => exceptionType == typeof(ArgumentException)
            ? new ArgumentException("bug")
            : new ObjectDisposedException("client"));

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => InvokeAsync(handler, ["audience", "list"]));
        Assert.IsType(exceptionType, exception);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task Verbose_WritesIdsSafeForTheTerminal()
    {
        var result = await InvokeAsync(
            Handler(Testers), ["audience", "get", Id + "\u001b[31m", "--verbose"], sellerId: "1234567\u0007");

        Assert.DoesNotContain('\u001b', result.Stderr);
        Assert.DoesNotContain('\u0007', result.Stderr);
        Assert.Contains($"Reading audience {Id}", result.Stderr);
    }

    [Fact]
    public async Task CallerCancellationIsNotReportedAsARequestFailure()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new ThrowingHandler(() =>
        {
            cancellation.Cancel();
            return new OperationCanceledException(cancellation.Token);
        });

        var result = await InvokeAsync(handler, ["audience", "list"], cancellationToken: cancellation.Token);

        Assert.Equal(130, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Empty(TestEnvironment.WithoutEndpointWarnings(result.Stderr));
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task ATimeoutIsReportedAsARequestFailure()
    {
        var result = await InvokeAsync(
            new ThrowingHandler(() => new TaskCanceledException("timeout")), ["audience", "get", Id]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("Request timed out.", result.Stderr);
    }

    [Fact]
    public async Task MissingSellerFailsBeforeAnyRequest()
    {
        var previous = Environment.GetEnvironmentVariable("XBOX_SELLER_ID");
        Environment.SetEnvironmentVariable("XBOX_SELLER_ID", null);
        try
        {
            var handler = Handler("""{"audiences":[]}""");
            var result = await InvokeAsync(handler, ["audience", "list"], sellerId: null);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Empty(result.Stdout);
            Assert.Contains("seller", result.Stderr, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(handler.Requests);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XBOX_SELLER_ID", previous);
        }
    }

    [Theory]
    [InlineData("list", "xbox audience list")]
    [InlineData("get", "xbox audience get 1152921504606846976")]
    public async Task Help_DescribesOutputAndExamples(string command, string example)
    {
        var result = await InvokeAsync(Handler(Testers), ["audience", command, "--help"], addScope: false);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(example, result.Stdout);
        Assert.Contains("--seller-id", result.Stdout);
        if (command == "get")
        {
            Assert.Contains("stored member list", result.Stdout);
        }
        else
        {
            Assert.Contains("--name <text>", result.Stdout);
        }
    }

    [Fact]
    public async Task Create_PostsTheNameAndTrimmedMembersOnce()
    {
        var file = MembersFile([.. "\uFEFF"u8.ToArray(), .. "  dev@nodpublishers.com \r\n\r\n \nTester@NodPublishers.com"u8.ToArray()]);
        var handler = new XboxPartnerHttpMessageHandler(Response(Testers, HttpStatusCode.Created));
        var result = await InvokeAsync(
            handler, ["audience", "create", "--name", "Nod Arena testers", "--members-file", file, "-y", "--output", "json-compact"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(AsGot(Testers), result.Stdout.Trim());
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/" + XboxPartnerClient.AudiencesPath, request.RequestUri!.AbsolutePath);
        Assert.Equal("?sellerId=seller-1", request.RequestUri!.Query);
        Assert.Equal(
            """{"name":"Nod Arena testers","members":["dev@nodpublishers.com","Tester@NodPublishers.com"]}""",
            request.Body);
    }

    [Fact]
    public async Task Create_WithoutAMembersFile_SendsAnEmptyList()
    {
        var handler = new XboxPartnerHttpMessageHandler(Response(Devices, HttpStatusCode.Created));
        var result = await InvokeAsync(handler, ["audience", "create", "--name", "Nod Rally", "--confirm"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("""{"name":"Nod Rally","members":[]}""", Assert.Single(handler.Requests).Body);
    }

    [Fact]
    public async Task Update_PatchesOnlyTheMembersOfTheEscapedId()
    {
        var file = MembersFile(" \r\n\n"u8.ToArray());
        var handler = new XboxPartnerHttpMessageHandler(Response(Devices));
        var result = await InvokeAsync(handler, ["audience", "update", "a/b c", "--members-file", file, "--confirm"]);

        Assert.Equal(0, result.ExitCode);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Patch, request.Method);
        Assert.EndsWith("/a%2Fb%20c?sellerId=seller-1", request.RequestUri!.OriginalString);
        Assert.Equal("""{"members":[]}""", request.Body);
    }

    [Theory]
    [InlineData(new string[0], """{"name":"Nod Arena beta testers"}""")]
    [InlineData(new[] { "dev@nodpublishers.com" }, """{"name":"Nod Arena beta testers","members":["dev@nodpublishers.com"]}""")]
    public async Task Update_WithName_PatchesTheNameAndOnlyTheMembersGiven(string[] members, string body)
    {
        string[] arguments = ["audience", "update", Id, "--name", "Nod Arena beta testers", "-y"];
        if (members.Length > 0)
        {
            arguments = [.. arguments, "--members-file", MembersFile(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", members)))];
        }

        var handler = new XboxPartnerHttpMessageHandler(Response(Testers));
        var result = await InvokeAsync(handler, arguments);

        Assert.Equal(0, result.ExitCode);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Patch, request.Method);
        Assert.Equal(body, request.Body);
    }

    [Theory]
    [InlineData("invalid-utf8")]
    [InlineData("too-many")]
    [InlineData("missing-file")]
    [InlineData("long-name")]
    [InlineData("dot-id")]
    [InlineData("update-without-changes")]
    [InlineData("blank-update-name")]
    [InlineData("long-update-name")]
    [InlineData("not-confirmed")]
    public async Task InvalidWrites_FailBeforeAnyRequest(string scenario)
    {
        var members = MembersFile("dev@nodpublishers.com"u8.ToArray());
        string[] arguments = scenario switch
        {
            "invalid-utf8" => ["audience", "create", "--name", "Nod Rally", "--members-file", MembersFile([0x64, 0xFF, 0x0A]), "-y"],
            "too-many" => ["audience", "update", Id, "--members-file",
                MembersFile(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", Enumerable.Range(0, 1001).Select(i => $"p{i}@nodpublishers.com")))), "-y"],
            "missing-file" => ["audience", "update", Id, "--members-file", members + ".missing", "-y"],
            "long-name" => ["audience", "create", "--name", new string('n', 201), "-y"],
            "dot-id" => ["audience", "update", "..", "--members-file", members, "-y"],
            "update-without-changes" => ["audience", "update", Id, "-y"],
            "blank-update-name" => ["audience", "update", Id, "--name", " ", "--members-file", members, "-y"],
            "long-update-name" => ["audience", "update", Id, "--name", new string('n', 201), "-y"],
            _ => ["audience", "update", Id, "--members-file", members],
        };
        var handler = Handler(Testers);
        var result = await InvokeAsync(handler, arguments);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(handler.Requests);
        Assert.DoesNotContain("dev@nodpublishers.com", result.Stderr);
        if (scenario == "update-without-changes")
        {
            Assert.Contains("Pass --name, --members-file, or both", result.Stderr);
        }
        else if (scenario.EndsWith("-update-name", StringComparison.Ordinal))
        {
            Assert.Contains("Invalid --name", result.Stderr);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway, "downstream_request_rejected", "Partner Center rejected the request")]
    [InlineData(HttpStatusCode.BadGateway, "audience_update_preflight_failed", "couldn't be checked")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "audience_write_not_available", "aren't available right now")]
    [InlineData(HttpStatusCode.GatewayTimeout, "audience_write_not_sent", "timed out before it reached")]
    [InlineData(HttpStatusCode.Forbidden, "audience_write_forbidden", "Playtests aren't enabled for this seller")]
    [InlineData(HttpStatusCode.Forbidden, "audience_permission_denied", "for access, or pass --seller-id if you meant another seller.")]
    [InlineData(HttpStatusCode.Conflict, "audience_write_rejected", "may not be a known-user audience")]
    public async Task KnownRefusals_AreNotWritten(HttpStatusCode status, string code, string explanation)
    {
        var handler = new XboxPartnerHttpMessageHandler(Response($$"""{"status":{{(int)status}},"code":"{{code}}"}""", status));
        var result = await InvokeAsync(handler, ["audience", "update", Id, "--members-file", MembersFile([]), "-y"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Single(handler.Requests);
        Assert.Contains(explanation, result.Stderr);
        Assert.Contains("wasn't changed", result.Stderr);
        Assert.DoesNotContain("unknown", result.Stderr);
    }

    [Fact]
    public async Task UnrecognizedClientError_IsNotWritten()
    {
        var handler = new XboxPartnerHttpMessageHandler(Response(
            """{"status":400,"code":"audience_write_rejected"}""", HttpStatusCode.BadRequest));
        var result = await InvokeAsync(handler, ["audience", "update", Id, "--members-file", MembersFile([]), "-y"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Single(handler.Requests);
        Assert.Contains("The request was rejected", result.Stderr);
        Assert.DoesNotContain("unknown", result.Stderr);
    }

    [Theory]
    [InlineData("plain-504")]
    [InlineData("outcome-unknown")]
    [InlineData("unreadable-success")]
    [InlineData("transport")]
    public async Task SentButUnconfirmedCreates_AreUnknownAndNotRetried(string scenario)
    {
        HttpMessageHandler handler = scenario switch
        {
            "plain-504" => new XboxPartnerHttpMessageHandler(Response("<html>dev@nodpublishers.com</html>", HttpStatusCode.GatewayTimeout)),
            "outcome-unknown" => new XboxPartnerHttpMessageHandler(Response(
                """{"status":504,"code":"audience_create_outcome_unknown"}""", HttpStatusCode.GatewayTimeout)),
            "unreadable-success" => new XboxPartnerHttpMessageHandler(Response(
                """{"members":["dev@nodpublishers.com"]""", HttpStatusCode.Created)),
            _ => new ThrowingHandler(() => new HttpRequestException("connection reset")),
        };
        var file = MembersFile("dev@nodpublishers.com"u8.ToArray());
        var result = await InvokeAsync(handler, ["audience", "create", "--name", "Nod Rally", "--members-file", file, "-y"]);

        Assert.Equal(PlaytestCommandFactory.OutcomeUnknownExitCode, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("it is unknown whether", result.Stderr);
        Assert.Contains("xbox audience list --name", result.Stderr);
        Assert.DoesNotContain("dev@nodpublishers.com", result.Stderr);
        Assert.Equal(1, handler switch
        {
            XboxPartnerHttpMessageHandler captured => captured.Requests.Count,
            ThrowingHandler throwing => throwing.Requests,
            _ => 0,
        });
    }

    [Theory]
    [InlineData("create", "xbox audience list --name")]
    [InlineData("update", $"xbox audience get {Id}")]
    public async Task WriteCanceledAfterSending_IsUnknownAndNotRetried(string command, string recovery)
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new ThrowingHandler(() =>
        {
            cancellation.Cancel();
            return new OperationCanceledException(cancellation.Token);
        });

        var result = await InvokeAsync(handler, WriteArguments(command), cancellationToken: cancellation.Token);

        Assert.Equal(PlaytestCommandFactory.OutcomeUnknownExitCode, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Equal(1, handler.Requests);
        Assert.Contains("it is unknown whether", result.Stderr);
        Assert.Contains(recovery, result.Stderr);
        Assert.Contains($"{XboxPartnerClient.CorrelationIdHeader}: ", result.Stderr);
        Assert.Contains("Request canceled.", result.Stderr);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    public async Task WriteCanceledBeforeSending_SendsNothing(string command)
    {
        using var cancellation = new CancellationTokenSource();
        var auth = CreateAuth();
        auth.Setup(service => service.AcquireTokenSilentAsync(It.IsAny<CancellationToken>()))
            .Callback(cancellation.Cancel)
            .ThrowsAsync(new OperationCanceledException(cancellation.Token));
        var handler = Handler(Testers);

        var result = await InvokeAsync(handler, WriteArguments(command), auth: auth, cancellationToken: cancellation.Token);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains(
            $"Canceled before the request was sent, so the audience wasn't {(command == "create" ? "created" : "changed")}.",
            result.Stderr);
        Assert.Empty(handler.Requests);
    }

    private static string[] WriteArguments(string command) => command == "create"
        ? ["audience", "create", "--name", "Nod Rally", "--members-file", MembersFile([]), "-y"]
        : ["audience", "update", Id, "--members-file", MembersFile([]), "-y"];

    [Theory]
    [InlineData("create", "xbox audience create --name \"Nod Arena testers\"")]
    [InlineData("update", "xbox audience update 1152921504606846976 --members-file testers.txt")]
    public async Task WriteHelp_DescribesMembersFilesAndExitCodes(string command, string example)
    {
        var result = await InvokeAsync(Handler(Testers), ["audience", command, "--help"], addScope: false);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(example, result.Stdout);
        Assert.Contains("--members-file <path>", result.Stdout);
        Assert.Contains("--confirm", result.Stdout);
        Assert.Contains("customer groups Write", result.Stdout);
        Assert.Contains("outcome is unknown", result.Stdout);
    }

    private static string MembersFile(byte[] contents)
    {
        var path = Path.Combine(Path.GetTempPath(), $"audience-members-{Guid.NewGuid():N}.txt");
        File.WriteAllBytes(path, contents);
        return path;
    }

    private static XboxPartnerHttpMessageHandler Handler(params string[] responses) =>
        new(responses.Select(response => Response(response)).ToArray());

    private static HttpResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
        response.Headers.Add("x-ms-correlation-id", "response-correlation");
        return response;
    }

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
        auth.Setup(service => service.AcquireTokenSilentAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new AuthenticationResult(
            "access-token", false, "user", DateTimeOffset.UtcNow.AddHours(1),
            DateTimeOffset.UtcNow.AddHours(1), "test-tenant", account,
            "id-token", ["api://test/.default"], Guid.NewGuid()));
        return auth;
    }

    private static async Task<CapturedInvocation> InvokeAsync(
        HttpMessageHandler handler,
        string[] arguments,
        Mock<AuthenticationService>? auth = null,
        string? sellerId = "seller-1",
        bool addScope = true,
        CancellationToken cancellationToken = default)
    {
        auth ??= CreateAuth();
        using var httpClient = new HttpClient(handler);
        var root = Program.BuildRootCommand(audienceCommandFactory: () => AudienceCommandFactory.Create(
            configuration => new XboxPartnerClient(configuration, httpClient, auth.Object),
            new XboxPartnerJobDependencies((_, _) => Task.CompletedTask, () => DateTimeOffset.UtcNow, () => false)));
        var args = new List<string>(arguments);
        if (addScope)
        {
            args.AddRange(["--base-url", "https://gateway.example"]);
            if (sellerId != null)
            {
                args.AddRange(["--seller-id", sellerId]);
            }
        }

        var oldOut = Console.Out;
        var oldError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            ContextResolver.ReadIdentityAsync = _ => SignedInIdentity.FromCacheAsync(auth.Object, CancellationToken.None);
            ContextStore.DefaultDirectory = () => Path.Combine(Path.GetTempPath(), $"audience-tests-{Guid.NewGuid():N}");

            var parseResult = await Program.ResolveContextAsync(root.Parse(args.ToArray(), Program.NoResponseFileConfig), args);
            // The default handler is off so that exceptions the command lets through reach the test.
            var exitCode = parseResult is null ? 1 : await parseResult.InvokeAsync(
                new InvocationConfiguration { EnableDefaultExceptionHandler = false }, cancellationToken);
            return new CapturedInvocation(exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldError);
            TestEnvironment.Reset();
        }
    }

    private sealed record CapturedInvocation(int ExitCode, string Stdout, string Stderr);

    private sealed class ThrowingHandler(Func<Exception> exception) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            throw exception();
        }
    }
}
