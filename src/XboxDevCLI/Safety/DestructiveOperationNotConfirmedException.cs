// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace XboxDevCLI.Safety;

/// <summary>
/// Thrown by the PlayFab API client when a caller attempts a destructive,
/// irreversible operation (see <see cref="DestructiveOperations"/>) without
/// explicitly confirming it.
/// </summary>
/// <remarks>
/// This is the fail-closed backstop: the destructive gate is enforced at the
/// single API chokepoint (<c>PlayFabApiClient.CallApiAsync</c>), so any code path
/// that does not deliberately pass <c>confirmedDestructive: true</c> is refused —
/// regardless of which command or overload made the call. The CLI layer resolves
/// confirmation (via <c>--confirm</c> or an interactive prompt) and only then
/// passes <c>confirmedDestructive: true</c>.
/// </remarks>
public class DestructiveOperationNotConfirmedException : Exception
{
    /// <summary>The API path that was refused (e.g. <c>/Admin/DeleteTitle</c>).</summary>
    public string ApiPath { get; }

    public DestructiveOperationNotConfirmedException(string apiPath)
        : base($"'{apiPath}' is a destructive, irreversible operation that was not confirmed by the caller.")
    {
        ApiPath = apiPath;
    }
}
