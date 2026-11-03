// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace XboxDevCLI.Services;

internal sealed partial class XboxPartnerClient
{
    /// <remarks>
    /// <paramref name="onSending"/> is called immediately before the request is handed to the
    /// network. A failure before it is called means the gateway never saw the request.
    /// </remarks>
    public Task<XboxPartnerSandboxCreationJob> CreateSandboxAsync(
        string baseUrl,
        string sellerId,
        CreateXboxSandboxRequest request,
        CancellationToken cancellationToken = default,
        Action? onSending = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendJobAsync<XboxPartnerSandboxCreationJob>(
            HttpMethod.Post,
            baseUrl,
            "xboxpartner/v1/sandboxes",
            sellerId,
            JsonSerializer.Serialize(request),
            cancellationToken,
            onSending);
    }

    public Task<XboxPartnerSandboxCreationJob> GetSandboxCreationJobAsync(
        string baseUrl,
        string sellerId,
        string jobId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        return SendJobAsync<XboxPartnerSandboxCreationJob>(
            HttpMethod.Get,
            baseUrl,
            $"xboxpartner/v1/sandboxes/jobs/{Uri.EscapeDataString(jobId)}",
            sellerId,
            null,
            cancellationToken);
    }

    public async Task<string> GetSandboxAsync(
        string baseUrl,
        string sellerId,
        string sandboxId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sellerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxId);
        var uri = BuildRequestUri(baseUrl, $"xboxpartner/v1/sandboxes/{Uri.EscapeDataString(sandboxId)}");
        var token = await AcquireRequiredTokenAsync(cancellationToken);
        return await GetAuthorizedAsync(uri, token, sellerId.Trim(), cancellationToken);
    }

    /// <summary>
    /// Lists every sandbox visible to the seller, following each <c>continuationToken</c> until the
    /// gateway omits one, and returns the items as one JSON array.
    /// </summary>
    /// <remarks>
    /// Every page carries the same correlation ID so the gateway logs show one listing. A repeated
    /// token would loop forever, so it fails the listing rather than being followed, and so does a
    /// listing longer than <see cref="MaxSandboxListPages"/> pages.
    /// </remarks>
    public async Task<string> ListSandboxesAsync(
        string baseUrl,
        string sellerId,
        CancellationToken cancellationToken = default,
        Action<int>? pageRead = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sellerId);
        _ = BuildRequestUri(baseUrl, SandboxesPath);
        var correlationId = Guid.NewGuid().ToString();
        var sandboxes = new JsonArray();
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);
        string? continuationToken = null;
        var pages = 0;
        do
        {
            pages++;
            var path = continuationToken is null
                ? SandboxesPath
                : $"{SandboxesPath}?continuationToken={Uri.EscapeDataString(continuationToken)}";

            // Acquired per page so MSAL can refresh the token during a long listing.
            var token = await AcquireRequiredTokenAsync(cancellationToken);
            var response = await SendAuthorizedAsync(
                BuildRequestUri(baseUrl, path), token, sellerId.Trim(), cancellationToken,
                HttpMethod.Get, correlationId: correlationId);
            try
            {
                int before = sandboxes.Count;
                continuationToken = ReadSandboxPage(response.Body, sandboxes);
                pageRead?.Invoke(sandboxes.Count - before);
                if (continuationToken is not null && !seenTokens.Add(continuationToken))
                {
                    throw new InvalidDataException(
                        "The sandbox list response repeated a continuation token, so the list " +
                        "could not be completed.");
                }

                if (continuationToken is not null && pages >= MaxSandboxListPages)
                {
                    throw new InvalidDataException(
                        $"The sandbox list did not end after {MaxSandboxListPages} pages, so it was stopped.");
                }
            }
            catch (InvalidDataException exception)
            {
                // Carries the page's body and correlation ID to the error output.
                throw new XboxPartnerResponseException(response.Body, response.CorrelationId, exception);
            }
        }
        while (continuationToken is not null);

        return sandboxes.ToJsonString();
    }

    private const string SandboxesPath = "xboxpartner/v1/sandboxes";

    /// <summary>
    /// An upper bound on pages in one listing: at the gateway's default page size, far more
    /// sandboxes than a seller has, so only a gateway that never ends the list reaches it.
    /// </summary>
    internal const int MaxSandboxListPages = 1000;

    private static string? ReadSandboxPage(string body, JsonArray sandboxes)
    {
        JsonNode? page;
        try
        {
            // Duplicate properties fail here; otherwise JsonObject throws ArgumentException on first access.
            page = JsonNode.Parse(body, documentOptions: new JsonDocumentOptions { AllowDuplicateProperties = false });
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The sandbox list response was not valid JSON.", exception);
        }

        if (page is not JsonObject root
            || !root.TryGetPropertyValue("sandboxes", out var items)
            || items is not JsonArray array)
        {
            throw new InvalidDataException("The sandbox list response must contain a sandboxes array.");
        }

        foreach (var item in array)
        {
            if (item is not JsonObject sandbox
                || !sandbox.TryGetPropertyValue("sandboxId", out var id)
                || id is not JsonValue value
                || !value.TryGetValue<string>(out var text)
                || string.IsNullOrWhiteSpace(text))
            {
                throw new InvalidDataException(
                    "The sandbox list response contained an entry with no 'sandboxId'.");
            }

            sandboxes.Add(sandbox.DeepClone());
        }

        if (!root.TryGetPropertyValue("continuationToken", out var next) || next is null)
        {
            return null;
        }

        if (next is JsonValue tokenValue
            && tokenValue.TryGetValue<string>(out var token)
            && !string.IsNullOrWhiteSpace(token))
        {
            return token;
        }

        throw new InvalidDataException(
            "The sandbox list response contained an invalid 'continuationToken'.");
    }
}

