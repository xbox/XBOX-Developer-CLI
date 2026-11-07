// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.IO.Compression;
using System.Text.Json;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Output;
using XboxDevCLI.Services;

namespace XboxDevCLI.Commands;

internal static class GetDocumentsCommandFactory
{
    internal const int MaxArchiveEntries = 256;
    internal const long MaxExtractedArchiveBytes =
        XboxPartnerClient.DefaultMaxDocumentArchiveBytes;

    public static Command Create()
    {
        var sellerOption = new XdtsSellerOption();
        var typeOption = new Option<string>("--type", "-t")
        {
            Description = "Document scope: 'sandbox' or 'account'. Default: sandbox.",
            DefaultValueFactory = _ => "sandbox",
        };
        typeOption.Validators.Add(result =>
            ValidateAllowedValue(result, "sandbox", "account"));
        var scidOption = new Option<Guid?>("--scid", "-c")
        {
            Description = "Service configuration ID. Required for the default sandbox scope.",
        };
        var sandboxOption = new Option<string?>("--sandbox-id")
        {
            HelpName = "id",
            Description = "Case-sensitive sandbox ID. Required for the default sandbox scope.",
        };
        var destinationOption = new Option<string>("--dest", "-d")
        {
            Description = "Directory where downloaded documents and ETag metadata are saved.",
            Required = true,
        };
        var viewOption = new Option<string>("--view")
        {
            Description = "Document-set view: 'working' or 'published'. Default: working.",
            DefaultValueFactory = _ => "working",
        };
        viewOption.Validators.Add(result =>
            ValidateAllowedValue(result, "working", "published"));
        var versionOption = new Option<ulong?>("--version")
        {
            Description = "Historical document-set version. Default: latest version.",
        };
        versionOption.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<ulong?>() == 0)
            {
                result.AddError("--version must be positive when provided.");
            }
        });
        var documentOption = new Option<string[]>("--document")
        {
            Description = "Document filename filter. Default: all documents. May be repeated.",
            Arity = ArgumentArity.ZeroOrMore,
            AllowMultipleArgumentsPerToken = true,
        };

        var command = new Command(
            "download",
            "Download XBOX services configuration documents");
        command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.ReadOnly,
            Scope: "A sandbox or account XBOX services configuration document set.",
            Output: "Extracted documents, ETag metadata, and a JSON or table download summary.",
            Examples:
            [
                "xbox xbl document download --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id XDKS.1 --dest xbox-config",
                "xbox xbl document download --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id XDKS.1 --dest xbox-config --output table",
                "xbox xbl document download --type account --dest xbox-config",
            ],
            Table: XboxPartnerCommandSupport.DocumentDownloadTableView));
        sellerOption.AddTo(command);
        command.Options.Add(typeOption);
        command.Options.Add(scidOption);
        command.Options.Add(sandboxOption);
        command.Options.Add(destinationOption);
        command.Options.Add(viewOption);
        command.Options.Add(versionOption);
        command.Options.Add(documentOption);
        command.Validators.Add(result =>
        {
            // An --scid that isn't a GUID is already a parse error, and reading it would throw.
            if (result.GetResult(scidOption) is { Tokens: [var scidToken, ..] } && !Guid.TryParse(scidToken.Value, out _))
            {
                return;
            }

            string type = result.GetValue(typeOption) ?? "sandbox";
            Guid? scid = result.GetValue(scidOption);
            string? sandbox = result.GetValue(sandboxOption);
            if (type.Equals("sandbox", StringComparison.OrdinalIgnoreCase))
            {
                if (!scid.HasValue || scid.Value == Guid.Empty)
                {
                    result.AddError("--scid is required for sandbox documents.");
                }

                if (!IsValidSandbox(sandbox))
                {
                    result.AddError(
                        "--sandbox-id is required for sandbox documents and cannot contain " +
                        "whitespace or path characters.");
                }

            }
            else
            {
                if (scid.HasValue || !string.IsNullOrWhiteSpace(sandbox))
                {
                    result.AddError("--scid and --sandbox-id cannot be used with account documents.");
                }
            }
        });
        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var output = OutputOptions.From(parseResult);
            var verbose = parseResult.GetValue(Program.VerboseOption);
            try
            {
                string type = (parseResult.GetValue(typeOption) ?? "sandbox")
                    .ToLowerInvariant();
                string? sandbox = parseResult.GetValue(sandboxOption)?.Trim();
                if (string.IsNullOrWhiteSpace(sandbox))
                {
                    sandbox = null;
                }

                var request = new XboxLiveGetDocumentsRequest(
                    type,
                    parseResult.GetValue(scidOption),
                    sandbox,
                    (parseResult.GetValue(viewOption) ?? "working").ToLowerInvariant(),
                    parseResult.GetValue(versionOption),
                    parseResult.GetValue(documentOption) ?? []);
                var configuration = Program.GetConfiguration(parseResult);
                using var client = Program.XboxPartnerClientFactory is null
                    ? new XboxPartnerClient(
                        configuration,
                        authenticationService: Program.GetAuthentication(parseResult))
                    : Program.XboxPartnerClientFactory(configuration);
                string baseUrl = XboxPartnerCommandSupport.ResolveBaseUrl(configuration);
                long? sellerId = await sellerOption.GetSellerIdAsync(
                    parseResult,
                    configuration,
                    client,
                    baseUrl,
                    cancellationToken);
                Program.WriteVerbose(
                    verbose,
                    () => $"Download {request.DocumentType} {request.View} documents to " +
                        parseResult.GetValue(destinationOption));
                return await RunAsync(
                    client,
                    sellerId,
                    request,
                    parseResult.GetValue(destinationOption)!,
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
        long? sellerId,
        XboxLiveGetDocumentsRequest request,
        string destination,
        OutputOptions output,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        bool compact = output.IsCompact;
        TableView? tableView = output.TableView;
        try
        {
            XboxPartnerDocumentArchive archive;
            using (RequestProgress.Start(DescribeDownload(request), stderr))
            {
                archive = await client
                    .GetXboxLiveDocumentsAsync(sellerId, request, cancellationToken)
                    .ConfigureAwait(false);
            }

            string directory = Path.GetFullPath(destination);
            string eTagFileName = request.DocumentType.Equals(
                "account",
                StringComparison.OrdinalIgnoreCase)
                ? "Account.etag"
                : $"{request.Sandbox}.etag";
            if (LocalFileNameValidator.IsInvalid(
                    eTagFileName,
                    enforceWindowsRules: OperatingSystem.IsWindows()))
            {
                throw new InvalidDataException(
                    "The sandbox name cannot be used for ETag metadata.");
            }
            string eTagPath = Path.Combine(directory, eTagFileName);
            ValidateEtagPath(eTagPath);
            string[] paths = await ExtractArchiveAsync(
                archive.Content,
                directory,
                MaxExtractedArchiveBytes,
                MaxArchiveEntries,
                cancellationToken).ConfigureAwait(false);
            await WriteEtagAsync(eTagPath, archive.ETag, cancellationToken)
                .ConfigureAwait(false);

            stdout.WriteLine(tableView is not null
                ? XboxPartnerTableFormatter.FormatRows(
                    tableView,
                    [
                        [
                            request.DocumentType.ToLowerInvariant(),
                            request.Sandbox ?? "",
                            archive.View,
                            archive.Version.ToString(
                                System.Globalization.CultureInfo.InvariantCulture),
                            paths.Length.ToString(
                                System.Globalization.CultureInfo.InvariantCulture),
                            directory,
                        ],
                    ])
                : JsonSerializer.Serialize(
                    new
                    {
                        type = request.DocumentType.ToLowerInvariant(),
                        view = archive.View,
                        version = archive.Version,
                        etag = archive.ETag,
                        etagFile = eTagPath,
                        files = paths,
                    },
                    compact ? CompactJsonOptions : PrettyJsonOptions));
            return 0;
        }
        catch (XboxPartnerApiException ex)
        {
            stderr.WriteLine(XboxPartnerCommandErrorFormatter.Format(ex, compact));
            return 1;
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"Request failed: {ex.Message}");
            return 1;
        }
    }

    internal static string DescribeDownload(XboxLiveGetDocumentsRequest request) =>
        request.Sandbox is { } sandbox
            ? $"Downloading {request.View} documents from {sandbox}"
            : $"Downloading {request.View} account documents";

    private static readonly JsonSerializerOptions PrettyJsonOptions = new()
    {
        WriteIndented = true,
    };

    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        WriteIndented = false,
    };

    internal static async Task<string[]> ExtractArchiveAsync(
        byte[] content,
        string directory,
        long maxExtractedBytes,
        int maxEntries,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxExtractedBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEntries);
        using var stream = new MemoryStream(content, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        if (archive.Entries.Count > maxEntries)
        {
            throw new InvalidDataException(
                $"The document archive contained more than {maxEntries} documents.");
        }

        var entries = new List<(ZipArchiveEntry Entry, string Name)>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string name = entry.FullName;
            if (LocalFileNameValidator.IsInvalid(name, enforceWindowsRules: true) ||
                !string.Equals(name, Path.GetFileName(name), StringComparison.Ordinal) ||
                !names.Add(name))
            {
                throw new InvalidDataException(
                    "The document archive contained an invalid or duplicate document name.");
            }

            entries.Add((entry, name));
        }

        directory = SafeOutputDirectory.Create(directory);
        string stagingDirectory = Path.Combine(
            directory,
            $".xboxdevcli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);
        try
        {
            long totalExtractedBytes = 0;
            foreach ((ZipArchiveEntry entry, string name) in entries)
            {
                string stagedPath = Path.Combine(stagingDirectory, name);
                await using Stream source = entry.Open();
                await using var target = new FileStream(
                    stagedPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 81920,
                    useAsync: true);
                totalExtractedBytes += await CopyWithLimitAsync(
                    source,
                    target,
                    maxExtractedBytes - totalExtractedBytes,
                    maxExtractedBytes,
                    cancellationToken).ConfigureAwait(false);
            }

            string[] paths = entries
                .Select(entry => Path.Combine(directory, entry.Name))
                .ToArray();
            foreach (string path in paths)
            {
                if (Directory.Exists(path))
                {
                    throw new IOException(
                        $"Cannot replace document '{Path.GetFileName(path)}' because the " +
                        "destination is a directory.");
                }

                try
                {
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new IOException(
                            $"Cannot replace document '{Path.GetFileName(path)}' because the " +
                            "destination is a reparse point.");
                    }
                }
                catch (FileNotFoundException)
                {
                }
                catch (DirectoryNotFoundException)
                {
                }
            }

            for (int index = 0; index < entries.Count; index++)
            {
                string name = entries[index].Name;
                string path = paths[index];
                File.Move(Path.Combine(stagingDirectory, name), path, overwrite: true);
            }

            return paths;
        }
        finally
        {
            Directory.Delete(stagingDirectory, recursive: true);
        }
    }

    private static async Task<long> CopyWithLimitAsync(
        Stream source,
        Stream target,
        long remainingBytes,
        long maxExtractedBytes,
        CancellationToken cancellationToken)
    {
        if (remainingBytes < 0)
        {
            throw ExtractedArchiveTooLarge(maxExtractedBytes);
        }

        byte[] buffer = new byte[81920];
        long bytesWritten = 0;
        while (true)
        {
            int bytesRead = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                return bytesWritten;
            }

            if (bytesRead > remainingBytes - bytesWritten)
            {
                throw ExtractedArchiveTooLarge(maxExtractedBytes);
            }

            await target.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken)
                .ConfigureAwait(false);
            bytesWritten += bytesRead;
        }
    }

    private static InvalidDataException ExtractedArchiveTooLarge(long maxExtractedBytes) =>
        new(
            $"The document archive expands beyond " +
            $"{maxExtractedBytes} bytes.");

    private static void ValidateEtagPath(string eTagPath)
    {
        if (Directory.Exists(eTagPath))
        {
            throw new IOException("The existing ETag path is a directory.");
        }

        if (!File.Exists(eTagPath))
        {
            return;
        }
        FileAttributes attributes = File.GetAttributes(eTagPath);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The existing ETag path is a reparse point.");
        }
    }

    private static async Task WriteEtagAsync(
        string eTagPath,
        string eTag,
        CancellationToken cancellationToken)
    {
        string temporaryPath = Path.Combine(
            Path.GetDirectoryName(eTagPath)!,
            $".xboxdevcli-etag-{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllTextAsync(temporaryPath, eTag, cancellationToken)
                .ConfigureAwait(false);
            FileAttributes? originalAttributes = null;
            if (File.Exists(eTagPath))
            {
                originalAttributes = File.GetAttributes(eTagPath);
                if ((originalAttributes.Value & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("The existing ETag path is a reparse point.");
                }

                if (OperatingSystem.IsWindows())
                {
                    File.SetAttributes(
                        eTagPath,
                        originalAttributes.Value & ~FileAttributes.Hidden);
                }
            }

            try
            {
                File.Move(temporaryPath, eTagPath, overwrite: true);
            }
            catch
            {
                if (originalAttributes.HasValue && File.Exists(eTagPath))
                {
                    File.SetAttributes(eTagPath, originalAttributes.Value);
                }

                throw;
            }

            if (OperatingSystem.IsWindows())
            {
                File.SetAttributes(
                    eTagPath,
                    File.GetAttributes(eTagPath) | FileAttributes.Hidden);
            }
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static bool IsValidSandbox(string? sandbox)
    {
        string value = sandbox?.Trim() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value) &&
            !value.Any(char.IsWhiteSpace) &&
            value.IndexOfAny(['/', '\\', ':', '\0']) < 0 &&
            value is not "." and not "..";
    }

    private static void ValidateAllowedValue(
        OptionResult result,
        params string[] allowedValues)
    {
        if (result.Tokens.Count == 0)
        {
            return;
        }

        string value = result.Tokens[0].Value;
        if (!allowedValues.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            result.AddError(
                $"Invalid value '{value}' for option '{result.Option.Name}'. " +
                $"Allowed values: {string.Join(", ", allowedValues)}.");
        }
    }
}
