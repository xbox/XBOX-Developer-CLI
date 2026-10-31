// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;

namespace XboxDevCLI.Services;

internal sealed class XboxPartnerApiException : Exception
{
    public XboxPartnerApiException(
        HttpStatusCode statusCode,
        string? reasonPhrase,
        string responseBody,
        Uri requestUri,
        string? correlationId = null,
        TimeSpan? retryAfter = null,
        Exception? innerException = null)
        : base(
            $"XboxPartner request to '{requestUri}' failed with HTTP {(int)statusCode}"
            + (string.IsNullOrWhiteSpace(reasonPhrase) ? "." : $" ({reasonPhrase}).")
            + (innerException is null ? string.Empty : " The response body could not be read.")
            + (innerException is InvalidOperationException ? $" {innerException.Message}" : string.Empty),
            innerException)
    {
        StatusCode = statusCode;
        ReasonPhrase = reasonPhrase;
        ResponseBody = responseBody;
        RequestUri = requestUri;
        CorrelationId = correlationId;
        RetryAfter = retryAfter;
    }

    public HttpStatusCode StatusCode { get; }

    public string? ReasonPhrase { get; }

    public string ResponseBody { get; }

    public Uri RequestUri { get; }

    public string? CorrelationId { get; }

    /// <summary>How long the error response's <c>Retry-After</c> header asks the caller to wait, if it sent one.</summary>
    public TimeSpan? RetryAfter { get; }
}
