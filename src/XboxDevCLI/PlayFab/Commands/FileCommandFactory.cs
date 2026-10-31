// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using XboxDevCLI.Output;
using XboxDevCLI.CommandLine;
using System.Text.Encodings.Web;
using System.Text.Json;
using XboxDevCLI.PlayFab.Generated.Data;
using XboxDevCLI.Services;

namespace XboxDevCLI.PlayFab.Commands;

/// <summary>
/// Creates the "playfab file" command group for listing, downloading, uploading, and deleting
/// entity files via the PlayFab Data (File) API.
/// </summary>
internal static class FileCommandFactory
{
    private static readonly string[] AllowedAzureBlobHostSuffixes =
    [
        "blob.core.windows.net",
        "blob.core.usgovcloudapi.net",
        "blob.core.chinacloudapi.cn",
        "blob.core.cloudapi.de",
    ];

    private static readonly JsonSerializerOptions OutputOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions RequestOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    internal const string GetFilesPath = "/File/GetFiles";
    internal const string InitiateFileUploadsPath = "/File/InitiateFileUploads";
    internal const string FinalizeFileUploadsPath = "/File/FinalizeFileUploads";
    internal const string AbortFileUploadsPath = "/File/AbortFileUploads";
    internal const string DeleteFilesPath = "/File/DeleteFiles";

    /// <summary>How long canceling a pending upload may take, which also bounds the wait after Ctrl-C.</summary>
    internal static readonly TimeSpan AbortTimeout = TimeSpan.FromSeconds(15);

    public static Command Create()
    {
        var command = new Command("file", "Manage entity files");
        command.Subcommands.Add(CreateListCommand());
        command.Subcommands.Add(CreateDownloadCommand());
        command.Subcommands.Add(CreateUploadCommand());
        command.Subcommands.Add(CreateDeleteCommand());

        return command;
    }

    internal static (string apiPath, GetFilesRequest request) BuildListCall(
        string entityId, string entityType)
    {
        var request = new GetFilesRequest
        {
            Entity = new EntityKey { Id = entityId, Type = entityType },
        };

        return (GetFilesPath, request);
    }

    internal static (string apiPath, DeleteFilesRequest request) BuildDeleteCall(
        string entityId, string entityType, string[] fileNames)
    {
        var request = new DeleteFilesRequest
        {
            Entity = new EntityKey { Id = entityId, Type = entityType },
            FileNames = new List<string>(fileNames),
        };

        return (DeleteFilesPath, request);
    }

    private static Option<string> CreateEntityIdOption() => new("--entity-id")
    {
        Description = "Entity ID (e.g., a player's title player account ID)",
        Required = true,
    };

    private static Option<string> CreateEntityTypeOption() => new("--entity-type")
    {
        Description = "Entity type (e.g., title_player_account, title, character)",
        Required = true,
    };

    private static Command CreateListCommand()
    {
        var entityIdOption = CreateEntityIdOption();
        var entityTypeOption = CreateEntityTypeOption();

        var command = new Command("list", "List files associated with an entity")
        {
            entityIdOption,
            entityTypeOption,
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var entityId = parseResult.GetValue(entityIdOption)!;
            var entityType = parseResult.GetValue(entityTypeOption)!;
            var output = Output.OutputOptions.From(parseResult);

            try
            {
                using var client = PlayFabCommandHelper.CreateClient(parseResult);
                return await RunListAsync(
                    client, entityId, entityType, output, Console.Out, Console.Error,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Request failed: {ex.Message}");
                return 1;
            }
        });

        // File metadata is keyed by file name rather than returned as an array; the renderer
        // enumerates the values of a keyed collection, so a view describes it the same way.
        return command.WithMetadata(new CommandMetadata(
            ApiPaths: [GetFilesPath],
            Table: new TableView(
                nameof(GetFilesResponse.Metadata),
                [
                    new TableColumn("FILE NAME", nameof(GetFileMetadata.FileName), 40),
                    new TableColumn("LAST MODIFIED", nameof(GetFileMetadata.LastModified), 24),
                    new TableColumn("CHECKSUM", nameof(GetFileMetadata.Checksum), 34),
                ])));
    }

    /// <summary>
    /// Executes the "file list" logic against an already-constructed client.
    /// Extracted from the command action so the branching/output logic can be
    /// unit-tested with a fake transport and captured output streams.
    /// </summary>
    internal static async Task<int> RunListAsync(
        PlayFabApiClient client,
        string entityId,
        string entityType,
        Output.OutputOptions output,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);

