// Copyright (C) Microsoft Corporation. All rights reserved.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text;
using System.Text.Json;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Output;
using XboxDevCLI.Services;

namespace XboxDevCLI.Commands;

internal static class AchievementImageCommandFactory
{
    private const string ImageRequirements =
        "Image requirements: .png filename, PNG image, exactly 1920x1080 pixels, at most 20 MiB (20,971,520 bytes).";
    private static readonly IReadOnlyList<TableColumn> ImageColumns =
    [
        new("ASSET ID", JsonNamingPolicy.CamelCase.ConvertName(nameof(XboxLiveAchievementImage.AssetId)), 38),
        new("CDN FILENAME", JsonNamingPolicy.CamelCase.ConvertName(nameof(XboxLiveAchievementImage.CdnUrl)), 36,
            Transform: url => AchievementImageCdnFileName.FromUrl(url) ?? "-"),
        new("WIDTH", JsonNamingPolicy.CamelCase.ConvertName(nameof(XboxLiveAchievementImage.WidthInPixels)), 8),
        new("HEIGHT", JsonNamingPolicy.CamelCase.ConvertName(nameof(XboxLiveAchievementImage.HeightInPixels)), 8),
        new("IMAGE TYPE", JsonNamingPolicy.CamelCase.ConvertName(nameof(XboxLiveAchievementImage.ImageType)), 12),
        new("PUBLIC", JsonNamingPolicy.CamelCase.ConvertName(nameof(XboxLiveAchievementImage.IsPublic)), 8),
    ];

    private static readonly TableView ImageRecordTable = TableView.Record(ImageColumns);
    // `list` prints a bare array; the collection name binds the view to the typed result.
    private static readonly TableView ImageListTable = new(
        JsonNamingPolicy.CamelCase.ConvertName(nameof(XboxLiveAchievementImageCollection.Images)),
        ImageColumns);

    public static Command Create()
    {
        var image = new Command("achievement-image", "Upload, get, list, and download registered achievement images");
        image.Subcommands.Add(CreateUpload());
        image.Subcommands.Add(CreateGet());
        image.Subcommands.Add(CreateList());
        image.Subcommands.Add(DownloadAchievementImagesCommandFactory.Create());
        return image;
    }

