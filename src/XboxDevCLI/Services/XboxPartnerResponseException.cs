// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using XboxDevCLI.Telemetry;

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

        // Thrown from many places and often handled by the command itself, so the reason is
        // recorded once, here.
        TelemetryRecorder.RecordError(this);
    }

    internal string RawResponse { get; }

    internal string? CorrelationId { get; }

}