        var (apiPath, request) = BuildListCall(entityId, entityType);
        var jsonBody = JsonSerializer.Serialize(request, RequestOptions);

        try
        {
            var response = await client.CallApiAsync(apiPath, jsonBody, cancellationToken: cancellationToken);

            // This command has its own formatter rather than routing through the shared invoke
            // path, so it honours the declared view itself. Taking OutputOptions rather than a
            // bare format string is what makes that obligation visible in the signature.
            stdout.WriteLine(output.TableView is null
                ? FormatFileMetadata(response, output.IsCompact)
                : FormatFileMetadataTable(response, output.TableView));
            return 0;
        }
        catch (PlayFabApiException ex)
        {
            stderr.WriteLine(Program.FormatErrorOutput(ex, output.IsCompact));
            return 1;
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"Request failed: {ex.Message}");
            return 1;
        }
    }

    private static Command CreateDownloadCommand()
    {
        var entityIdOption = CreateEntityIdOption();
        var entityTypeOption = CreateEntityTypeOption();

        var nameOption = new Option<string>("--name")
        {
            Description = "Name of the file to download",
            Required = true,
        };

        var outputPathOption = new Option<string?>("--dest")
        {
            Description = "Local destination file path (defaults to the PlayFab file name in the current directory; required when that name is not valid locally)",
        };

        var command = new Command("download", "Download a file from an entity's profile")
        {
            entityIdOption,
            entityTypeOption,
            nameOption,
            outputPathOption,
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var entityId = parseResult.GetValue(entityIdOption)!;
            var entityType = parseResult.GetValue(entityTypeOption)!;
            var fileName = parseResult.GetValue(nameOption)!;
            var outputPath = parseResult.GetValue(outputPathOption);
            var output = Output.OutputOptions.From(parseResult);

            try
            {
                using var client = PlayFabCommandHelper.CreateClient(parseResult);
                using var httpClient = new HttpClient();
                return await RunDownloadAsync(
                    client, httpClient, entityId, entityType, fileName, outputPath,
                    BuildListCommandSuggestion(parseResult, entityId, entityType),
                    output.Format, Console.Error, cancellationToken);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Download failed: {ex.Message}");
                return 1;
            }
        });

        // Only GetFiles is a PlayFab call; the bytes come from the storage URL it returns. The
        // detail names the local write because that is the part of the effect the read-only
        // classification does not cover.
        return command.WithMetadata(new CommandMetadata(
            ApiPaths: [GetFilesPath],
            Output: DescribeFileOperationOutput(
                "a line such as \"Downloaded 'save.json' to '<path>' (2048 bytes).\" " +
                "when it succeeds"),
            SafetyDetails:
                "Writes the downloaded bytes to the local destination, replacing an existing " +
                "file there."));
    }

    /// <summary>
    /// Describes what a file operation writes, for its Help. Upload, download, and delete move or
    /// remove a file, like <c>cp</c> and <c>rm</c>; they aren't data commands, so they return no
    /// JSON and leave stdout empty, which keeps a script from reading their message as data.
    /// </summary>
    private static string DescribeFileOperationOutput(string stderrMessages) =>
        "Writes nothing to stdout. On stderr, writes " + stderrMessages +
        ". Messages are for people and may change, so scripts should check the exit " +
        "code: 0 on success, or nonzero on failure, with the error on stderr. --output and " +
        "--data-only don't change the success output; --output json-compact puts a PlayFab " +
        "error on one line, and --output table is refused. Use 'file list' to get the files' " +
        "metadata as JSON.";

    /// <summary>
    /// The <c>file list</c> invocation that shows the files on the same entity of the same title,
    /// for an error to suggest. The title is always named, even when it came from saved context,
    /// so the suggestion reads the same title wherever it is run.
    /// </summary>
    internal static string BuildListCommandSuggestion(
        ParseResult parseResult, string entityId, string entityType) =>
        $"{BuildRecoveryCommandPrefix(parseResult)} playfab file list " +
        $"--entity-id {ShellQuoting.Quote(entityId)} --entity-type {ShellQuoting.Quote(entityType)}";

    /// <summary>
    /// Executes the "file download" logic against an already-constructed
    /// PlayFab client and a blob-transfer <see cref="HttpClient"/>. Extracted
    /// from the command action so the metadata-parsing and download branches
    /// can be unit-tested with fake transports and a captured error stream.
    /// It takes no stdout: a file operation writes nothing there. When the
    /// file isn't on the entity, the error suggests <paramref name="listCommand"/>,
    /// from <see cref="BuildListCommandSuggestion"/>.
    /// </summary>
    internal static async Task<int> RunDownloadAsync(
        PlayFabApiClient client,
        HttpClient blobClient,
        string entityId,
        string entityType,
        string fileName,
        string? outputPath,
        string listCommand,
        string outputFormat,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        string resolvedOutputPath;

        try
        {
            resolvedOutputPath = ResolveDownloadOutputPath(outputPath, fileName);

            // Get the file metadata (which includes the download URL)
            var (apiPath, request) = BuildListCall(entityId, entityType);
            var response = await client.CallApiAsync(apiPath, JsonSerializer.Serialize(request, RequestOptions), cancellationToken: cancellationToken);

            // Extract the download URL from the Metadata dictionary
            using var doc = JsonDocument.Parse(response);
            var dataElement = doc.RootElement.TryGetProperty("data", out var d) ? d : doc.RootElement;

            if (!dataElement.TryGetProperty("Metadata", out var metadata) ||
                metadata.ValueKind != JsonValueKind.Object)
            {
                stderr.WriteLine("Error: No files found on this entity.");
                return 1;
            }

            // Suggests `file list` instead of printing the entity's file names, which are
            // unbounded and come from PlayFab unsanitized.
            if (!metadata.TryGetProperty(fileName, out var fileEntry))
            {
                stderr.WriteLine($"Error: File '{fileName}' not found on this entity. To see its files, run:");
                stderr.WriteLine($"  {listCommand}");
                return 1;
            }

            if (!fileEntry.TryGetProperty("DownloadUrl", out var urlElement) ||
                urlElement.ValueKind != JsonValueKind.String)
            {
                stderr.WriteLine($"Error: No download URL available for '{fileName}'.");
                return 1;
            }

            var downloadUrl = urlElement.GetString()!;
            ValidateAzureBlobTransferUrl(downloadUrl, "download");

            // Download the file
            using var downloadResponse = await blobClient.GetAsync(downloadUrl, cancellationToken);
            if (!downloadResponse.IsSuccessStatusCode)
            {
                stderr.WriteLine($"Error: Download failed with HTTP {(int)downloadResponse.StatusCode}.");
                return 1;
            }

            var directory = Path.GetDirectoryName(resolvedOutputPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            long bytesWritten;
            await using (var fileStream = new FileStream(resolvedOutputPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await downloadResponse.Content.CopyToAsync(fileStream, cancellationToken);
                bytesWritten = fileStream.Length;
            }

            stderr.WriteLine($"Downloaded '{fileName}' to '{resolvedOutputPath}' ({bytesWritten} bytes).");
            return 0;
        }
        catch (PlayFabApiException ex)
        {
            var compact = outputFormat.Equals("json-compact", StringComparison.OrdinalIgnoreCase);
            stderr.WriteLine(Program.FormatErrorOutput(ex, compact));
            return 1;
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"Download failed: {ex.Message}");
            return 1;
        }
    }

    internal static string ResolveDownloadOutputPath(string? outputPath, string fileName)
    {
        var baseDirectory = Path.GetFullPath(Directory.GetCurrentDirectory());

        if (outputPath is null)
        {
            ValidateDefaultDownloadFileName(fileName);
            return ResolveConfinedPath(baseDirectory, fileName);
        }

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new InvalidOperationException("Download destination resolves to an empty path.");
        }

        // `-` is reserved for stdout, which isn't implemented yet. Without this, it would be a
        // relative path, and the download would land in a file named `-`.
        if (outputPath == "-")
        {
            throw new InvalidOperationException(
                "Downloading to stdout with '--dest -' isn't implemented yet. Pass a file path to --dest.");
        }

        ValidateDestinationFileName(outputPath);

        string resolvedOutputPath;
        if (Path.IsPathRooted(outputPath))
        {
            resolvedOutputPath = Path.GetFullPath(outputPath);
        }
        else
        {
            resolvedOutputPath = ResolveConfinedPath(baseDirectory, outputPath);
        }

        if (Directory.Exists(resolvedOutputPath))
        {
            throw new InvalidOperationException("Download destination must include a file name.");
        }

        return resolvedOutputPath;
    }

    internal static void ValidateDefaultDownloadFileName(string fileName)
    {
        if (LocalFileNameValidator.IsInvalid(
            fileName,
            enforceWindowsRules: OperatingSystem.IsWindows()))
        {
            throw new InvalidOperationException(
                $"PlayFab file name '{fileName}' is not a valid local file name. Specify a local file path with --dest.");
        }
    }

    private static void ValidateDestinationFileName(string outputPath)
    {
        var destinationFileName = Path.GetFileName(outputPath);
        if (string.IsNullOrEmpty(destinationFileName) || destinationFileName is "." or "..")
        {
            throw new InvalidOperationException("Download destination must include a file name.");
        }
    }

    internal static bool HasInvalidWindowsFileNameSyntax(string fileName)
        => LocalFileNameValidator.HasInvalidWindowsSyntax(fileName);

    private static string ResolveConfinedPath(string baseDirectory, string path)
    {
        var candidatePath = Path.GetFullPath(path, baseDirectory);
        var relativeToBase = Path.GetRelativePath(baseDirectory, candidatePath);

        if (relativeToBase.Equals("..", StringComparison.Ordinal) ||
            relativeToBase.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relativeToBase.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Download destination must stay within the expected directory.");
        }

        return candidatePath;
    }

    internal static void ValidateAzureBlobTransferUrl(string url, string operation)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsedUrl) || string.IsNullOrWhiteSpace(parsedUrl.Host))
        {
            throw new InvalidOperationException($"Invalid {operation} URL returned by PlayFab.");
        }

        if (!IsAllowedAzureBlobHost(parsedUrl.Host))
        {
            throw new InvalidOperationException(
                $"Rejected {operation} URL host '{parsedUrl.Host}'. Only Azure Blob Storage hosts are allowed.");
        }
    }

    internal static bool IsAllowedAzureBlobHost(string host)
    {
        foreach (var suffix in AllowedAzureBlobHostSuffixes)
        {
            if (host.Equals(suffix, StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith($".{suffix}", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static Command CreateUploadCommand()
    {
        var entityIdOption = CreateEntityIdOption();
        var entityTypeOption = CreateEntityTypeOption();

        var fileOption = new Option<string>("--file")
        {
            Description = "Path to the local file to upload",
            Required = true,
        };

        var nameOption = new Option<string?>("--name")
        {
            Description = "Remote file name (defaults to the local file name)",
        };

        var command = new Command("upload", "Upload a file to an entity's profile")
        {
            entityIdOption,
            entityTypeOption,
            fileOption,
            nameOption,
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var entityId = parseResult.GetValue(entityIdOption)!;
            var entityType = parseResult.GetValue(entityTypeOption)!;
            var filePath = parseResult.GetValue(fileOption)!;
            var remoteName = parseResult.GetValue(nameOption);
            var output = Output.OutputOptions.From(parseResult);

            try
            {
                using var client = PlayFabCommandHelper.CreateClient(parseResult);
                using var httpClient = new HttpClient();
                return await RunUploadAsync(
                    client, httpClient, entityId, entityType, filePath, remoteName,
                    output.Format, BuildRecoveryCommandPrefix(parseResult),
                    Console.Error, cancellationToken);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Upload failed: {ex.Message}");
                return 1;
            }
        });

        return command.WithMetadata(new CommandMetadata(
            ApiPaths: [InitiateFileUploadsPath, FinalizeFileUploadsPath, AbortFileUploadsPath],
            Output: DescribeFileOperationOutput(
                "\"Uploading source file: <path>\" when it starts, and a line such as " +
                "\"Uploaded 'save.json' (2048 bytes).\" when it succeeds"),
            SafetyDetails:
                "PlayFab holds an upload pending from its start until it's finalized, and a pending " +
                "upload blocks later uploads to the entity's files, including the game's own. If " +
                "the upload fails or is canceled with Ctrl-C after it starts, the CLI cancels the " +
                "pending upload with /File/AbortFileUploads and says whether that worked. The " +
                "cancel applies only if the entity's files haven't changed since the upload " +
                "started, so it can't undo an upload PlayFab finalized."));
    }

    /// <summary>
    /// The start of a follow-up command that reaches the same title with the same identity as
    /// this invocation: <c>xbox</c>, its configuration and identity options, and the title.
    /// </summary>
    private static string BuildRecoveryCommandPrefix(ParseResult parseResult)
    {
        var arguments = RecoveryCommandArguments.Create(parseResult);
        if (parseResult.GetValue(Program.PlayFabTitleIdOption)?.Trim() is { Length: > 0 } titleId)
        {
            arguments.Add("--playfab-title-id");
            arguments.Add(ShellQuoting.Quote(titleId));
        }

        return string.Join(' ', arguments);
    }

    /// <summary>
    /// Executes the "file upload" logic against an already-constructed PlayFab
    /// client and a blob-transfer <see cref="HttpClient"/>. Extracted from the
    /// command action so the initiate/PUT/finalize/abort branches can be unit-tested
    /// with fake transports and a captured error stream. It takes no stdout: a
    /// file operation writes nothing there. <c>recoveryCommandPrefix</c> starts the
    /// follow-up commands a failure suggests, such as <c>xbox --playfab-title-id ABC123</c>.
    /// </summary>
    internal static async Task<int> RunUploadAsync(
        PlayFabApiClient client,
        HttpClient blobClient,
        string entityId,
        string entityType,
        string filePath,
        string? remoteName,
        string outputFormat,
        string recoveryCommandPrefix,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        // `-` is reserved for stdin, which isn't implemented yet. Without this, a local file named
        // `-` would be uploaded.
        if (filePath == "-")
        {
            stderr.WriteLine(
                "Error: Uploading from stdin with '--file -' isn't implemented yet. Pass a file path to --file.");
            return 1;
        }

        if (!File.Exists(filePath))
        {
            stderr.WriteLine($"Error: File not found: {filePath}");
            return 1;
        }

        var resolvedFilePath = Path.GetFullPath(filePath);
        stderr.WriteLine($"Uploading source file: {resolvedFilePath}");

        var fileName = remoteName ?? Path.GetFileName(resolvedFilePath);
        var entity = new EntityKey { Id = entityId, Type = entityType };
        var compact = outputFormat.Equals("json-compact", StringComparison.OrdinalIgnoreCase);

        // Step 1: Initiate the upload. From here until it's finalized, PlayFab holds it pending,
        // and a pending upload blocks every later upload to the entity's files.
        InitiateFileUploadsResponse initiateResponse;
        try
        {
            var initiateRequest = new InitiateFileUploadsRequest
            {
                Entity = entity,
                FileNames = new List<string> { fileName },
            };

            initiateResponse = await client.CallApiAsync<InitiateFileUploadsRequest, InitiateFileUploadsResponse>(
                InitiateFileUploadsPath, initiateRequest,
                cancellationToken: cancellationToken);
        }
        catch (PlayFabApiException ex)
        {
            stderr.WriteLine(Program.FormatErrorOutput(ex, compact));

            // PlayFab may have started the upload, but without the profile version its response
            // carries, a cancel could also cancel an upload the game started afterwards.
            if (ex.Outcome is PlayFabCallOutcome.NoResponse or PlayFabCallOutcome.Inconclusive or PlayFabCallOutcome.Succeeded)
            {
                stderr.WriteLine(
                    $"PlayFab may have started the upload of '{fileName}', and the CLI can't tell which upload " +
                    $"to cancel, so it leaves it alone.{Environment.NewLine}" +
                    PendingUpload.LockedAdvice(recoveryCommandPrefix, entity, fileName));
            }

            return 1;
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"Upload failed: {ex.Message}");
            return 1;
        }

        var pending = new PendingUpload(
            client, entity, fileName, initiateResponse.ProfileVersion, recoveryCommandPrefix, stderr);

        // Tracked from here, not from when a cancel starts: after Ctrl-C, System.CommandLine stops
        // waiting for the command after two seconds, which the transfer or finalize can outlast
        // before it gets to the cancel.
        return await ExitCleanup.Default.Track(TransferAndFinalizeAsync(
            blobClient, initiateResponse, pending, resolvedFilePath, compact, stderr, cancellationToken));
    }

    /// <summary>
    /// Steps 2 and 3 of an upload PlayFab has started: sends the bytes and finalizes the upload,
    /// or cancels it if either fails. Returns the command's exit code.
    /// </summary>
    private static async Task<int> TransferAndFinalizeAsync(
        HttpClient blobClient,
        InitiateFileUploadsResponse initiateResponse,
        PendingUpload pending,
        string resolvedFilePath,
        bool compact,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        // Step 2: Upload the file content via HTTP PUT (stream to avoid loading entire file in memory)
        try
        {
            if (initiateResponse.UploadDetails == null || initiateResponse.UploadDetails.Count == 0)
            {
                stderr.WriteLine("Error: No upload URL returned by PlayFab.");
                return await pending.AbortAsync(finalizeMayHaveApplied: false);
            }

            var uploadUrl = initiateResponse.UploadDetails[0].UploadUrl;
            if (string.IsNullOrEmpty(uploadUrl))
            {
                stderr.WriteLine("Error: Empty upload URL returned by PlayFab.");
                return await pending.AbortAsync(finalizeMayHaveApplied: false);
            }

            ValidateAzureBlobTransferUrl(uploadUrl, "upload");

            using var fileStream = new FileStream(resolvedFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);
            using var putContent = new StreamContent(fileStream);
            putContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

            using var putResponse = await blobClient.PutAsync(uploadUrl, putContent, cancellationToken);
            if (!putResponse.IsSuccessStatusCode)
            {
                var errorBody = await putResponse.Content.ReadAsStringAsync(cancellationToken);
                stderr.WriteLine($"Error: File upload failed with HTTP {(int)putResponse.StatusCode}: {errorBody}");
                return await pending.AbortAsync(finalizeMayHaveApplied: false);
            }
        }
        catch (Exception ex)
        {
            stderr.WriteLine(ex switch
            {
                OperationCanceledException when cancellationToken.IsCancellationRequested => "Upload canceled.",
                OperationCanceledException => "Upload failed: the file transfer timed out.",
                _ => $"Upload failed: {ex.Message}",
            });
            return await pending.AbortAsync(finalizeMayHaveApplied: false);
        }

        // Step 3: Finalize the upload
        try
        {
            var finalizeRequest = new FinalizeFileUploadsRequest
            {
                Entity = pending.Entity,
                FileNames = new List<string> { pending.FileName },
                ProfileVersion = initiateResponse.ProfileVersion,
            };

            await pending.Client.CallApiAsync<FinalizeFileUploadsRequest, FinalizeFileUploadsResponse>(
                FinalizeFileUploadsPath, finalizeRequest,
                cancellationToken: cancellationToken);
        }
        catch (PlayFabApiException ex)
        {
            stderr.WriteLine(Program.FormatErrorOutput(ex, compact));

            // PlayFab answered with success, so the upload is finalized and nothing is pending;
            // only its response was unusable.
            if (ex.Outcome == PlayFabCallOutcome.Succeeded)
            {
                return 1;
            }

            return await pending.AbortAsync(
                finalizeMayHaveApplied: ex.Outcome is PlayFabCallOutcome.NoResponse or PlayFabCallOutcome.Inconclusive);
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"Upload failed: {ex.Message}");
            return await pending.AbortAsync(finalizeMayHaveApplied: true);
        }

        stderr.WriteLine($"Uploaded '{pending.FileName}' ({new FileInfo(resolvedFilePath).Length} bytes).");
        return 0;
    }

    /// <summary>
    /// An upload PlayFab holds pending between <c>InitiateFileUploads</c> and
    /// <c>FinalizeFileUploads</c>, which a failed upload cancels so it doesn't block later uploads.
    /// </summary>
    private sealed record PendingUpload(
        PlayFabApiClient Client,
        EntityKey Entity,
        string FileName,
        double ProfileVersion,
        string RecoveryCommandPrefix,
        TextWriter Stderr)
    {
        /// <summary>
        /// Cancels the pending upload and reports the outcome, returning the command's exit code.
        /// </summary>
        /// <remarks>
        /// The cancel is sent with the profile version <c>InitiateFileUploads</c> returned, so
        /// PlayFab refuses it with <c>EntityProfileVersionMismatch</c> if anything has changed the
        /// entity's files since, including a finalize that applied. That makes it safe to send when
        /// the finalize's outcome is unknown: it can cancel only an upload that is still pending.
        /// It runs on its own timeout rather than the command's token, so it still runs after Ctrl-C.
        /// </remarks>
        /// <param name="finalizeMayHaveApplied">
        /// Whether <c>FinalizeFileUploads</c> was sent and its outcome is unknown.
        /// </param>
        public async Task<int> AbortAsync(bool finalizeMayHaveApplied)
        {
            Stderr.WriteLine($"Canceling the pending upload of '{FileName}' so it doesn't block later uploads to this entity's files...");

            using var timeout = new CancellationTokenSource(AbortTimeout);
            try
            {
                var request = new AbortFileUploadsRequest
                {
                    Entity = Entity,
                    FileNames = new List<string> { FileName },
                    ProfileVersion = ProfileVersion,
                };

                await Client.CallApiAsync<AbortFileUploadsRequest, AbortFileUploadsResponse>(
                    AbortFileUploadsPath, request, timeout.Token);

                WriteCanceled();
            }
            catch (PlayFabApiException ex) when (ex.Outcome == PlayFabCallOutcome.Succeeded)
            {
                // PlayFab answered with success, so the cancel applied; only its response was unusable.
                WriteCanceled();
            }
            // PlayFab answers FileNotFound, not NoEntityFileOperationPending, for a file that has
            // neither a pending upload nor a version on the profile.
            catch (PlayFabApiException ex) when (ex.ApiError is "NoEntityFileOperationPending" or "FileNotFound")
            {
                Stderr.WriteLine(finalizeMayHaveApplied
                    ? $"PlayFab has no pending upload of '{FileName}' to cancel, so nothing is locked, but the finalize may have applied. {CheckFinalizeAdvice()}"
                    : $"PlayFab has no pending upload of '{FileName}' to cancel, so nothing is locked.");
            }
            catch (PlayFabApiException ex) when (ex.ApiError == "EntityProfileVersionMismatch")
            {
                Stderr.WriteLine(finalizeMayHaveApplied
                    ? $"Didn't cancel the upload of '{FileName}', because the entity's files changed after it started, most likely because the finalize applied. {CheckFinalizeAdvice()}{Environment.NewLine}{LockedAdvice()}"
                    : $"Couldn't cancel the pending upload of '{FileName}', because the entity's files changed after it started, so the CLI left them alone.{Environment.NewLine}{LockedAdvice()}");
            }
            catch (Exception ex)
            {
                var reason = timeout.IsCancellationRequested
                    ? $"PlayFab didn't answer within {AbortTimeout.TotalSeconds:0} seconds"
                    : ex.Message;

                // Without an answer, or with a server error, the cancel may still have applied.
                var lead = ex is PlayFabApiException { Outcome: PlayFabCallOutcome.NoResponse or PlayFabCallOutcome.Inconclusive }
                    ? $"Couldn't tell whether the pending upload of '{FileName}' was canceled: {reason}"
                    : $"Couldn't cancel the pending upload of '{FileName}': {reason}";
                Stderr.WriteLine(finalizeMayHaveApplied
                    ? $"{lead}{Environment.NewLine}The finalize may have applied. {CheckFinalizeAdvice()}{Environment.NewLine}{LockedAdvice()}"
                    : $"{lead}{Environment.NewLine}{LockedAdvice()}");
            }

            return 1;
        }

        private void WriteCanceled() =>
            Stderr.WriteLine($"Canceled the pending upload of '{FileName}'. The entity's files are unchanged.");

        private string CheckFinalizeAdvice() =>
            $"To check whether '{FileName}' changed, run:" +
            $"{Environment.NewLine}  {RecoveryCommandPrefix} playfab file list " +
            $"--entity-id {ShellQuoting.Quote(Entity.Id!)} --entity-type {ShellQuoting.Quote(Entity.Type!)}";

        private string LockedAdvice() => LockedAdvice(RecoveryCommandPrefix, Entity, FileName);

        /// <summary>
        /// Says a pending upload of <paramref name="fileName"/> blocks later uploads, and prints the
        /// command that cancels it.
        /// </summary>
        /// <remarks>
        /// The body goes through stdin rather than <c>--body</c>: Windows PowerShell 5.1, and
        /// PowerShell before 7.3, drop the double quotes inside an argument passed to a native
        /// program, but not inside one piped to it. The body holds only the entity and file name
        /// from the command line, so printing it can't put a secret in a log.
        /// </remarks>
        internal static string LockedAdvice(string recoveryCommandPrefix, EntityKey entity, string fileName)
        {
            var body = JsonSerializer.Serialize(
                new AbortFileUploadsRequest { Entity = entity, FileNames = new List<string> { fileName } },
                RequestOptions);

            return
                $"If the upload is still pending, it blocks later uploads to this entity's files, including the game's own, " +
                $"which fail with EntityFileOperationPending. If they do, cancel it with:{Environment.NewLine}" +
                $"  {ShellQuoting.PrintLine(body)} | {recoveryCommandPrefix} playfab api {AbortFileUploadsPath} --body-file -";
        }
    }

    private static Command CreateDeleteCommand()
    {
        var entityIdOption = CreateEntityIdOption();
        var entityTypeOption = CreateEntityTypeOption();

        var namesArgument = new Argument<string[]>("names")
        {
            Description = "Names of the files to delete",
            Arity = ArgumentArity.OneOrMore,
        };

        var command = new Command("delete", "Delete files from an entity's profile")
        {
            entityIdOption,
            entityTypeOption,
            namesArgument,
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var entityId = parseResult.GetValue(entityIdOption)!;
            var entityType = parseResult.GetValue(entityTypeOption)!;
            var names = parseResult.GetValue(namesArgument)!;
            var output = Output.OutputOptions.From(parseResult);

            // /File/DeleteFiles is a destructive operation. Resolve confirmation
            // (via --confirm or an interactive prompt) before calling, then pass
            // confirmedDestructive so the client's fail-closed guard allows it.
            var confirmedDestructive = parseResult.GetValue(Program.ConfirmOption)
                || Program.ConfirmDestructiveOperation(DeleteFilesPath);
            if (!confirmedDestructive)
            {
                return 1;
            }

            try
            {
                using var client = PlayFabCommandHelper.CreateClient(parseResult);
                return await RunDeleteAsync(
                    client, entityId, entityType, names, confirmedDestructive,
                    output.Format, Console.Error,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Request failed: {ex.Message}");
                return 1;
            }
        });

        return command.WithMetadata(new CommandMetadata(
            ApiPaths: [DeleteFilesPath],
            Output: DescribeFileOperationOutput(
                "a line such as \"Deleted file: 'save.json'\" or " +
                "\"Deleted files: 'save.json', 'settings.json'\" when it succeeds")));
    }

    /// <summary>
    /// Executes the "file delete" logic against an already-constructed client.
    /// The destructive-confirmation decision is resolved by the caller and
    /// passed in as <paramref name="confirmedDestructive"/>, so this method's
    /// branches can be unit-tested without the interactive prompt. It takes no
    /// stdout: a file operation writes nothing there.
    /// </summary>
    internal static async Task<int> RunDeleteAsync(
        PlayFabApiClient client,
        string entityId,
        string entityType,
        string[] names,
        bool confirmedDestructive,
        string outputFormat,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        if (!confirmedDestructive)
        {
            return 1;
        }

        var (apiPath, request) = BuildDeleteCall(entityId, entityType, names);
        var jsonBody = JsonSerializer.Serialize(request, RequestOptions);

        try
        {
            await client.CallApiAsync(apiPath, jsonBody, confirmedDestructive: true, cancellationToken: cancellationToken);

            var fileList = string.Join(", ", names.Select(n => $"'{n}'"));
            stderr.WriteLine($"Deleted {(names.Length == 1 ? "file" : "files")}: {fileList}");
            return 0;
        }
        catch (PlayFabApiException ex)
        {
            var compact = outputFormat.Equals("json-compact", StringComparison.OrdinalIgnoreCase);
            stderr.WriteLine(Program.FormatErrorOutput(ex, compact));
            return 1;
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"Request failed: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Extracts just the file metadata entries from the raw GetFiles response.
    /// The Metadata field is a dictionary keyed by file name.
    /// </summary>
    /// <summary>
    /// Projects the <c>Metadata</c> map out of a <c>GetFiles</c> response.
    /// </summary>
    /// <remarks>
    /// A response that will not parse is a failed request, not a result: returning the raw body
    /// here wrote it to stdout and reported success, which breaks the rule that a failure emits no
    /// success-shaped stdout. Letting it throw routes it to the caller's error path — the same
    /// place the table branch already lands for the identical response.
    /// </remarks>
    private static string FormatFileMetadata(string rawResponse, bool compact)
    {
        using var doc = ParseFileMetadataResponse(rawResponse);
        {
            var dataElement = GetDataElement(doc.RootElement);

            if (dataElement.TryGetProperty("Metadata", out var metadata))
            {
                var options = compact
                    ? new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }
                    : OutputOptions;
                return JsonSerializer.Serialize(metadata, options);
            }

            return "{}";
        }
    }

    private static string FormatFileMetadataTable(string rawResponse, TableView view)
    {
        using var doc = ParseFileMetadataResponse(rawResponse);
        var dataElement = GetDataElement(doc.RootElement);

        // GetFiles may omit Metadata instead of returning an empty object. The JSON path has long
        // treated that as an empty result; table output should do the same rather than surfacing a
        // missing-collection error for a valid empty response.
        if (dataElement.ValueKind == JsonValueKind.Object
            && !dataElement.TryGetProperty("Metadata", out _))
        {
            return TableFormatter.EmptyNotice;
        }

        return Program.FormatTableOutput(rawResponse, view);
    }

    private static JsonDocument ParseFileMetadataResponse(string rawResponse)
    {
        try
        {
            return JsonDocument.Parse(rawResponse);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            throw new InvalidDataException(
                "The file list response was not valid JSON, so it cannot be shown. Re-run with --verbose to see the request.",
                exception);
        }
    }

    private static JsonElement GetDataElement(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data)
            ? data
            : root;
}
