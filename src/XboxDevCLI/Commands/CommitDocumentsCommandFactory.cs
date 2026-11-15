// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using System.Text.Json;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Output;
using XboxDevCLI.Services;

namespace XboxDevCLI.Commands;

internal static class CommitDocumentsCommandFactory
{
    /// <summary>The write was sent, but whether it took effect is unknown.</summary>
    internal const int OutcomeUnknownExitCode = 3;

    public static Command Create(PublishConfirmationDependencies? confirmationDependencies = null)
    {
        var dependencies = confirmationDependencies ?? PublishConfirmationDependencies.Default;
        var sellerOption = new XdtsSellerOption();
        var typeOption = new Option<string>("--type")
        {
            Description = "Document scope: 'sandbox' or 'account'. Default: sandbox.",
            DefaultValueFactory = _ => "sandbox",
        };
        typeOption.Validators.Add(result =>
            ValidateAllowedValue(result, "sandbox", "account"));
        var target = new XboxLiveConfigurationTarget(
            result => !string.Equals(result.GetValue(typeOption), "account", StringComparison.OrdinalIgnoreCase));
        var sandboxOption = new Option<string?>("--sandbox-id")
        {
            HelpName = "id",
            Description = "Case-sensitive sandbox ID. Required for the default sandbox scope.",
        };
        var filesOption = new Option<string[]>("--file")
        {
            Description =
                "Local file or filename glob pattern. Repeat --file to select multiple inputs. " +
                "Each base filename is sent as the " +
                "document name. Every supplied value must match at least one file.",
            Required = true,
            Arity = ArgumentArity.OneOrMore,
            AllowMultipleArgumentsPerToken = false,
        };
        filesOption.Validators.Add(result =>
        {
            // Collection arity applies across occurrences, not to each repeated option.
            if (result.Tokens.Count != result.IdentifierTokenCount ||
                result.Tokens.Any(token => string.IsNullOrWhiteSpace(token.Value)))
            {
                result.AddError("Each --file requires one non-empty path or glob pattern.");
            }
        });
        var validateOnlyOption = new Option<bool>("--validate-only")
        {
            Description = "Validate without saving. Default: false, which saves documents.",
        };
        var eTagOption = new Option<string?>("--etag")
        {
            Description = "ETag for optimistic concurrency. Default: the saved ETag.",
        };
        var forceOption = new Option<bool>("--force")
        {
            Description =
                "Save over changes made since the saved ETag, using the server's current ETag. Default: false.",
        };
        var messageOption = new Option<string?>("--message")
        {
            Description = "Save message. Default: omitted.",
        };
        var allowSharedOption = XboxLiveConfigurationTarget.CreateAllowSharedConfigurationOption();

        var command = new Command(
            "save",
            "Validate or save XBOX services configuration documents to the working configuration");
        command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.RemoteMutation,
            Scope: "A sandbox or account XBOX services working configuration document set.",
            Output: "The validation or save result and local ETag metadata as JSON or a table.",
            Examples:
            [
                "xbox xbl config save --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id XDKS.1 --file \"xbox-config/*.xml\" --validate-only",
                "xbox xbl config save --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id XDKS.1 --file xbox-config/achievements2017.xml --file xbox-config/localization.xml --output table",
                "xbox xbl config save --type account --file \"xbox-config/*.xml\" --message \"Update account configuration\"",
                "xbox xbl config save --title-id 1234ABCD --sandbox-id XDKS.1 --file \"xbox-config/*.xml\" --validate-only",
            ],
            SafetyDetails:
                "Without --validate-only, saves the selected working configuration documents. " +
                "Every title bound to a primary title shares its configuration. A --title-id that selects a " +
                "bound title is refused, even with --validate-only; rerun with the primary's --scid. A write by " +
                "--title-id, validation included, to configuration other titles are bound to lists them and " +
                "asks before changing it; without a terminal it's refused unless you pass " +
                "--allow-shared-configuration. A write by --scid isn't checked.",
            Table: XboxPartnerCommandSupport.DocumentCommitTableView));
        sellerOption.AddTo(command);
        command.Options.Add(typeOption);
        target.AddTo(command);
        command.Options.Add(sandboxOption);
        command.Options.Add(filesOption);
        command.Options.Add(validateOnlyOption);
        command.Options.Add(eTagOption);
        command.Options.Add(forceOption);
        command.Options.Add(messageOption);
        command.Options.Add(allowSharedOption);
        command.Validators.Add(result =>
        {
            string type = result.GetValue(typeOption) ?? "sandbox";
            string? sandbox = result.GetValue(sandboxOption);
            if (type.Equals("sandbox", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsValidSandbox(sandbox))
                {
                    result.AddError(
                        "--sandbox-id is required for sandbox documents and cannot contain " +
                        "whitespace or path characters.");
                }
            }
            else
            {
                if (result.GetResult("--scid") is OptionResult { Implicit: false } || !string.IsNullOrWhiteSpace(sandbox))
                {
                    result.AddError("--scid and --sandbox-id cannot be used with account documents.");
                }

                if (result.GetResult("--title-id") is OptionResult { Implicit: false })
                {
                    result.AddError("--title-id cannot be used with account documents.");
                }

                if (result.GetResult(allowSharedOption) is { Implicit: false })
                {
                    result.AddError(
                        $"{XboxLiveConfigurationTarget.AllowSharedConfigurationOptionName} cannot be used with account documents.");
                }
            }
        });
        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var output = OutputOptions.From(parseResult);
            var verbose = parseResult.GetValue(Program.VerboseOption);
            try
            {
                string type = parseResult.GetValue(typeOption) ?? "sandbox";
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
                var selected = type.Equals("sandbox", StringComparison.OrdinalIgnoreCase)
                    ? await target.SelectAsync(parseResult, sellerId)
                    : null;
                string description = $"{(parseResult.GetValue(validateOnlyOption) ? "Validate" : "Save")} " +
                    $"{type} documents";
                Program.WriteVerbose(verbose, () => selected is null
                    ? description
                    : $"{description} for {selected.Describe()} in {parseResult.GetValue(sandboxOption)}");
                return await RunAsync(
                    client,
                    selected?.SellerId ?? sellerId,
                    type,
                    selected?.Scid,
                    parseResult.GetValue(sandboxOption),
                    parseResult.GetValue(filesOption) ?? [],
                    parseResult.GetValue(validateOnlyOption),
                    parseResult.GetValue(forceOption),
                    parseResult.GetValue(eTagOption),
                    parseResult.GetValue(messageOption),
                    output,
                    Console.Out,
                    Console.Error,
                    cancellationToken,
                    selected?.TitleId,
                    parseResult.GetValue(allowSharedOption),
                    dependencies,
                    string.Join(' ', RecoveryCommandArguments.Create(parseResult))).ConfigureAwait(false);
            }
            catch (XboxPartnerApiException ex)
            {
                Console.Error.WriteLine(XboxPartnerCommandErrorFormatter.Format(ex, output.IsCompact));
                return 1;
            }
            catch (XboxPartnerResponseException ex)
            {
                XboxPartnerCommandSupport.WriteCorrelationId(ex.CorrelationId, Console.Error);
                return XboxPartnerCommandSupport.WriteLocalError(ex);
            }
            catch (Exception ex) when (!XboxPartnerCommandSupport.IsCallerCancellation(ex, cancellationToken))
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
        string documentType,
        Guid? scid,
        string? sandbox,
        IReadOnlyList<string> filePatterns,
        bool validateOnly,
        bool force,
        string? eTag,
        string? message,
        OutputOptions output,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken,
        string? titleId = null,
        bool allowSharedConfiguration = false,
        PublishConfirmationDependencies? sharedConfirmation = null,
        string? recoveryPrefix = null)
    {
        bool compact = output.IsCompact;
        TableView? tableView = output.TableView;
        string[] files = [];
        var sent = false;
        string? sentCorrelationId = null;
        var answered = false;
        XboxLiveResolvedConfigurationTarget? resolved = null;
        try
        {
            string normalizedDocumentType = documentType.Trim().ToLowerInvariant();
            string? normalizedSandbox = NormalizeOptional(sandbox);
            files = ResolveFiles(filePatterns);
            IReadOnlyList<XboxDocumentUpload> documents = await ReadDocumentsAsync(
                files,
                cancellationToken).ConfigureAwait(false);
            string eTagPath = GetEtagPath(
                files[0],
                normalizedDocumentType,
                normalizedSandbox);
            ValidateEtagDestination(eTagPath);
            string? resolvedEtag = force
                ? null
                : NormalizeOptional(eTag) ?? await ReadSavedEtagAsync(
                    eTagPath,
                    cancellationToken).ConfigureAwait(false);
            var request = new XboxLiveCommitDocumentsRequest(
                normalizedDocumentType,
                scid,
                normalizedSandbox,
                validateOnly,
                force,
                resolvedEtag,
                NormalizeOptional(message),
                documents,
                titleId,
                allowSharedConfiguration);
            XboxPartnerOperationResult<XboxLiveCommitResponse> response;
            try
            {
                response = await SendAsync(request).ConfigureAwait(false);
            }
            catch (XboxPartnerApiException ex) when (
                !request.AllowSharedConfiguration &&
                XboxLiveConfigurationTarget.ReadSharedConfiguration(ex) is not null)
            {
                // The service refused the first send, so nothing was saved.
                sent = false;
                if (!await XboxLiveConfigurationTarget.ConfirmSharedConfigurationAsync(
                    XboxLiveConfigurationTarget.ReadSharedConfiguration(ex)!, ex, compact, validateOnly,
                    sharedConfirmation, stderr,
                    cancellationToken).ConfigureAwait(false))
                {
                    return 1;
                }

                request = request with { AllowSharedConfiguration = true };
                response = await SendAsync(request).ConfigureAwait(false);
            }

            async Task<XboxPartnerOperationResult<XboxLiveCommitResponse>> SendAsync(
                XboxLiveCommitDocumentsRequest body)
            {
                resolved = null;
                try
                {
                    using (RequestProgress.Start(DescribeCommit(body), stderr))
                    {
                        return await client.CommitXboxLiveDocumentsAsync(
                            sellerId, body, cancellationToken,
                            target => resolved = target,
                            onSending: correlationId =>
                            {
                                sent = true;
                                sentCorrelationId = correlationId;
                            })
                            .ConfigureAwait(false);
                    }
                }
                finally
                {
                    if (resolved is not null)
                    {
                        XboxLiveConfigurationTarget.WriteResolved(resolved, sellerId, stderr);
                    }
                }
            }

            answered = true;
            XboxLiveCommitResponse result = response.Value;
            IReadOnlyList<XboxLiveValidationMessage> validationMessages =
                XboxLiveValidationMessages.Read(result.ValidationInfo);
            bool failed = IsValidationFailure(result, validateOnly, validationMessages);
            string? responseEtag = NormalizeOptional(result.ETag);
            string? savedEtagPath = null;

            // A refused commit leaves the saved ETag alone, so the retry checks the same version.
            // Once a save is accepted, Ctrl+C no longer stops the command: the small ETag write
            // finishes and the result is written, so the next save doesn't fail on a stale ETag.
            if (!failed && !string.IsNullOrWhiteSpace(responseEtag))
            {
                savedEtagPath = await SaveEtagAsync(
                    eTagPath,
                    responseEtag,
                    validateOnly ? cancellationToken : CancellationToken.None).ConfigureAwait(false);
            }

            stdout.WriteLine(tableView is not null
                ? XboxPartnerTableFormatter.FormatRows(
                    tableView,
                    [
                        [
                            normalizedDocumentType,
                            normalizedSandbox ?? "",
                            validateOnly.ToString(),
                            result.CanCommit?.ToString() ?? "",
                            result.Committed?.ToString() ?? "",
                            files.Length.ToString(
                                System.Globalization.CultureInfo.InvariantCulture),
                            savedEtagPath ?? "",
                        ],
                    ])
                : JsonSerializer.Serialize(
                    new
                    {
                        result,
                        etagFile = savedEtagPath,
                        files,
                    },
                    compact ? CompactJsonOptions : PrettyJsonOptions));
            if (!failed)
            {
                return 0;
            }

            WriteValidationFailure(validationMessages, validateOnly, stderr);
            return 1;
        }
        catch (OperationCanceledException) when (!validateOnly && !sent && cancellationToken.IsCancellationRequested)
        {
            stderr.WriteLine("Canceled before the request was sent, so no documents were saved.");
            return 1;
        }
        catch (Exception ex) when (!validateOnly && sent && !answered
            && XboxPartnerCommandSupport.IsUncertainSubmission(ex))
        {
            XboxPartnerCommandSupport.WriteUncertainCause(ex, compact, stderr, cancellationToken, sentCorrelationId);
            stderr.WriteLine("It is unknown whether the documents were saved. Don't resubmit them.");
            stderr.WriteLine(
                $"Check with: {FormatDownloadCheck(documentType, scid, sandbox, sellerId, titleId, resolved, recoveryPrefix)}");
            return OutcomeUnknownExitCode;
        }
        catch (XboxPartnerApiException ex)
        {
            stderr.WriteLine(XboxPartnerCommandErrorFormatter.Format(
                ex,
                compact,
                CreateInvalidDocumentNameGuidance(ex, files)));
            return 1;
        }
        catch (XboxPartnerResponseException ex)
        {
            XboxPartnerCommandSupport.WriteCorrelationId(ex.CorrelationId, stderr);
            stderr.WriteLine($"Request failed: {ex.Message}");
            return 1;
        }
        catch (Exception ex) when (!XboxPartnerCommandSupport.IsCallerCancellation(ex, cancellationToken))
        {
            stderr.WriteLine($"Request failed: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// The read-only command that shows whether an interrupted save landed: the latest working
    /// documents, which a saved set replaces.
    /// </summary>
    internal static string FormatDownloadCheck(
        string documentType, Guid? scid, string? sandbox, long? sellerId = null,
        string? titleId = null, XboxLiveResolvedConfigurationTarget? resolved = null,
        string? recoveryPrefix = null)
    {
        string prefix = recoveryPrefix ?? CliIdentity.CommandName;
        if (string.Equals(documentType.Trim(), "account", StringComparison.OrdinalIgnoreCase))
        {
            long? seller = resolved?.SellerId ?? sellerId;
            return $"{prefix} xbl config download --type account " +
                (seller is { } s ? $"--seller-id {s.ToString(CultureInfo.InvariantCulture)} " : "") +
                "--view draft --dest <directory>";
        }

        return $"{prefix} xbl config download " +
            XboxPartnerCommandSupport.FormatConfigurationTarget(resolved, scid, titleId, sellerId) +
            $" --sandbox-id {ShellQuoting.Quote(sandbox?.Trim() ?? "")} --view draft --dest <directory>";
    }

    internal static string DescribeCommit(XboxLiveCommitDocumentsRequest request)
    {
        int count = request.Documents.Count;
        string documents = FormattableString.Invariant(
            $"{count} {(request.Sandbox is null ? "account " : "")}{(count == 1 ? "document" : "documents")}");
        return (request.ValidateOnly, request.Sandbox) switch
        {
            (true, null) => $"Validating {documents}",
            (true, { } sandbox) => $"Validating {documents} for {sandbox}",
            (false, null) => $"Saving {documents}",
            (false, { } sandbox) => $"Saving {documents} to {sandbox}",
        };
    }

    private static readonly JsonSerializerOptions PrettyJsonOptions = new()
    {
        WriteIndented = true,
    };

    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        WriteIndented = false,
    };

    private static string[] ResolveFiles(IReadOnlyList<string> patterns)
    {
        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string patternValue in patterns)
        {
            string pattern = patternValue?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(pattern))
            {
                throw new ArgumentException(
                    "--file values must not be empty.",
                    nameof(patterns));
            }

            IEnumerable<string> matches;
            if (File.Exists(pattern))
            {
                matches = [Path.GetFullPath(pattern)];
            }
            else
            {
                string? directory = Path.GetDirectoryName(pattern);
                string root = Path.GetFullPath(
                    string.IsNullOrWhiteSpace(directory)
                        ? Directory.GetCurrentDirectory()
                        : directory);
                if (!Directory.Exists(root))
                {
                    throw new DirectoryNotFoundException(
                        $"Document directory does not exist: {root}");
                }

                string searchPattern = Path.GetFileName(pattern);
                matches = Directory.GetFiles(root, searchPattern, SearchOption.TopDirectoryOnly);
            }

            string[] orderedMatches =
            [
                .. matches.OrderBy(
                    static path => path,
                    StringComparer.OrdinalIgnoreCase),
            ];
            if (orderedMatches.Length == 0)
            {
                throw new FileNotFoundException(
                    $"No files matched --file value '{patternValue}'. " +
                    "Check the path or pattern, or remove it.");
            }

            foreach (string match in orderedMatches)
            {
                string fullPath = Path.GetFullPath(match);
                if (seen.Add(fullPath))
                {
                    files.Add(fullPath);
                }
            }
        }

        if (files.Count == 0)
        {
            throw new FileNotFoundException("There are no files selected to save.");
        }

        return files.ToArray();
    }

    /// <summary>
    /// Whether XBOX services didn't validate or commit the documents. Documents that match the latest ones
    /// aren't a failure.
    /// </summary>
    private static bool IsValidationFailure(
        XboxLiveCommitResponse result,
        bool validateOnly,
        IReadOnlyList<XboxLiveValidationMessage> messages)
    {
        bool succeeded = validateOnly ? result.CanCommit == true : result.Committed == true;
        return !succeeded && !XboxLiveValidationMessages.IsUnchanged(messages);
    }

    /// <summary>Writes the validation messages to stderr after XBOX services refused the documents.</summary>
    private static void WriteValidationFailure(
        IReadOnlyList<XboxLiveValidationMessage> messages,
        bool validateOnly,
        TextWriter stderr)
    {
        string summary = validateOnly
            ? "XBOX services can't save these documents"
            : "XBOX services didn't save these documents";
        XboxLiveValidationMessage[] shown = [.. messages.Where(static message => message.HasText)];
        stderr.WriteLine(shown.Length == 0 ? $"{summary}." : $"{summary}:");
        foreach (XboxLiveValidationMessage message in shown)
        {
            stderr.WriteLine(XboxLiveValidationMessages.Format(message));
        }
    }

    private static async Task<IReadOnlyList<XboxDocumentUpload>> ReadDocumentsAsync(
        IReadOnlyList<string> files,
        CancellationToken cancellationToken)
    {
        long totalBytes = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var documents = new List<XboxDocumentUpload>(files.Count);
        foreach (string file in files)
        {
            EnsureRegularFile(file, "Document");
            string name = Path.GetFileName(file);
            ValidateDocumentFileName(name);
            if (!names.Add(name))
            {
                throw new InvalidOperationException(
                    $"Multiple selected files have the document name '{name}'.");
            }

            long fileLength = new FileInfo(file).Length;
            if (fileLength > XboxUploadPolicy.MaxDocumentContentBytes - totalBytes)
            {
                throw new InvalidOperationException(
                    "The total document content cannot exceed 50 MiB.");
            }

            byte[] content = await File.ReadAllBytesAsync(file, cancellationToken)
                .ConfigureAwait(false);
            totalBytes += content.LongLength;
            if (totalBytes > XboxUploadPolicy.MaxDocumentContentBytes)
            {
                throw new InvalidOperationException(
                    "The total document content cannot exceed 50 MiB.");
            }

            documents.Add(new XboxDocumentUpload(name, content));
        }

        return documents;
    }

    internal static void ValidateDocumentFileName(string name)
    {
        if (LocalFileNameValidator.IsInvalid(name, enforceWindowsRules: true))
        {
            throw new InvalidDataException(
                $"Document file name is not supported: '{name}'.");
        }
    }

    private static string? CreateInvalidDocumentNameGuidance(
        XboxPartnerApiException exception,
        IReadOnlyList<string> files)
    {
        if (!exception.ResponseBody.Contains(
                "Invalid Document Name",
                StringComparison.OrdinalIgnoreCase) ||
            files.Count == 0)
        {
            return null;
        }

        string[] names = files
            .Select(static file => Path.GetFileName(file) ?? string.Empty)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (names.Length == 1)
        {
            return $"The selected document name '{names[0]}' was rejected. " +
                "Check the selected file name, or select a different document " +
                "with --file <path>.";
        }

        const int displayedNameLimit = 10;
        string selectedNames = string.Join(
            ", ",
            names.Take(displayedNameLimit).Select(static name => $"'{name}'"));
        if (names.Length > displayedNameLimit)
        {
            selectedNames +=
                $" (and {names.Length - displayedNameLimit} more)";
        }

        return "The error did not identify which selected document name was invalid. " +
            $"Selected names: {selectedNames}. Run again with --validate-only and one " +
            "--file <path> value at a time to identify the offending file, then rename it " +
            "or exclude it from --file.";
    }

    private static async Task<string?> ReadSavedEtagAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        EnsureRegularFile(path, "ETag");
        return NormalizeOptional(await File.ReadAllTextAsync(path, cancellationToken)
            .ConfigureAwait(false));
    }

    private static async Task<string> SaveEtagAsync(
        string path,
        string eTag,
        CancellationToken cancellationToken)
    {
        string temporaryPath = Path.Combine(
            Path.GetDirectoryName(path)!,
            $".xboxdevcli-etag-{Guid.NewGuid():N}");
        FileAttributes? originalAttributes = null;
        try
        {
            await File.WriteAllTextAsync(temporaryPath, eTag, cancellationToken)
                .ConfigureAwait(false);
            if (File.Exists(path))
            {
                originalAttributes = File.GetAttributes(path);
                File.SetAttributes(path, originalAttributes.Value & ~FileAttributes.Hidden);
            }

            File.Move(temporaryPath, path, overwrite: true);
            if (OperatingSystem.IsWindows())
            {
                File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
            }
        }
        catch
        {
            if (originalAttributes.HasValue && File.Exists(path))
            {
                File.SetAttributes(path, originalAttributes.Value);
            }

            throw;
        }
        finally
        {
            File.Delete(temporaryPath);
        }

        return path;
    }

    private static string GetEtagPath(
        string firstFile,
        string documentType,
        string? sandbox)
    {
        string fileName = documentType.Equals("account", StringComparison.OrdinalIgnoreCase)
            ? "Account.etag"
            : $"{sandbox}.etag";
        if (LocalFileNameValidator.IsInvalid(
                fileName,
                enforceWindowsRules: OperatingSystem.IsWindows()))
        {
            throw new InvalidDataException(
                $"The sandbox cannot be used as a local ETag file name: '{fileName}'.");
        }

        return Path.Combine(Path.GetDirectoryName(firstFile)!, fileName);
    }

    private static void ValidateEtagDestination(string path)
    {
        if (Directory.Exists(path))
        {
            throw new IOException("The ETag path is an existing directory.");
        }

        if (File.Exists(path))
        {
            EnsureRegularFile(path, "ETag");
        }
    }

    private static string? NormalizeOptional(string? value)
    {
        string? normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static void EnsureRegularFile(string path, string description)
    {
        // These checks prevent accidental reparse-point use. A concurrent local process
        // replacing a path after validation is outside the CLI threat model.
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new IOException($"{description} path must be a regular file: {path}");
        }
    }

    private static bool IsValidSandbox(string? sandbox)
    {
        string value = sandbox?.Trim() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value) &&
            !value.Any(char.IsWhiteSpace) &&
            value.IndexOfAny(['/', '\\', ':', '\0']) < 0 &&
            value is not "." and not ".." &&
            !LocalFileNameValidator.IsInvalid(
                $"{value}.etag",
                enforceWindowsRules: OperatingSystem.IsWindows());
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
