// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Identity.Client;
using Moq;
using XboxDevCLI.Commands;
using XboxDevCLI.Output;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

[Collection("ConsoleRedirect")]
public class SandboxCommandTests
{
    private const string Sandbox = """{"sandboxId":"ABCDEF.1","type":"Shared","sellerId":"seller-1"}""";
    private const string Accepted = """{"jobId":"a1b2c3d4-0000-4000-8000-000000000001","status":"notStarted","result":"pending"}""";
    private const string Running = """{"jobId":"a1b2c3d4-0000-4000-8000-000000000001","status":"running","result":"pending"}""";
    private const string Succeeded = """{"jobId":"a1b2c3d4-0000-4000-8000-000000000001","status":"completed","result":"succeeded"}""";
    private const string Failed =
        """{"jobId":"a1b2c3d4-0000-4000-8000-000000000001","status":"completed","result":"failed","errors":[{"code":"invalidRequest","message":"Sandbox exists.","details":[{"code":"conflict","message":"Owned by another seller."}]}]}""";
    private static readonly string[] CreateArguments = ["sandbox", "create", "abcdef.1"];

    private static readonly string LongestCreateId = new string('A', 62) + ".1";
    private static readonly string TooLongId = new string('A', 63) + ".1";
    private static readonly string LongestCreateMoniker = new string('A', 60) + ".999";
    private static readonly string TooLongCreateMoniker = new string('A', 61) + ".0";

    public static TheoryData<string?, bool, bool> SandboxIds => new()
    {
        // value, readable, creatable
        { "ABCDEF.1", true, true },
        { " abcdef.1 ", true, true },
        { "XDKS.0", true, true },
        { "ABCD.99", true, true },
        { "MSFT.1", true, false },
        { "msft.1", true, false },
        { "MSFT.2", true, true },
        { "READONLY.1", true, true },
        { "RETAIL", true, false },
        { "READONLY", true, false },
        { "CERT", true, false },
        { "CERT.DEBUG", true, false },
        { "ABC.1", true, false },
        { "ABCD.01", true, false },
        { "ABCD.100", true, true },
        { "ABCD.200", true, true },
        { "ABCD.999", true, true },
        { "ABCD.58", true, false },
        { "abcd.58", true, false },
        { "ABCD.57", true, true },
        { "ABCD.59", true, true },
        { "ABCD.580", true, true },
        { "ABCD.158", true, true },
        { "undefined.1", true, false },
        { "UNDEFINED.1", true, false },
        { "UnDeFiNeD.0", true, false },
        { "UNDEFINEDX.1", true, true },
        { "XUNDEFINED.1", true, true },
        { "ABCD.1000", true, false },
        { "ABCD.010", true, false },
        { LongestCreateId, true, false },
        { LongestCreateMoniker, true, true },
        { TooLongCreateMoniker, true, false },
        { TooLongId, false, false },
        { new string('A', 65), false, false },
        { "\u017F\u017F\u017F\u017F.1", false, false },
        { "ABCD-1.1", false, false },
        { "ABCD.", false, false },
        { ".1", false, false },
        { "A.B.C", false, false },
        { "ABCD.1\n", true, true },
        { "ABCD .1", false, false },
        { "", false, false },
        { null, false, false },
    };

    [Theory]
    [MemberData(nameof(SandboxIds))]
    public void SandboxId_MatchesGatewayRules(string? value, bool readable, bool creatable)
    {
        Assert.Equal(readable, XboxSandboxId.TryNormalizeForRead(value, out var read));
        Assert.Equal(creatable, XboxSandboxId.TryNormalizeForCreate(value, out var created));
        if (readable)
        {
            Assert.Equal(value!.Trim().ToUpperInvariant(), read);
        }

        if (creatable)
        {
            Assert.Equal(value!.Trim().ToUpperInvariant(), created);
        }
    }

