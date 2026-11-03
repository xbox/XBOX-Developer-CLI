// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

public class XboxPartnerErrorGuidanceTests
{
    [Fact]
    public void InvalidUtf16Body_KeepsTheRetryGuidance()
    {
        // Built at run time: InlineData would replace the unpaired surrogate when it's encoded.
        string body = "{\"code\":\"rate_limited\",\"detail\":\"" + '\ud800' + "\"}";
        var exception = new XboxPartnerApiException(
            HttpStatusCode.TooManyRequests,
            "Too Many Requests",
            body,
            new Uri("https://nodpublishers.com/request"),
            retryAfter: TimeSpan.FromSeconds(30));

        Assert.False(XboxPartnerErrorGuidance.IsRateLimited(body));
        Assert.Null(Commands.TestAccountCommandFactory.ReadProblemString(body, "code"));
        Assert.Contains(
            "The request was throttled. Try again in 30 seconds.",
            XboxPartnerErrorGuidance.Describe(exception, isAutomation: false),
            StringComparison.Ordinal);
        Assert.Contains(
            "Try again in 30 seconds.",
            Commands.XboxPartnerCommandErrorFormatter.Format(exception, compact: true),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"code":"\ud800"}""")]
    [InlineData("""{"code":"rate_limited","detail":"\ud800"}""")]
    [InlineData("""{"code":"rate_limited","title":"\udc00"}""")]
    public void EscapedInvalidUtf16Body_KeepsTheRetryGuidance(string body)
    {
        var exception = new XboxPartnerApiException(
            HttpStatusCode.TooManyRequests,
            "Too Many Requests",
            body,
            new Uri("https://nodpublishers.com/request"),
            retryAfter: TimeSpan.FromSeconds(30));

        Assert.Contains(
            "Try again in 30 seconds.",
            XboxPartnerErrorGuidance.Describe(exception, isAutomation: false),
            StringComparison.Ordinal);
        Assert.Contains(
            "Try again in 30 seconds.",
            Commands.XboxPartnerCommandErrorFormatter.Format(exception, compact: true),
            StringComparison.Ordinal);
        _ = Commands.TestAccountCommandFactory.DescribeFailure(
            HttpStatusCode.TooManyRequests, body, Commands.TestAccountCommandFactory.TestAccountRequest.Get);
    }

    [Theory]
    [InlineData(29.2, "Try again in 30 seconds.")]
    [InlineData(120.0, "Try again in 120 seconds.")]
    [InlineData(0.0, "Try again in 1 second.")]
    [InlineData(-1.0, "Try again in 1 second.")]
    [InlineData(3000000000.0, "Try again in 3000000000 seconds.")]
    [InlineData(null, "Wait before retrying.")]
    public void Describe_ThrottledReportsTheWait(double? seconds, string expected)
    {
        var exception = new XboxPartnerApiException(
            HttpStatusCode.TooManyRequests,
            "Too Many Requests",
            string.Empty,
            new Uri("https://nodpublishers.com/request"),
            retryAfter: seconds is { } value ? TimeSpan.FromSeconds(value) : null);

        string guidance = XboxPartnerErrorGuidance.Describe(exception, isAutomation: false);

        Assert.Contains($"The request was throttled. {expected}", guidance, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "Check the supplied input")]
    [InlineData(HttpStatusCode.Unauthorized, "xbox auth login")]
    [InlineData(HttpStatusCode.Forbidden, "not authorized")]
    [InlineData(HttpStatusCode.TooManyRequests, "throttled")]
    [InlineData(HttpStatusCode.BadGateway, "temporarily unavailable")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "temporarily unavailable")]
    [InlineData(HttpStatusCode.GatewayTimeout, "timed out")]
    [InlineData(HttpStatusCode.InternalServerError, "HTTP 500")]
    public void Describe_ReturnsStatusSpecificGuidance(
        HttpStatusCode statusCode,
        string expected)
    {
        var exception = CreateException(statusCode, string.Empty);

        var guidance = XboxPartnerErrorGuidance.Describe(exception, isAutomation: false);

        Assert.Contains(expected, guidance, StringComparison.Ordinal);
        Assert.Contains($"HTTP {(int)statusCode}", guidance, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_SellerNotFoundIncludesConfigurationGuidance()
    {
        var guidance = XboxPartnerErrorGuidance.Describe(
            CreateException(HttpStatusCode.NotFound, string.Empty), isAutomation: false);

        Assert.Contains("The seller list could not be retrieved.", guidance, StringComparison.Ordinal);
        Assert.Contains("HTTP 404", guidance, StringComparison.Ordinal);
        Assert.Contains("XboxPartner:BaseUrl", guidance, StringComparison.Ordinal);
        Assert.Contains("XBOX_XBOXPARTNER__BASEURL", guidance, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://gateway.example/xboxpartner/v1/jobs/job-1")]
    [InlineData("https://gateway.example/xboxpartner/v1/jobs/sellers")]
    public void Describe_NotFoundOutsideSellersDoesNotBlameSellerEndpoint(string requestUri)
    {
        var guidance = XboxPartnerErrorGuidance.Describe(
            CreateException(HttpStatusCode.NotFound, string.Empty, new Uri(requestUri)), isAutomation: false);

        Assert.Contains("HTTP 404", guidance, StringComparison.Ordinal);
        Assert.Contains("Check the IDs", guidance, StringComparison.Ordinal);
        Assert.DoesNotContain(requestUri, guidance, StringComparison.Ordinal);
        Assert.DoesNotContain("seller endpoint", guidance, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_AppendsProblemDetail()
    {
        var guidance = XboxPartnerErrorGuidance.Describe(
            CreateException(
                HttpStatusCode.BadRequest,
                """{"detail":"Seller id has an invalid format."}"""), isAutomation: false);

        Assert.Contains(
            "Error detail: Seller id has an invalid format.",
            guidance,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Describe_PreservesBodyReadFailureWithoutRepeatingStatus(bool oversized)
    {
        const string limit =
            "The response exceeded the configured maximum size of 5 bytes. " +
            "Increase 'XboxPartner:MaxResponseBytes' to allow larger responses.";
        Exception inner = oversized ? new InvalidOperationException(limit) : new IOException("private");
        var exception = new XboxPartnerApiException(
            HttpStatusCode.ServiceUnavailable,
            "Service Unavailable",
            string.Empty,
            new Uri("https://nodpublishers.com/request"),
            innerException: inner);

        string guidance = XboxPartnerErrorGuidance.Describe(exception, isAutomation: false);

        Assert.Single(guidance.Split(Environment.NewLine),
            line => line.Contains("HTTP 503 (Service Unavailable).", StringComparison.Ordinal));
        Assert.Contains("The response body could not be read.", guidance, StringComparison.Ordinal);
        if (oversized)
        {
            Assert.Contains(limit, guidance, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("private", guidance, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_IgnoresMalformedProblemBody()
    {
        var guidance = XboxPartnerErrorGuidance.Describe(
            CreateException(HttpStatusCode.BadRequest, "not-json"), isAutomation: false);

        Assert.DoesNotContain("Error detail:", guidance, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Maintenance until 12:00", "HTTP 503 (Maintenance until 12:00).")]
    [InlineData(null, "HTTP 503.")]
    [InlineData("", "HTTP 503.")]
    [InlineData("   ", "HTTP 503.")]
    public void Describe_PreservesStatusAndServerReason(string? reason, string expected)
    {
        var exception = new XboxPartnerApiException(
            HttpStatusCode.ServiceUnavailable,
            reason,
            string.Empty,
            new Uri("https://nodpublishers.com/request"));

        string guidance = XboxPartnerErrorGuidance.Describe(exception, isAutomation: false);

        Assert.Contains(expected, guidance, StringComparison.Ordinal);
        Assert.Equal(reason, exception.ReasonPhrase);
    }

    [Fact]
    public void Describe_PreservesServerDetailAndSanitizesReason()
    {
        var exception = new XboxPartnerApiException(
            HttpStatusCode.BadGateway,
            "Unavailable\u001b[2J\nFAKE line",
            """{"detail":"The document could not be read."}""",
            new Uri("https://nodpublishers.com/request"));

        string guidance = XboxPartnerErrorGuidance.Describe(exception, isAutomation: false);

        Assert.Contains("Error detail: The document could not be read.", guidance, StringComparison.Ordinal);
        Assert.Contains("HTTP 502 (Unavailable[2JFAKE line).", guidance, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", guidance, StringComparison.Ordinal);
    }

    /// <summary>
    /// A service principal can't run <c>auth login</c>, so a 401 in a service-principal mode points
    /// at its credentials instead.
    /// </summary>
    [Fact]
    public void Describe_UnauthorizedInAutomation_PointsAtCredentials()
    {
        var guidance = XboxPartnerErrorGuidance.Describe(
            CreateException(HttpStatusCode.Unauthorized, string.Empty), isAutomation: true);

        Assert.Contains("Check its credentials", guidance, StringComparison.Ordinal);
        Assert.Contains("xbox auth status", guidance, StringComparison.Ordinal);
        Assert.DoesNotContain("auth login", guidance, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(30.0, "Try again in 30 seconds.")]
    [InlineData(0.2, "Try again in 1 second.")]
    [InlineData(null, "Wait before retrying.")]
    public void Describe_RateLimited_SaysTheCallerSentTooManyRequests(double? retryAfterSeconds, string expected)
    {
        var exception = CreateException(
            HttpStatusCode.TooManyRequests,
            """{"code":"rate_limited","detail":"The caller made too many requests."}""",
            retryAfter: retryAfterSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null);

        var guidance = XboxPartnerErrorGuidance.Describe(exception, isAutomation: false);

        Assert.Contains("You sent too many requests", guidance, StringComparison.Ordinal);
        Assert.Contains(expected, guidance, StringComparison.Ordinal);
        Assert.DoesNotContain("was throttled", guidance, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_TooManyRequestsWithAnotherCode_KeepsTheGenericMessage()
    {
        var guidance = XboxPartnerErrorGuidance.Describe(
            CreateException(HttpStatusCode.TooManyRequests, """{"code":"downstream_throttled"}"""),
            isAutomation: false);

        Assert.Contains("The request was throttled", guidance, StringComparison.Ordinal);
    }

    private static XboxPartnerApiException CreateException(
        HttpStatusCode statusCode,
        string responseBody,
        Uri? requestUri = null,
        TimeSpan? retryAfter = null) =>
        new(
            statusCode,
            statusCode.ToString(),
            responseBody,
            requestUri ?? new Uri("https://gateway.example/xboxpartner/v1/sellers"),
            retryAfter: retryAfter);
}
