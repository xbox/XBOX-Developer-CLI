// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace XboxDevCLI.Services;

internal sealed partial class XboxPartnerClient
{
    internal const string AudiencesPath = "xboxpartner/v1/audiences";

    /// <summary>
    /// Lists every audience visible to the seller in one request. Each audience is returned as the
    /// service sent it; only its <c>id</c> is checked.
    /// </summary>
    /// <remarks>The gateway returns the complete collection as a JSON array, or fails.</remarks>
    public async Task<IReadOnlyList<JsonObject>> ListAudiencesAsync(
        string baseUrl,
        string sellerId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sellerId);
        var uri = BuildRequestUri(baseUrl, AudiencesPath + SellerQuery(sellerId));
        var response = await SendAuthorizedAsync(uri, sellerId: null, cancellationToken, HttpMethod.Get);
        try
        {
            // One level less than the default, so the output's wrapper object stays within it.
            if (ParseAudienceJson(response.Body, "audience list", maxDepth: 63) is not JsonArray array)
            {
                throw new InvalidDataException("The audience list response must be a JSON array.");
            }

            return array.Select(CheckAudience).ToList();
        }
        catch (InvalidDataException exception)
        {
            throw new XboxPartnerResponseException(response.Body, response.CorrelationId, exception);
        }
    }

    /// <summary>
    /// Gets one audience, with its stored members, as the service sent it; only its <c>id</c> is
    /// checked. The audience ID is opaque: pass it exactly as the list returned it.
    /// </summary>
    public async Task<JsonObject> GetAudienceAsync(
        string baseUrl,
        string sellerId,
        string audienceId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sellerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(audienceId);
        if (IsDotSegment(audienceId))
        {
            throw new ArgumentException("The audience ID can't be '.' or '..'.", nameof(audienceId));
        }

        var uri = BuildRequestUri(
            baseUrl, $"{AudiencesPath}/{Uri.EscapeDataString(audienceId)}" + SellerQuery(sellerId));
        var response = await SendAuthorizedAsync(uri, sellerId: null, cancellationToken, HttpMethod.Get);
        try
        {
            return CheckAudience(ParseAudienceJson(response.Body, "audience"));
        }
        catch (InvalidDataException exception)
        {
            throw new XboxPartnerResponseException(response.Body, response.CorrelationId, exception);
        }
    }

    /// <summary>Creates a known-user audience with <paramref name="members"/> as its complete member list.</summary>
    /// <remarks>
    /// The request is sent once and never retried. <paramref name="correlationId"/> is sent as the
    /// correlation ID, and <paramref name="onSending"/> is called immediately before the request is
    /// handed to the network.
    /// </remarks>
    public async Task<JsonObject> CreateAudienceAsync(
        string baseUrl,
        string sellerId,
        string name,
        IReadOnlyList<string> members,
        string correlationId,
        CancellationToken cancellationToken = default,
        Action? onSending = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sellerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(members);
        var body = new JsonObject { ["name"] = name, ["members"] = MembersJson(members) };
        var uri = BuildRequestUri(baseUrl, AudiencesPath + SellerQuery(sellerId));
        return await WriteAudienceAsync(uri, HttpMethod.Post, body, correlationId, onSending, cancellationToken);
    }

    /// <summary>
    /// Renames an audience, replaces its complete member list, or both. A <see langword="null"/>
    /// <paramref name="name"/> or <paramref name="members"/> is left out of the request, so the audience
    /// keeps it; an empty <paramref name="members"/> removes every member.
    /// </summary>
    /// <remarks>Sent once and never retried, as <see cref="CreateAudienceAsync"/> is.</remarks>
    public async Task<JsonObject> UpdateAudienceAsync(
        string baseUrl,
        string sellerId,
        string audienceId,
        string? name,
        IReadOnlyList<string>? members,
        string correlationId,
        CancellationToken cancellationToken = default,
        Action? onSending = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sellerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(audienceId);
        if (IsDotSegment(audienceId))
        {
            throw new ArgumentException("The audience ID can't be '.' or '..'.", nameof(audienceId));
        }

        if (name is null && members is null)
        {
            throw new ArgumentException("An update must change the name, the members, or both.");
        }

        if (name is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
        }

        var body = new JsonObject();
        if (name is not null)
        {
            body["name"] = name;
        }

        if (members is not null)
        {
            body["members"] = MembersJson(members);
        }

        var uri = BuildRequestUri(
            baseUrl, $"{AudiencesPath}/{Uri.EscapeDataString(audienceId)}" + SellerQuery(sellerId));
        return await WriteAudienceAsync(uri, HttpMethod.Patch, body, correlationId, onSending, cancellationToken);
    }

    private static JsonArray MembersJson(IReadOnlyList<string> members) =>
        new(members.Select(member => (JsonNode?)JsonValue.Create(member)).ToArray());

    private async Task<JsonObject> WriteAudienceAsync(
        Uri uri,
        HttpMethod method,
        JsonObject body,
        string correlationId,
        Action? onSending,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        var response = await SendAuthorizedAsync(
            uri, sellerId: null, cancellationToken, method, body.ToJsonString(), correlationId, onSending);
        try
        {
            return CheckAudience(ParseAudienceJson(response.Body, "audience"));
        }
        catch (InvalidDataException exception)
        {
            throw new XboxPartnerResponseException(response.Body, response.CorrelationId, exception);
        }
    }

    /// <summary>
    /// Whether <paramref name="audienceId"/> is a dot segment, which escaping leaves unchanged and
    /// <see cref="Uri"/> removes, sending the request to another route.
    /// </summary>
    internal static bool IsDotSegment(string audienceId) => audienceId is "." or "..";

    // Audience routes check the seller against the signed-in developer and read it from the query,
    // as test-account routes do, not from the seller header.
    private static string SellerQuery(string sellerId) => "?sellerId=" + Uri.EscapeDataString(sellerId.Trim());

    private static JsonObject CheckAudience(JsonNode? node)
    {
        if (node is not JsonObject audience)
        {
            throw new InvalidDataException("The audience response contained an audience that is not a JSON object.");
        }

        if (!audience.TryGetPropertyValue("id", out var id)
            || id is not JsonValue value
            || !value.TryGetValue<string>(out var text)
            || string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidDataException("The audience response contained an audience with no 'id'.");
        }

        return audience;
    }

    private static JsonNode? ParseAudienceJson(string body, string what, int maxDepth = 64)
    {
        try
        {
            // Duplicate properties fail here rather than one value silently winning. Writing the
            // parsed node decodes every string, so a lone surrogate fails here, not during output.
            var node = JsonNode.Parse(
                body, documentOptions: new JsonDocumentOptions { AllowDuplicateProperties = false, MaxDepth = maxDepth });
            _ = node?.ToJsonString();
            return node;
        }
        // An escaped lone surrogate, such as "\uD800", throws InvalidOperationException.
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw new InvalidDataException($"The {what} response was not valid JSON.", exception);
        }
    }
}

/// <summary>
/// The audience fields that table output shows. Other fields are output as the service returns them.
/// </summary>
/// <param name="Id">The audience ID, an opaque string. Pass it to <c>get</c> unchanged.</param>
/// <param name="Name">The audience name, or null.</param>
/// <param name="GroupType">The service's group type, such as <c>User</c>, or null.</param>
internal sealed record XboxPartnerAudience(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("groupType")] string? GroupType);
