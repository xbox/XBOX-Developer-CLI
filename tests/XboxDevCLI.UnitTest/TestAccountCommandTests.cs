// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using XboxDevCLI.Commands;
using XboxDevCLI.Safety;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

[Collection("ConsoleRedirect")]
public class TestAccountCommandTests
{
    private const string BatchesResponse = """
        {"batches":[{"id":9007199254740993,"emailTemplate":"ABCDEF-smoke","batchSize":2,
          "createdBy":7654321,"familyPermission":"Adult","creationDate":"2026-09-12T04:00:00Z",
          "accessExpirationDate":"2027-03-12T04:00:00Z","locale":"en-US","keywords":null,
          "sandboxAccess":null,"isComplete":true,"progress":null}]}
        """;

    private const string IncludeSandboxesNote =
        "Listed at most the newest 10 matching batches: --include-sandboxes reads each batch's details, so set " +
        "--limit to list more.";

    private const string BatchResponse = """
        {"batch":{"id":42,"emailTemplate":"ABCDEF-smoke","batchSize":1,"createdBy":7654321,
          "familyPermission":"Adult","isComplete":true,"sandboxAccess":["ABCDEF.0"]},
         "accounts":[{"xuid":"2814600000000001","email":"a@example.com","gamertag":"Alpha","isDeleted":false}]}
        """;

