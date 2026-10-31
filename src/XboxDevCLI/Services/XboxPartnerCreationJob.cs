// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace XboxDevCLI.Services;

/// <summary>
/// An asynchronous XboxPartner creation job, such as a game or sandbox creation, read from its
/// submission response or its job route.
/// </summary>
internal abstract record XboxPartnerCreationJob
{
    [JsonPropertyName("jobId")]
    public required string JobId { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("result")]
    public string? Result { get; init; }

    [JsonPropertyName("errors")]
    public XboxPartnerGameError[]? Errors { get; init; }

    [JsonIgnore]
    public string RawResponse { get; init; } = string.Empty;

    [JsonIgnore]
    public TimeSpan? RetryAfter { get; init; }

    [JsonIgnore]
    public string? CorrelationId { get; init; }

    internal bool Completed =>
        string.Equals(Status, "completed", StringComparison.Ordinal);

    internal bool Succeeded =>
        Completed && string.Equals(Result, "succeeded", StringComparison.Ordinal);

    /// <summary>
    /// Whether the job completed and reported failure. A completed job whose result is neither
    /// <c>succeeded</c> nor <c>failed</c> has an unknown outcome, not a failed one.
    /// </summary>
    internal bool Failed =>
        Completed && string.Equals(Result, "failed", StringComparison.Ordinal);
}
