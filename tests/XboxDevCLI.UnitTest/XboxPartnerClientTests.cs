// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using Microsoft.Identity.Client;
using Moq;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

public class XboxPartnerClientTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetGameCreationJobAsync_ReadsRetryAfterAndCorrelation(bool useDate)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"jobId":"job/id","status":"running","result":"pending","game":null,"errors":null}"""),
        };
        response.Headers.RetryAfter = useDate
            ? new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(17))
            : new RetryConditionHeaderValue(TimeSpan.FromSeconds(17));
        response.Headers.Add("x-ms-correlation-id", "server-correlation");
        var handler = new XboxPartnerHttpMessageHandler(response);
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);

        var job = await client.GetGameCreationJobAsync("https://gateway.example", "seller", "job/id");

        Assert.InRange(job.RetryAfter!.Value.TotalSeconds, 15, 17);
        Assert.Equal("server-correlation", job.CorrelationId);
        Assert.EndsWith("/xboxpartner/v1/jobs/job%2Fid", handler.LastRequest!.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task GetGameCreationJobAsync_PastRetryAfterBecomesZero()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"jobId":"job","status":"running","result":"pending","game":null,"errors":null}"""),
        };
        response.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(-17));
        var handler = new XboxPartnerHttpMessageHandler(response);
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);
        var job = await client.GetGameCreationJobAsync("https://gateway.example", "seller", "job");
        Assert.Equal(TimeSpan.Zero, job.RetryAfter);
        Assert.Equal(Assert.Single(handler.Requests[0].Headers["x-ms-correlation-id"]), job.CorrelationId);
    }

    [Fact]
    public async Task GetGameCreationJobAsync_PreservesMalformedResponseAndCorrelationId()
    {
        const string body = """{"jobId":"job","status":false,"game":null,"errors":null}""";
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        response.Headers.Add("x-ms-correlation-id", "server-correlation");
        var handler = new XboxPartnerHttpMessageHandler(response);
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);
        var error = await Assert.ThrowsAsync<XboxPartnerResponseException>(() =>
            client.GetGameCreationJobAsync("https://gateway.example", "seller", "job"));
        Assert.Equal(body, error.RawResponse);
        Assert.Equal("server-correlation", error.CorrelationId);
    }

    [Fact]
    public async Task GetGameCreationJobAsync_DeserializesDtoAndPreservesOriginalResponse()
    {
        const string body =
            """{"jobId":"job","status":"completed","result":"succeeded","game":{"productId":"000123","xboxTitleId":"00000042","SCID":"00000000-0000-0000-0000-000000000042","primaryDevelopmentSandbox":"TESTSB.42"},"futureField":true}""";
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(5));
        response.Headers.Add("x-ms-correlation-id", "server-correlation");
        var handler = new XboxPartnerHttpMessageHandler(response);
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);

        var job = await client.GetGameCreationJobAsync("https://gateway.example", "seller", "job");

        Assert.True(job.Succeeded);
        Assert.Equal("000123", job.Game!.ProductId);
        Assert.Equal("00000042", job.Game.XboxTitleId);
        Assert.Equal("00000000-0000-0000-0000-000000000042", job.Game.Scid);
        Assert.Equal("TESTSB.42", job.Game.PrimaryDevelopmentSandbox);
        Assert.Null(job.Errors);
        Assert.Equal(body, job.RawResponse);
        Assert.Equal(TimeSpan.FromSeconds(5), job.RetryAfter);
        Assert.Equal("server-correlation", job.CorrelationId);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"jobId":null}""")]
    [InlineData("""{"jobId":""}""")]
    [InlineData("""{"jobId":" "}""")]
    public async Task GetGameCreationJobAsync_RejectsMissingOrBlankJobId(string body)
    {
        var handler = new XboxPartnerHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);

        var error = await Assert.ThrowsAsync<XboxPartnerResponseException>(() =>
            client.GetGameCreationJobAsync("https://gateway.example", "seller", "job"));

        Assert.Equal(body, error.RawResponse);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task CreateGameAsync_DoesNotRetryAndPreservesRequestCorrelation(HttpStatusCode status)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent("{}") };
        var handler = new XboxPartnerHttpMessageHandler(response);
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);
        var error = await Assert.ThrowsAsync<XboxPartnerApiException>(() =>
            client.CreateGameAsync(
                "https://gateway.example", "seller",
                new CreateXboxGameRequest("Game", [XboxPartnerDeviceFamily.Xbox])));
        var request = Assert.Single(handler.Requests);
        Assert.Equal(Assert.Single(request.Headers["x-ms-correlation-id"]), error.CorrelationId);
        Assert.Equal(status, error.StatusCode);
    }

    [Fact]
    public async Task CreateGameAsync_LeavesNameSandboxAndSellerFormatValidationToService()
    {
        var handler = new XboxPartnerHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.Accepted)
        {
            Content = new StringContent("""{"jobId":"job","status":"notStarted","result":"pending"}"""),
        });
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);
        var name = new string('n', 201);
        var sandbox = new string('s', 257);
        var seller = "seller/ " + new string('a', 129);

        await client.CreateGameAsync(
            "https://gateway.example", $" {seller} ",
            new CreateXboxGameRequest(
                name, [XboxPartnerDeviceFamily.Xbox, XboxPartnerDeviceFamily.Desktop], sandbox));

        var request = Assert.Single(handler.Requests);
        Assert.Equal(seller, Assert.Single(request.Headers["X-DeveloperAccountId"]));
        using var json = System.Text.Json.JsonDocument.Parse(request.Body!);
        Assert.Equal(name, json.RootElement.GetProperty("name").GetString());
        Assert.Equal(sandbox, json.RootElement.GetProperty("sandboxId").GetString());
        Assert.Equal("""["xbox","desktop"]""", json.RootElement.GetProperty("deviceFamilies").GetRawText());
    }

    [Fact]
    public async Task CreateGameAsync_PreservesFreeTextIdWhitespace()
    {
        var handler = new XboxPartnerHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.Accepted)
        {
            Content = new StringContent("""{"jobId":"job","status":"notStarted","result":"pending","game":null,"errors":null}"""),
        });
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);
        var request = new CreateXboxGameRequest(
            " Game ", [XboxPartnerDeviceFamily.Xbox, XboxPartnerDeviceFamily.Desktop], " sandbox ");
        await client.CreateGameAsync("https://gateway.example", "seller", request);
        using var json = System.Text.Json.JsonDocument.Parse(Assert.Single(handler.Requests).Body!);
        Assert.Equal(" Game ", request.Name);
        Assert.Equal("Game", json.RootElement.GetProperty("name").GetString());
        Assert.Equal("""["xbox","desktop"]""", json.RootElement.GetProperty("deviceFamilies").GetRawText());
        Assert.Equal(" sandbox ", json.RootElement.GetProperty("sandboxId").GetString());
        Assert.False(json.RootElement.TryGetProperty("serviceManagementId", out _));
        Assert.False(json.RootElement.TryGetProperty("letMicrosoftTargetFutureDeviceFamilies", out _));
        Assert.False(json.RootElement.TryGetProperty("public", out _));
    }

    [Fact]
    public async Task JobRequests_RespectCancellationBeforeAuthenticationAndNetwork()
    {
        var handler = new XboxPartnerHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK));
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetGameCreationJobAsync(
                "https://gateway.example", "seller", "job", cancellation.Token));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CreateGameAsync_PreservesResponseSizeLimit()
    {
        var handler = new XboxPartnerHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.Accepted)
        {
            Content = new StringContent("""{"jobId":"job","errors":[]}"""),
        });
        using var http = new HttpClient(handler);
        using var client = CreateClient(http, CreateConfiguration(("XboxPartner:MaxResponseBytes", "5")));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.CreateGameAsync(
                "https://gateway.example", "seller",
                new CreateXboxGameRequest("Game", [XboxPartnerDeviceFamily.Xbox])));
        Assert.Contains("exceeded the configured maximum size", error.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task GetSellersAsync_SendsExpectedPathAndBearerToken()
    {
        var handler = new MockHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"sellers":[]}"""),
        });
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);

        var result = await client.GetSellersAsync("http://localhost:54848");

        Assert.Equal("""{"sellers":[]}""", result.ResponseBody);
        Assert.Equal(
            "http://localhost:54848/xboxpartner/v1/sellers",
            handler.LastRequest!.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization!.Scheme);
        Assert.Equal("access-token", handler.LastRequest.Headers.Authorization.Parameter);
        Assert.Equal(CliIdentity.UserAgent, handler.LastRequest.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task GetGamesAsync_RequestsTheFirstPageWithTheSellerIdHeader()
    {
        var handler = new MockHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"games":[]}"""),
        });
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);

        await client.GetGamesAsync(
            "http://localhost:54848",
            "1234567");

        Assert.Equal(
            "http://localhost:54848/xboxpartner/v1/games?pageSize=100",
            handler.LastRequest!.RequestUri!.AbsoluteUri);
        Assert.Equal(
            "1234567",
            Assert.Single(handler.LastRequest.Headers.GetValues("X-DeveloperAccountId")));
    }

    [Fact]
    public void DefaultTimeout_IsTwoMinutes()
    {
        var handler = new MockHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK));
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);

        Assert.Equal(TimeSpan.FromSeconds(120), httpClient.Timeout);
    }

    [Theory]
    [InlineData("123\r456")]
    [InlineData("123\n456")]
    public async Task GetGamesAsync_RejectsSellerIdWithLineBreaks(string sellerId)
    {
        var handler = new MockHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"games":[]}"""),
        });
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetGamesAsync(
                "http://localhost:54848",
                sellerId));

        Assert.Contains("seller ID", exception.Message, StringComparison.Ordinal);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task GetGamesAsync_SendsTitleAndStoreFilters()
    {
        var handler = new MockHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"games":[]}"""),
        });
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);

        await client.GetGamesAsync(
            "http://localhost:54848",
            "1234567",
            titleId: "0x1234 ABCD",
            storeId: "9N0000000000");

        Assert.Equal(
            "http://localhost:54848/xboxpartner/v1/games?titleId=0x1234%20ABCD&storeId=9N0000000000&pageSize=100",
            handler.LastRequest!.RequestUri!.AbsoluteUri);
        Assert.Equal(
            "1234567",
            Assert.Single(handler.LastRequest.Headers.GetValues("X-DeveloperAccountId")));
    }

    [Fact]
    public async Task GetGamesAsync_SendsEscapedKeywordFilter()
    {
        var handler = new MockHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"games":[]}"""),
        });
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);

        await client.GetGamesAsync(
            "http://localhost:54848",
            "1234567",
            keyword: "headless game");

        Assert.Equal(
            "http://localhost:54848/xboxpartner/v1/games?keyword=headless%20game&pageSize=100",
            handler.LastRequest!.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task GetGamesAsync_FollowsEveryPageWithTheSameFiltersAndCorrelationId()
    {
        var handler = new XboxPartnerHttpMessageHandler(
            OkResponse("""{"games":[{"productId":"a","name":"Nod Ar\u00e9na"}],"continuationToken":"a+b/c="}"""),
            OkResponse("""{"games":[],"continuationToken":"t2"}"""),
            OkResponse("""{"games":[{"productId":"b"},{"productId":"c"}]}"""));
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);

        var result = await client.GetGamesAsync(
            "http://localhost:54848",
            "1234567",
            keyword: "headless game");

        // Games keep the gateway's exact text, escapes included.
        Assert.Equal(
            """{"games":[{"productId":"a","name":"Nod Ar\u00e9na"},{"productId":"b"},{"productId":"c"}]}""",
            result);
        Assert.Equal(
            new[]
            {
                "http://localhost:54848/xboxpartner/v1/games?keyword=headless%20game&pageSize=100",
                "http://localhost:54848/xboxpartner/v1/games?keyword=headless%20game&continuationToken=a%2Bb%2Fc%3D",
                "http://localhost:54848/xboxpartner/v1/games?keyword=headless%20game&continuationToken=t2",
            },
            handler.Requests.Select(request => request.RequestUri!.AbsoluteUri));
        var correlationId = Assert.Single(handler.Requests[0].Headers["x-ms-correlation-id"]);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(correlationId, Assert.Single(request.Headers["x-ms-correlation-id"]));
            Assert.Equal("1234567", Assert.Single(request.Headers["X-DeveloperAccountId"]));
        });
    }

    [Theory]
    [InlineData("""{"games":[]}""")]
    [InlineData("""{"games":[],"continuationToken":null}""")]
    [InlineData("""{"games":[],"continuationToken":""}""")]
    public async Task GetGamesAsync_StopsWhenAPageHasNoToken(string body)
    {
        var handler = new XboxPartnerHttpMessageHandler(
            OkResponse(body),
            OkResponse("""{"games":[{"productId":"unexpected"}]}"""));
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);

        var result = await client.GetGamesAsync("http://localhost:54848", "1234567");

        Assert.Equal("""{"games":[]}""", result);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task GetGamesAsync_StoreIdLookupStopsAtTheFirstMatch()
    {
        var handler = new XboxPartnerHttpMessageHandler(
            OkResponse("""{"games":[],"continuationToken":"t1"}"""),
            OkResponse("""{"games":[{"productId":"a","storeId":"9N0000000000"}],"continuationToken":"t2"}"""),
            OkResponse("""{"games":[{"productId":"b","storeId":"9N0000000000"}]}"""));
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);

        var result = await client.GetGamesAsync(
            "http://localhost:54848",
            "1234567",
            storeId: "9N0000000000");

        Assert.Equal("""{"games":[{"productId":"a","storeId":"9N0000000000"}]}""", result);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
            Assert.Contains("storeId=9N0000000000", request.RequestUri!.Query, StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetGamesAsync_RejectsARepeatedContinuationToken()
    {
        var handler = new XboxPartnerHttpMessageHandler(
            OkResponse("""{"games":[{"productId":"a"}],"continuationToken":"t1"}"""),
            OkResponse("""{"games":[],"continuationToken":"t1"}"""));
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);

        var exception = await Assert.ThrowsAsync<XboxPartnerResponseException>(
            () => client.GetGamesAsync("http://localhost:54848", "1234567"));

        Assert.Contains("repeated a continuation token", exception.Message, StringComparison.Ordinal);
        Assert.Equal("""{"games":[],"continuationToken":"t1"}""", exception.RawResponse);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(
            Assert.Single(handler.Requests[0].Headers["x-ms-correlation-id"]),
            exception.CorrelationId);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"games":{}}""")]
    [InlineData("""{"games":[],"continuationToken":5}""")]
    public async Task GetGamesAsync_RejectsAMalformedPage(string body)
    {
        var handler = new XboxPartnerHttpMessageHandler(OkResponse(body));
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);

        var exception = await Assert.ThrowsAsync<XboxPartnerResponseException>(
            () => client.GetGamesAsync("http://localhost:54848", "1234567"));

        Assert.Contains("XboxPartner game response", exception.Message, StringComparison.Ordinal);
        Assert.IsType<InvalidDataException>(exception.InnerException);
        Assert.Equal(body, exception.RawResponse);
        Assert.Equal(
            Assert.Single(Assert.Single(handler.Requests).Headers["x-ms-correlation-id"]),
            exception.CorrelationId);
    }

    [Fact]
    public async Task GetGamesAsync_RespectsCancellationBeforeAuthenticationAndNetwork()
    {
        var handler = new XboxPartnerHttpMessageHandler(OkResponse("""{"games":[]}"""));
        using var http = new HttpClient(handler);
        var configuration = CreateConfiguration();
        var authentication = new Mock<AuthenticationService>(configuration) { CallBase = false };
        using var client = new XboxPartnerClient(configuration, http, authentication.Object);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetGamesAsync("https://gateway.example", "seller", cancellationToken: cancellation.Token));
        authentication.Verify(service => service.AcquireTokenSilentAsync(It.IsAny<CancellationToken>()), Times.Never());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task GetGamesAsync_CancellationEndsTheWaitForTheNextPagesToken()
    {
        var handler = new XboxPartnerHttpMessageHandler(
            OkResponse("""{"games":[{"productId":"a"}],"continuationToken":"t1"}"""));
        using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var configuration = CreateConfiguration();
        var authentication = new Mock<AuthenticationService>(configuration) { CallBase = false };
        var tokenRequests = 0;
        authentication
            .Setup(service => service.AcquireTokenSilentAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken cancellationToken) =>
            {
                if (++tokenRequests == 1)
                {
                    return Task.FromResult<AuthenticationResult?>(CreateMockAuthResult());
                }

                // The second page's token never arrives, as when a refresh hangs, and the user
                // cancels; like MSAL, the request ends when its token is canceled.
                cancellation.Cancel();
                return new TaskCompletionSource<AuthenticationResult?>().Task.WaitAsync(cancellationToken);
            });
        using var client = new XboxPartnerClient(configuration, http, authentication.Object);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetGamesAsync("https://gateway.example", "seller", cancellationToken: cancellation.Token)
                .WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(2, tokenRequests);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task GetGamesAsync_FailureOnALaterPageCarriesTheListingCorrelationId()
    {
        var handler = new XboxPartnerHttpMessageHandler(
            OkResponse("""{"games":[{"productId":"a"}],"continuationToken":"t1"}"""),
            new HttpResponseMessage(HttpStatusCode.GatewayTimeout)
            {
                Content = new StringContent("upstream request timeout"),
            });
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);

        var exception = await Assert.ThrowsAsync<XboxPartnerApiException>(
            () => client.GetGamesAsync("http://localhost:54848", "1234567"));

        Assert.Equal(HttpStatusCode.GatewayTimeout, exception.StatusCode);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(
            Assert.Single(handler.Requests[0].Headers["x-ms-correlation-id"]),
            exception.CorrelationId);
    }

    [Fact]
    public async Task NonSuccessResponse_ThrowsStructuredException()
    {
        var handler = new MockHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            ReasonPhrase = "Not Found",
            Content = new StringContent("""{"detail":"missing"}"""),
        });
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);

        var exception = await Assert.ThrowsAsync<XboxPartnerApiException>(
            () => client.GetSellersAsync("http://localhost:54848"));

        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
        Assert.Equal("Not Found", exception.ReasonPhrase);
        Assert.Equal(
            "XboxPartner request to 'http://localhost:54848/xboxpartner/v1/sellers' failed with HTTP 404 (Not Found).",
            exception.Message);
        Assert.Equal("""{"detail":"missing"}""", exception.ResponseBody);
        Assert.Equal(
            "http://localhost:54848/xboxpartner/v1/sellers",
            exception.RequestUri.AbsoluteUri);
    }

    [Fact]
    public async Task ResponseLargerThanConfiguredLimit_IsRejected()
    {
        var configuration = CreateConfiguration(
            ("XboxPartner:MaxResponseBytes", "3"));
        var handler = new MockHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("four"),
        });
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient, configuration);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetSellersAsync("http://localhost:54848"));

        Assert.Contains("exceeded the configured maximum size", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("gateway.example")]
    [InlineData("ftp://gateway.example")]
    [InlineData("http://gateway.example")]
    [InlineData("http://192.0.2.10:8080")]
    public void BuildRequestUri_RejectsRelativeNonHttpAndPlaintextRemoteBaseUrls(string baseUrl)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => XboxPartnerClient.BuildRequestUri(baseUrl, "xboxpartner/v1/sellers"));

        Assert.Contains("XboxPartner:BaseUrl", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://localhost:54848")]
    [InlineData("https://localhost:54847")]
    [InlineData("http://127.0.0.1:8080")]
    [InlineData("https://gateway.example")]
    public void BuildRequestUri_AllowsHttpsAndLoopbackDevelopmentUrls(string baseUrl)
    {
        var uri = XboxPartnerClient.BuildRequestUri(baseUrl, "xboxpartner/v1/sellers");

        Assert.EndsWith("/xboxpartner/v1/sellers", uri.AbsoluteUri, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("XboxPartner:MaxResponseBytes", "0", "positive number of bytes")]
    [InlineData("XboxPartner:TimeoutSeconds", "0", "positive number")]
    [InlineData("XboxPartner:TimeoutSeconds", "NaN", "positive number")]
    [InlineData("XboxPartner:TimeoutSeconds", "Infinity", "positive number")]
    [InlineData("XboxPartner:TimeoutSeconds", "1e12", "positive number")]
    [InlineData("XboxPartner:TimeoutSeconds", "1e-9", "positive number")]
    public void Constructor_RejectsInvalidLimits(
        string key,
        string value,
        string expectedMessage)
    {
        var configuration = CreateConfiguration((key, value));

        var exception = Assert.Throws<InvalidOperationException>(
            () => new XboxPartnerClient(configuration));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingCachedToken_DirectsUserToLogin()
    {
        var configuration = CreateConfiguration();
        var authenticationService = new Mock<AuthenticationService>(configuration)
        {
            CallBase = false,
        };
        authenticationService
            .Setup(service => service.AcquireTokenSilentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((AuthenticationResult?)null);
        using var client = new XboxPartnerClient(
            configuration,
            new HttpClient(new MockHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK))),
            authenticationService.Object);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetSellersAsync("http://localhost:54848"));

        Assert.Contains("xbox auth login", exception.Message, StringComparison.Ordinal);
    }

    private static HttpResponseMessage OkResponse(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    internal static XboxPartnerClient CreateClient(
        HttpClient httpClient,
        IConfiguration? configuration = null)
    {
        configuration ??= CreateConfiguration();
        var authenticationService = new Mock<AuthenticationService>(configuration)
        {
            CallBase = false,
        };
        authenticationService
            .Setup(service => service.AcquireTokenSilentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateMockAuthResult());
        return new XboxPartnerClient(configuration, httpClient, authenticationService.Object);
    }

    private static AuthenticationResult CreateMockAuthResult()
    {
        var account = new Mock<IAccount>();
        account
            .SetupGet(value => value.HomeAccountId)
            .Returns(new AccountId("identifier", "object-id", "tenant-id"));
        return new AuthenticationResult(
            accessToken: "access-token",
            isExtendedLifeTimeToken: false,
            uniqueId: "object-id",
            expiresOn: DateTimeOffset.UtcNow.AddHours(1),
            extendedExpiresOn: DateTimeOffset.UtcNow.AddHours(1),
            tenantId: "tenant-id",
            account: account.Object,
            idToken: "id-token",
            scopes: ["scope"],
            correlationId: Guid.NewGuid());
    }

    private static IConfiguration CreateConfiguration(
        params (string Key, string Value)[] values)
    {
        var settings = new Dictionary<string, string?>
        {
            ["AzureAd:ClientId"] = "client-id",
            ["AzureAd:TenantId"] = "common",
            ["AzureAd:Scopes"] = "scope",
        };
        foreach (var (key, value) in values)
        {
            settings[key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }
}
