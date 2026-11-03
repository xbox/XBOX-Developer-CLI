// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XboxDevCLI.Services;

internal sealed partial class XboxPartnerClient
{
    private const long MaxAchievementImageBytes = 20L * 1024 * 1024;

    public async Task<XboxLiveAchievementImage> UploadAchievementImageAsync(
        long? sellerId,
        Guid scid,
        string filePath,
        CancellationToken cancellationToken,
        HttpClient? uploadClient = null,
        Action<string>? setPhase = null,
        Action<long>? reportBytes = null)
    {
        ValidateSellerId(sellerId);
        ValidateRequiredGuid(scid, nameof(scid));
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!string.Equals(Path.GetExtension(filePath), ".png", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("--file must have a .png filename.");
        }

        await using var file = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 8192,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length > MaxAchievementImageBytes)
        {
            throw new InvalidDataException("--file must not exceed 20 MiB (20,971,520 bytes).");
        }

        byte[] header = new byte[33];
        int count = await file.ReadAtLeastAsync(
            header, header.Length, throwOnEndOfStream: false,
            cancellationToken: cancellationToken);
        if (count != header.Length ||
            !PngHeader.TryReadDimensions(header, out uint width, out uint height) ||
            width != 1920 || height != 1080)
        {
            throw new InvalidDataException("--file must contain a PNG image with exactly 1920x1080 pixels.");
        }

        using var ownedClient = uploadClient is null
            ? AchievementImageBlobUploader.CreateClient()
            : null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            setPhase?.Invoke("Initializing image");
            string initializationResponse = await SendAsync(
                HttpMethod.Post,
                "xboxpartner/v1/xboxlive/achievement-images/upload/initialize",
                JsonContent.Create(new XboxLiveImageUploadInitializeRequest(
                    sellerId, scid, Path.GetFileName(filePath), file.Length)),
                cancellationToken).ConfigureAwait(false);
            XboxLiveImageUploadInitialization initialization;
            try
            {
                initialization = JsonSerializer.Deserialize<XboxLiveImageUploadInitialization>(
                    initializationResponse) ?? throw new JsonException();
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException)
            {
                throw new InvalidDataException(
                    "The image upload initialization response was invalid.", ex);
            }

            if (initialization.XfusId == Guid.Empty ||
                string.IsNullOrWhiteSpace(initialization.UploadToken) ||
                !Uri.TryCreate(initialization.UploadUri, UriKind.Absolute, out Uri? uploadUri) ||
                initialization.ExpiresAt == default ||
                initialization.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                throw new InvalidDataException(
                    "The image upload initialization response was invalid or expired.");
            }

            file.Position = 0;
            setPhase?.Invoke("Uploading");
            reportBytes?.Invoke(0);
            await AchievementImageBlobUploader.UploadAsync(
                uploadClient ?? ownedClient!, uploadUri, file, reportBytes, cancellationToken)
                .ConfigureAwait(false);

