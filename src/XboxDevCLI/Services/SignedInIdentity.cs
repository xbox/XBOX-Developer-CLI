// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Identity.Client;

namespace XboxDevCLI.Services;

/// <summary>
/// The signed-in developer: the account's home tenant and object ID, which key saved context, so
/// signing in as another account never applies this one's. The username is for display only.
/// </summary>
/// <remarks>
/// Always read from the token cache, never from a token: a session whose refresh token has
/// expired still finds its saved context, and resolving context needs no network request.
/// </remarks>
internal sealed record SignedInIdentity(string TenantId, string ObjectId, string? Username = null)
{
    /// <summary>The account as one string, <c>tenant/object</c>, ignoring the username.</summary>
    public string Id => $"{TenantId}/{ObjectId}";

    /// <summary>
    /// The account signed in to <paramref name="auth"/>, or <c>null</c> when nobody is signed in.
    /// A service principal (<see cref="AuthenticationService.IsAutomation"/>) has no cached account,
    /// so it never uses saved context.
    /// </summary>
    public static async Task<SignedInIdentity?> FromCacheAsync(
        AuthenticationService auth, CancellationToken cancellationToken) =>
        !auth.IsAutomation && await auth.GetCachedAccountAsync(cancellationToken) is { } account
            ? FromAccount(account)
            : null;

    public static SignedInIdentity FromAccount(IAccount account)
    {
        var tenantId = account.HomeAccountId?.TenantId;
        var objectId = account.HomeAccountId?.ObjectId;
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(objectId))
        {
            throw new InvalidOperationException(
                "The cached sign-in does not identify a tenant and user. "
                + $"Run '{CliIdentity.CommandName} auth login' again.");
        }

        return new SignedInIdentity(tenantId, objectId, account.Username);
    }
}
