// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using XboxDevCLI.Safety;

namespace XboxDevCLI.Services;

/// <summary>
/// A PlayFab call that failed. <see cref="Outcome"/> says how far it got, and so whether PlayFab
/// may have acted on it; <see cref="PlayFabApiClient"/> decides that in one place.
/// </summary>
public class PlayFabApiException : Exception
{
    /// <summary>The API path that was called, when known.</summary>
    public string? ApiPath { get; }

    /// <summary>How far the call got.</summary>
    public PlayFabCallOutcome Outcome { get; }

    /// <summary>The HTTP status PlayFab answered with, or <c>null</c> when no response arrived.</summary>
    public int? HttpStatusCode { get; }

    public string? HttpStatus { get; }

    /// <summary>The response body, or empty when none arrived or it couldn't be read.</summary>
    public string RawBody { get; }

    // Parsed PlayFab error fields (populated when the response is valid PlayFab JSON)
    public int? ApiErrorCode { get; }
    public string? ApiError { get; }
    public string? ApiErrorMessage { get; }

    /// <summary>What failed, without <see cref="OutcomeWarning"/>.</summary>
    public string FailureMessage { get; }

    /// <summary>
    /// Set when a call that isn't <c>Get*</c>, <c>List*</c>, or <c>Search*</c> may have been
    /// applied despite failing (<see cref="PlayFabCallOutcome.NoResponse"/> or
    /// <see cref="PlayFabCallOutcome.Inconclusive"/>). Says so and to check before retrying.
    /// </summary>
    public string? OutcomeWarning { get; }

    public override string Message => OutcomeWarning switch
    {
        null => FailureMessage,
        _ when HttpStatusCode is null => OutcomeWarning,
        _ => $"{FailureMessage}. {OutcomeWarning}",
    };

    /// <summary>PlayFab answered <paramref name="httpStatusCode"/>, with <paramref name="rawBody"/>.</summary>
    public PlayFabApiException(int httpStatusCode, string httpStatus, string rawBody)
        : this(apiPath: null, httpStatusCode, httpStatus, rawBody, bodyFailure: null, innerException: null)
    {
    }

    /// <summary>
    /// PlayFab answered <paramref name="httpStatusCode"/>. <paramref name="bodyFailure"/> says why
    /// its body couldn't be read or used, if it couldn't.
    /// </summary>
    internal PlayFabApiException(
        string? apiPath,
        int httpStatusCode,
        string httpStatus,
        string rawBody,
        string? bodyFailure,
        Exception? innerException)
        : base(message: null, innerException)
    {
        ApiPath = apiPath;
        HttpStatusCode = httpStatusCode;
        HttpStatus = httpStatus;
        RawBody = rawBody;
        Outcome = httpStatusCode switch
        {
            >= 500 => PlayFabCallOutcome.Inconclusive,
            >= 200 and < 300 => PlayFabCallOutcome.Succeeded,
            _ => PlayFabCallOutcome.Rejected,
        };

        var statusMessage = BuildMessage(httpStatusCode, httpStatus, rawBody, out var code, out var error, out var errorMessage);
        ApiErrorCode = code;
        ApiError = error;
        ApiErrorMessage = errorMessage;
        FailureMessage = bodyFailure switch
        {
            null => statusMessage,
            _ when Outcome == PlayFabCallOutcome.Succeeded =>
                $"PlayFab API request succeeded with HTTP {httpStatusCode} {httpStatus}, but {bodyFailure}",
            _ => $"{statusMessage}: {bodyFailure}",
        };
        OutcomeWarning = DescribeUnknownOutcome(apiPath, Outcome, $"the request failed with HTTP {httpStatusCode}");
    }

    private PlayFabApiException(string apiPath, PlayFabCallOutcome outcome, string? reason, Exception innerException)
        : base(message: null, innerException)
    {
        ApiPath = apiPath;
        Outcome = outcome;
        RawBody = string.Empty;
        FailureMessage = innerException.Message;
        OutcomeWarning = DescribeUnknownOutcome(apiPath, outcome, reason);
    }

    /// <summary>The request never left; <paramref name="innerException"/> says why.</summary>
    internal static PlayFabApiException NotSent(string apiPath, Exception innerException) =>
        new(apiPath, PlayFabCallOutcome.NotSent, reason: null, innerException);

    /// <summary>
    /// The request may have reached PlayFab, but no response arrived. <paramref name="reason"/>
    /// completes "…may have reached PlayFab, but …", such as "the request was canceled".
    /// </summary>
    internal static PlayFabApiException NoResponse(string apiPath, string reason, Exception innerException) =>
        new(apiPath, PlayFabCallOutcome.NoResponse, reason, innerException);

    private static string? DescribeUnknownOutcome(string? apiPath, PlayFabCallOutcome outcome, string? reason) =>
        apiPath is not null &&
        reason is not null &&
        outcome is PlayFabCallOutcome.NoResponse or PlayFabCallOutcome.Inconclusive &&
        !DestructiveOperations.IsReadOnly(apiPath)
            ? $"{apiPath} may have reached PlayFab, but {reason}, so its outcome is unknown. " +
              "Check whether it was applied before retrying."
            : null;

    private static string BuildMessage(
        int httpStatusCode,
        string httpStatus,
        string rawBody,
        out int? apiErrorCode,
        out string? apiError,
        out string? apiErrorMessage)
    {
        apiErrorCode = null;
        apiError = null;
        apiErrorMessage = null;

        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            var root = doc.RootElement;

            if (root.TryGetProperty("errorCode", out var codeEl))
            {
                apiErrorCode = codeEl.GetInt32();
            }

            if (root.TryGetProperty("error", out var errorEl))
            {
                apiError = errorEl.GetString();
            }

            if (root.TryGetProperty("errorMessage", out var msgEl))
            {
                apiErrorMessage = msgEl.GetString();
            }

            if (apiErrorMessage != null)
            {
                var codeStr = apiErrorCode.HasValue ? $" (errorCode={apiErrorCode.Value})" : "";
                return $"PlayFab API error ({httpStatusCode}): [{apiError}]{codeStr} {apiErrorMessage}";
            }
        }
        catch (Exception ex) when (
            ex is JsonException or ArgumentException or InvalidOperationException or FormatException)
        {
            // The body is not valid PlayFab error JSON: malformed, not transcodable to
            // UTF-8 (e.g. an unpaired UTF-16 surrogate), or a field has an unexpected
            // type — fall through to the generic message.
        }

        return $"PlayFab API request failed with HTTP {httpStatusCode} {httpStatus}";
    }
}
