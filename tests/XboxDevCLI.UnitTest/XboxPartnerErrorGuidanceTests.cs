// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

public class XboxPartnerErrorGuidanceTests
{
    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "Verify the gateway base URL")]
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
    }

    [Fact]
    public void Describe_NotFoundIsReportedAsDeploymentIssueNotEmptyResult()
    {
        var guidance = XboxPartnerErrorGuidance.Describe(
            CreateException(HttpStatusCode.NotFound, string.Empty), isAutomation: false);

        Assert.Contains("seller endpoint was not found", guidance, StringComparison.Ordinal);
        Assert.Contains("empty sellers array", guidance, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://gateway.example/xboxpartner/v1/jobs/job-1")]
    [InlineData("https://gateway.example/xboxpartner/v1/jobs/sellers")]
    public void Describe_NotFoundOutsideSellersDoesNotBlameSellerEndpoint(string requestUri)
    {
        var guidance = XboxPartnerErrorGuidance.Describe(
            CreateException(HttpStatusCode.NotFound, string.Empty, new Uri(requestUri)), isAutomation: false);

        Assert.Contains($"404 Not Found for '{requestUri}'", guidance, StringComparison.Ordinal);
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
            "Gateway detail: Seller id has an invalid format.",
            guidance,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_IgnoresMalformedProblemBody()
    {
        var guidance = XboxPartnerErrorGuidance.Describe(
            CreateException(HttpStatusCode.BadRequest, "not-json"), isAutomation: false);

        Assert.DoesNotContain("Gateway detail:", guidance, StringComparison.Ordinal);
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

    private static XboxPartnerApiException CreateException(
        HttpStatusCode statusCode,
        string responseBody,
        Uri? requestUri = null) =>
        new(
            statusCode,
            statusCode.ToString(),
            responseBody,
            requestUri ?? new Uri("https://gateway.example/xboxpartner/v1/sellers"));
}
