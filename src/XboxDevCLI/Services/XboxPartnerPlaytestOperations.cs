// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using XboxDevCLI.Commands;

namespace XboxDevCLI.Services;

internal sealed partial class XboxPartnerClient
{
    /// <summary>
    /// An upper bound on pages in one listing. The gateway's largest page is 100 playtests, and it
    /// ends a listing once the next page would start past row 100,000, which with full pages is
    /// page 1001 at the latest. Only a gateway that never ends the list goes past it.
    /// </summary>
    internal const int MaxPlaytestListPages = 1001;

    internal const int PlaytestsPageSize = 100;

    /// <summary>
    /// Lists every playtest of a game, following each <c>continuationToken</c> until the gateway
    /// omits one, and returns them as <c>{"playtests":[...]}</c>.
    /// </summary>
    /// <remarks>
    /// Every page carries the same correlation ID so the gateway logs show one listing. A repeated
    /// token or a listing past <see cref="MaxPlaytestListPages"/> pages fails the listing.
    /// </remarks>
    public async Task<string> ListPlaytestsAsync(
        string baseUrl,
        string sellerId,
        string storeId,
        CancellationToken cancellationToken = default,
        Action<int>? pageRead = null)
    {
        var basePath = PlaytestsPath(sellerId, storeId) + SellerQuery(sellerId, '?');
        IReadOnlyList<string> playtests = await ReadEveryPageAsync(
            "playtests",
            "playtest list",
            token =>
            {
                var paging = token is null
                    ? $"pageSize={PlaytestsPageSize}"
                    : $"continuationToken={Uri.EscapeDataString(token)}";
                return (HttpMethod.Get, $"{basePath}&{paging}", null);
            },
            cancellationToken,
            pageRead: pageRead,
            baseUrl: baseUrl,
            maxPages: MaxPlaytestListPages);
        return "{\"playtests\":[" + string.Join(',', playtests) + "]}";
    }

    public Task<string> GetPlaytestAsync(
        string baseUrl,
        string sellerId,
        string storeId,
        string playtestId,
        CancellationToken cancellationToken = default) =>
        GetPlaytestResourceAsync(
            baseUrl, sellerId, $"{PlaytestsPath(sellerId, storeId)}/{Uri.EscapeDataString(playtestId)}", null,
            cancellationToken);

    public Task<string> GetPlaytestDiffAsync(
        string baseUrl,
        string sellerId,
        string storeId,
        string playtestId,
        string? comparePlaytestId,
        CancellationToken cancellationToken = default) =>
        GetPlaytestResourceAsync(
            baseUrl,
            sellerId,
            $"{PlaytestsPath(sellerId, storeId)}/{Uri.EscapeDataString(playtestId)}/diff",
            comparePlaytestId,
            cancellationToken);

    /// <summary>
    /// Reads the checks to review before publishing a playtest: an object with <c>ready</c>, and
    /// <c>checks</c> whose <c>status</c> is <c>passed</c>, <c>warning</c>, <c>notApplicable</c>, or
    /// <c>checkFailed</c>. It changes nothing.
    /// </summary>
    public Task<string> GetPlaytestReadinessAsync(
        string baseUrl,
        string sellerId,
        string storeId,
        string playtestId,
        CancellationToken cancellationToken = default) =>
        GetPlaytestResourceAsync(
            baseUrl,
            sellerId,
            $"{PlaytestsPath(sellerId, storeId)}/{Uri.EscapeDataString(playtestId)}/readiness",
            null,
            cancellationToken);

    /// <summary>Creates a private draft playtest and returns the gateway's response.</summary>
    /// <remarks>
    /// <paramref name="idempotencyKey"/> is sent as <c>Idempotency-Key</c>, so a repeat with the same key
    /// returns the playtest the first request created. The request is sent once and never retried.
    /// <paramref name="correlationId"/> is sent as the correlation ID, so the caller can report it when
    /// no response arrives. <paramref name="onSending"/> is called immediately before the request is
    /// handed to the network.
    /// </remarks>
    public async Task<string> CreatePlaytestAsync(
        string baseUrl,
        string sellerId,
        string storeId,
        PlaytestWrite request,
        string idempotencyKey,
        string correlationId,
        CancellationToken cancellationToken = default,
        Action? onSending = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        var uri = BuildRequestUri(baseUrl, PlaytestsPath(sellerId, storeId) + SellerQuery(sellerId, '?'));
        var response = await SendAuthorizedAsync(
            uri,
            null,
            cancellationToken,
            HttpMethod.Post,
            request.ToJson(),
            correlationId,
            onSending,
            [new("Idempotency-Key", idempotencyKey)]);
        return RequirePlaytest(response);
    }

