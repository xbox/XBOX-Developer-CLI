// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace XboxDevCLI.Services;

/// <summary>A game creation job from <c>/xboxpartner/v1/games</c>, which can carry the new game.</summary>
internal sealed record XboxPartnerGameCreationJob : XboxPartnerCreationJob
{
    [JsonPropertyName("game")]
    public XboxPartnerGameSummary? Game { get; init; }
}

internal sealed record XboxPartnerGameSummary
{
    [JsonPropertyName("productId")]
    public string? ProductId { get; init; }

    [JsonPropertyName("storeId")]
    public string? StoreId { get; init; }

    [JsonPropertyName("xboxTitleId")]
    public string? XboxTitleId { get; init; }

    [JsonPropertyName("SCID")]
    public string? Scid { get; init; }

    [JsonPropertyName("primaryDevelopmentSandbox")]
    public string? PrimaryDevelopmentSandbox { get; init; }

    [JsonPropertyName("deviceFamilies")]
    public string[]? DeviceFamilies { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("packageIdentityName")]
    public string? PackageIdentityName { get; init; }

    [JsonPropertyName("packageIdentityPublisher")]
    public string? PackageIdentityPublisher { get; init; }

    [JsonPropertyName("packageFamilyName")]
    public string? PackageFamilyName { get; init; }

    [JsonPropertyName("msaAppId")]
    public string? MsaAppId { get; init; }
}

internal sealed record XboxPartnerGameError
{
    [JsonPropertyName("code")]
    public string? Code { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("details")]
    public XboxPartnerGameError[]? Details { get; init; }
}