internal sealed record CreateXboxSandboxRequest(
    [property: JsonPropertyName("sandboxId")] string SandboxId,
    [property: JsonPropertyName("type")] string Type);

/// <summary>A sandbox returned by <c>GET /xboxpartner/v1/sandboxes/{sandboxId}</c>.</summary>
internal sealed record XboxPartnerSandbox(
    [property: JsonPropertyName("sandboxId")] string? SandboxId,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("sellerId")] string? SellerId);

/// <summary>A sandbox in a <c>GET /xboxpartner/v1/sandboxes</c> page.</summary>
/// <param name="SandboxId">The sandbox ID, such as <c>ABCDEF.1</c> or <c>RETAIL</c>.</param>
/// <param name="Type"><c>Shared</c>, <c>Isolated</c>, or <c>Unknown</c>.</param>
/// <param name="SellerId">The seller that owns the sandbox, if any.</param>
/// <param name="Global">Whether the sandbox is a global sandbox, such as <c>RETAIL</c>.</param>
internal sealed record XboxPartnerSandboxSummary(
    [property: JsonPropertyName("sandboxId")] string? SandboxId,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("sellerId")] string? SellerId,
    [property: JsonPropertyName("global")] bool? Global);

/// <summary>Validates sandbox IDs with the same rules as the XboxPartner gateway.</summary>
/// <remarks>
/// The trimmed input is validated before it is upper-cased: some non-ASCII letters, such as
/// <c>ſ</c>, upper-case to ASCII and would otherwise pass as a different ID.
/// </remarks>
internal static partial class XboxSandboxId
{
    internal const string SharedType = "Shared";

    internal const int MaxLength = 64;

    internal const string ReadFormatDescription =
        "A sandbox ID is letters or digits with an optional period-separated suffix, at most " +
        "64 characters, such as ABCDEF.1 or RETAIL.";

    internal const string CreateFormatDescription =
        "A new sandbox ID is 4 to 60 letters or digits, a period, and a number from 0 to 999 " +
        "without a leading zero, at most 64 characters in total, such as ABCDEF.1. The suffix 58 is " +
        "reserved for Isolated sandboxes, the name UNDEFINED is reserved, and the global sandboxes " +
        "RETAIL, CERT, CERT.DEBUG, READONLY, and MSFT.1 cannot be created.";

    /// <summary>The suffix always provisioned as an Isolated sandbox, so it can't be created as Shared.</summary>
    private const string ReservedIsolatedSuffix = ".58";

    private const string ReservedMoniker = "UNDEFINED";

    /// <summary>Global sandboxes, owned by XBOX rather than a seller; the gateway won't create them.</summary>
    private static readonly HashSet<string> GlobalSandboxes =
        new(["RETAIL", "CERT", "CERT.DEBUG", "READONLY", "MSFT.1"], StringComparer.Ordinal);

    /// <summary>Returns the upper-case form of an ID that can be read, including global sandboxes.</summary>
    internal static bool TryNormalizeForRead(string? value, out string normalized) =>
        TryNormalize(value, ReadPattern(), out normalized);

    /// <summary>Returns the upper-case form of an ID that a Shared development sandbox can be created with.</summary>
    internal static bool TryNormalizeForCreate(string? value, out string normalized)
    {
        if (TryNormalize(value, CreatePattern(), out normalized)
            && !normalized.EndsWith(ReservedIsolatedSuffix, StringComparison.Ordinal)
            && !normalized.StartsWith(ReservedMoniker + ".", StringComparison.Ordinal)
            && !GlobalSandboxes.Contains(normalized))
        {
            return true;
        }

        normalized = string.Empty;
        return false;
    }

    private static bool TryNormalize(string? value, Regex pattern, out string normalized)
    {
        var candidate = value?.Trim() ?? string.Empty;
        if (candidate.Length > MaxLength || !pattern.IsMatch(candidate))
        {
            normalized = string.Empty;
            return false;
        }

        normalized = candidate.ToUpperInvariant();
        return true;
    }

    [GeneratedRegex(@"^[A-Za-z0-9]+(\.[A-Za-z0-9]+)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex ReadPattern();

    [GeneratedRegex(@"^[A-Za-z0-9]{4,60}\.(0|[1-9][0-9]{0,2})\z", RegexOptions.CultureInvariant)]
    private static partial Regex CreatePattern();
}
