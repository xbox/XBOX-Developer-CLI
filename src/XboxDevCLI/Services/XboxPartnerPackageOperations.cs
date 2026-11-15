// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XboxDevCLI.Services;

/// <summary>
/// The XboxPartner package routes, kept together so a change to the gateway's contract is made in one place.
/// </summary>
/// <remarks>
/// Every route is under the game, identified by its 12-character Store ID, and takes the seller as an
/// optional numeric <c>sellerId</c> query parameter. Without one, the gateway uses the developer's only
/// seller, and refuses with <c>seller_selection_required</c> when there are several.
/// </remarks>
internal static class XboxPartnerPackageRoutes
{
    public static string PackageSets(string storeId, long? sellerId) =>
        WithSeller($"{Game(storeId)}/package-sets", sellerId);

    public static string Package(string storeId, string packageId, long? sellerId) =>
        WithSeller(PackagePath(storeId, packageId), sellerId);

    /// <summary>
    /// Whether a package ID can name a package in a route. Escaping leaves <c>.</c> and <c>..</c> as they
    /// are, and URI normalization would then send the request to the packages or game route instead.
    /// </summary>
    public static bool IsValidPackageId(string? packageId) =>
        !string.IsNullOrWhiteSpace(packageId) && packageId is not "." and not "..";

    private static string Game(string storeId) => $"xboxpartner/v1/games/{Uri.EscapeDataString(storeId)}";

    private static string PackagePath(string storeId, string packageId) =>
        $"{Game(storeId)}/packages/{Uri.EscapeDataString(packageId)}";

    private static string WithSeller(string path, long? sellerId) =>
        sellerId is { } id ? $"{path}?sellerId={id.ToString(CultureInfo.InvariantCulture)}" : path;
}

/// <summary>The problem <c>code</c> values the package routes return that the CLI acts on.</summary>
internal static class XboxPartnerPackageErrors
{
    public const string PermissionDenied = "package_permission_denied";
    public const string SellerSelectionRequired = "seller_selection_required";
    public const string IdentityOverrideNotAuthorized = "identity_override_not_authorized";
    public const string MainPackageSetNotFound = "main_package_set_not_found";
}

internal sealed partial class XboxPartnerClient
{
    /// <summary>Packages read at once while listing.</summary>
    internal const int PackageListConcurrency = 4;

    /// <summary>Reads the game's package sets, and which one is Main.</summary>
    public async Task<XboxPartnerPackageSets> GetPackageSetsAsync(
        string baseUrl,
        long? sellerId,
        string storeId,
        CancellationToken cancellationToken,
        string? correlationId = null)
    {
        GatewayResponse response = await SendPackageRequestAsync(
            baseUrl, XboxPartnerPackageRoutes.PackageSets(storeId, sellerId),
            correlationId ?? Guid.NewGuid().ToString(), cancellationToken).ConfigureAwait(false);
        string body = response.Body;
        try
        {
            XboxPartnerPackageSets sets = JsonSerializer.Deserialize<XboxPartnerPackageSets>(body)
                ?? throw new InvalidDataException("The package set response was empty.");
            if (sets.PackageSets is null)
            {
                throw new InvalidDataException("The package set response must contain the packageSets array.");
            }

            return sets;
        }
        catch (JsonException exception)
        {
            throw new XboxPartnerResponseException(
                body, response.CorrelationId ?? correlationId, new InvalidDataException("The package set response was not valid JSON.", exception));
        }
        catch (InvalidDataException exception)
        {
            throw new XboxPartnerResponseException(body, response.CorrelationId ?? correlationId, exception);
        }
    }

    /// <summary>
    /// Lists the packages on the game's Main branch: every package ID in Main's market groups, each
    /// read with every field the gateway returns.
    /// </summary>
    /// <remarks>
    /// The gateway has no route that lists packages, so this reads the package sets and then each
    /// package, a few at a time, under one correlation ID. A package the gateway no longer finds is
    /// left out and named in <see cref="XboxPartnerPackageListing.MissingPackageIds"/>.
    /// </remarks>
    public async Task<XboxPartnerPackageListing> ListPackagesAsync(
        string baseUrl,
        long? sellerId,
        string storeId,
        CancellationToken cancellationToken)
    {
        string correlationId = Guid.NewGuid().ToString();
        XboxPartnerPackageSets sets = await GetPackageSetsAsync(baseUrl, sellerId, storeId, cancellationToken, correlationId)
            .ConfigureAwait(false);
        XboxPartnerPackageSet? main = sets.Main;
        if (main is null)
        {
            return new XboxPartnerPackageListing(
                $"{{\"packageSetId\":null,\"{XboxPartnerPackage.Collection}\":[]}}", null, []);
        }

        string[] listed = [.. (main.MarketGroups ?? [])
            .SelectMany(group => group.PackageIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)];
        string[] packageIds = [.. listed.Where(XboxPartnerPackageRoutes.IsValidPackageId)];
        var packages = new XboxPartnerPackageResult?[packageIds.Length];
        var missing = new List<string>(listed.Where(id => !XboxPartnerPackageRoutes.IsValidPackageId(id)));
        await Parallel.ForEachAsync(
            Enumerable.Range(0, packageIds.Length),
            new ParallelOptions { MaxDegreeOfParallelism = PackageListConcurrency, CancellationToken = cancellationToken },
            async (index, token) =>
            {
                try
                {
                    packages[index] = await GetPackageAsync(
                        baseUrl, sellerId, storeId, packageIds[index], token, correlationId).ConfigureAwait(false);
                }
                catch (XboxPartnerApiException exception)
                    when (exception.StatusCode == HttpStatusCode.NotFound && !XboxPartnerErrorGuidance.IsGameNotFound(exception))
                {
                    lock (missing)
                    {
                        missing.Add(packageIds[index]);
                    }
                }
            }).ConfigureAwait(false);

        XboxPartnerPackageResult[] found = [.. packages.OfType<XboxPartnerPackageResult>()];
        string json = $"{{\"packageSetId\":{JsonSerializer.Serialize(main.Id)},\"{XboxPartnerPackage.Collection}\":" +
            $"[{string.Join(',', found.Select(package => package.RawResponse))}]}}";
        return new XboxPartnerPackageListing(json, main.Id, [.. listed.Where(missing.Contains)]);
    }