    [Fact]
    public async Task BatchList_SendsFiltersAndPrintsJsonByDefault()
    {
        var handler = Respond(HttpStatusCode.OK, BatchesResponse);

        Invocation result = await InvokeAsync(
            [
                "test-account", "batch", "list",
                "--seller-id", "1234567",
                "--mine",
                "--email-prefix", " ABCDEF ",
                "--tag", "nightly",
                "--state", "complete",
                "--limit", "5",
            ],
            handler);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Stderr);
        CapturedHttpRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/xboxpartner/v1/testaccounts/batches", request.RequestUri?.AbsolutePath);
        Assert.Equal(
            "?sellerId=1234567&mine=true&emailPrefix=ABCDEF&keyword=nightly&state=complete&top=5&pageSize=100",
            request.RequestUri?.Query);
        using JsonDocument output = JsonDocument.Parse(result.Stdout);
        JsonProperty batches = Assert.Single(output.RootElement.EnumerateObject());
        Assert.Equal("batches", batches.Name);
        Assert.Equal(9007199254740993, batches.Value[0].GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task BatchList_TableWithSandboxesRendersSandboxColumn()
    {
        var handler = Respond(
            HttpStatusCode.OK,
            BatchesResponse.Replace("\"sandboxAccess\":null", "\"sandboxAccess\":[\"ABCDEF.0\"]"));

        Invocation result = await InvokeAsync(
            [
                "test-account", "batch", "list",
                "--seller-id", "1234567",
                "--created-by-id", "7654321",
                "--include-sandboxes",
                "--output", "table",
            ],
            handler);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            "?sellerId=1234567&createdBy=7654321&top=10&includeSandboxes=true&pageSize=100",
            Assert.Single(handler.Requests).RequestUri?.Query);
        Assert.Contains("SANDBOXES", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("ABCDEF.0", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("Returned", result.Stdout, StringComparison.Ordinal);
        Assert.Equal(
            ["Returned 1 batches.", IncludeSandboxesNote],
            result.Stderr.TrimEnd().Split(Environment.NewLine));
    }

    [Theory]
    [InlineData(new[] { "--include-sandboxes" }, "&top=10&includeSandboxes=true", IncludeSandboxesNote)]
    [InlineData(new[] { "--include-sandboxes", "--limit", "25" }, "&top=25&includeSandboxes=true", "")]
    [InlineData(new[] { "--limit", "25" }, "&top=25", "")]
    public async Task BatchList_OnlyIncludeSandboxesWithoutLimitListsAtMostTenAndSaysSo(
        string[] options,
        string expectedFilters,
        string expectedStderr)
    {
        var handler = Respond(HttpStatusCode.OK, BatchesResponse);

        Invocation result = await InvokeAsync(
            ["test-account", "batch", "list", "--seller-id", "1234567", .. options],
            handler);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            $"?sellerId=1234567{expectedFilters}&pageSize=100",
            Assert.Single(handler.Requests).RequestUri?.Query);
        using JsonDocument output = JsonDocument.Parse(result.Stdout);
        Assert.Equal(1, output.RootElement.GetProperty("batches").GetArrayLength());
        Assert.Equal(expectedStderr, result.Stderr.TrimEnd());
    }

    [Fact]
    public async Task BatchList_FailureOnALaterPageWritesTheCorrelationIdAndNothingToStdout()
    {
        var handler = new XboxPartnerHttpMessageHandler(
            Response(HttpStatusCode.OK, """{"batches":[{"id":2}],"continuationToken":"t1"}"""),
            Response(HttpStatusCode.GatewayTimeout, """{"title":"Gateway Timeout","code":"downstream_timeout"}"""));

        Invocation result = await InvokeAsync(
            ["test-account", "batch", "list", "--seller-id", "1234567"],
            handler);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Equal(2, handler.Requests.Count);
        string correlationId = Assert.Single(handler.Requests[0].Headers["x-ms-correlation-id"]);
        Assert.Equal(correlationId, Assert.Single(handler.Requests[1].Headers["x-ms-correlation-id"]));
        string[] stderr = result.Stderr.Split(Environment.NewLine, 2);
        Assert.Equal($"x-ms-correlation-id: {correlationId}", stderr[0]);
        using JsonDocument error = JsonDocument.Parse(stderr[1]);
        Assert.Equal("downstream_timeout", error.RootElement.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData(new[] { "--mine", "--created-by-id", "5" }, "--mine and --created-by-id cannot be combined.")]
    [InlineData(new[] { "--created-by-id", "0" }, "--created-by-id must be a positive numeric user ID.")]
    [InlineData(new[] { "--limit", "0" }, "--limit must be a positive number.")]
    [InlineData(new[] { "--email-prefix", " " }, "--email-prefix must not be empty.")]
    [InlineData(new[] { "--tag", "" }, "--tag must not be empty.")]
    [InlineData(new[] { "--state", "failed" }, "Argument 'failed' not recognized. Must be one of:")]
    [InlineData(new[] { "--seller-id", "0" }, "--seller-id must be a positive numeric value.")]
    public async Task BatchList_InvalidOptionsFailBeforeAnyRequest(string[] options, string expected)
    {
        var handler = Respond(HttpStatusCode.OK, BatchesResponse);

        Invocation result = await InvokeAsync(["test-account", "batch", "list", .. options], handler);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(expected, result.Stderr, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task BatchGet_TableShowsAccountsAndSummarizesBatchOnStderr()
    {
        var handler = Respond(HttpStatusCode.OK, BatchResponse);

        Invocation result = await InvokeAsync(
            ["test-account", "batch", "get", "42", "--seller-id", "1234567", "-o", "table"],
            handler);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            "/xboxpartner/v1/testaccounts/batches/42?sellerId=1234567",
            Assert.Single(handler.Requests).RequestUri?.PathAndQuery);
        Assert.StartsWith("EMAIL", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("a@example.com", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("Batch 42", result.Stdout, StringComparison.Ordinal);
        Assert.Equal(
            [
                "Batch 42 (ABCDEF-smoke): complete, 1 accounts returned of batch size 1.",
                "Sandbox access: ABCDEF.0",
            ],
            result.Stderr.TrimEnd().Split(Environment.NewLine));
    }

    [Fact]
    public async Task BatchGet_CompactJson()
    {
        var handler = Respond(HttpStatusCode.OK, BatchResponse);

        Invocation result = await InvokeAsync(
            ["test-account", "batch", "get", "42", "--seller-id", "1234567", "-o", "json-compact"],
            handler);

        Assert.Equal(0, result.ExitCode);
        Assert.Single(result.Stdout.TrimEnd().Split(Environment.NewLine));
        using JsonDocument output = JsonDocument.Parse(result.Stdout);
        Assert.Equal("Alpha", output.RootElement.GetProperty("accounts")[0].GetProperty("gamertag").GetString());
    }

    [Fact]
    public async Task BatchGet_InvalidBatchIdFailsBeforeAnyRequest()
    {
        var handler = Respond(HttpStatusCode.OK, BatchResponse);

        Invocation result = await InvokeAsync(
            ["test-account", "batch", "get", "0", "--seller-id", "1234567"],
            handler);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("batch-id must be a positive number.", result.Stderr, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task BatchGet_GatewayFailureIsSanitizedWithHint()
    {
        var handler = Respond(
            HttpStatusCode.BadGateway,
            """{"title":"Gateway failure","detail":"Upstream said no.","stackTrace":null}""");

        Invocation result = await InvokeAsync(
            ["test-account", "batch", "get", "42", "--seller-id", "1234567"],
            handler);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        string[] stderr = result.Stderr.Split(Environment.NewLine, 2);
        Assert.Equal(
            $"x-ms-correlation-id: {Assert.Single(Assert.Single(handler.Requests).Headers["x-ms-correlation-id"])}",
            stderr[0]);
        using JsonDocument error = JsonDocument.Parse(stderr[1]);
        Assert.Equal(502, error.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("Test account request failed", error.RootElement.GetProperty("title").GetString());
        Assert.Contains(
            "another developer created the batch",
            error.RootElement.GetProperty("detail").GetString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain("stackTrace", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Upstream said no.", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BatchGet_GatewayDetailIsKeptAndHintAppended()
    {
        var handler = Respond(
            HttpStatusCode.Forbidden,
            """{"title":"Forbidden","detail":"Seller access denied.","code":"xbox_seller_not_authorized"}""");

        Invocation result = await InvokeAsync(
            ["test-account", "batch", "get", "42", "--seller-id", "1234567"],
            handler);

        Assert.Equal(1, result.ExitCode);
        using JsonDocument error = JsonDocument.Parse(result.Stderr.Split(Environment.NewLine, 2)[1]);
        Assert.Equal("Forbidden", error.RootElement.GetProperty("title").GetString());
        string detail = error.RootElement.GetProperty("detail").GetString()!;
        Assert.StartsWith("Seller access denied.", detail, StringComparison.Ordinal);
        Assert.Contains("pass --seller-id", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BatchGet_NonJsonGatewayErrorPrintsPlainTextWithHint()
    {
        var handler = new XboxPartnerHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("<html>Bad Gateway</html>", Encoding.UTF8, "text/html"),
        });

        Invocation result = await InvokeAsync(
            ["test-account", "batch", "get", "42", "--seller-id", "1234567"],
            handler);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        string[] stderr = result.Stderr.Split(Environment.NewLine, 2);
        Assert.StartsWith("x-ms-correlation-id: ", stderr[0], StringComparison.Ordinal);
        Assert.StartsWith("The test account request failed with HTTP 502", stderr[1], StringComparison.Ordinal);
        Assert.Contains("Retry first.", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("<html>", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BatchList_ClientFailureBeforeRequestPrintsRequestFailed()
    {
        TextWriter originalOut = Console.Out;
        TextWriter originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        Program.XboxPartnerClientFactory = _ => throw new InvalidOperationException("boom");

        int exitCode;
        try
        {
            exitCode = await Program.Main(["test-account", "batch", "list", "--seller-id", "1234567"]);
        }
        finally
        {
            Program.XboxPartnerClientFactory = null;
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }

        Assert.Equal(1, exitCode);
        Assert.Empty(stdout.ToString());
        Assert.StartsWith("Request failed: boom", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BatchList_NotFoundHintDoesNotMentionBatchId()
    {
        var handler = Respond(HttpStatusCode.NotFound, """{"title":"Not Found"}""");

        Invocation result = await InvokeAsync(
            ["test-account", "batch", "list", "--seller-id", "1234567"],
            handler);

        Assert.Equal(1, result.ExitCode);
        string[] stderr = result.Stderr.Split(Environment.NewLine, 2);
        Assert.StartsWith("x-ms-correlation-id: ", stderr[0], StringComparison.Ordinal);
        using JsonDocument error = JsonDocument.Parse(stderr[1]);
        string detail = error.RootElement.GetProperty("detail").GetString()!;
        Assert.Contains("Test accounts may be unavailable.", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("batch ID", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BatchList_InvalidResponseBodyFailsClearly()
    {
        var handler = Respond(HttpStatusCode.OK, "not-json");

        Invocation result = await InvokeAsync(
            ["test-account", "batch", "list", "--seller-id", "1234567"],
            handler);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Equal(
            [
                $"x-ms-correlation-id: {Assert.Single(Assert.Single(handler.Requests).Headers["x-ms-correlation-id"])}",
                "Request failed: The test account batch list response was not valid JSON.",
            ],
            result.Stderr.TrimEnd().Split(Environment.NewLine));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, false, null, null)]
    [InlineData(HttpStatusCode.BadRequest, false, """{"detail":"includeSandboxes allows at most 10 matching batches."}""", null)]
    [InlineData(HttpStatusCode.Forbidden, true, null, "different seller")]
    [InlineData(HttpStatusCode.Forbidden, false, null, "not a member of this seller")]
    [InlineData(HttpStatusCode.Forbidden, true, """{"code":"authentication_interaction_required"}""", null)]
    [InlineData(HttpStatusCode.Forbidden, false, """{"code":"authentication_interaction_required"}""", null)]
    [InlineData(HttpStatusCode.Forbidden, true, """{"code":"test_account_access_denied"}""", "another developer created it")]
    [InlineData(HttpStatusCode.Forbidden, true, """{"code":"test_account_access_denied"}""", "test account role")]
    [InlineData(HttpStatusCode.Forbidden, true, """{"code":42}""", "may belong to a different seller")]
    [InlineData(HttpStatusCode.Forbidden, false, """{"code":"test_account_access_denied"}""", "not a member of this seller")]
    [InlineData(HttpStatusCode.Forbidden, true, "not json", "different seller")]
    [InlineData(HttpStatusCode.NotFound, true, null, "batch ID")]
    [InlineData(HttpStatusCode.NotFound, false, null, "Test accounts may be unavailable.")]
    [InlineData(HttpStatusCode.BadGateway, false, null, "Retry first.")]
    [InlineData(HttpStatusCode.BadGateway, true, null, "check that your account has")]
    [InlineData(HttpStatusCode.ServiceUnavailable, false, null, "disabled or the service is not configured")]
    [InlineData(HttpStatusCode.GatewayTimeout, true, null, "did not respond in time")]
    [InlineData(HttpStatusCode.Unauthorized, true, null, null)]
    [InlineData(HttpStatusCode.InternalServerError, false, null, null)]
    public void DescribeFailure_OnlyAddsHintsForKnownTestAccountStatuses(
        HttpStatusCode statusCode,
        bool isGet,
        string? responseBody,
        string? expected)
    {
        string? hint = TestAccountCommandFactory.DescribeFailure(
            statusCode,
            responseBody,
            isGet ? TestAccountCommandFactory.TestAccountRequest.Get : TestAccountCommandFactory.TestAccountRequest.List);

        if (expected is null)
        {
            Assert.Null(hint);
        }
        else
        {
            Assert.Contains(expected, hint, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DescribeFailure_BadGatewayOnlyMentionsOtherDevelopersForGet()
    {
        string? list = TestAccountCommandFactory.DescribeFailure(
            HttpStatusCode.BadGateway, null, TestAccountCommandFactory.TestAccountRequest.List);

        Assert.DoesNotContain("another developer", list, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DescribeFailure_GatewayTimeoutAsksForARetry(bool isGet)
    {
        string? hint = TestAccountCommandFactory.DescribeFailure(
            HttpStatusCode.GatewayTimeout,
            null,
            isGet ? TestAccountCommandFactory.TestAccountRequest.Get : TestAccountCommandFactory.TestAccountRequest.List);

        Assert.Equal("The test account service did not respond in time. Retry.", hint);
    }

    private static XboxPartnerHttpMessageHandler Respond(HttpStatusCode statusCode, string body) =>
        new(Response(statusCode, body));

    [Theory]
    [InlineData("batch list")]
    [InlineData("batch get 42")]
    [InlineData("get ABCD-tester00007@xboxtest.com")]
    [InlineData("job get job1 --batch-id 42")]
    [InlineData("job wait job1 --batch-id 42")]
    public async Task ReadCommands_FallbackErrorsDoNotExposeInternalServices(string command)
    {
        foreach (string body in new[]
        {
            "{}",
            """{"detail":"Do not show diagnostic text.","stackTrace":"internal trace"}""",
            "[]",
            "<html>Bad Gateway</html>",
        })
        {
            var handler = Respond(HttpStatusCode.BadGateway, body);
            Invocation result = await InvokeAsync(
                ["test-account", .. command.Split(' '), "--seller-id", "1234567", "--verbose"],
                handler);

            Assert.Equal(1, result.ExitCode);
            Assert.Empty(result.Stdout);
            Assert.Single(handler.Requests);
            Assert.Contains("HTTP 502", result.Stderr, StringComparison.Ordinal);
            Assert.Contains("x-ms-correlation-id:", result.Stderr, StringComparison.Ordinal);
            Assert.DoesNotContain("XboxPartner", result.Stderr, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("XTAS", result.Stderr, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("deployment", result.Stderr, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Do not show diagnostic text.", result.Stderr, StringComparison.Ordinal);
            Assert.DoesNotContain("<html>", result.Stderr, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task BatchList_OverriddenEndpointWarningOmitsTheDefaultUrl()
    {
        var handler = Respond(HttpStatusCode.OK, BatchesResponse);

        Invocation result = await InvokeAsync(
            ["test-account", "batch", "list", "--seller-id", "1234567"],
            handler,
            configuration => configuration["XboxPartner:BaseUrl"] = "https://nodpublishers.com");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("nodpublishers.com", Assert.Single(handler.Requests).RequestUri?.Host);
        Assert.Contains("Warning: the service is overridden to 'https://nodpublishers.com'", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("XboxPartner:BaseUrl", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Requests and your access token will be sent to that host", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("XBOX_XBOXPARTNER__BASEURL", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(EndpointOverrideWarning.DefaultXboxPartnerBaseUrl, result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("default gateway", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"stackTrace":"internal trace"}""")]
    [InlineData("[]")]
    [InlineData("<html>Unavailable</html>")]
    public async Task BatchGet_FallbackPreservesTheServerReasonPhrase(string body)
    {
        HttpResponseMessage response = Response(HttpStatusCode.ServiceUnavailable, body);
        response.ReasonPhrase = "Maintenance until 12:00";
        var handler = new XboxPartnerHttpMessageHandler(response);

        Invocation result = await InvokeAsync(["test-account", "batch", "get", "42"], handler);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("HTTP 503 (Maintenance until 12:00).", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("ServiceUnavailable", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("/xboxpartner/", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("<html>", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("internal trace", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "")]
    [InlineData(HttpStatusCode.ServiceUnavailable, " ")]
    [InlineData((HttpStatusCode)599, null)]
    public async Task BatchGet_FallbackOmitsAnEmptyReasonPhrase(HttpStatusCode statusCode, string? reasonPhrase)
    {
        HttpResponseMessage response = Response(statusCode, "not json");
        response.ReasonPhrase = reasonPhrase;
        Assert.Equal(reasonPhrase, response.ReasonPhrase);
        var handler = new XboxPartnerHttpMessageHandler(response);

        Invocation result = await InvokeAsync(["test-account", "batch", "get", "42"], handler);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains($"The test account request failed with HTTP {(int)statusCode}.", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("ServiceUnavailable", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BatchGet_UnreadableErrorBodyPreservesReasonAndSizeGuidance()
    {
        HttpResponseMessage response = Response(HttpStatusCode.ServiceUnavailable, new string('x', 100));
        response.ReasonPhrase = "Maintenance until 12:00";
        var handler = new XboxPartnerHttpMessageHandler(response);

        Invocation result = await InvokeAsync(
            ["test-account", "batch", "get", "42"],
            handler,
            configuration => configuration["XboxPartner:MaxResponseBytes"] = "10");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("HTTP 503 (Maintenance until 12:00).", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("The response body could not be read.", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Test account response exceeded", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Increase 'XboxPartner:MaxResponseBytes'", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("/xboxpartner/", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BatchGet_ServerSuppliedErrorTextIsNotRewritten()
    {
        var handler = Respond(
            HttpStatusCode.BadRequest,
            """{"title":"XboxPartner title","detail":"XboxPartner detail","code":"invalid_request","errors":{"name":["XboxPartner validation"]}}""");

        Invocation result = await InvokeAsync(["test-account", "batch", "get", "42"], handler);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        using JsonDocument error = JsonDocument.Parse(result.Stderr.Split(Environment.NewLine, 2)[1]);
        Assert.Equal("XboxPartner title", error.RootElement.GetProperty("title").GetString());
        Assert.Equal("XboxPartner detail", error.RootElement.GetProperty("detail").GetString());
        Assert.Equal("invalid_request", error.RootElement.GetProperty("code").GetString());
        Assert.Equal("XboxPartner validation", error.RootElement.GetProperty("errors").GetProperty("name")[0].GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public void DescribeFailure_DoesNotExplainInternalDeployments(HttpStatusCode statusCode)
    {
        foreach (TestAccountCommandFactory.TestAccountRequest request in
            Enum.GetValues<TestAccountCommandFactory.TestAccountRequest>())
        {
            foreach (string? code in new[] { null, "feature_disabled", "service_configuration_invalid" })
            {
                string? detail = TestAccountCommandFactory.DescribeFailure(
                    statusCode, JsonSerializer.Serialize(new { code }), request);
                if (detail is not null)
                {
                    Assert.DoesNotContain("XboxPartner", detail, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("deployment", detail, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
    }

    private static HttpResponseMessage Response(HttpStatusCode statusCode, string body) =>
        new(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private static async Task<Invocation> InvokeAsync(
        string[] args,
        HttpMessageHandler handler,
        Action<IConfiguration>? configure = null)
    {
        TextWriter originalOut = Console.Out;
        TextWriter originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        Program.XboxPartnerClientFactory = configuration =>
        {
            configure?.Invoke(configuration);
            return new XboxPartnerClient(
                configuration,
                new HttpClient(handler),
                () => Task.FromResult<string?>("caller-token"));
        };

        try
        {
            int exitCode = await Program.Main(args);
            return new Invocation(exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Program.XboxPartnerClientFactory = null;
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private sealed record Invocation(int ExitCode, string Stdout, string Stderr);
}