            setPhase?.Invoke("Registering image");
            string? responseBody = null;
            bool registrationAttempted = false;
            try
            {
                responseBody = await SendAsync(
                    HttpMethod.Post,
                    "xboxpartner/v1/xboxlive/achievement-images/upload/register",
                    JsonContent.Create(new XboxLiveImageUploadRegisterRequest(
                        sellerId, scid, initialization.XfusId, initialization.UploadToken)),
                    cancellationToken,
                    () => registrationAttempted = true).ConfigureAwait(false);
                return ParseAchievementImage(responseBody);
            }
            catch (XboxPartnerApiException ex) when (ex.StatusCode ==
                System.Net.HttpStatusCode.Conflict &&
                ReadRegistrationCode(ex.ResponseBody) == "image_upload_restart_required" &&
                attempt == 0)
            {
                continue;
            }
            catch (XboxPartnerApiException ex) when ((int)ex.StatusCode is >= 400 and < 500 &&
                ex.StatusCode != System.Net.HttpStatusCode.RequestTimeout)
            {
                string? code = ReadRegistrationCode(ex.ResponseBody);
                string resolution = (code, ex.StatusCode) switch
                {
                    ("image_upload_png_verification_failed", System.Net.HttpStatusCode.BadRequest) =>
                        "The uploaded blob is not a valid PNG. Check --file and rerun upload.",
                    ("image_upload_restart_required", System.Net.HttpStatusCode.Conflict) =>
                        "Image upload session could not be registered. Rerun upload to start a new image.",
                    (_, System.Net.HttpStatusCode.Unauthorized) =>
                        "Image registration was not authorized. Run 'xbox auth login', then rerun upload.",
                    (_, System.Net.HttpStatusCode.Forbidden) =>
                        "Image registration was denied. Check access to the seller and SCID, then rerun upload.",
                    (_, System.Net.HttpStatusCode.TooManyRequests) =>
                        "Image registration was rate limited. Wait, then rerun upload.",
                    _ => "Image registration was rejected. Rerun upload to create a new image asset.",
                };
                throw new XboxPartnerImageRegistrationRejectedException(
                    ex, [initialization.UploadToken, initialization.UploadUri!], resolution);
            }
            catch (Exception ex) when (registrationAttempted &&
                (ex is not XboxPartnerApiException api ||
                 (int)api.StatusCode >= 500 ||
                 api.StatusCode == System.Net.HttpStatusCode.RequestTimeout))
            {
                throw new XboxPartnerUploadOutcomeException(
                    ex, TryReadAssetId(responseBody),
                    [initialization.UploadToken, initialization.UploadUri!]);
            }
        }

        throw new InvalidOperationException("Image registration did not complete.");
    }

    private static string? ReadRegistrationCode(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("code", out JsonElement code) &&
                code.ValueKind == JsonValueKind.String
                    ? code.GetString()
                    : null;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return null;
        }
    }

    private static Guid? TryReadAssetId(string? responseBody)
    {
        if (responseBody is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("assetId", out var assetId) &&
                assetId.ValueKind == JsonValueKind.String &&
                Guid.TryParse(assetId.GetString(), out Guid parsed) &&
                parsed != Guid.Empty
                    ? parsed
                    : null;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return null;
        }
    }

    public async Task<XboxLiveAchievementImage> GetAchievementImageAsync(
        long? sellerId,
        Guid scid,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        ValidateSellerId(sellerId);
        ValidateRequiredGuid(scid, nameof(scid));
        ValidateRequiredGuid(assetId, nameof(assetId));
        string response = await SendAsync(
            HttpMethod.Post,
            "xboxpartner/v1/xboxlive/achievement-image",
            JsonContent.Create(new XboxLiveAchievementImageRequest(sellerId, scid, assetId)),
            cancellationToken).ConfigureAwait(false);
        return ParseAchievementImage(response);
    }

    public async Task<XboxLiveAchievementImageCollection> ListAchievementImagesAsync(
        long? sellerId,
        Guid scid,
        CancellationToken cancellationToken,
        Action<int>? pageRead = null)
    {
        ValidateSellerId(sellerId);
        ValidateRequiredGuid(scid, nameof(scid));

        var images = new List<XboxLiveAchievementImage>();
        IReadOnlyList<string> rawImages = await ReadEveryPageAsync(
            "images",
            "achievement image list",
            token => (
                HttpMethod.Post,
                "xboxpartner/v1/xboxlive/achievement-images",
                JsonContent.Create(new XboxLiveAchievementImagesRequest(
                    sellerId,
                    scid,
                    token is null ? AchievementImagesPageSize : null,
                    token))),
            cancellationToken,
            readItem: rawImage => images.Add(ParseListedAchievementImage(rawImage)),
            pageRead: pageRead).ConfigureAwait(false);

        return new XboxLiveAchievementImageCollection(images, "[" + string.Join(',', rawImages) + "]");
    }

    private static XboxLiveAchievementImage ParseListedAchievementImage(string rawImage)
    {
        try
        {
            XboxLiveAchievementImage? image =
                JsonSerializer.Deserialize<XboxLiveAchievementImage>(rawImage);
            ValidateAchievementImage(image);
            return image!;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            throw new InvalidDataException(
                "The achievement image response was not valid JSON.", ex);
        }
    }

    private static XboxLiveAchievementImage ParseAchievementImage(string response)
    {
        try
        {
            using var document = JsonDocument.Parse(response);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    "The achievement image response must contain an assetId.");
            }

            XboxLiveAchievementImage? image =
                document.RootElement.Deserialize<XboxLiveAchievementImage>();
            ValidateAchievementImage(image);
            return image! with { RawResponse = response };
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            throw new InvalidDataException(
                "The achievement image response was not valid JSON.", ex);
        }
    }

    private static void ValidateAchievementImage(XboxLiveAchievementImage? image)
    {
        if (image is null || image.AssetId == Guid.Empty)
        {
            throw new InvalidDataException(
                "The achievement image response must contain an assetId.");
        }

        if (image.Scid == Guid.Empty)
        {
            throw new InvalidDataException(
                "The achievement image response must contain a scid.");
        }
    }

    public async Task<string> GetXboxLiveConfigurationsAsync(
        long? sellerId,
        Guid scid,
        CancellationToken cancellationToken,
        Action<int>? pageRead = null)
    {
        ValidateSellerId(sellerId);
        ValidateRequiredGuid(scid, nameof(scid));
        IReadOnlyList<string> configurations = await ReadEveryPageAsync(
            "configurations",
            "configuration list",
            token => (
                HttpMethod.Post,
                "xboxpartner/v1/xboxlive/configurations",
                JsonContent.Create(new XboxLiveConfigurationsRequest(
                    sellerId,
                    scid,
                    token is null ? ListPageSize : null,
                    token))),
            cancellationToken,
            pageRead: pageRead).ConfigureAwait(false);
        return "[" + string.Join(',', configurations) + "]";
    }

    public async Task<string> GetXboxLiveConfigurationAsync(
        long? sellerId,
        Guid scid,
        string sandbox,
        CancellationToken cancellationToken)
    {
        ValidateSellerId(sellerId);
        ValidateRequiredGuid(scid, nameof(scid));
        string validatedSandbox = ValidateSandbox(sandbox, nameof(sandbox));
        return await SendAsync(
            HttpMethod.Post,
            "xboxpartner/v1/xboxlive/configurations/get",
            JsonContent.Create(new XboxLiveConfigurationRequest(
                sellerId,
                scid,
                validatedSandbox)),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> GetSchemaTypesAsync(CancellationToken cancellationToken) =>
        await SendAsync(
            HttpMethod.Get,
            "xboxpartner/v1/xboxlive/schemas",
            content: null,
            cancellationToken).ConfigureAwait(false);

    public async Task<string> GetSchemaVersionsAsync(
        string schemaType,
        CancellationToken cancellationToken)
    {
        string validatedType = ValidateSchemaType(schemaType);
        return await SendAsync(
            HttpMethod.Get,
            $"xboxpartner/v1/xboxlive/schemas/{Uri.EscapeDataString(validatedType)}",
            content: null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<byte[]> GetSchemaAsync(
        string schemaType,
        int version,
        CancellationToken cancellationToken)
    {
        string validatedType = ValidateSchemaType(schemaType);
        if (version <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version), "Schema version must be positive.");
        }

        return await SendBytesAsync(
            $"xboxpartner/v1/xboxlive/schemas/{Uri.EscapeDataString(validatedType)}/{version}",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<XboxPartnerDocumentArchive> GetXboxLiveDocumentsAsync(
        long? sellerId,
        XboxLiveGetDocumentsRequest request,
        CancellationToken cancellationToken)
    {
        ValidateSellerId(sellerId);
        ArgumentNullException.ThrowIfNull(request);
        using HttpResponseMessage response = await SendResponseAsync(
            HttpMethod.Post,
            "xboxpartner/v1/xboxlive/documents",
            JsonContent.Create(new XboxLiveGetDocumentsApiRequest(
                sellerId,
                request.DocumentType,
                request.Scid,
                request.Sandbox,
                request.View,
                request.Version,
                request.Documents)),
            "application/zip",
            cancellationToken).ConfigureAwait(false);
        using var bodyTimeout = CreateResponseBodyTimeout(cancellationToken);
        byte[] content = await ReadResponseBytesWithLimitAsync(
            response.Content,
            _maxDocumentArchiveBytes,
            "XboxPartner:MaxDocumentArchiveBytes",
            bodyTimeout.Token).ConfigureAwait(false);
        string? eTag = response.Headers.ETag?.ToString();
        string? versionValue = response.Headers.TryGetValues("X-Version", out var versions)
            ? versions.FirstOrDefault()
            : null;
        string? view = response.Headers.TryGetValues("X-Document-View", out var views)
            ? views.FirstOrDefault()
            : null;
        if (string.IsNullOrWhiteSpace(eTag) ||
            !ulong.TryParse(
                versionValue,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out ulong version) ||
            version == 0 ||
            string.IsNullOrWhiteSpace(view))
        {
            throw new InvalidDataException(
                "The document response did not contain valid metadata.");
        }

        return new XboxPartnerDocumentArchive(content, eTag, version, view);
    }

    public async Task<string> CommitXboxLiveDocumentsAsync(
        long? sellerId,
        XboxLiveCommitDocumentsRequest request,
        CancellationToken cancellationToken)
    {
        ValidateSellerId(sellerId);
        ArgumentNullException.ThrowIfNull(request);
        return await SendAsync(
            HttpMethod.Post,
            "xboxpartner/v1/xboxlive/documents/commit",
            JsonContent.Create(new XboxLiveCommitDocumentsApiRequest(
                sellerId,
                request.DocumentType,
                request.Scid,
                request.Sandbox,
                request.ValidateOnly,
                request.Force,
                request.ETag,
                request.Message,
                request.Documents)),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> PublishXboxLiveConfigurationAsync(
        long? sellerId,
        XboxLivePublishRequest request,
        CancellationToken cancellationToken)
    {
        ValidateSellerId(sellerId);
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequiredGuid(request.Scid, nameof(request.Scid));
        ValidateSandbox(request.Sandbox, nameof(request.Sandbox));
        ValidateSandbox(request.SourceSandbox, nameof(request.SourceSandbox));
        if (request.ConfigSetVersion == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.ConfigSetVersion),
                "Config set version must be positive when provided.");
        }

        return await SendAsync(
            HttpMethod.Post,
            "xboxpartner/v1/xboxlive/publish",
            JsonContent.Create(new XboxLivePublishApiRequest(
                sellerId,
                request.Scid,
                request.Sandbox,
                request.SourceSandbox,
                request.ValidateOnly,
                request.ConfigSetVersion,
                request.Republish)),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> GetXboxLivePublishStatusAsync(
        long? sellerId,
        Guid scid,
        string sandbox,
        CancellationToken cancellationToken)
    {
        ValidateSellerId(sellerId);
        ValidateRequiredGuid(scid, nameof(scid));
        ValidateSandbox(sandbox, nameof(sandbox));
        return await SendAsync(
            HttpMethod.Post,
            "xboxpartner/v1/xboxlive/publish/status",
            JsonContent.Create(new XboxLivePublishStatusRequest(
                sellerId,
                scid,
                sandbox)),
            cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateSellerId(long? sellerId)
    {
        if (sellerId is <= 0)
        {
            throw new ArgumentException(
                "Seller ID must be a positive numeric value.",
                nameof(sellerId));
        }
    }

    private static string ValidateSchemaType(string schemaType)
    {
        string validatedType = schemaType?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(validatedType) ||
            validatedType is "." or "..")
        {
            throw new ArgumentException(
                "Schema type must be a non-empty path segment other than '.' or '..'.",
                nameof(schemaType));
        }

        return validatedType;
    }

    private static void ValidateRequiredGuid(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A non-empty GUID is required.", parameterName);
        }
    }

    private static string ValidateSandbox(string sandbox, string parameterName)
    {
        string value = sandbox?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException(
                "Sandbox values must be non-empty and cannot contain whitespace.",
                parameterName);
        }

        return value;
    }
}

internal sealed record XboxLiveAchievementImageRequest(
    long? SellerId,
    Guid Scid,
    Guid AssetId);

internal sealed record XboxLiveAchievementImagesRequest(
    long? SellerId,
    Guid Scid,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? PageSize,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ContinuationToken);

internal sealed record XboxLiveImageUploadInitializeRequest(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? SellerId,
    Guid Scid,
    string FileName,
    long SizeBytes);

internal sealed record XboxLiveImageUploadRegisterRequest(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? SellerId,
    Guid Scid,
    Guid XfusId,
    string UploadToken);

internal sealed record XboxLiveImageUploadInitialization
{
    [JsonPropertyName("uploadUri")]
    public string? UploadUri { get; init; }

    [JsonPropertyName("xfusId")]
    public Guid XfusId { get; init; }

    [JsonPropertyName("uploadToken")]
    public string? UploadToken { get; init; }

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset ExpiresAt { get; init; }
}

internal interface IXboxLiveAchievementImageResult
{
    string RawResponse { get; }
}

internal sealed record XboxLiveAchievementImage : IXboxLiveAchievementImageResult
{
    [JsonPropertyName("assetId")]
    public Guid AssetId { get; init; }

    [JsonPropertyName("scid")]
    public Guid Scid { get; init; }

    [JsonPropertyName("cdnUrl")]
    public string? CdnUrl { get; init; }

    [JsonPropertyName("thumbnailCdnUrl")]
    public string? ThumbnailCdnUrl { get; init; }

    [JsonPropertyName("widthInPixels")]
    public int? WidthInPixels { get; init; }

    [JsonPropertyName("heightInPixels")]
    public int? HeightInPixels { get; init; }

    [JsonPropertyName("imageType")]
    public string? ImageType { get; init; }

    [JsonPropertyName("isPublic")]
    public bool? IsPublic { get; init; }

    [JsonIgnore]
    public string RawResponse { get; init; } = string.Empty;
}

internal sealed record XboxLiveAchievementImageCollection(
    IReadOnlyList<XboxLiveAchievementImage> Images,
    string RawResponse) : IXboxLiveAchievementImageResult;

internal sealed class XboxPartnerUploadOutcomeException(
    Exception innerException,
    Guid? assetId,
    IReadOnlyList<string>? sensitiveValues = null)
    : Exception(innerException.Message, innerException)
{
    public Guid? AssetId { get; } = assetId;
    public IReadOnlyList<string> SensitiveValues { get; } = sensitiveValues ?? [];
}

internal sealed class XboxPartnerImageRegistrationRejectedException(
    XboxPartnerApiException innerException,
    IReadOnlyList<string> sensitiveValues,
    string resolution)
    : Exception("Image registration was rejected.", innerException)
{
    public IReadOnlyList<string> SensitiveValues { get; } = sensitiveValues;
    public string Resolution { get; } = resolution;
}

public sealed record XboxLiveConfigurationsRequest(
    long? SellerId,
    Guid Scid,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? PageSize = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ContinuationToken = null);

public sealed record XboxLiveConfigurationRequest(
    long? SellerId,
    Guid Scid,
    string Sandbox);

public sealed record XboxLiveConfigurationSummary(
    string Sandbox,
    bool HasWorkingConfiguration,
    ulong? WorkingVersion,
    DateTimeOffset? WorkingCommitTime,
    bool HasPublishedConfiguration,
    ulong? PublishedVersion,
    DateTimeOffset? PublishedCommitTime,
    bool HasUnpublishedChanges);

public sealed record XboxLiveGetDocumentsRequest(
    string DocumentType,
    Guid? Scid,
    string? Sandbox,
    string View,
    ulong? Version,
    IReadOnlyList<string> Documents);

public sealed record XboxPartnerDocumentArchive(
    byte[] Content,
    string ETag,
    ulong Version,
    string View);

public sealed record XboxDocumentUpload(string Name, byte[] Content);

public sealed record XboxLiveCommitDocumentsRequest(
    string DocumentType,
    Guid? Scid,
    string? Sandbox,
    bool ValidateOnly,
    bool Force,
    string? ETag,
    string? Message,
    IReadOnlyList<XboxDocumentUpload> Documents);

public sealed record XboxLivePublishRequest(
    Guid Scid,
    string Sandbox,
    string SourceSandbox,
    bool ValidateOnly,
    ulong? ConfigSetVersion,
    bool Republish = false);

internal sealed record XboxLivePublishApiRequest(
    long? SellerId,
    Guid Scid,
    string Sandbox,
    string SourceSandbox,
    bool ValidateOnly,
    ulong? ConfigSetVersion,
    bool Republish);

internal sealed record XboxLivePublishStatusRequest(
    long? SellerId,
    Guid Scid,
    string Sandbox);

internal sealed record XboxLiveGetDocumentsApiRequest(
    long? SellerId,
    string DocumentType,
    Guid? Scid,
    string? Sandbox,
    string View,
    ulong? Version,
    IReadOnlyList<string> Documents);

internal sealed record XboxLiveCommitDocumentsApiRequest(
    long? SellerId,
    string DocumentType,
    Guid? Scid,
    string? Sandbox,
    bool ValidateOnly,
    bool Force,
    string? ETag,
    string? Message,
    IReadOnlyList<XboxDocumentUpload> Documents);

internal sealed record XboxLiveCommitResponse
{
    [JsonPropertyName("ValidationInfo")]
    public JsonElement ValidationInfo { get; init; }

    [JsonPropertyName("CanCommit")]
    public bool? CanCommit { get; init; }

    [JsonPropertyName("Committed")]
    public bool? Committed { get; init; }

    [JsonPropertyName("ETag")]
    public string? ETag { get; init; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; init; }
}

internal sealed record XboxLivePublishResponse
{
    [JsonPropertyName("Status")]
    public string? Status { get; init; }

    /// <summary>XORC's working version of the source, which it sends as a string.</summary>
    [JsonPropertyName("SourceVersion")]
    public JsonElement? SourceVersion { get; init; }

    [JsonPropertyName("sourceHasUnpublishedChanges")]
    public bool? SourceHasUnpublishedChanges { get; init; }

    [JsonPropertyName("sourceLiveVersion")]
    public ulong? SourceLiveVersion { get; init; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; init; }

    [JsonIgnore]
    public string? SourceVersionText => SourceVersion switch
    {
        { ValueKind: JsonValueKind.String } value when !string.IsNullOrWhiteSpace(value.GetString()) =>
            value.GetString(),
        { ValueKind: JsonValueKind.Number } value => value.GetRawText(),
        _ => null,
    };
}