    /// <summary>Reads one package.</summary>
    public async Task<XboxPartnerPackageResult> GetPackageAsync(
        string baseUrl,
        long? sellerId,
        string storeId,
        string packageId,
        CancellationToken cancellationToken,
        string? correlationId = null)
    {
        if (!XboxPartnerPackageRoutes.IsValidPackageId(packageId))
        {
            throw new ArgumentException("The package ID must not be empty, '.', or '..'.", nameof(packageId));
        }

        correlationId ??= Guid.NewGuid().ToString();
        GatewayResponse response = await SendPackageRequestAsync(
            baseUrl, XboxPartnerPackageRoutes.Package(storeId, packageId, sellerId), correlationId, cancellationToken).ConfigureAwait(false);
        string body = response.Body;
        return ReadPackageResult(body, response.CorrelationId ?? correlationId);
    }

    // The package routes take the seller in the query, so no X-DeveloperAccountId header is sent.
    private async Task<GatewayResponse> SendPackageRequestAsync(
        string baseUrl,
        string path,
        string correlationId,
        CancellationToken cancellationToken)
    {
        Uri uri = BuildRequestUri(baseUrl, path);
        return await SendAuthorizedAsync(
            uri, null, cancellationToken, HttpMethod.Get, null, correlationId).ConfigureAwait(false);
    }

    private static XboxPartnerPackageResult ReadPackageResult(string body, string? correlationId)
    {
        XboxPartnerPackage? package;
        try
        {
            package = JsonSerializer.Deserialize<XboxPartnerPackage>(body);
        }
        catch (JsonException exception)
        {
            throw new XboxPartnerResponseException(
                body, correlationId, new InvalidDataException("The package response was not valid JSON.", exception));
        }

        if (package is null || string.IsNullOrWhiteSpace(package.Id))
        {
            throw new XboxPartnerResponseException(
                body, correlationId, new InvalidDataException("The package response has no 'id'."));
        }

        return new XboxPartnerPackageResult(package, body, correlationId);
    }
}

/// <summary>A package and the exact JSON the gateway returned for it.</summary>
internal sealed record XboxPartnerPackageResult(XboxPartnerPackage Package, string RawResponse, string? CorrelationId);

/// <summary>Main's packages as one <c>{"packageSetId","packages"}</c> object, and the IDs that couldn't be read.</summary>
internal sealed record XboxPartnerPackageListing(
    string Json,
    string? PackageSetId,
    IReadOnlyList<string> MissingPackageIds);

/// <summary>
/// The package fields the CLI reads. Output keeps every field the gateway returns; these are only the
/// ones the CLI shows in a table.
/// </summary>
internal sealed record XboxPartnerPackage(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("platform")] string? Platform,
    [property: JsonPropertyName("processingState")] string? ProcessingState,
    [property: JsonPropertyName("isAvailableForPlaytest")] bool? IsAvailableForPlaytest,
    [property: JsonPropertyName("createdAt")] string? CreatedAt)
{
    /// <summary>The list output's array.</summary>
    public const string Collection = "packages";
}

/// <summary>The game's package sets. Main is the one the gateway names in <see cref="MainPackageSetId"/>.</summary>
internal sealed record XboxPartnerPackageSets(
    [property: JsonPropertyName("packageSets")] IReadOnlyList<XboxPartnerPackageSet>? PackageSets,
    [property: JsonPropertyName("mainPackageSetId")] string? MainPackageSetId)
{
    public XboxPartnerPackageSet? Main => MainPackageSetId is null
        ? null
        : PackageSets?.FirstOrDefault(set => string.Equals(set.Id, MainPackageSetId, StringComparison.Ordinal));
}

internal sealed record XboxPartnerPackageSet(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("marketGroups")] IReadOnlyList<XboxPartnerMarketGroup>? MarketGroups);

internal sealed record XboxPartnerMarketGroup(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("packageIds")] IReadOnlyList<string>? PackageIds);
