// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace XboxDevCLI.E2ETest;

public class XblTests
{
    private const string SchemaType = "achievements2017";

    private static readonly XName XsdRoot = XName.Get("schema", "http://www.w3.org/2001/XMLSchema");

    private static readonly byte[] PngSignature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    private static E2ESettings Settings => E2ESettings.Current;

    [LiveFact]
    public async Task SchemaGet_ListsTheTypes()
    {
        CliResult result = await Cli.RunAsync("xbl", "schema", "get", "--output", "json-compact");

        result.Check(result.Json().Items().HasString(SchemaType), $"Expected the {SchemaType} type.");
    }

    [LiveFact]
    public async Task SchemaGet_DownloadsTheNewestVersionOfAType()
    {
        CliResult versions = await Cli.RunAsync("xbl", "schema", "get", "--type", SchemaType, "--output", "json-compact");
        long newest = versions.Json().Items().Select(version => version.Number("version") ?? 0).DefaultIfEmpty().Max();
        versions.Check(newest > 0, $"Expected versions of {SchemaType}.");

        CliResult schema = await Cli.RunAsync(
            "xbl", "schema", "get", "--type", SchemaType, "--version", newest.ToString(CultureInfo.InvariantCulture));

        schema.Check(schema.ExitCode == 0, "Expected exit code 0.");
        schema.Check(ParseXml(schema.Stdout)?.Name == XsdRoot, "Expected the XSD on stdout.");
    }

    [LiveFact]
    public async Task DocumentDownload_SavesTheWorkingDocuments()
    {
        DirectoryInfo destination = Directory.CreateTempSubdirectory("xbox-e2e-");
        try
        {
            CliResult result = await Cli.RunForSellerAsync(
                "xbl", "document", "download", "--scid", Settings.Scid, "--sandbox-id", Settings.Sandbox,
                "--dest", destination.FullName);

            JsonElement download = result.Json();
            JsonElement[] files = download.Items("files");
            result.Check(download.Is("view", "working"), "Expected the working view.");
            result.Check(files.Length > 0, "Expected documents.");
            result.Check(
                files.All(file => file.ValueKind == JsonValueKind.String && File.Exists(file.GetString())),
                "Expected every listed document on disk.");
            result.Check(File.Exists(download.Text("etagFile")), "Expected the ETag file on disk.");
        }
        finally
        {
            destination.Delete(recursive: true);
        }
    }

    [LiveFact]
    public async Task ConfigurationList_IncludesTheSandbox()
    {
        CliResult result = await Cli.RunForSellerAsync("xbl", "configuration", "list", "--scid", Settings.Scid);

        result.Check(
            result.Json().Items().Any(configuration => configuration.Is("sandbox", Settings.Sandbox)),
            $"Expected {Settings.Sandbox}.");
    }

    [LiveFact]
    public async Task ConfigurationGet_ShowsPublishedConfiguration()
    {
        CliResult result = await Cli.RunForSellerAsync(
            "xbl", "configuration", "get", "--scid", Settings.Scid, "--sandbox-id", Settings.Sandbox);

        JsonElement configuration = result.Json();
        result.Check(configuration.Is("sandbox", Settings.Sandbox), $"Expected {Settings.Sandbox}.");
        result.Check(
            configuration.Flag("hasPublishedConfiguration") == true && configuration.Number("publishedVersion") > 0,
            "Expected published configuration.");
    }

    [LiveFact]
    public async Task PublishStatus_ReportsAStatus()
    {
        CliResult result = await Cli.RunForSellerAsync(
            "xbl", "publish", "status", "--scid", Settings.Scid, "--sandbox-id", Settings.Sandbox);

        // The CLI prints the service's response as it is, which names its fields in PascalCase.
        result.Check(!string.IsNullOrWhiteSpace(result.Json().Text("Status")), "Expected a status.");
    }

    [LiveFact]
    public async Task AchievementImageList_IncludesTheImage()
    {
        CliResult result = await Cli.RunForSellerAsync("xbl", "achievement-image", "list", "--scid", Settings.Scid);

        result.Check(
            result.Json().Items().Any(image =>
                image.IsGuid("assetId", Settings.AchievementImageId) &&
                image.IsGuid("iconImageId", Settings.AchievementImageId)),
            $"Expected image {Settings.AchievementImageId}, with iconImageId set to it.");
    }

    [LiveFact]
    public async Task AchievementImageGet_ReturnsTheImage()
    {
        CliResult result = await Cli.RunForSellerAsync(
            "xbl", "achievement-image", "get", "--scid", Settings.Scid, "--asset-id", Settings.AchievementImageId);

        JsonElement image = result.Json();
        result.Check(
            image.IsGuid("assetId", Settings.AchievementImageId) && image.IsGuid("iconImageId", Settings.AchievementImageId),
            $"Expected image {Settings.AchievementImageId}, with iconImageId set to it.");
    }

