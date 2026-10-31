// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace XboxDevCLI.Services;

internal sealed class XboxPartnerResponseException : Exception
{
    internal XboxPartnerResponseException(
        string rawResponse,
        string? correlationId,
        Exception innerException)
        : base(innerException.Message, innerException)
    {
        RawResponse = rawResponse;
        CorrelationId = correlationId;
    }

    internal string RawResponse { get; }

    internal string? CorrelationId { get; }

}
