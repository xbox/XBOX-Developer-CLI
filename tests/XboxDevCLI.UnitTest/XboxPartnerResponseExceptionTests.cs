// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

public class XboxPartnerResponseExceptionTests
{
    [Theory]
    [InlineData("""{"jobId":"job-1","status":true}""")]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("{\"name\":\"\ud800\"}")]
    public void PreservesOriginalFailureBodyAndCorrelationId(string body)
    {
        var cause = new InvalidOperationException("original failure");
        var exception = new XboxPartnerResponseException(body, "correlation-1", cause);

        Assert.Equal("original failure", exception.Message);
        Assert.Same(cause, exception.InnerException);
        Assert.Equal("correlation-1", exception.CorrelationId);
        Assert.Equal(body, exception.RawResponse);
    }
}