    private static Command CreateUpload()
    {
        var sellerOption = new XdtsSellerOption();
        var scidOption = CreateScidOption();
        var fileOption = new Option<string>("--file")
        {
            Description = "Path to a PNG with a .png filename, exactly 1920x1080 pixels and at most 20 MiB.",
            Required = true,
        };
        var command = new Command("upload", "Upload and register an achievement image");
        command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.RemoteMutation,
            Scope: "An XBOX Live title; --scid identifies its service configuration, and --seller-id selects its authorized seller.",
            Output: "The registered image object as JSON (default), including iconImageId for achievements2017.xml, or a one-row table.",
            Examples:
            [
                "xbox xbl achievement-image upload --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --file achievement-1920x1080.png --seller-id 1234567",
            ],
            SafetyDetails: "Registers a new image in production through a direct Blob upload. The CLI restarts upload once only when the backend service requires a new upload session; it never retries registration after a timeout or temporary failure. If the outcome is unknown, inspect registered metadata and CDN images by running the printed read-only get or list command, and reuse an asset ID if you can identify your upload. Otherwise retry the upload.",
            Table: ImageRecordTable));
        sellerOption.AddTo(command);
        command.Options.Add(scidOption);
        command.Options.Add(fileOption);
        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
            await InvokeAsync(
                parseResult,
                sellerOption,
                scidOption,
                (client, sellerId, scid, token) =>
                    UploadAsync(client, sellerId, scid, parseResult.GetValue(fileOption)!, token),
                action: "upload",
                cancellationToken).ConfigureAwait(false));
        return command;
    }

    private static async Task<XboxLiveAchievementImage> UploadAsync(
        XboxPartnerClient client,
        long? sellerId,
        Guid scid,
        string path,
        CancellationToken cancellationToken)
    {
        using var progress = new AchievementImageUploadProgress(
            Console.Error,
            Environment.UserInteractive && !Console.IsErrorRedirected,
            Path.GetFileName(path),
            new FileInfo(path).Length);
        using var blobClient = Program.AchievementImageUploadClientFactory?.Invoke()
            ?? AchievementImageBlobUploader.CreateClient();
        return await client.UploadAchievementImageAsync(
            sellerId, scid, path, cancellationToken, blobClient,
            progress.SetPhase, progress.ReportBytes).ConfigureAwait(false);
    }

    private static Command CreateGet()
    {
        var sellerOption = new XdtsSellerOption();
        var scidOption = CreateScidOption();
        var assetIdOption = new Option<Guid>("--asset-id")
        {
            Description = "Asset ID returned by achievement-image upload.",
            Required = true,
        };
        assetIdOption.Validators.Add(result =>
        {
            if (result.Tokens.Count > 0 &&
                (!Guid.TryParse(result.Tokens[0].Value, out Guid value) || value == Guid.Empty))
            {
                result.AddError("--asset-id must be a non-empty GUID.");
            }
        });
        var command = new Command("get", "Get an already registered achievement image");
        command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.ReadOnly,
            Scope: "One image in an XBOX Live title; --scid identifies its service configuration.",
            Output: "The registered image object as JSON (default), including iconImageId for achievements2017.xml, or a one-row table.",
            Examples:
            [
                "xbox xbl achievement-image get --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --asset-id 11111111-2222-3333-4444-555555555555 --seller-id 1234567",
            ],
            LearnMore:
            [
                "Use the asset ID returned by upload or list to verify a registered image.",
            ],
            Table: ImageRecordTable));
        sellerOption.AddTo(command);
        command.Options.Add(scidOption);
        command.Options.Add(assetIdOption);
        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
            await InvokeAsync(
                parseResult,
                sellerOption,
                scidOption,
                (client, sellerId, scid, token) =>
                    client.GetAchievementImageAsync(
                        sellerId, scid, parseResult.GetValue(assetIdOption)!, token),
                action: "get",
                cancellationToken).ConfigureAwait(false));
        return command;
    }

    private static Command CreateList()
    {
        var sellerOption = new XdtsSellerOption();
        var scidOption = CreateScidOption();
        var command = new Command("list", "List registered achievement images for a SCID");
        command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.ReadOnly,
            Scope: "Registered images in an XBOX Live title; --scid identifies its service configuration.",
            Output: "A JSON array of registered images with iconImageId for achievements2017.xml (default), or a table.",
            Examples:
            [
                "xbox xbl achievement-image list --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --seller-id 1234567 --output json-compact",
            ],
            LearnMore:
            [
                "Find image asset IDs registered for this title.",
            ],
            Table: ImageListTable));
        sellerOption.AddTo(command);
        command.Options.Add(scidOption);
        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
            await InvokeAsync(
                parseResult,
                sellerOption,
                scidOption,
                (client, sellerId, scid, token) =>
                    client.ListAchievementImagesAsync(sellerId, scid, token),
                action: "list",
                cancellationToken).ConfigureAwait(false));
        return command;
    }

    internal static Option<Guid> CreateScidOption()
    {
        var option = new Option<Guid>("--scid")
        {
            Description = "Service configuration ID of the XBOX Live title. No default.",
            Required = true,
        };
        option.Validators.Add(result =>
        {
            if (result.Tokens.Count > 0 &&
                (!Guid.TryParse(result.Tokens[0].Value, out Guid value) || value == Guid.Empty))
            {
                result.AddError("--scid must be a non-empty GUID.");
            }
        });
        return option;
    }

    private static async Task<int> InvokeAsync<TResult>(
        ParseResult parseResult,
        XdtsSellerOption sellerOption,
        Option<Guid> scidOption,
        Func<XboxPartnerClient, long?, Guid, CancellationToken, Task<TResult>> send,
        string action,
        CancellationToken cancellationToken)
        where TResult : IXboxLiveAchievementImageResult
    {
        var output = OutputOptions.From(parseResult);
        long? sellerId = null;
        try
        {
            var configuration = Program.BuildConfiguration(parseResult);
            using var client = Program.XboxPartnerClientFactory is null
                ? new XboxPartnerClient(
                    configuration,
                    authenticationService: Program.GetAuthentication(parseResult))
                : Program.XboxPartnerClientFactory(configuration);
            string baseUrl = XboxPartnerCommandSupport.ResolveBaseUrl(configuration);
            sellerId = await sellerOption.GetSellerIdAsync(
                parseResult, configuration, client, baseUrl, cancellationToken);
            Guid scid = parseResult.GetValue(scidOption);
            Program.WriteVerbose(
                parseResult.GetValue(Program.VerboseOption),
                () => $"POST XboxPartner achievement image {action} endpoint for {scid:D}" +
                    (sellerId is { } id ? $" (seller {id})" : string.Empty));
            TResult result = await send(
                client, sellerId, scid, cancellationToken)
                .ConfigureAwait(false);
            string formatted;
            if (output.TableView is { } view)
            {
                using var document = JsonDocument.Parse(result.RawResponse);
                formatted = result is XboxLiveAchievementImageCollection
                    ? TableFormatter.FormatCollection(document.RootElement, view)
                    : TableFormatter.Format(document.RootElement, view);
            }
            else
            {
                formatted = FormatImageJson(result, output.IsCompact);
            }

            Console.Out.WriteLine(formatted);
            return 0;
        }
        catch (XboxPartnerUploadOutcomeException ex)
        {
            string originalError = ex.InnerException is XboxPartnerApiException api
                ? XboxPartnerCommandErrorFormatter.Format(
                    api, output.IsCompact, sensitiveValues: ex.SensitiveValues)
                : $"Request failed: {XboxPartnerCommandErrorFormatter.Redact(ex.Message, ex.SensitiveValues)}";
            Console.Error.WriteLine(originalError);
            Console.Error.WriteLine();
            Console.Error.WriteLine("Upload outcome unknown. The service did not confirm image registration.");
            Console.Error.WriteLine("Inspect registered image metadata and CDN image contents by running:");
            Console.Error.WriteLine();
            Console.Error.WriteLine(
                BuildRecoveryCommand(parseResult, parseResult.GetValue(scidOption), sellerId, ex.AssetId));
            Console.Error.WriteLine();
            Console.Error.WriteLine("Use the existing asset ID if you find your image, or retry the upload.");
            Console.Error.WriteLine("Each upload creates a separate image asset.");
            return 1;
        }
        catch (XboxPartnerImageRegistrationRejectedException ex)
        {
            Console.Error.WriteLine(XboxPartnerCommandErrorFormatter.Format(
                (XboxPartnerApiException)ex.InnerException!, output.IsCompact,
                sensitiveValues: ex.SensitiveValues));
            Console.Error.WriteLine(ex.Resolution);
            return 1;
        }
        catch (XboxPartnerApiException ex)
        {
            bool uploadBadRequest = action == "upload" &&
                ex.StatusCode == System.Net.HttpStatusCode.BadRequest;
            XboxPartnerCommandSupport.WriteCorrelationId(ex.CorrelationId, Console.Error);
            Console.Error.WriteLine(XboxPartnerCommandErrorFormatter.Format(
                ex, output.IsCompact,
                uploadBadRequest ? ImageRequirements : null,
                omitAdditionalDetailWithFieldErrors: uploadBadRequest));
            return 1;
        }
        catch (XboxPartnerResponseException ex)
        {
            XboxPartnerCommandSupport.WriteCorrelationId(ex.CorrelationId, Console.Error);
            Console.Error.WriteLine($"Request failed: {ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Request failed: {ex.Message}");
            return 1;
        }
    }

    private static string FormatImageJson(IXboxLiveAchievementImageResult result, bool compact)
    {
        using var document = JsonDocument.Parse(result.RawResponse);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = !compact }))
        {
            if (result is XboxLiveAchievementImageCollection collection)
            {
                writer.WriteStartArray();
                for (int index = 0; index < collection.Images.Count; index++)
                {
                    WriteImage(document.RootElement[index], collection.Images[index].AssetId, writer);
                }
                writer.WriteEndArray();
            }
            else if (result is XboxLiveAchievementImage image)
            {
                WriteImage(document.RootElement, image.AssetId, writer);
            }
            else
            {
                throw new InvalidOperationException("Unsupported achievement image response.");
            }
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteImage(JsonElement response, Guid assetId, Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        foreach (JsonProperty property in response.EnumerateObject())
        {
            if (property.Name != "iconImageId")
            {
                property.WriteTo(writer);
            }
        }
        writer.WriteString("iconImageId", assetId);
        writer.WriteEndObject();
    }

    private static string BuildRecoveryCommand(
        ParseResult result, Guid scid, long? sellerId, Guid? assetId)
    {
        var arguments = RecoveryCommandArguments.Create(result);

        arguments.AddRange([
            "xbl", "achievement-image", assetId is null ? "list" : "get",
            "--scid", scid.ToString("D"),
        ]);
        if (sellerId is { } id)
        {
            arguments.AddRange(["--seller-id", id.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        }

        if (assetId is { } knownAssetId)
        {
            arguments.AddRange(["--asset-id", knownAssetId.ToString("D")]);
        }

        return string.Join(' ', arguments);
    }
}