    [Theory]
    [InlineData("RETAIL")]
    [InlineData("cert.debug")]
    [InlineData("READONLY")]
    [InlineData("ABCD.58")]
    [InlineData("undefined.1")]
    public async Task Get_AcceptsGlobalSandboxes(string sandboxId)
    {
        var handler = Handler($$"""{"sandboxId":"{{sandboxId.ToUpperInvariant()}}","type":"Shared","sellerId":null}""");
        var result = await InvokeAsync(handler, ["sandbox", "get", sandboxId]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            $"/xboxpartner/v1/sandboxes/{sandboxId.ToUpperInvariant()}",
            Assert.Single(handler.Requests).RequestUri!.AbsolutePath);
    }
    [Theory]
    [InlineData("json", false)]
    [InlineData("json", true)]
    [InlineData("json-compact", false)]
    [InlineData("json-compact", true)]
    public async Task List_FollowsEveryPageWithOneCorrelationId(string format, bool dataOnly)
    {
        var handler = Handler(
            """{"sandboxes":[{"sandboxId":"RETAIL","type":"Unknown","sellerId":null,"global":true},{"sandboxId":"ABCDEF.1","type":"Shared","sellerId":"seller-1","global":false}],"continuationToken":"page 2"}""",
            """{"sandboxes":[],"continuationToken":"page3"}""",
            """{"sandboxes":[{"sandboxId":"ABCDEF.2","type":"Isolated","sellerId":"seller-1","global":false,"extra":1}]}""");
        string[] arguments = ["sandbox", "list", "--output", format];
        if (dataOnly)
        {
            arguments = [.. arguments, "--data-only"];
        }
        var result = await InvokeAsync(handler, arguments);

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal(
            """{"sandboxes":[{"sandboxId":"RETAIL","type":"Unknown","sellerId":null,"global":true},{"sandboxId":"ABCDEF.1","type":"Shared","sellerId":"seller-1","global":false},{"sandboxId":"ABCDEF.2","type":"Isolated","sellerId":"seller-1","global":false,"extra":1}]}""",
            JsonSerializer.Serialize(document.RootElement));
        Assert.Equal(format == "json", result.Stdout.Trim().Contains('\n'));
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/xboxpartner/v1/sandboxes", request.RequestUri!.AbsolutePath);
            Assert.Equal("seller-1", Assert.Single(request.Headers["X-DeveloperAccountId"]));
            Assert.Null(request.Body);
        });
        Assert.Equal(string.Empty, handler.Requests[0].RequestUri!.Query);
        Assert.Equal("?continuationToken=page%202", handler.Requests[1].RequestUri!.Query);
        Assert.Equal("?continuationToken=page3", handler.Requests[2].RequestUri!.Query);
        Assert.Single(handler.Requests.Select(request => Assert.Single(request.Headers["x-ms-correlation-id"])).Distinct());
    }

    [Fact]
    public async Task List_AcquiresATokenForEveryPage()
    {
        var auth = CreateAuth();
        var account = Mock.Of<IAccount>(
            account => account.HomeAccountId == new AccountId("id", "user", "test-tenant"));
        var tokens = 0;
        auth.Setup(service => service.AcquireTokenSilentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new AuthenticationResult(
                $"token-{++tokens}", false, "user", DateTimeOffset.UtcNow.AddHours(1),
                DateTimeOffset.UtcNow.AddHours(1), "test-tenant", account,
                "id-token", ["api://test/.default"], Guid.NewGuid()));
        var handler = Handler(
            """{"sandboxes":[{"sandboxId":"A1"}],"continuationToken":"t1"}""",
            """{"sandboxes":[{"sandboxId":"A2"}]}""");
        var result = await InvokeAsync(handler, ["sandbox", "list"], auth: auth);

        Assert.Equal(0, result.ExitCode);
        var authorizations = handler.Requests.Select(request => Assert.Single(request.Headers["Authorization"])).ToArray();
        Assert.Equal(2, authorizations.Length);
        Assert.Equal(2, authorizations.Distinct().Count());
        Assert.Single(handler.Requests.Select(request => Assert.Single(request.Headers["x-ms-correlation-id"])).Distinct());
    }

    [Fact]
    public async Task List_RendersTheDeclaredTableAndNeutralizesControlCharacters()
    {
        var handler = Handler(
            """{"sandboxes":[{"sandboxId":"RETAIL","type":"Unknown","global":true},{"sandboxId":"XD\nKS.1","type":"Shared","global":false}]}""");
        var result = await InvokeAsync(handler, ["sandbox", "list", "--output", "table"]);

        Assert.Equal(0, result.ExitCode);
        var lines = result.Stdout.TrimEnd().Split(Environment.NewLine);
        Assert.Equal(4, lines.Length);
        Assert.Contains("SANDBOX", lines[0]);
        Assert.Contains("TYPE", lines[0]);
        Assert.Contains("GLOBAL", lines[0]);
        Assert.DoesNotContain("SELLER", lines[0]);
        Assert.Contains("RETAIL", lines[2]);
        Assert.Contains("true", lines[2]);
        Assert.Contains("XD KS.1", lines[3]);
        Assert.All(lines, line => Assert.DoesNotContain(line, char.IsControl));
    }

    [Theory]
    [InlineData("json", """{"sandboxes":[]}""")]
    [InlineData("json-compact", """{"sandboxes":[]}""")]
    [InlineData("table", "No results.")]
    public async Task List_EmptyIsSuccess(string format, string expected)
    {
        var result = await InvokeAsync(Handler("""{"sandboxes":[]}"""), ["sandbox", "list", "--output", format]);

        Assert.Equal(0, result.ExitCode);
        var actual = result.Stdout.Trim();
        if (format != "table")
        {
            using var document = JsonDocument.Parse(actual);
            actual = JsonSerializer.Serialize(document.RootElement);
        }
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task List_RepeatedContinuationTokenFails()
    {
        var handler = Handler(
            """{"sandboxes":[{"sandboxId":"A1"}],"continuationToken":"t1"}""",
            """{"sandboxes":[{"sandboxId":"A2"}],"continuationToken":"t1"}""");
        var result = await InvokeAsync(handler, ["sandbox", "list"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("sandbox list response repeated a continuation token", result.Stderr);
        Assert.Contains("x-ms-correlation-id: response-correlation", result.Stderr);
    }

    [Fact]
    public async Task List_StopsAfterThePageCap()
    {
        var pages = Enumerable.Range(1, XboxPartnerClient.MaxSandboxListPages + 1)
            .Select(page => $$"""{"sandboxes":[],"continuationToken":"t{{page}}"}""")
            .ToArray();
        var handler = Handler(pages);
        var result = await InvokeAsync(handler, ["sandbox", "list"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Equal(XboxPartnerClient.MaxSandboxListPages, handler.Requests.Count);
        Assert.Contains($"did not end after {XboxPartnerClient.MaxSandboxListPages} pages", result.Stderr);
        Assert.Contains("x-ms-correlation-id: response-correlation", result.Stderr);
    }

    [Fact]
    public async Task List_SucceedsWhenTheLastAllowedPageEndsTheList()
    {
        var pages = Enumerable.Range(1, XboxPartnerClient.MaxSandboxListPages)
            .Select(page => page == XboxPartnerClient.MaxSandboxListPages
                ? """{"sandboxes":[{"sandboxId":"A1"}]}"""
                : $$"""{"sandboxes":[],"continuationToken":"t{{page}}"}""")
            .ToArray();
        var result = await InvokeAsync(Handler(pages), ["sandbox", "list", "--output", "json-compact"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("""{"sandboxes":[{"sandboxId":"A1"}]}""", result.Stdout.Trim());
    }
    [Theory]
    [InlineData("""[]""", "must contain a sandboxes array")]
    [InlineData("""{"sandboxes":null}""", "must contain a sandboxes array")]
    [InlineData("""{"sandboxes":[{"type":"Shared"}]}""", "no 'sandboxId'")]
    [InlineData("""{"sandboxes":["RETAIL"]}""", "no 'sandboxId'")]
    [InlineData("""{"sandboxes":[],"continuationToken":""}""", "invalid 'continuationToken'")]
    [InlineData("""{"sandboxes":[],"continuationToken":5}""", "invalid 'continuationToken'")]
    [InlineData("<html></html>", "not valid JSON")]
    [InlineData("""{"sandboxes":[],"sandboxes":[]}""", "not valid JSON")]
    [InlineData("""{"sandboxes":[{"sandboxId":"A","sandboxId":"B"}]}""", "not valid JSON")]
    public async Task List_RejectsAMalformedPage(string body, string message)
    {
        var result = await InvokeAsync(Handler(body), ["sandbox", "list"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains(message, result.Stderr);
        Assert.Contains("x-ms-correlation-id: response-correlation", result.Stderr);
    }

    [Fact]
    public async Task List_GatewayErrorWritesStderrOnly()
    {
        var handler = new XboxPartnerHttpMessageHandler(
            Response("""{"title":"Problem","detail":"Seller not authorized."}""", HttpStatusCode.Forbidden));
        var result = await InvokeAsync(handler, ["sandbox", "list"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("not authorized", result.Stderr);
    }

    [Fact]
    public async Task List_OutputFeedsSandboxGet()
    {
        var list = await InvokeAsync(
            Handler("""{"sandboxes":[{"sandboxId":"RETAIL","type":"Unknown","global":true},{"sandboxId":"CERT.DEBUG","global":true},{"sandboxId":"ABCDEF.1","type":"Shared","sellerId":"seller-1","global":false}]}"""),
            ["sandbox", "list", "--output", "json-compact"]);
        Assert.Equal(0, list.ExitCode);

        using var sandboxes = JsonDocument.Parse(list.Stdout);
        foreach (var sandbox in sandboxes.RootElement.GetProperty("sandboxes").EnumerateArray())
        {
            var id = sandbox.GetProperty("sandboxId").GetString()!;
            var handler = Handler($$"""{"sandboxId":"{{id}}","type":"Shared","sellerId":"seller-1"}""");
            var get = await InvokeAsync(handler, ["sandbox", "get", id]);

            Assert.Equal(0, get.ExitCode);
            Assert.Equal($"/xboxpartner/v1/sandboxes/{id}", Assert.Single(handler.Requests).RequestUri!.AbsolutePath);
        }
    }

    [Theory]
    [InlineData("json-compact")]
    [InlineData("table")]
    public async Task Get_ShowsIsolatedSandboxes(string format)
    {
        const string isolated = """{"sandboxId":"ABCD.58","type":"Isolated","sellerId":"seller-1","global":false}""";
        var result = await InvokeAsync(Handler(isolated), ["sandbox", "get", "abcd.58", "--output", format]);

        Assert.Equal(0, result.ExitCode);
        if (format == "table")
        {
            var row = result.Stdout.TrimEnd().Split(Environment.NewLine)[^1];
            Assert.Contains("ABCD.58", row);
            Assert.Contains("Isolated", row);
        }
        else
        {
            Assert.Equal(isolated, result.Stdout.Trim());
        }
    }

    [Fact]
    public async Task Get_SendsSellerHeaderAndWritesTheSandbox()
    {
        var handler = Handler(Sandbox);
        var result = await InvokeAsync(handler, ["sandbox", "get", "abcdef.1", "--output", "json-compact"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Sandbox, result.Stdout.Trim());
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/xboxpartner/v1/sandboxes/ABCDEF.1", request.RequestUri!.AbsolutePath);
        Assert.Equal("seller-1", Assert.Single(request.Headers["X-DeveloperAccountId"]));
        Assert.Null(request.Body);
    }

    [Fact]
    public async Task Get_RendersTheDeclaredTable()
    {
        var result = await InvokeAsync(Handler(Sandbox), ["sandbox", "get", "ABCDEF.1", "--output", "table"]);

        Assert.Equal(0, result.ExitCode);
        var lines = result.Stdout.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("SANDBOX ID", lines[0]);
        Assert.Contains("TYPE", lines[0]);
        Assert.Contains("SELLER ID", lines[0]);
        Assert.Contains("ABCDEF.1", lines[^1]);
        Assert.Contains("Shared", lines[^1]);
        Assert.Contains("seller-1", lines[^1]);
    }

    [Theory]
    [InlineData("create", "RETAIL")]
    [InlineData("create", "CERT.DEBUG")]
    [InlineData("create", "ABC.1")]
    [InlineData("create", "ABCD.01")]
    [InlineData("create", "ABCD.1000")]
    [InlineData("create", "ABCD.58")]
    [InlineData("create", "undefined.1")]
    [InlineData("create", "UNDEFINED.1")]
    [InlineData("create", "MSFT.1")]
    [InlineData("create", "\u017F\u017F\u017F\u017F.1")]
    [InlineData("get", "\u017F\u017F\u017F\u017F.1")]
    [InlineData("get", "ABCD-1.1")]
    [InlineData("get", "A.B.C")]
    public async Task InvalidSandboxId_FailsBeforeAuthentication(string command, string sandboxId)
    {
        var handler = Handler(Sandbox);
        var auth = CreateAuth();
        var result = await InvokeAsync(handler, ["sandbox", command, sandboxId, "--confirm"], auth: auth);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains($"Invalid sandbox ID '{sandboxId}'", result.Stderr);
        Assert.Empty(handler.Requests);
        auth.Verify(service => service.AcquireTokenSilentAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_MonikerLongerThan60Characters_FailsBeforeAuthentication()
    {
        var handler = Handler(Accepted);
        var auth = CreateAuth();
        var result = await InvokeAsync(handler, ["sandbox", "create", TooLongCreateMoniker, "--confirm"], auth: auth);

        Assert.Equal(63, TooLongCreateMoniker.Length);
        Assert.Equal(64, LongestCreateMoniker.Length);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("4 to 60 letters or digits", result.Stderr);
        Assert.Empty(handler.Requests);
        auth.Verify(service => service.AcquireTokenSilentAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_AcceptsA60CharacterMonikerWithA999Suffix()
    {
        var handler = Handler(Succeeded, $$"""{"sandboxId":"{{LongestCreateMoniker}}","type":"Shared","sellerId":"seller-1"}""");
        var result = await InvokeAsync(handler, ["sandbox", "create", LongestCreateMoniker.ToLowerInvariant(), "--confirm"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains($"\"sandboxId\":\"{LongestCreateMoniker}\"", handler.Requests[0].Body);
    }

    [Theory]
    [InlineData("get")]
    [InlineData("create")]
    public async Task SandboxIdLongerThan64Characters_FailsBeforeAuthentication(string command)
    {
        var handler = Handler(Sandbox);
        var auth = CreateAuth();
        var result = await InvokeAsync(handler, ["sandbox", command, TooLongId, "--confirm"], auth: auth);

        Assert.Equal(65, TooLongId.Length);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("at most 64 characters", result.Stderr);
        Assert.Empty(handler.Requests);
        auth.Verify(service => service.AcquireTokenSilentAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "HTTP 404")]
    [InlineData(HttpStatusCode.BadRequest, "request was rejected")]
    public async Task Get_GatewayErrorWritesStderrOnly(HttpStatusCode status, string message)
    {
        var handler = new XboxPartnerHttpMessageHandler(
            Response("""{"title":"Problem","detail":"Sandbox not found."}""", status));
        var result = await InvokeAsync(handler, ["sandbox", "get", "ABCDEF.1"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains(message, result.Stderr);
        Assert.Contains("Sandbox not found.", result.Stderr);
    }

    [Theory]
    [InlineData("""{"type":"Shared"}""")]
    [InlineData("""["ABCDEF.1"]""")]
    [InlineData("<html></html>")]
    public async Task Get_RejectsAMalformedSuccessBody(string body)
    {
        var result = await InvokeAsync(Handler(body), ["sandbox", "get", "ABCDEF.1"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("sandbox", result.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("--confirm", 0)]
    [InlineData("--yes", 1)]
    [InlineData("-y", 3)]
    public async Task Create_SubmitsSharedSandboxWaitsAndWritesIt(string alias, int position)
    {
        var handler = Handler(Accepted, Running, Succeeded, Sandbox);
        var args = CreateArguments.ToList();
        args.Insert(position, alias);
        var result = await InvokeAsync(handler, [.. args, "--output", "json-compact"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Sandbox, result.Stdout.Trim());
        Assert.Equal(4, handler.Requests.Count);

        var post = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, post.Method);
        Assert.Equal("/xboxpartner/v1/sandboxes", post.RequestUri!.AbsolutePath);
        Assert.Equal("seller-1", Assert.Single(post.Headers["X-DeveloperAccountId"]));
        Assert.Equal("""{"sandboxId":"ABCDEF.1","type":"Shared"}""", post.Body);

        Assert.All(handler.Requests.Skip(1).Take(2), poll =>
        {
            Assert.Equal(HttpMethod.Get, poll.Method);
            Assert.Equal("/xboxpartner/v1/sandboxes/jobs/a1b2c3d4-0000-4000-8000-000000000001", poll.RequestUri!.AbsolutePath);
        });
        Assert.Equal("/xboxpartner/v1/sandboxes/ABCDEF.1", handler.Requests[3].RequestUri!.AbsolutePath);
        Assert.Contains("Submitting sandbox creation job...", result.Stderr);
        Assert.Contains("Sandbox creation job submitted: a1b2c3d4-0000-4000-8000-000000000001 (Correlation ID: response-correlation)", result.Stderr);
        Assert.Contains("Sandbox creation job a1b2c3d4-0000-4000-8000-000000000001 completed.", result.Stderr);
        Assert.DoesNotContain("[y/N]", result.Stderr);
    }

    [Theory]
    [InlineData(false, "", false)]
    [InlineData(true, "n", false)]
    [InlineData(true, "", false)]
    [InlineData(true, "y", true)]
    [InlineData(true, "YES", true)]
    public async Task Create_FailsClosedOrPromptsOnStderr(bool interactive, string answer, bool accepted)
    {
        var handler = Handler(Succeeded, Sandbox);
        var result = await InvokeAsync(handler, CreateArguments, interactive, answer);

        Assert.Equal(accepted ? 0 : 1, result.ExitCode);
        Assert.Equal(accepted ? 2 : 0, handler.Requests.Count);
        Assert.Contains(
            "Creating the Shared sandbox 'ABCDEF.1' under seller seller-1 cannot be undone from this CLI.",
            result.Stderr);
        Assert.Equal(interactive, result.Stderr.Contains("Create this sandbox? [y/N]: ", StringComparison.Ordinal));
        if (!accepted)
        {
            Assert.Empty(result.Stdout);
            Assert.Contains(interactive ? "Sandbox creation canceled." : "Re-run with --confirm", result.Stderr);
        }
    }

    [Fact]
    public async Task Create_LocalFalseDoesNotInheritRootConfirmation()
    {
        var handler = Handler(Succeeded, Sandbox);
        var result = await InvokeAsync(handler, ["--confirm", .. CreateArguments, "--confirm", "false"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Create_RootConfirmationIsHonored()
    {
        var handler = Handler(Succeeded, Sandbox);
        var result = await InvokeAsync(handler, ["--confirm", .. CreateArguments]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Create_FailedJobWritesTheJobToStdoutAndExitsTwo()
    {
        var handler = Handler(Accepted, Failed);
        var result = await InvokeAsync(handler, [.. CreateArguments, "--confirm", "--output", "json-compact"]);

        Assert.Equal(SandboxCommandFactory.JobFailedExitCode, result.ExitCode);
        Assert.Equal(Failed, result.Stdout.Trim());
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("completed without creating sandbox ABCDEF.1", result.Stderr);
        Assert.DoesNotContain(Failed, result.Stderr);
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, "may already exist")]
    [InlineData(HttpStatusCode.BadRequest, "request was rejected")]
    [InlineData(HttpStatusCode.TooManyRequests, "throttled")]
    public async Task Create_DefiniteRejectionIsNotReportedAsUncertain(HttpStatusCode status, string message)
    {
        var handler = new XboxPartnerHttpMessageHandler(
            Response("""{"title":"Problem","detail":"Gateway detail."}""", status));
        var result = await InvokeAsync(handler, [.. CreateArguments, "--confirm"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Single(handler.Requests);
        Assert.Contains(message, result.Stderr);
        Assert.Contains("Gateway detail.", result.Stderr);
        Assert.DoesNotContain("Do not resubmit", result.Stderr);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.MultipleChoices)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    public async Task Create_UncertainOutcomeIsNeverRetried(HttpStatusCode status)
    {
        var handler = new XboxPartnerHttpMessageHandler(Response("{}", status));
        var result = await InvokeAsync(handler, [.. CreateArguments, "--confirm"]);

        Assert.Equal(SandboxCommandFactory.OutcomeUnknownExitCode, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Single(handler.Requests);
        Assert.Contains("it is unknown whether sandbox ABCDEF.1 was created. Do not resubmit.", result.Stderr);
        Assert.Contains(
            "Check whether it exists with: xbox sandbox get ABCDEF.1 --seller-id seller-1 --base-url https://gateway.example",
            result.Stderr);
    }

    [Fact]
    public async Task Create_RecoveryCommandKeepsTheProfile()
    {
        var handler = new XboxPartnerHttpMessageHandler(Response("{}", HttpStatusCode.ServiceUnavailable));
        var result = await InvokeAsync(handler, ["--profile", "default", .. CreateArguments, "--confirm"]);

        Assert.Equal(SandboxCommandFactory.OutcomeUnknownExitCode, result.ExitCode);
        Assert.Contains(
            "Check whether it exists with: xbox --profile default sandbox get ABCDEF.1 --seller-id seller-1 --base-url https://gateway.example",
            result.Stderr);
    }

    [Fact]
    public async Task Create_NotSignedInIsADefiniteFailure()
    {
        var handler = Handler(Accepted);
        var auth = CreateAuth();
        auth.Setup(service => service.AcquireTokenSilentAsync(It.IsAny<CancellationToken>())).ReturnsAsync((AuthenticationResult?)null);
        var result = await InvokeAsync(handler, [.. CreateArguments, "--confirm"], auth: auth);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Empty(handler.Requests);
        Assert.Contains("No cached authentication token. Run 'xbox auth login' first.", result.Stderr);
        Assert.DoesNotContain("Do not resubmit", result.Stderr);
        Assert.DoesNotContain("Check whether it exists", result.Stderr);
    }

    [Fact]
    public async Task Create_MsalFailureIsADefiniteFailure()
    {
        var handler = Handler(Accepted);
        var auth = CreateAuth();
        auth.Setup(service => service.AcquireTokenSilentAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new MsalUiRequiredException("invalid_grant", "Interactive sign-in is required."));
        var result = await InvokeAsync(handler, [.. CreateArguments, "--confirm"], auth: auth);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(handler.Requests);
        Assert.DoesNotContain("Do not resubmit", result.Stderr);
    }

    [Fact]
    public async Task Create_InvalidSellerHeaderIsADefiniteFailure()
    {
        var handler = Handler(Accepted);
        var result = await InvokeAsync(handler, [.. CreateArguments, "--confirm"], sellerId: "seller\n1");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("must not contain line breaks", result.Stderr);
        Assert.DoesNotContain("Do not resubmit", result.Stderr);
    }
    [Fact]
    public async Task Create_NonAsciiSellerIsADefiniteFailure()
    {
        var handler = Handler(Accepted);
        var result = await InvokeAsync(handler, [.. CreateArguments, "--confirm"], sellerId: "s\u00e9ller-1");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("must contain only printable ASCII characters", result.Stderr);
        Assert.DoesNotContain("Do not resubmit", result.Stderr);
    }

    [Fact]
    public async Task Create_StripsControlCharactersFromServerSuppliedJobText()
    {
        const string hostileJob = "job\u001b[2J\r\nFAKE\u0007";
        var accepted = new HttpResponseMessage(HttpStatusCode.Accepted)
        {
            Content = new StringContent(
                """{"jobId":"job\u001b[2J\r\nFAKE\u0007","status":"running","result":"pending"}"""),
        };
        accepted.Headers.TryAddWithoutValidation("x-ms-correlation-id", "corr\u001b]0;title\u0007");
        var handler = new XboxPartnerHttpMessageHandler(accepted);
        var result = await InvokeAsync(handler, [.. CreateArguments, "--confirm"]);

        // A job ID that isn't a GUID can't be followed, so the outcome is unknown and it isn't polled.
        Assert.Equal(SandboxCommandFactory.OutcomeUnknownExitCode, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Single(handler.Requests);
        Assert.Contains("Sandbox creation job submitted: job[2JFAKE (Correlation ID: corr]0;title)", result.Stderr);
        Assert.Contains("sandbox creation job response was invalid", result.Stderr);
        Assert.Contains("Do not resubmit.", result.Stderr);
        Assert.DoesNotContain('\u001b', result.Stderr);
        Assert.DoesNotContain('\u0007', result.Stderr);
        Assert.DoesNotContain(hostileJob, result.Stderr);
    }

    [Fact]
    public async Task Create_PollingFailureReasonPhraseIsWrittenWithoutControlCharacters()
    {
        var failure = new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("{}"),
            ReasonPhrase = "Bad\u001b[2J\u0007 Gateway",
        };
        var handler = new XboxPartnerHttpMessageHandler(Response(Accepted), failure);
        var result = await InvokeAsync(handler, [.. CreateArguments, "--confirm"]);

        Assert.Equal(SandboxCommandFactory.WaitEndedExitCode, result.ExitCode);
        Assert.Contains("its status could not be read.", result.Stderr);
        Assert.Contains("(Bad[2J Gateway)", result.Stderr);
        Assert.DoesNotContain('\u001b', result.Stderr);
        Assert.DoesNotContain('\u0007', result.Stderr);
    }

    [Fact]
    public void WriteLocalError_WritesTheMessageWithoutControlCharacters()
    {
        var oldError = Console.Error;
        using var stderr = new StringWriter();
        try
        {
            Console.SetError(stderr);
            var exitCode = XboxPartnerCommandSupport.WriteLocalError(
                new InvalidOperationException("Local\u001b[31m\u0007\r\nFAKE failure"));

            Assert.Equal(1, exitCode);
        }
        finally
        {
            Console.SetError(oldError);
        }

        Assert.Equal("Local[31m\nFAKE failure" + Environment.NewLine, stderr.ToString());
    }

    [Fact]
    public async Task Create_PollingFailureCorrelationIdIsWrittenWithoutControlCharacters()
    {
        var failure = new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("{}") };
        failure.Headers.TryAddWithoutValidation("x-ms-correlation-id", "poll\u001b[31m\u0007");
        var handler = new XboxPartnerHttpMessageHandler(Response(Accepted), failure);
        var result = await InvokeAsync(handler, [.. CreateArguments, "--confirm"]);

        Assert.Equal(SandboxCommandFactory.WaitEndedExitCode, result.ExitCode);
        Assert.Contains("x-ms-correlation-id: poll", result.Stderr);
        Assert.DoesNotContain('\u001b', result.Stderr);
        Assert.DoesNotContain('\u0007', result.Stderr);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("abc-123", "abc-123")]
    [InlineData("a\u0000b\tc\u007f\u0085d", "abcd")]
    public void ForTerminal_RemovesControlCharacters(string? value, string expected) =>
        Assert.Equal(expected, XboxPartnerCommandSupport.ForTerminal(value));

    [Fact]
    public async Task Create_MalformedSubmissionBodyIsWrittenWithoutControlCharacters()
    {
        var handler = Handler("not-json\u001b[31m\u0007\r\nFAKE");
        var result = await InvokeAsync(handler, [.. CreateArguments, "--confirm"]);

        Assert.Equal(SandboxCommandFactory.OutcomeUnknownExitCode, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("not-json[31m", result.Stderr);
        Assert.DoesNotContain('\u001b', result.Stderr);
        Assert.DoesNotContain('\u0007', result.Stderr);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Get_GatewayProblemDetailIsWrittenWithoutControlCharacters(bool verbose)
    {
        var handler = new XboxPartnerHttpMessageHandler(Response(
            """{"title":"Problem","detail":"Bad\u001b[2J\u0007\nFAKE line"}""", HttpStatusCode.BadRequest));
        string[] args = verbose ? ["sandbox", "get", "ABCDEF.1", "--verbose"] : ["sandbox", "get", "ABCDEF.1"];
        var result = await InvokeAsync(handler, args);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Error detail: Bad[2JFAKE line", result.Stderr);
        Assert.DoesNotContain('\u001b', result.Stderr);
        Assert.DoesNotContain('\u0007', result.Stderr);
        Assert.DoesNotContain(
            result.Stderr.Split('\n'),
            line => line.StartsWith("FAKE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Get_VerboseRawErrorBodyIsWrittenWithoutControlCharacters()
    {
        var handler = new XboxPartnerHttpMessageHandler(Response(
            "<html>\u001b[31mproxy\u0007 error</html>", HttpStatusCode.BadGateway));
        var result = await InvokeAsync(handler, ["sandbox", "get", "ABCDEF.1", "--verbose"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("<html>[31mproxy error</html>", result.Stderr);
        Assert.DoesNotContain('\u001b', result.Stderr);
        Assert.DoesNotContain('\u0007', result.Stderr);
    }

    [Theory]
    [InlineData(null, "", "")]
    [InlineData("a\u001bb\u0007c", "abc", "abc")]
    [InlineData("line1\r\nline2\tx", "line1line2x", "line1\nline2\tx")]
    [InlineData("a\u2028b\u2029c", "abc", "abc")]
    [InlineData("err\u202eor\u2066x\u200f", "errorx", "errorx")]
    public void TerminalText_RemovesControlCharacters(string? value, string singleLine, string multiLine)
    {
        Assert.Equal(singleLine, TerminalText.SingleLine(value));
        Assert.Equal(multiLine, TerminalText.MultiLine(value));
    }

    /// <summary>
    /// The likeliest real "outcome unknown" cases. Each must exit 3, never 1, so a script doesn't
    /// read the failure as safe to resubmit, and must send the request exactly once.
    /// </summary>
    [Fact]
    public async Task Create_TransportFailureAfterSendingIsUnknown()
    {
        var handler = new ThrowingHandler(new HttpRequestException("The connection was reset."));
        var result = await InvokeAsync(handler, [.. CreateArguments, "--confirm"]);

        Assert.Equal(SandboxCommandFactory.OutcomeUnknownExitCode, result.ExitCode);
        Assert.Equal(1, handler.Count);
        Assert.Empty(result.Stdout);
        Assert.Contains("it is unknown whether sandbox ABCDEF.1 was created. Do not resubmit.", result.Stderr);
    }

    [Fact]
    public async Task Create_CancellationAfterSendingIsUnknown()
    {
        var handler = new ThrowingHandler(new OperationCanceledException("Simulated Ctrl+C after sending."));
        var result = await InvokeAsync(handler, [.. CreateArguments, "--confirm"]);

        Assert.Equal(SandboxCommandFactory.OutcomeUnknownExitCode, result.ExitCode);
        Assert.Equal(1, handler.Count);
        Assert.Empty(result.Stdout);
        Assert.Contains("it is unknown whether sandbox ABCDEF.1 was created. Do not resubmit.", result.Stderr);
    }

    [Theory]
    [InlineData("job-1")]
    [InlineData("00000000-0000-0000-0000-00000000000Z")]
    public async Task Create_JobIdThatIsNotAGuidIsUnknown(string jobId)
    {
        var handler = Handler($$"""{"jobId":"{{jobId}}","status":"notStarted","result":"pending"}""");
        var result = await InvokeAsync(handler, [.. CreateArguments, "--confirm"]);

        Assert.Equal(SandboxCommandFactory.OutcomeUnknownExitCode, result.ExitCode);
        Assert.Single(handler.Requests);
        Assert.Empty(result.Stdout);
        Assert.Contains("sandbox creation job response was invalid", result.Stderr);
        Assert.Contains("Do not resubmit.", result.Stderr);
    }

    [Fact]
    public async Task Create_MalformedSubmissionResponseIsUncertain()
    {
        var result = await InvokeAsync(Handler("""{"status":"notStarted"}"""), [.. CreateArguments, "--confirm"]);

        Assert.Equal(SandboxCommandFactory.OutcomeUnknownExitCode, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("Do not resubmit", result.Stderr);
    }

    [Fact]
    public async Task Create_TimeoutExitsFourWithNothingOnStdout()
    {
        var responses = new[] { Accepted }.Concat(Enumerable.Repeat(Running, 40)).ToArray();
        var handler = Handler(responses);
        var result = await InvokeAsync(handler, [.. CreateArguments, "--confirm", "--output", "json-compact"]);

        Assert.Equal(SandboxCommandFactory.WaitEndedExitCode, result.ExitCode);
        Assert.Equal(4, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("Sandbox creation job a1b2c3d4-0000-4000-8000-000000000001 is still running after 120 seconds. Do not resubmit.", result.Stderr);
        Assert.Contains("Check whether it exists with: xbox sandbox get ABCDEF.1", result.Stderr);
        Assert.Equal(1, handler.Requests.Count(request => request.Method == HttpMethod.Post));
        Assert.All(handler.Requests.Skip(1), poll =>
            Assert.Equal("/xboxpartner/v1/sandboxes/jobs/a1b2c3d4-0000-4000-8000-000000000001", poll.RequestUri!.AbsolutePath));
    }

    public static TheoryData<string> UnrecognizedResults => new()
    {
        """{"jobId":"a1b2c3d4-0000-4000-8000-000000000001","status":"completed"}""",
        """{"jobId":"a1b2c3d4-0000-4000-8000-000000000001","status":"completed","result":null}""",
        """{"jobId":"a1b2c3d4-0000-4000-8000-000000000001","status":"completed","result":"partial"}""",
    };

    [Theory]
    [MemberData(nameof(UnrecognizedResults))]
    public async Task Create_PolledJobWithoutARecognizedResultIsNotAFailure(string completedJob)
    {
        var handler = Handler(Accepted, completedJob);
        var result = await InvokeAsync(handler, [.. CreateArguments, "--confirm"]);

        Assert.Equal(SandboxCommandFactory.WaitEndedExitCode, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("completed without a recognized result", result.Stderr);
        Assert.DoesNotContain("without creating sandbox", result.Stderr);
        Assert.Contains(
            "Check whether it exists with: xbox sandbox get ABCDEF.1 --seller-id seller-1 --base-url https://gateway.example",
            result.Stderr);
    }

    [Theory]
    [MemberData(nameof(UnrecognizedResults))]
    public async Task Create_SubmittedJobWithoutARecognizedResultIsUnknown(string completedJob)
    {
        var handler = Handler(completedJob);
        var result = await InvokeAsync(handler, [.. CreateArguments, "--confirm"]);

        Assert.Equal(SandboxCommandFactory.OutcomeUnknownExitCode, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Single(handler.Requests);
        Assert.Contains("sandbox creation job response was invalid", result.Stderr);
        Assert.Contains("Check whether it exists with: xbox sandbox get ABCDEF.1", result.Stderr);
    }

    [Fact]
    public async Task Create_SubmittedJobThatAlreadyFailedExitsTwo()
    {
        var handler = Handler(Failed);
        var result = await InvokeAsync(handler, [.. CreateArguments, "--confirm", "--output", "json-compact"]);

        Assert.Equal(SandboxCommandFactory.JobFailedExitCode, result.ExitCode);
        Assert.Equal(Failed, result.Stdout.Trim());
        Assert.Single(handler.Requests);
    }
    [Fact]
    public async Task Create_PollingFailureReportsRecovery()
    {
        var handler = new XboxPartnerHttpMessageHandler(
            Response(Accepted),
            Response("""{"detail":"Down."}""", HttpStatusCode.BadGateway));
        var result = await InvokeAsync(handler, [.. CreateArguments, "--confirm"]);

        Assert.Equal(SandboxCommandFactory.WaitEndedExitCode, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("The sandbox was submitted, but its status could not be read", result.Stderr);
        Assert.Contains("Check whether it exists with: xbox sandbox get ABCDEF.1", result.Stderr);
        Assert.Single(result.Stderr.Split(Environment.NewLine),
            line => line.Contains("HTTP 502", StringComparison.Ordinal));
        Assert.Contains("Error detail: Down.", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_ReadBackFailureReportsTheCreatedSandbox()
    {
        var handler = new XboxPartnerHttpMessageHandler(
            Response(Succeeded),
            Response("""{"detail":"Not yet."}""", HttpStatusCode.NotFound),
            Response("""{"detail":"Still not."}""", HttpStatusCode.NotFound));
        var result = await InvokeAsync(handler, [.. CreateArguments, "--confirm"]);

        Assert.Equal(SandboxCommandFactory.WaitEndedExitCode, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Contains("Sandbox ABCDEF.1 was created, but it could not be read back.", result.Stderr);
        Assert.Contains("Check it with: xbox sandbox get ABCDEF.1", result.Stderr);
        Assert.Contains("Still not.", result.Stderr);
    }

    /// <summary>
    /// Cancelling during the pause before the read-back retry must still say the sandbox was
    /// created and how to check it, rather than only "Request canceled.".
    /// </summary>
    [Fact]
    public async Task Create_CancellationDuringReadBackRetryReportsTheCreatedSandbox()
    {
        var handler = new XboxPartnerHttpMessageHandler(
            Response(Succeeded),
            Response("""{"detail":"Not yet."}""", HttpStatusCode.NotFound));
        var result = await InvokeAsync(
            handler, [.. CreateArguments, "--confirm"],
            delayAsync: (_, _) => throw new OperationCanceledException("Simulated Ctrl+C."));

        Assert.Equal(SandboxCommandFactory.WaitEndedExitCode, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("Sandbox ABCDEF.1 was created, but it could not be read back.", result.Stderr);
        Assert.Contains("Check it with: xbox sandbox get ABCDEF.1", result.Stderr);
    }
    [Fact]
    public async Task Create_ReadBackIsRetriedOnce()
    {
        var handler = new XboxPartnerHttpMessageHandler(
            Response(Succeeded),
            Response("""{"detail":"Not yet."}""", HttpStatusCode.NotFound),
            Response(Sandbox));
        var result = await InvokeAsync(handler, [.. CreateArguments, "--confirm", "--output", "json-compact"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Sandbox, result.Stdout.Trim());
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests.Skip(1), request =>
            Assert.Equal("/xboxpartner/v1/sandboxes/ABCDEF.1", request.RequestUri!.AbsolutePath));
        Assert.DoesNotContain("could not be read back", result.Stderr);
    }

    [Fact]
    public async Task Create_CancellationWhileWaitingStopsWithoutResubmitting()
    {
        var handler = new CancelingHandler();
        var result = await InvokeAsync(handler, [.. CreateArguments, "--confirm"]);

        Assert.Equal(SandboxCommandFactory.WaitEndedExitCode, result.ExitCode);
        Assert.Equal(2, handler.Count);
        Assert.Empty(result.Stdout);
        Assert.Contains("Stopped waiting for job a1b2c3d4-0000-4000-8000-000000000001", result.Stderr);
    }

    /// <summary>
    /// The table gate runs in <c>Program.Main</c>, which builds its own command tree, so an
    /// injected handler cannot observe it. The test instead checks the declaration and that the
    /// gate's message is the only output: the action would otherwise have written its
    /// confirmation or submission progress. <c>--confirm</c> is omitted so a regression stops at
    /// the confirmation step rather than submitting.
    /// </summary>
    [Fact]
    public async Task Create_RefusesTableOutputBeforeTheActionRuns()
    {
        var root = Program.BuildRootCommand();
        var sandbox = Assert.Single(root.Subcommands, command => command.Name == "sandbox");
        Assert.False(Program.SupportsTableOutput(Assert.Single(sandbox.Subcommands, command => command.Name == "create")));
        Assert.True(Program.SupportsTableOutput(Assert.Single(sandbox.Subcommands, command => command.Name == "get")));
        Assert.True(Program.SupportsTableOutput(Assert.Single(sandbox.Subcommands, command => command.Name == "list")));

        var result = await InvokeAsync(
            Handler(Sandbox), [.. CreateArguments, "--output", "table"], throughMain: true);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Equal(Program.TableUnavailableMessage, TestEnvironment.WithoutEndpointWarnings(result.Stderr).Trim());
    }

    [Fact]
    public async Task Create_VerboseDescribesEveryOperation()
    {
        var handler = Handler(Accepted, Succeeded, Sandbox);
        var result = await InvokeAsync(handler, [.. CreateArguments, "--confirm", "--verbose"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Submitting sandbox creation for seller seller-1.", result.Stderr);
        Assert.Contains("Reading sandbox creation job a1b2c3d4-0000-4000-8000-000000000001.", result.Stderr);
        Assert.Contains("Reading sandbox ABCDEF.1.", result.Stderr);
    }

    [Fact]
    public async Task MissingSeller_FailsWithContextGuidance()
    {
        var previous = Environment.GetEnvironmentVariable("XBOX_SELLER_ID");
        Environment.SetEnvironmentVariable("XBOX_SELLER_ID", null);
        try
        {
            foreach (var command in new[] { "list", "get", "create" })
            {
                var handler = Handler(Sandbox);
                string[] args = command == "list"
                    ? ["sandbox", "list"]
                    : ["sandbox", command, "ABCDEF.1", "--confirm"];
                var result = await InvokeAsync(handler, args, sellerId: null);

                Assert.Equal(1, result.ExitCode);
                Assert.Empty(handler.Requests);
                Assert.Empty(result.Stdout);
                Assert.Contains(
                    "No seller-id is set. Pass --seller-id, set XBOX_SELLER_ID, or run "
                    + "'xbox context set seller-id <value>'.",
                    result.Stderr);
                Assert.DoesNotContain("Do not resubmit", result.Stderr);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("XBOX_SELLER_ID", previous);
        }
    }

    [Fact]
    public async Task SavedSeller_IsSentInTheHeader()
    {
        var previous = Environment.GetEnvironmentVariable("XBOX_SELLER_ID");
        Environment.SetEnvironmentVariable("XBOX_SELLER_ID", null);
        var directory = Path.Combine(Path.GetTempPath(), $"sandbox-tests-{Guid.NewGuid():N}");
        try
        {
            await new ContextStore(Path.Combine(directory, ContextStore.FileName)).UpdateAsync(
                new SignedInIdentity("test-tenant", "user"),
                [new ContextChange("seller-id", "https://gateway.example", "7654321")]);
            var handler = Handler(Sandbox);
            var result = await InvokeAsync(
                handler, ["sandbox", "get", "ABCDEF.1"], sellerId: null, stateDirectory: directory);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal("7654321", Assert.Single(Assert.Single(handler.Requests).Headers["X-DeveloperAccountId"]));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XBOX_SELLER_ID", previous);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("get")]
    [InlineData("create")]
    public async Task Help_DescribesSafetyAndExamples(string command)
    {
        var result = await InvokeAsync(Handler(Sandbox), ["sandbox", command, "--help"], addScope: false);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains($"xbox sandbox {command} ABCDEF.", result.Stdout);
        Assert.Contains("--seller-id", result.Stdout);
        if (command == "create")
        {
            Assert.Contains("--confirm", result.Stdout);
            Assert.Contains("Mutates production state.", result.Stdout);
        }
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
        bool interactive = false,
        string input = "",
        Mock<AuthenticationService>? auth = null,
        string? sellerId = "seller-1",
        bool addScope = true,
        bool throughMain = false,
        string? stateDirectory = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        auth ??= CreateAuth();
        using var httpClient = new HttpClient(handler);
        var clock = DateTimeOffset.UnixEpoch;
        var dependencies = new XboxPartnerJobDependencies(
            delayAsync ?? ((delay, token) =>
            {
                token.ThrowIfCancellationRequested();
                clock += delay;
                return Task.CompletedTask;
            }),
            () => clock,
            () => interactive);
        var root = Program.BuildRootCommand(sandboxCommandFactory: () => SandboxCommandFactory.Create(
            configuration => new XboxPartnerClient(configuration, httpClient, auth.Object),
            dependencies));
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
        var oldIn = Console.In;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        using var stdin = new StringReader(input);
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            Console.SetIn(stdin);

            // Context values come from the state directory given, for the account auth signs in as.
            ContextResolver.ReadIdentityAsync = _ => SignedInIdentity.FromCacheAsync(auth.Object, CancellationToken.None);
            ContextStore.DefaultDirectory = () =>
                stateDirectory ?? Path.Combine(Path.GetTempPath(), $"sandbox-tests-{Guid.NewGuid():N}");

            var parseResult = throughMain
                ? null
                : await Program.ResolveContextAsync(root.Parse(args.ToArray(), Program.NoResponseFileConfig), args);
            var exitCode = throughMain
                ? await Program.Main(args.ToArray())
                : parseResult is null ? 1 : await parseResult.InvokeAsync();
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

    /// <summary>Throws <paramref name="exception"/> once the request reaches the network.</summary>
    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        internal int Count { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            throw exception;
        }
    }

    private sealed class CancelingHandler : HttpMessageHandler
    {
        internal int Count { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            if (Count == 1)
            {
                return Task.FromResult(Response(Running));
            }

            throw new OperationCanceledException("Simulated cancellation.");
        }
    }
}
