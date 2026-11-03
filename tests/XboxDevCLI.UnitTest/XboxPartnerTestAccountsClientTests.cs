// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

public class XboxPartnerTestAccountsClientTests
{
    private const string BatchesUrl = "http://localhost:8080/xboxpartner/v1/testaccounts/batches";

    [Fact]
    public async Task ListTestAccountBatchesAsync_WithoutFiltersAsksOnlyForTheFirstPage()
    {
        var handler = Respond(HttpStatusCode.OK, """{"batches":[]}""");
        using var client = CreateClient(handler);

        string response = await client.ListTestAccountBatchesAsync(
            sellerId: null,
            new TestAccountBatchFilter(),
            CancellationToken.None);

        Assert.Equal("""{"batches":[]}""", response);
        CapturedHttpRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"{BatchesUrl}?pageSize=100", request.RequestUri?.AbsoluteUri);
        Assert.Null(request.Body);
        Assert.Equal(["Bearer caller-token"], request.Headers["Authorization"]);
    }

    [Fact]
    public async Task ListTestAccountBatchesAsync_SendsEverySetFilterEscaped()
    {
        var handler = Respond(HttpStatusCode.OK, """{"batches":[]}""");
        using var client = CreateClient(handler);

        await client.ListTestAccountBatchesAsync(
            1234567,
            new TestAccountBatchFilter(
                CreatedBy: 9007199254740993,
                EmailPrefix: "  ABCDEF smoke&x=1 ",
                Keyword: "nightly+ci",
                State: "pending",
                Top: 5,
                IncludeSandboxes: true),
            CancellationToken.None);

        Uri uri = Assert.Single(handler.Requests).RequestUri!;
        Assert.Equal(
            "?sellerId=1234567&createdBy=9007199254740993&emailPrefix=ABCDEF%20smoke%26x%3D1" +
            "&keyword=nightly%2Bci&state=pending&top=5&includeSandboxes=true&pageSize=100",
            uri.Query);
    }

    [Fact]
    public async Task ListTestAccountBatchesAsync_SendsMineAndSkipsBlankText()
    {
        var handler = Respond(HttpStatusCode.OK, """{"batches":[]}""");
        using var client = CreateClient(handler);

        await client.ListTestAccountBatchesAsync(
            null,
            new TestAccountBatchFilter(Mine: true, EmailPrefix: " ", Keyword: ""),
            CancellationToken.None);

        Assert.Equal("?mine=true&pageSize=100", Assert.Single(handler.Requests).RequestUri!.Query);
    }

    [Fact]
    public async Task ListTestAccountBatchesAsync_RejectsNonPositiveSellerWithoutSending()
    {
        var handler = Respond(HttpStatusCode.OK, "{}");
        using var client = CreateClient(handler);

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            client.ListTestAccountBatchesAsync(0, new TestAccountBatchFilter(), CancellationToken.None));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ListTestAccountBatchesAsync_FollowsEveryPageWithTheSameFiltersAndCorrelationId()
    {
        var handler = new XboxPartnerHttpMessageHandler(
            Response(HttpStatusCode.OK, """{"batches":[{"id":3}],"continuationToken":"a+b/c="}"""),
            Response(HttpStatusCode.OK, """{"batches":[],"continuationToken":"t2"}"""),
            Response(HttpStatusCode.OK, """{"batches":[{"id":2},{"id":1}]}"""));
        using var client = CreateClient(handler);
        var pages = new List<int>();

        string response = await client.ListTestAccountBatchesAsync(
            1234567,
            new TestAccountBatchFilter(Mine: true, Top: 25, IncludeSandboxes: true),
            CancellationToken.None,
            pages.Add);

        Assert.Equal("""{"batches":[{"id":3},{"id":2},{"id":1}]}""", response);
        Assert.Equal([1, 0, 2], pages);
        const string Filters = "?sellerId=1234567&mine=true&top=25&includeSandboxes=true";
        Assert.Equal(
            new[]
            {
                Filters + "&pageSize=100",
                Filters + "&continuationToken=a%2Bb%2Fc%3D",
                Filters + "&continuationToken=t2",
            },
            handler.Requests.Select(request => request.RequestUri!.Query));
        string correlationId = Assert.Single(handler.Requests[0].Headers["x-ms-correlation-id"]);
        Assert.All(handler.Requests, request =>
            Assert.Equal(correlationId, Assert.Single(request.Headers["x-ms-correlation-id"])));
    }

    [Fact]
    public async Task ListTestAccountBatchesAsync_RejectsARepeatedContinuationToken()
    {
        var handler = new XboxPartnerHttpMessageHandler(
            Response(HttpStatusCode.OK, """{"batches":[{"id":2}],"continuationToken":"t1"}"""),
            Response(HttpStatusCode.OK, """{"batches":[],"continuationToken":"t1"}"""));
        using var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<XboxPartnerResponseException>(() =>
            client.ListTestAccountBatchesAsync(null, new TestAccountBatchFilter(), CancellationToken.None));

        Assert.Equal(
            "The test account batch list response repeated a continuation token.",
            exception.Message);
        Assert.Equal("""{"batches":[],"continuationToken":"t1"}""", exception.RawResponse);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(Assert.Single(handler.Requests[0].Headers["x-ms-correlation-id"]), exception.CorrelationId);
    }

    [Theory]
    [InlineData("not json", "was not valid JSON.")]
    [InlineData("""{"batches":{}}""", "must contain the batches array.")]
    [InlineData("""{"batches":[],"continuationToken":5}""", "contained a continuationToken that was not a string.")]
    public async Task ListTestAccountBatchesAsync_RejectsAMalformedPage(string body, string expected)
    {
        var handler = Respond(HttpStatusCode.OK, body);
        using var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<XboxPartnerResponseException>(() =>
            client.ListTestAccountBatchesAsync(null, new TestAccountBatchFilter(), CancellationToken.None));

        Assert.Equal($"The test account batch list response {expected}", exception.Message);
        Assert.Equal(body, exception.RawResponse);
        Assert.Equal(
            Assert.Single(Assert.Single(handler.Requests).Headers["x-ms-correlation-id"]),
            exception.CorrelationId);
    }

    [Fact]
    public async Task ListTestAccountBatchesAsync_FailureOnALaterPageCarriesTheListingCorrelationId()
    {
        var handler = new XboxPartnerHttpMessageHandler(
            Response(HttpStatusCode.OK, """{"batches":[{"id":2}],"continuationToken":"t1"}"""),
            Response(HttpStatusCode.GatewayTimeout, """{"code":"downstream_timeout"}"""));
        using var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<XboxPartnerApiException>(() =>
            client.ListTestAccountBatchesAsync(null, new TestAccountBatchFilter(), CancellationToken.None));

        Assert.Equal(HttpStatusCode.GatewayTimeout, exception.StatusCode);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(Assert.Single(handler.Requests[0].Headers["x-ms-correlation-id"]), exception.CorrelationId);
    }

    [Fact]
    public async Task GetTestAccountBatchAsync_SendsBatchIdAndSeller()
    {
        var handler = Respond(HttpStatusCode.OK, """{"batch":{},"accounts":[]}""");
        using var client = CreateClient(handler);

        await client.GetTestAccountBatchAsync(1234567, 9007199254740993, CancellationToken.None);

        CapturedHttpRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"{BatchesUrl}/9007199254740993?sellerId=1234567", request.RequestUri?.AbsoluteUri);
        Assert.True(Guid.TryParse(Assert.Single(request.Headers["x-ms-correlation-id"]), out _));
    }

    [Fact]
    public async Task GetTestAccountBatchAsync_OmitsSellerWhenNotSet()
    {
        var handler = Respond(HttpStatusCode.OK, """{"batch":{},"accounts":[]}""");
        using var client = CreateClient(handler);

        await client.GetTestAccountBatchAsync(null, 42, CancellationToken.None);

        Assert.Equal($"{BatchesUrl}/42", Assert.Single(handler.Requests).RequestUri?.AbsoluteUri);
    }

    [Fact]
    public async Task GetTestAccountBatchAsync_RejectsNonPositiveBatchIdWithoutSending()
    {
        var handler = Respond(HttpStatusCode.OK, "{}");
        using var client = CreateClient(handler);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.GetTestAccountBatchAsync(null, 0, CancellationToken.None));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task GetTestAccountBatchAsync_GatewayFailureBecomesApiException()
    {
        var handler = Respond(HttpStatusCode.BadGateway, """{"title":"failed","detail":"Service said no."}""");
        using var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<XboxPartnerApiException>(() =>
            client.GetTestAccountBatchAsync(null, 42, CancellationToken.None));

        Assert.Equal(HttpStatusCode.BadGateway, exception.StatusCode);
        Assert.Contains("Service said no.", exception.ResponseBody, StringComparison.Ordinal);
        Assert.Equal(
            Assert.Single(Assert.Single(handler.Requests).Headers["x-ms-correlation-id"]),
            exception.CorrelationId);
    }

    [Fact]
    public async Task GetTestAccountBatchAsync_FailureKeepsTheGatewayCorrelationId()
    {
        var response = new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("""{"title":"failed"}""", Encoding.UTF8, "application/json"),
        };
        response.Headers.Add("x-ms-correlation-id", "server-correlation");
        var handler = new XboxPartnerHttpMessageHandler(response);
        using var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<XboxPartnerApiException>(() =>
            client.GetTestAccountBatchAsync(null, 42, CancellationToken.None));

        Assert.Equal("server-correlation", exception.CorrelationId);
    }

    private const string RunningJob =
        """{"batchId":42,"jobId":"job-1","state":"running","total":5,"succeeded":1,"failed":0,"errorCode":null,"errorMessage":null,"progress":"1 of 5","sandboxes":["ABCD.1"],"locale":"en-US","subscription":"Silver"}""";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetTestAccountBatchAsync_SizeLimitErrorUsesNeutralWording(bool errorResponse)
    {
        var handler = Respond(errorResponse ? HttpStatusCode.BadGateway : HttpStatusCode.OK, new string('x', 100));
        using var client = CreateClient(handler, maxResponseBytes: "10");

        Exception exception = errorResponse
            ? await Assert.ThrowsAsync<XboxPartnerApiException>(() =>
                client.GetTestAccountBatchAsync(null, 42, CancellationToken.None))
            : await Assert.ThrowsAsync<InvalidOperationException>(() =>
                client.GetTestAccountBatchAsync(null, 42, CancellationToken.None));
        string message = errorResponse ? exception.InnerException!.Message : exception.Message;

        Assert.StartsWith("Test account response exceeded", message, StringComparison.Ordinal);
        Assert.Contains("Increase 'XboxPartner:MaxResponseBytes'", message, StringComparison.Ordinal);
        Assert.DoesNotContain("XboxPartner response", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateTestAccountBatchAsync_PostsCamelCaseBodyWithoutNullsAndReadsHeaders()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Accepted)
        {
            Content = new StringContent(RunningJob, Encoding.UTF8, "application/json"),
        };
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
        response.Headers.Add("x-ms-correlation-id", "server-correlation");
        var handler = new XboxPartnerHttpMessageHandler(response);
        using var client = CreateClient(handler);

        TestAccountJobResult result = await client.CreateTestAccountBatchAsync(
            new CreateTestAccountBatchRequest
            {
                SellerId = 1234567,
                Count = 5,
                EmailPrefix = "ABCD-tester",
                Password = "p@ss word",
                SecurityQuestion = "Favorite game?",
                SecurityAnswer = "chess",
                Sandboxes = ["ABCD.1", "ABCD.2"],
                Keywords = ["nightly"],
                Subscription = "Silver",
            },
            CancellationToken.None);

        CapturedHttpRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(BatchesUrl, request.RequestUri?.AbsoluteUri);
        Assert.Equal("application/json", request.ContentType);
        Assert.Equal(
            """{"sellerId":1234567,"count":5,"emailPrefix":"ABCD-tester","password":"p@ss word","securityQuestion":"Favorite game?","securityAnswer":"chess","sandboxes":["ABCD.1","ABCD.2"],"keywords":["nightly"],"subscription":"Silver"}""",
            request.Body);
        Assert.True(Guid.TryParse(Assert.Single(request.Headers["x-ms-correlation-id"]), out _));
        Assert.Equal(RunningJob, result.RawResponse);
        Assert.Equal(42, result.Job.BatchId);
        Assert.Equal("job-1", result.Job.JobId);
        Assert.True(result.Job.IsRunning);
        Assert.Equal(TimeSpan.FromSeconds(30), result.RetryAfter);
        Assert.Equal("server-correlation", result.CorrelationId);
    }

    [Fact]
    public async Task CreateTestAccountBatchAsync_RejectsNonPositiveSellerWithoutSending()
    {
        var handler = Respond(HttpStatusCode.Accepted, RunningJob);
        using var client = CreateClient(handler);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => client.CreateTestAccountBatchAsync(
            new CreateTestAccountBatchRequest
            {
                SellerId = 0,
                Count = 1,
                EmailPrefix = "ABCD-tester",
                Password = "p",
                SecurityQuestion = "q",
                SecurityAnswer = "a",
                Sandboxes = ["ABCD.1"],
            },
            CancellationToken.None));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task GetTestAccountJobAsync_SendsEscapedJobIdAndSeller()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"batchId":42,"jobId":"job/1","state":"succeeded","total":5,"succeeded":5,"failed":0}""",
                Encoding.UTF8,
                "application/json"),
        };
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddMinutes(1));
        var handler = new XboxPartnerHttpMessageHandler(response);
        using var client = CreateClient(handler);

        TestAccountJobResult result = await client.GetTestAccountJobAsync(1234567, 42, " job/1 ", CancellationToken.None);

        CapturedHttpRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"{BatchesUrl}/42/jobs/job%2F1?sellerId=1234567", request.RequestUri?.AbsoluteUri);
        Assert.True(result.Job.IsSucceeded);
        Assert.NotNull(result.RetryAfter);
        Assert.InRange(result.RetryAfter!.Value, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));
        Assert.True(Guid.TryParse(result.CorrelationId, out _));
    }

    [Theory]
    [InlineData(0, "job")]
    [InlineData(42, " ")]
    public async Task GetTestAccountJobAsync_RejectsInvalidIdsWithoutSending(long batchId, string jobId)
    {
        var handler = Respond(HttpStatusCode.OK, RunningJob);
        using var client = CreateClient(handler);

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            client.GetTestAccountJobAsync(null, batchId, jobId, CancellationToken.None));

        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("null")]
    [InlineData("""{"batchId":0,"jobId":"job","state":"running"}""")]
    [InlineData("""{"batchId":42,"jobId":"","state":"running"}""")]
    [InlineData("""{"batchId":42,"jobId":"job","state":null}""")]
    public async Task GetTestAccountJobAsync_UnreadableSuccessBodyThrowsResponseException(string body)
    {
        var handler = Respond(HttpStatusCode.OK, body);
        using var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<XboxPartnerResponseException>(() =>
            client.GetTestAccountJobAsync(null, 42, "job", CancellationToken.None));

        Assert.True(Guid.TryParse(exception.CorrelationId, out _));
    }

    [Fact]
    public async Task GetTestAccountJobAsync_FailureCarriesServerCorrelationId()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("""{"code":"test_account_access_denied"}""", Encoding.UTF8, "application/json"),
        };
        response.Headers.Add("x-ms-correlation-id", "server-correlation");
        var handler = new XboxPartnerHttpMessageHandler(response);
        using var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<XboxPartnerApiException>(() =>
            client.GetTestAccountJobAsync(null, 42, "job", CancellationToken.None));

        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
        Assert.Equal("server-correlation", exception.CorrelationId);
        Assert.Null(exception.RetryAfter);
    }

    [Fact]
    public async Task GetTestAccountJobAsync_ThrottledFailureCarriesRetryAfter()
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("""{"code":"downstream_throttled"}""", Encoding.UTF8, "application/json"),
        };
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
        var handler = new XboxPartnerHttpMessageHandler(response);
        using var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<XboxPartnerApiException>(() =>
            client.GetTestAccountJobAsync(null, 42, "job", CancellationToken.None));

        Assert.Equal(HttpStatusCode.TooManyRequests, exception.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(60), exception.RetryAfter);
    }

    [Fact]
    public async Task GetTestAccountJobAsync_StalledTokenRefreshHonorsCancellation()
    {
        var handler = Respond(HttpStatusCode.OK, RunningJob);
        var stalled = new TaskCompletionSource<string?>();
        using var client = new XboxPartnerClient(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["XboxPartner:BaseUrl"] = "http://localhost:8080",
                })
                .Build(),
            new HttpClient(handler),
            () => stalled.Task);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetTestAccountJobAsync(null, 42, "job", cancellation.Token));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CreateTestAccountBatchAsync_ReportsTheSendBoundaryOnce()
    {
        var handler = Respond(HttpStatusCode.Accepted, RunningJob);
        using var client = CreateClient(handler);
        int sends = 0;

        await client.CreateTestAccountBatchAsync(CreateRequest(), CancellationToken.None, () => sends++);

        Assert.Equal(1, sends);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CreateTestAccountBatchAsync_CanceledTokenNeverReachesTheSendBoundary()
    {
        var handler = Respond(HttpStatusCode.Accepted, RunningJob);
        using var client = CreateClient(handler);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        bool sent = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.CreateTestAccountBatchAsync(CreateRequest(), cancellation.Token, () => sent = true));

        Assert.False(sent);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CreateTestAccountBatchAsync_MissingTokenFailsBeforeTheSendBoundary()
    {
        var handler = Respond(HttpStatusCode.Accepted, RunningJob);
        using var client = CreateClient(handler, token: null);
        bool sent = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.CreateTestAccountBatchAsync(CreateRequest(), CancellationToken.None, () => sent = true));

        Assert.False(sent);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CreateTestAccountBatchAsync_OversizedAcceptedBodyThrowsResponseException()
    {
        var handler = Respond(HttpStatusCode.Accepted, RunningJob);
        using var client = CreateClient(handler, maxResponseBytes: "16");

        var exception = await Assert.ThrowsAsync<XboxPartnerResponseException>(() =>
            client.CreateTestAccountBatchAsync(CreateRequest(), CancellationToken.None));

        Assert.True(Guid.TryParse(exception.CorrelationId, out _));
        Assert.IsType<InvalidOperationException>(exception.InnerException);
    }

    [Theory]
    [InlineData("", "a")]
    [InlineData("p", "")]
    public async Task CreateTestAccountBatchAsync_RejectsEmptySecretsWithoutSending(string password, string answer)
    {
        var handler = Respond(HttpStatusCode.Accepted, RunningJob);
        using var client = CreateClient(handler);
        CreateTestAccountBatchRequest request = CreateRequest();
        request.Password = password;
        request.SecurityAnswer = answer;

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            client.CreateTestAccountBatchAsync(request, CancellationToken.None));

        Assert.Empty(handler.Requests);
    }

    private static CreateTestAccountBatchRequest CreateRequest() =>
        new()
        {
            Count = 1,
            EmailPrefix = "ABCD-tester",
            Password = "p",
            SecurityQuestion = "q",
            SecurityAnswer = "a",
            Sandboxes = ["ABCD.1"],
        };

    private static XboxPartnerHttpMessageHandler Respond(HttpStatusCode statusCode, string body) =>
        new(Response(statusCode, body));

    private static HttpResponseMessage Response(HttpStatusCode statusCode, string body) =>
        new(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private static XboxPartnerClient CreateClient(
        HttpMessageHandler handler,
        string? token = "caller-token",
        string? maxResponseBytes = null) =>
        new(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["XboxPartner:BaseUrl"] = "http://localhost:8080",
                    ["XboxPartner:MaxResponseBytes"] = maxResponseBytes,
                })
                .Build(),
            new HttpClient(handler),
            () => Task.FromResult(token));
}
