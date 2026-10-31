// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace XboxDevCLI.Services;

/// <summary>
/// How far a failed PlayFab call got, and so whether it may have taken effect.
/// <see cref="PlayFabApiClient"/> decides this in one place, and callers act on it
/// rather than on the kind of exception that caused the failure.
/// </summary>
public enum PlayFabCallOutcome
{
    /// <summary>The request never left, so PlayFab didn't act on it.</summary>
    NotSent,

    /// <summary>
    /// The request may have reached PlayFab, but no response arrived: it was canceled, timed
    /// out, or lost its connection. PlayFab may have acted on it.
    /// </summary>
    NoResponse,

    /// <summary>
    /// PlayFab, or a gateway in front of it, answered with a server error (HTTP 5xx), which can
    /// come after the call took effect. PlayFab may have acted on it.
    /// </summary>
    Inconclusive,

    /// <summary>PlayFab answered with a status that rejects the request, such as HTTP 4xx.</summary>
    Rejected,

    /// <summary>
    /// PlayFab answered with a success status, so the call took effect, but its response
    /// couldn't be read or used.
    /// </summary>
    Succeeded,
}
