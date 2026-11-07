// Copyright (C) Microsoft Corporation. All rights reserved.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Net.Http.Headers;
using System.Text.Json;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Output;
using XboxDevCLI.Services;

namespace XboxDevCLI.Commands;

internal sealed record AchievementImageDownloadResult(
    Guid AssetId,
    string Destination,
    string ImagePath,
    string ThumbnailPath,
    long ImageBytes,
    long ThumbnailBytes);

internal sealed record AchievementImageDownloadPolicy(
    TimeSpan Timeout,
    long MaxBytes)
{
    public static AchievementImageDownloadPolicy Default { get; } = new(
        TimeSpan.FromMinutes(2),
        20L * 1024 * 1024);
}

internal static class DownloadAchievementImagesCommandFactory
{
    private const int BufferSize = 81920;
    private const int MaxRedirects = 5;
    private static readonly TableView DownloadTable = new(
        "downloads",
        [
            new("ASSET ID", JsonNamingPolicy.CamelCase.ConvertName(nameof(AchievementImageDownloadResult.AssetId)), 38),
            new("DESTINATION", JsonNamingPolicy.CamelCase.ConvertName(nameof(AchievementImageDownloadResult.Destination)), 70),
        ]);
    private static readonly JsonSerializerOptions PrettyJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static Command Create()
    {
        var sellerOption = new XdtsSellerOption();
        Option<Guid> scidOption = AchievementImageCommandFactory.CreateScidOption();
        var assetIdOption = new Option<Guid?>("--asset-id")
        {
            Description = "Download one achievement image by asset ID. No default.",
        };
        assetIdOption.Validators.Add(result =>
        {
            if (result.Tokens.Count > 0 &&
                (!Guid.TryParse(result.Tokens[0].Value, out Guid value) ||
                 value == Guid.Empty))
            {
                result.AddError("--asset-id must be a non-empty GUID.");
            }
        });
        var allOption = new Option<bool>("--all")
        {
            Description =
                "Download every achievement image returned for the SCID. False by default.",
        };
        var destinationOption = new Option<string>("--dest")
        {
            Description =
                "Required destination directory. Each asset is saved under an AssetId subfolder.",
            Required = true,
        };
        destinationOption.Validators.Add(result =>
        {
            if (result.Tokens.Count > 0 &&
                string.IsNullOrWhiteSpace(result.Tokens[0].Value))
            {
                result.AddError("--dest must contain a directory path.");
            }
        });

        var command = new Command(
            "download",
            "Download achievement images and thumbnails");
        command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.ReadOnly,
            Scope: "One XBOX title; --scid identifies its service configuration.",
            Output:
                "A JSON download manifest by default. Table output shows asset ID and " +
                "destination folder. " + ListProgress.HelpOutput,
            Examples:
            [
                "xbox xbl achievement-image download --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --asset-id 11111111-2222-3333-4444-555555555555 --dest achievement-images",
                "xbox xbl achievement-image download --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --all --dest achievement-images --output table",
            ],
            LearnMore:
            [
                "Downloads image and thumbnail CDN files into separate asset ID folders.",
            ],
            SafetyDetails:
                "Writes downloaded image files under the local --dest directory, replacing an " +
                "existing file only when its CDN filename matches.",
            Table: DownloadTable));
        sellerOption.AddTo(command);
        command.Options.Add(scidOption);
        command.Options.Add(assetIdOption);
        command.Options.Add(allOption);
        command.Options.Add(destinationOption);
        command.Validators.Add(result =>
        {
            bool all = result.GetValue(allOption);
            bool hasAssetId = result.GetResult(assetIdOption) is { Tokens.Count: > 0 };
            if (all == hasAssetId)
            {
                result.AddError("Specify exactly one of --asset-id or --all.");
            }
        });
        command.SetAction(async (ParseResult result, CancellationToken cancellationToken) =>
        {
            var output = OutputOptions.From(result);
            bool verbose = result.GetValue(Program.VerboseOption);
            try
            {
                Guid scid = result.GetValue(scidOption);
                Guid? assetId = result.GetValue(assetIdOption);
                bool all = result.GetValue(allOption);
                string destination = result.GetValue(destinationOption)!;
                var configuration = Program.GetConfiguration(result);
                using var client = Program.XboxPartnerClientFactory is null
                    ? new XboxPartnerClient(
                        configuration,
                        authenticationService: Program.GetAuthentication(result))
                    : Program.XboxPartnerClientFactory(configuration);
                using HttpClient downloadClient =
                    Program.AchievementImageDownloadClientFactory?.Invoke()
                    ?? CreateDownloadClient();
                string baseUrl = XboxPartnerCommandSupport.ResolveBaseUrl(configuration);
                long? sellerId = await sellerOption.GetSellerIdAsync(
                    result,
                    configuration,
                    client,
                    baseUrl,
                    cancellationToken);
                Program.WriteVerbose(
                    verbose,
                    () => all
                        ? $"Download all achievement images for SCID {scid:D}"
                        : $"Download achievement image {assetId:D} for SCID {scid:D}");
                return await RunAsync(
                    client,
                    downloadClient,
                    sellerId,
                    scid,
                    assetId,
                    all,
                    destination,
                    output,
                    Console.Out,
                    Console.Error,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Request failed: {ex.Message}");
                return 1;
            }
        });
        return command;
    }

    internal static async Task<int> RunAsync(
        XboxPartnerClient client,
        HttpClient downloadClient,
        long? sellerId,
        Guid scid,
        Guid? assetId,
        bool all,
        string destination,
        OutputOptions output,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken,
        AchievementImageDownloadPolicy? policy = null)
    {
        try
        {
            policy ??= AchievementImageDownloadPolicy.Default;
            ValidatePolicy(policy);
            IReadOnlyList<XboxLiveAchievementImage> images;
            if (all)
            {
                using var listProgress = ListProgress.Start("Reading achievement images", stderr);
                images = (await client.ListAchievementImagesAsync(
                    sellerId, scid, cancellationToken, listProgress.PageRead).ConfigureAwait(false)).Images;
            }
            else
            {
                Guid requestedAssetId = assetId ?? throw new ArgumentException(
                    "assetId is required when all is false.",
                    nameof(assetId));
                XboxLiveAchievementImage image;
                using (RequestProgress.Start("Getting achievement image", stderr))
                {
                    image = await client.GetAchievementImageAsync(
                        sellerId, scid, requestedAssetId, cancellationToken).ConfigureAwait(false);
                }

                if (image.AssetId != requestedAssetId)
                {
                    throw new InvalidDataException(
                        "The achievement image response did not match the requested " +
                        $"asset ID {requestedAssetId:D}.");
                }

                images = [image];
            }

            var files = ValidateImages(images, scid);
            string root = SafeOutputDirectory.Create(destination);
            var downloads = new List<AchievementImageDownloadResult>(files.Count);
            var warnings = new List<string>();
            try
            {
                using var progress = RequestProgress.Start("Downloading achievement images", stderr);
                foreach (ImageFiles file in files)
                {
                    progress.Activity = FormattableString.Invariant(
                        $"Downloading achievement image {downloads.Count + 1} of {files.Count}");
                    downloads.Add(await DownloadImagePairAsync(
                        downloadClient,
                        file,
                        root,
                        warnings,
                        policy,
                        cancellationToken).ConfigureAwait(false));
                }
            }
            finally
            {
                // Written once the progress line is gone, so they don't land on it.
                foreach (string warning in warnings)
                {
                    stderr.WriteLine(warning);
                }
            }

            WriteOutput(downloads, all, output, stdout);
            return 0;
        }
        catch (XboxPartnerApiException ex)
        {
            XboxPartnerCommandSupport.WriteCorrelationId(ex.CorrelationId, stderr);
            stderr.WriteLine(XboxPartnerCommandErrorFormatter.Format(ex, output.IsCompact));
            return 1;
        }
        catch (XboxPartnerResponseException ex)
        {
            XboxPartnerCommandSupport.WriteCorrelationId(ex.CorrelationId, stderr);
            stderr.WriteLine($"Request failed: {ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"Request failed: {ex.Message}");
            return 1;
        }
    }

    private sealed record ImageFiles(
        Guid AssetId,
        Uri ImageUri,
        string ImageFileName,
        Uri ThumbnailUri,
        string ThumbnailFileName);

    private static HttpClient CreateDownloadClient() =>
        new(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = MaxRedirects,
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

    private static List<ImageFiles> ValidateImages(
        IReadOnlyList<XboxLiveAchievementImage> images,
        Guid expectedScid)
    {
        var assetIds = new HashSet<Guid>();
        var files = new List<ImageFiles>(images.Count);
        foreach (XboxLiveAchievementImage image in images)
        {
            if (image.AssetId == Guid.Empty ||
                image.Scid != expectedScid ||
                !assetIds.Add(image.AssetId))
            {
                throw new InvalidDataException(
                    "The achievement image response contained an invalid SCID, " +
                    "asset ID, or duplicate asset ID.");
            }

            Uri imageUri = ValidateDownloadUri(image.CdnUrl, image.AssetId, "image");
            Uri thumbnailUri = ValidateDownloadUri(
                image.ThumbnailCdnUrl,
                image.AssetId,
                "thumbnail");
            string imageFileName = GetDownloadFileName(imageUri, image.AssetId, "image");
            string thumbnailFileName = GetDownloadFileName(
                thumbnailUri,
                image.AssetId,
                "thumbnail");
            if (string.Equals(imageFileName, thumbnailFileName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"The achievement image and thumbnail CDN URLs for asset {image.AssetId:D} " +
                    $"resolve to the same filename '{imageFileName}'.");
            }

            files.Add(new ImageFiles(
                image.AssetId, imageUri, imageFileName, thumbnailUri, thumbnailFileName));
        }

        return files;
    }

    private static async Task<AchievementImageDownloadResult> DownloadImagePairAsync(
        HttpClient client,
        ImageFiles files,
        string root,
        List<string> warnings,
        AchievementImageDownloadPolicy policy,
        CancellationToken cancellationToken)
    {
        string assetName = files.AssetId.ToString("D");
        string stagingDirectory = Path.Combine(
            root,
            $".xboxdevcli-achievement-image-{assetName}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);
        try
        {
            string stagedImage = Path.Combine(stagingDirectory, "image-download");
            string stagedThumbnail = Path.Combine(stagingDirectory, "thumbnail-download");
            long imageBytes = await DownloadFileAsync(
                client, files.ImageUri, stagedImage, files.AssetId, "image", policy,
                cancellationToken).ConfigureAwait(false);
            long thumbnailBytes = await DownloadFileAsync(
                client, files.ThumbnailUri, stagedThumbnail, files.AssetId, "thumbnail", policy,
                cancellationToken).ConfigureAwait(false);

            // Both transfers finish before either file is replaced.
            string finalDirectory = SafeOutputDirectory.Create(Path.Combine(root, assetName));
            string imagePath = Path.Combine(finalDirectory, files.ImageFileName);
            string thumbnailPath = Path.Combine(finalDirectory, files.ThumbnailFileName);
            File.Move(stagedImage, imagePath, overwrite: true);
            File.Move(stagedThumbnail, thumbnailPath, overwrite: true);
            return new AchievementImageDownloadResult(
                files.AssetId,
                finalDirectory,
                imagePath,
                thumbnailPath,
                imageBytes,
                thumbnailBytes);
        }
        finally
        {
            try
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add(
                    $"Warning: Could not remove temporary achievement image directory " +
                    $"'{stagingDirectory}': {ex.Message}");
            }
        }
    }

    private static async Task<long> DownloadFileAsync(
        HttpClient client,
        Uri uri,
        string path,
        Guid assetId,
        string kind,
        AchievementImageDownloadPolicy policy,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(policy.Timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));
            using HttpResponseMessage response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            // A redirect may only land where the original URL was allowed to point.
            _ = ValidateDownloadUri(
                (response.RequestMessage?.RequestUri ?? uri).AbsoluteUri, assetId, kind);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"The achievement {kind} download for asset {assetId:D} returned HTTP " +
                    $"{(int)response.StatusCode}.");
            }

            if (response.Content.Headers.ContentLength > policy.MaxBytes)
            {
                throw new InvalidDataException(
                    $"The achievement {kind} download for asset {assetId:D} exceeded " +
                    $"{FormatByteLimit(policy.MaxBytes)}.");
            }

            await using Stream source = await response.Content
                .ReadAsStreamAsync(timeout.Token)
                .ConfigureAwait(false);
            long total = 0;
            await using var target = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                useAsync: true);
            var buffer = new byte[BufferSize];
            int read;
            while ((read = await source.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > policy.MaxBytes)
                {
                    throw new InvalidDataException(
                        $"The achievement {kind} download for asset {assetId:D} exceeded " +
                        $"{FormatByteLimit(policy.MaxBytes)}.");
                }

                await target
                    .WriteAsync(buffer.AsMemory(0, read), timeout.Token)
                    .ConfigureAwait(false);
            }

            return total;
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested &&
            timeout.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The achievement {kind} download for asset {assetId:D} timed out.");
        }
    }

    private static Uri ValidateDownloadUri(string? value, Guid assetId, string kind)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !IsAllowedDownloadSchemeAndHost(uri))
        {
            throw new InvalidDataException(
                $"The achievement {kind} CDN URL for asset {assetId:D} must be absolute HTTPS, " +
                "or HTTP on the xboxlive.com CDN.");
        }

        return uri;
    }

    private static void ValidatePolicy(AchievementImageDownloadPolicy policy)
    {
        if (policy.Timeout <= TimeSpan.Zero || policy.MaxBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(policy),
                "Achievement image download policy values must be positive.");
        }
    }

    private static string FormatByteLimit(long maxBytes) =>
        maxBytes == AchievementImageDownloadPolicy.Default.MaxBytes
            ? "20 MiB"
            : $"{maxBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)} bytes";

    private static bool IsAllowedDownloadSchemeAndHost(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps ||
        (uri.Scheme == Uri.UriSchemeHttp &&
         uri.IsDefaultPort &&
         (uri.Host.Equals("xboxlive.com", StringComparison.OrdinalIgnoreCase) ||
          uri.Host.EndsWith(".xboxlive.com", StringComparison.OrdinalIgnoreCase)));

    private static string GetDownloadFileName(Uri uri, Guid assetId, string kind)
    {
        string fileName = AchievementImageCdnFileName.FromUri(uri) ??
            throw new InvalidDataException(
                $"The achievement {kind} CDN URL for asset {assetId:D} does not contain a valid filename.");
        return Path.GetExtension(fileName).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg"
            ? fileName
            : throw new InvalidDataException(
                $"The achievement {kind} CDN filename for asset {assetId:D} must use a .png, " +
                ".jpg, or .jpeg extension.");
    }

    private static void WriteOutput(
        IReadOnlyList<AchievementImageDownloadResult> downloads,
        bool all,
        OutputOptions output,
        TextWriter stdout)
    {
        if (output.TableView is not null)
        {
            stdout.WriteLine(XboxPartnerTableFormatter.FormatRows(
                output.TableView,
                downloads.Select(download => new[]
                {
                    download.AssetId.ToString("D"),
                    download.Destination,
                })));
            return;
        }

        JsonSerializerOptions options = output.IsCompact
            ? CompactJsonOptions
            : PrettyJsonOptions;
        stdout.WriteLine(all
            ? JsonSerializer.Serialize(downloads, options)
            : JsonSerializer.Serialize(downloads.Single(), options));
    }
}