    /// <summary>Changes the fields of a playtest that <paramref name="request"/> sets.</summary>
    /// <remarks>
    /// <paramref name="ifMatch"/>, when given, is sent as <c>If-Match</c> unchanged, so it must already be a quoted etag, and
    /// <paramref name="correlationId"/> as the correlation ID. The request is sent once and never retried.
    /// </remarks>
    public async Task<string> UpdatePlaytestAsync(
        string baseUrl,
        string sellerId,
        string storeId,
        string playtestId,
        PlaytestWrite request,
        string? ifMatch,
        string correlationId,
        CancellationToken cancellationToken = default,
        Action? onSending = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(playtestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        var uri = BuildRequestUri(
            baseUrl,
            $"{PlaytestsPath(sellerId, storeId)}/{Uri.EscapeDataString(playtestId)}{SellerQuery(sellerId, '?')}");
        var response = await SendAuthorizedAsync(
            uri,
            null,
            cancellationToken,
            HttpMethod.Patch,
            request.ToJson(),
            correlationId,
            onSending,
            ifMatch is null ? null : [new("If-Match", ifMatch)]);
        return RequirePlaytest(response);
    }

    /// <summary>
    /// Asks Partner Center to publish a playtest, sending <paramref name="ifMatch"/> as <c>If-Match</c>.
    /// Returns the response, an object whose <c>warnings</c> list the checks that didn't block it.
    /// </summary>
    /// <remarks>
    /// Success means the publish was queued, not finished. The request has no body and is sent once,
    /// never retried.
    /// </remarks>
    public async Task<string> PublishPlaytestAsync(
        string baseUrl,
        string sellerId,
        string storeId,
        string playtestId,
        string ifMatch,
        string correlationId,
        CancellationToken cancellationToken = default,
        Action? onSending = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playtestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ifMatch);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        var uri = BuildRequestUri(
            baseUrl,
            $"{PlaytestsPath(sellerId, storeId)}/{Uri.EscapeDataString(playtestId)}/publish{SellerQuery(sellerId, '?')}");
        var response = await SendAuthorizedAsync(
            uri, null, cancellationToken, HttpMethod.Post, null, correlationId, onSending,
            [new("If-Match", ifMatch)]);
        return Require(response, PlaytestFields.RequirePublishResponse);
    }

    /// <summary>
    /// Asks Partner Center to delete a playtest, sending <paramref name="ifMatch"/> as <c>If-Match</c>.
    /// Returns the response, an object whose <c>status</c> is <c>Deleted</c> when the playtest is gone,
    /// or another status, such as <c>Deleting</c>, when the delete was queued.
    /// </summary>
    /// <remarks>The request is sent once, never retried.</remarks>
    public async Task<string> DeletePlaytestAsync(
        string baseUrl,
        string sellerId,
        string storeId,
        string playtestId,
        string ifMatch,
        string correlationId,
        CancellationToken cancellationToken = default,
        Action? onSending = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playtestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ifMatch);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        var uri = BuildRequestUri(
            baseUrl,
            $"{PlaytestsPath(sellerId, storeId)}/{Uri.EscapeDataString(playtestId)}{SellerQuery(sellerId, '?')}");
        var response = await SendAuthorizedAsync(
            uri, null, cancellationToken, HttpMethod.Delete, null, correlationId, onSending,
            [new("If-Match", ifMatch)]);
        return Require(response, PlaytestFields.RequireDeleteResponse);
    }

    /// <summary>
    /// Returns the body of a response that should hold one playtest, or fails with an
    /// <see cref="XboxPartnerResponseException"/> that keeps the response's correlation ID.
    /// </summary>
    private static string RequirePlaytest(GatewayResponse response) =>
        Require(response, PlaytestFields.RequirePlaytest);

    /// <summary>
    /// Returns the body of a response that <paramref name="check"/> accepts, or fails with an
    /// <see cref="XboxPartnerResponseException"/> that keeps the response's correlation ID.
    /// </summary>
    private static string Require(GatewayResponse response, Action<string> check)
    {
        try
        {
            check(response.Body);
            return response.Body;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            throw new XboxPartnerResponseException(response.Body, response.CorrelationId, exception);
        }
    }

    private async Task<string> GetPlaytestResourceAsync(
        string baseUrl,
        string sellerId,
        string path,
        string? comparePlaytestId,
        CancellationToken cancellationToken)
    {
        var query = SellerQuery(sellerId, '?');
        if (comparePlaytestId is not null)
        {
            query += $"&comparePlaytestId={Uri.EscapeDataString(comparePlaytestId)}";
        }

        var uri = BuildRequestUri(baseUrl, path + query);
        return (await SendAuthorizedAsync(uri, null, cancellationToken, HttpMethod.Get)).Body;
    }

    private static string PlaytestsPath(string sellerId, string storeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sellerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeId);
        return $"xboxpartner/v1/games/{Uri.EscapeDataString(storeId)}/playtests";
    }

    // The gateway reads the seller from the query on these routes, and only as a number.
    private static string SellerQuery(string sellerId, char separator) =>
        $"{separator}sellerId={Uri.EscapeDataString(sellerId.Trim())}";
}

/// <summary>
/// The body of a playtest create or update. A null field is left out of the JSON, so an update keeps
/// it; an empty description is sent, which clears it, and so is an empty list of audience IDs.
/// </summary>
internal sealed record PlaytestWrite(
    string? Name,
    string? Description,
    string? LongDescription,
    DateTimeOffset? StartDate,
    DateTimeOffset? EndDate,
    IReadOnlyList<string>? AudienceIds,
    string? PcPackageId = null,
    string? ConsolePackageId = null)
{
    internal string ToJson()
    {
        var body = new JsonObject();
        Add(body, "name", Name);
        Add(body, "description", Description);
        Add(body, "longDescription", LongDescription);
        Add(body, "startDate", StartDate?.ToString("o", CultureInfo.InvariantCulture));
        Add(body, "endDate", EndDate?.ToString("o", CultureInfo.InvariantCulture));
        if (AudienceIds is not null)
        {
            body["audienceIds"] = new JsonArray(AudienceIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
        }

        Add(body, "pcPackageId", PcPackageId);
        Add(body, "consolePackageId", ConsolePackageId);
        return body.ToJsonString();
    }

    private static void Add(JsonObject body, string name, string? value)
    {
        if (value is not null)
        {
            body[name] = value;
        }
    }
}