    // The CLI downloads the image and its thumbnail from the CDN itself.
    [LiveFact]
    public async Task AchievementImageDownload_SavesTheImage()
    {
        DirectoryInfo destination = Directory.CreateTempSubdirectory("xbox-e2e-");
        try
        {
            CliResult result = await Cli.RunForSellerAsync(
                "xbl", "achievement-image", "download", "--scid", Settings.Scid, "--asset-id", Settings.AchievementImageId,
                "--dest", destination.FullName);

            JsonElement download = result.Json();
            result.Check(download.IsGuid("assetId", Settings.AchievementImageId), $"Expected image {Settings.AchievementImageId}.");
            result.Check(File.Exists(download.Text("thumbnailPath")), "Expected the thumbnail on disk.");
            result.Check(
                download.Text("imagePath") is { } image && File.Exists(image) && ReadPngSize(image) == (1920, 1080),
                "Expected the image on disk, as a 1920x1080 PNG, the size XBOX services require.");
        }
        finally
        {
            destination.Delete(recursive: true);
        }
    }

    // XBOX services answer that the files haven't changed and store nothing, which the CLI counts as success.
    [LiveFact]
    public async Task DocumentCommit_ValidateOnly_AcceptsTheDocumentsJustDownloaded()
    {
        DirectoryInfo destination = Directory.CreateTempSubdirectory("xbox-e2e-");
        try
        {
            CliResult download = await Cli.RunForSellerAsync(
                "xbl", "document", "download", "--scid", Settings.Scid, "--sandbox-id", Settings.Sandbox,
                "--dest", destination.FullName);
            string[] files = [.. download.Json().Items("files").Select(file => file.GetString()).OfType<string>()];
            download.Check(files.Length > 0, "Expected documents.");

            CliResult result = await Cli.RunForSellerAsync(
            [
                "xbl", "document", "commit", "--validate-only", "--scid", Settings.Scid, "--sandbox-id", Settings.Sandbox,
                .. files.SelectMany(file => new[] { "--file", file }),
            ]);

            result.Check(result.Json().Field("result").Flag("Committed") == false, "Expected nothing committed.");
        }
        finally
        {
            destination.Delete(recursive: true);
        }
    }

    // A failed request writes one JSON error to stderr, whose detail carries the service's reason and its
    // correlation vector (MS-CV), which traces the request.
    [LiveFact]
    public async Task DocumentCommit_ValidateOnly_ShowsWhyABrokenDocumentIsRefused()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("xbox-e2e-");
        try
        {
            string document = Path.Combine(directory.FullName, "titlestorage.xml");
            await File.WriteAllTextAsync(document, "<{ not a configuration document");

            CliResult result = await Cli.RunForSellerAsync(
                "xbl", "document", "commit", "--validate-only", "--scid", Settings.Scid, "--sandbox-id", Settings.Sandbox,
                "--file", document);

            result.Check(result.ExitCode == 1, "Expected exit code 1.");
            result.Check(string.IsNullOrWhiteSpace(result.Stdout), "Expected nothing on stdout.");
            result.Check(
                result.Stderr.Contains("\"status\":400", StringComparison.Ordinal) &&
                    result.Stderr.Contains("Invalid Document", StringComparison.OrdinalIgnoreCase) &&
                    result.Stderr.Contains("MS-CV:", StringComparison.Ordinal),
                "Expected a 400 on stderr, with the service's reason and its MS-CV.");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    // With no --source-sandbox-id, XBOX services validate publishing the sandbox's own working configuration.
    [LiveFact]
    public async Task PublishStart_ValidateOnly_ValidatesTheSandbox()
    {
        CliResult result = await Cli.RunForSellerAsync(
            "xbl", "publish", "start", "--validate-only", "--scid", Settings.Scid, "--sandbox-id", Settings.Sandbox);

        // The CLI prints the service's response as it is, which names its fields in PascalCase.
        result.Check(!string.IsNullOrWhiteSpace(result.Json().Text("Status")), "Expected a status.");
    }

    /// <summary>The width and height in a PNG's header, or null if the file isn't a PNG.</summary>
    private static (int Width, int Height)? ReadPngSize(string path)
    {
        byte[] header = new byte[24];
        using (FileStream file = File.OpenRead(path))
        {
            if (file.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length)
            {
                return null;
            }
        }

        // The signature, then the IHDR chunk's length and type, then its width and height.
        return header.AsSpan(0, 8).SequenceEqual(PngSignature) && header.AsSpan(12, 4).SequenceEqual("IHDR"u8)
            ? (BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20)))
            : null;
    }

    private static XElement? ParseXml(string text)
    {
        try
        {
            return XDocument.Parse(text).Root;
        }
        catch (XmlException)
        {
            return null;
        }
    }
}
