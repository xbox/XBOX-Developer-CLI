// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text.Json;

namespace XboxDevCLI.Services;

/// <summary>
/// Non-secret claims read from an access token's payload for status and identity consistency
/// checks. Decoding does not validate the token's signature; the API validates the token.
/// </summary>
internal sealed record AccessTokenClaims(
    string? TenantId,
    string? ObjectId,
    string? AppId,
    string? Audience,
    string? IdentityType,
    IReadOnlyList<string> Roles,
    bool HasDelegatedScopes,
    bool SubjectIsObjectId)
{
    /// <summary>
    /// <c>app</c> for an app-only token, <c>user</c> for a delegated token, or <c>unknown</c>.
    /// Prefers the <c>idtyp</c> optional claim; without it, an app-only token has no <c>scp</c>
    /// claim and its <c>sub</c> equals its <c>oid</c>.
    /// </summary>
    public string PrincipalType =>
        IdentityType is "app" or "user" ? IdentityType
        : HasDelegatedScopes ? "user"
        : SubjectIsObjectId ? "app"
        : "unknown";

    /// <summary>Decodes the JWT payload, or returns null when the token is not a readable JWT.</summary>
    public static AccessTokenClaims? TryDecode(string? accessToken)
    {
        var parts = accessToken?.Split('.');
        if (parts is not { Length: 3 })
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(DecodeBase64Url(parts[1]));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string? Text(string name) =>
                root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;

            var roles = root.TryGetProperty("roles", out var rolesElement) && rolesElement.ValueKind == JsonValueKind.Array
                ? rolesElement.EnumerateArray()
                    .Where(role => role.ValueKind == JsonValueKind.String)
                    .Select(role => role.GetString()!)
                    .ToArray()
                : [];

            var objectId = Text("oid");
            return new AccessTokenClaims(
                TenantId: Text("tid"),
                ObjectId: objectId,
                AppId: Text("appid") ?? Text("azp"),
                Audience: Text("aud"),
                IdentityType: Text("idtyp"),
                Roles: roles,
                HasDelegatedScopes: Text("scp") is not null,
                SubjectIsObjectId: objectId is not null && objectId == Text("sub"));
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    private static byte[] DecodeBase64Url(string segment)
    {
        var base64 = segment.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=');
        return Convert.FromBase64String(base64);
    }
}
