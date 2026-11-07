// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Output;
using XboxDevCLI.Services;

namespace XboxDevCLI.Commands;

internal static class PublishCommandFactory
{
    /// <summary>Global sandboxes where a publish reaches certification or players.</summary>
    private static readonly HashSet<string> GlobalDestinations =
        new(["RETAIL", "CERT", "CERT.DEBUG"], StringComparer.OrdinalIgnoreCase);

    /// <summary>The sandbox XORC publishes the title-wide configuration to.</summary>
    private const string TitleWideSandbox = "HIDDEN";

    public static Command Create(PublishConfirmationDependencies? confirmationDependencies = null)
    {
        var dependencies = confirmationDependencies ?? PublishConfirmationDependencies.Default;
        var sellerOption = new XdtsSellerOption();
        var scidOption = CreateRequiredGuidOption("--scid", "-s", "The service configuration ID.");
        var sandboxOption = CreateRequiredSandboxOption(
            "--sandbox-id",
            "The destination sandbox ID whose published configuration will be updated.")
            .AsContextValue(ContextEndpoint.XboxPartner);
        var sourceSandboxOption = CreateOptionalSandboxOption(
            "--source-sandbox-id",
            "The source sandbox ID. Default: --sandbox-id.");
        var validateOnlyOption = new Option<bool>("--validate-only", "-v")
        {
            Description = "Validate without publishing. Default: false, which starts publishing.",
        };
        var configSetOption = new Option<ulong?>("--configset")
        {
            Description =
                "The source sandbox's working version to publish. Default: its latest working version.",
        };
        configSetOption.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<ulong?>() == 0)
            {
                result.AddError("--configset must be positive when provided.");
            }
        });
        var republishOption = new Option<bool>("--republish")
        {
            Description =
                "Publish in full even when nothing changed since the last publish. On a RETAIL locked " +
                "after a migration, forces the selected configuration, by default RETAIL's latest working one, " +
                "onto RETAIL. Default: false, which publishes only changes.",
        };
        var confirmOption = new Option<bool>("--confirm", "--yes", "-y")
        {
            Description =
                "Confirm publishing to RETAIL, CERT, CERT.DEBUG, or HIDDEN, or from another " +
                "sandbox, without prompting.",
        };

        var command = new Command(
            "start",
            "Publish XBOX services configuration documents");
        command.Validators.Add(result =>
        {
            if (result.GetValue(republishOption) && result.GetValue(validateOnlyOption))
            {
                result.AddError("--republish publishes, so it can't be combined with --validate-only.");
            }

            if (string.Equals(result.GetValue(sandboxOption), TitleWideSandbox, StringComparison.OrdinalIgnoreCase))
            {
                string[] given = [
                    .. result.GetResult(sourceSandboxOption) is { Implicit: false } ? ["--source-sandbox-id"] : Array.Empty<string>(),
                    .. result.GetResult(configSetOption) is { Implicit: false } ? ["--configset"] : Array.Empty<string>(),
                ];
                if (given.Length > 0)
                {
                    result.AddError(
                        "A title-wide (HIDDEN) publish always publishes the latest ALL working configuration, " +
                        $"so it takes no source or version. Remove {string.Join(" and ", given)}.");
                }
            }
        });
        command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.RemoteMutation,
            Scope: "One XBOX services configuration set promoted from a source sandbox to a destination sandbox.",
            Output: "The publish operation result as JSON or a table. Validation diagnostics from " +
                "XBOX services, such as warnings, go to stderr.",
            Examples:
            [
                "xbox xbl publish start --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id XDKS.1",
                "xbox xbl publish start --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --source-sandbox-id STAGE.0 --sandbox-id RETAIL --validate-only",
                "xbox xbl publish start --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --source-sandbox-id STAGE.0 --sandbox-id RETAIL --confirm",
                "xbox xbl publish start --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --source-sandbox-id STAGE.0 --sandbox-id TARGET.0 --validate-only --output table",
                "xbox xbl publish start --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --source-sandbox-id STAGE.0 --sandbox-id RETAIL --configset 42 --confirm",
                "xbox xbl publish start --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id XDKS.1 --republish",
            ],
            LearnMore:
            [
                "Status AlreadyPublished means nothing changed since the last publish, so no publish " +
                "started. --republish publishes again.",
            ],
            SafetyDetails:
                "Without --validate-only, starts publishing configuration to the destination sandbox. " +
                "Publishing from a different --source-sandbox-id also replaces the destination's global " +
                "title storage files with the source's and deletes files only the destination holds. " +
                "Publishing to RETAIL, CERT, CERT.DEBUG, or HIDDEN (title-wide), or from a different " +
                "--source-sandbox-id, requires --confirm (--yes, -y) or terminal confirmation. " +
                "Agent approval is still required.",
            Table: XboxPartnerCommandSupport.PublishTableView));
        sellerOption.AddTo(command);
        command.Options.Add(scidOption);
        command.Options.Add(sandboxOption);
        command.Options.Add(sourceSandboxOption);
        command.Options.Add(validateOnlyOption);
        command.Options.Add(configSetOption);
        command.Options.Add(republishOption);
        command.Options.Add(confirmOption);
        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var output = OutputOptions.From(parseResult);
            var verbose = parseResult.GetValue(Program.VerboseOption);
            try
            {
                string sandbox = parseResult.GetValue(sandboxOption)!;
                var request = new XboxLivePublishRequest(
                    parseResult.GetValue(scidOption),
                    sandbox,
                    parseResult.GetValue(sourceSandboxOption) ?? sandbox,
                    parseResult.GetValue(validateOnlyOption),
                    parseResult.GetValue(configSetOption),
                    parseResult.GetValue(republishOption));
                bool confirmed = parseResult.GetResult(confirmOption) is { Implicit: false }
                    ? parseResult.GetValue(confirmOption)
                    : parseResult.GetValue(Program.ConfirmOption);
                bool proceed;
                try
                {
                    proceed = await ConfirmAsync(
                        request, confirmed, dependencies, Console.Error, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    Console.Error.WriteLine();
                    Console.Error.WriteLine("Publish canceled.");
                    return 1;
                }

                if (!proceed)
                {
                    return 1;
                }

                cancellationToken.ThrowIfCancellationRequested();
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
                    () => $"{(request.ValidateOnly ? "Validate" : request.Republish ? "Republish" : "Publish")} " +
                        $"{request.Scid:D} " +
                        $"from {request.SourceSandbox} " +
                        $"(working version {request.ConfigSetVersion?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "latest"}) " +
                        $"to {request.Sandbox}");
                return await RunAsync(
                    client,
                    sellerId,
                    request,
                    output,
                    Console.Out,
                    Console.Error,
                    cancellationToken,
                    writeGlobalStorageWarning: !Prompts(request, confirmed)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Request failed: {ex.Message}");
                return 1;
            }
        });

        return command;
    }

    /// <summary>
    /// Sends the publish and writes its result. <paramref name="writeGlobalStorageWarning"/> is false when the
    /// confirmation prompt already showed the global title storage warning.
    /// </summary>
    internal static async Task<int> RunAsync(
        XboxPartnerClient client,
        long? sellerId,
        XboxLivePublishRequest request,
        OutputOptions output,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken,
        bool writeGlobalStorageWarning = true)
    {
        bool compact = output.IsCompact;
        TableView? tableView = output.TableView;
        string? globalStorageWarning = writeGlobalStorageWarning ? FormatGlobalStorageWarning(request) : null;
        if (globalStorageWarning is not null)
        {
            stderr.WriteLine(globalStorageWarning);
        }

        try
        {
            string response;
            using (RequestProgress.Start(DescribePublish(request), stderr))
            {
                response = await client
                    .PublishXboxLiveConfigurationAsync(sellerId, request, cancellationToken)
                    .ConfigureAwait(false);
            }

            XboxLivePublishResponse result = ParseResponse(response);
            WriteDiagnostics(result, stderr);
            if (string.Equals(result.Status, AlreadyPublishedStatus, StringComparison.OrdinalIgnoreCase))
            {
                stderr.WriteLine(
                    $"Nothing changed in {request.SourceSandbox} since its last publish to {request.Sandbox}, " +
                    "so no publish started. Use --republish to publish it again.");
            }

            stdout.WriteLine(tableView is not null
                ? XboxPartnerTableFormatter.FormatRows(
                    tableView,
                    [
                        [
                            result.Status!,
                            request.SourceSandbox,
                            request.Sandbox,
                            request.ValidateOnly.ToString(),
                            result.SourceVersionText ??
                                request.ConfigSetVersion?.ToString(
                                    System.Globalization.CultureInfo.InvariantCulture) ??
                                "latest",
                        ],
                    ])
                : PlayFabApiClient.FormatJson(response, compact));
            if (FormatSourceWarning(request, result) is { } warning)
            {
                stderr.WriteLine(warning);
            }

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

    internal static string DescribePublish(XboxLivePublishRequest request)
    {
        string verb = request.ValidateOnly ? "Validating publish"
            : request.Republish ? "Republishing"
            : "Publishing";
        return string.Equals(request.SourceSandbox, request.Sandbox, StringComparison.OrdinalIgnoreCase)
            ? $"{verb} to {request.Sandbox}"
            : $"{verb} from {request.SourceSandbox} to {request.Sandbox}";
    }

    // A cross-sandbox publish also runs XORC's global storage copy, which mirrors the
    // source's global title storage files into the destination. XORC and the gateway compare
    // sandbox IDs ignoring case, so a case-only difference publishes in place and copies nothing.
    internal static string? FormatGlobalStorageWarning(XboxLivePublishRequest request) =>
        string.Equals(request.SourceSandbox, request.Sandbox, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"Warning: publishing from {request.SourceSandbox} to {request.Sandbox} replaces " +
                $"{request.Sandbox}'s global title storage files with {request.SourceSandbox}'s. " +
                $"Files with the same path are overwritten, files only {request.SourceSandbox} " +
                $"holds are added, and files only {request.Sandbox} holds are deleted. " +
                "Deleted files can't be recovered.";

    /// <summary>
    /// Says when the source's latest working version, which a publish without --configset uses, isn't
    /// what's live in the source, and names the working version that is.
    /// </summary>
    internal static string? FormatSourceWarning(
        XboxLivePublishRequest request,
        XboxLivePublishResponse result)
    {
        if (result.SourceHasUnpublishedChanges != true)
        {
            return null;
        }

        string action = request.ValidateOnly ? "validation" : "publish";
        string latest = result.SourceVersionText is { } version
            ? $"its latest working version, {XboxPartnerCommandSupport.ForTerminal(version)}"
            : "its latest working version";
        if (result.SourceLiveVersion is not ulong live)
        {
            return $"Warning: {request.SourceSandbox} has unpublished changes. This {action} uses " +
                $"{latest}, which isn't what's live in {request.SourceSandbox}.";
        }

        string liveText = live.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return $"Warning: {request.SourceSandbox} has unpublished changes. This {action} uses " +
            $"{latest}; what's live in {request.SourceSandbox} is working version {liveText}. " +
            $"To {(request.ValidateOnly ? "validate" : "publish")} that instead, add --configset {liveText}.";
    }

    private static XboxLivePublishResponse ParseResponse(string response)
    {
        XboxLivePublishResponse result =
            XboxPartnerCommandSupport.DeserializeResponse<XboxLivePublishResponse>(
                response,
                "publish");
        if (string.IsNullOrWhiteSpace(result.Status))
        {
            throw new InvalidDataException(
                "The publish response did not contain a status.");
        }

        return result;
    }

    /// <summary>The status XboxPartner reports when nothing changed since the last publish.</summary>
    internal const string AlreadyPublishedStatus = "AlreadyPublished";

    /// <summary>
    /// Returns why publishing <paramref name="request"/> needs confirmation, one sentence per reason, or
    /// nothing when it doesn't. A validation never does.
    /// </summary>
    internal static IReadOnlyList<string> DescribeHighImpact(XboxLivePublishRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ValidateOnly)
        {
            return [];
        }

        var reasons = new List<string>();
        if (string.Equals(request.Sandbox, "RETAIL", StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add("RETAIL is live: players see the change when the publish finishes.");
        }
        else if (GlobalDestinations.Contains(request.Sandbox))
        {
            reasons.Add($"{request.Sandbox} is the sandbox XBOX certification tests in.");
        }
        else if (string.Equals(request.Sandbox, TitleWideSandbox, StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add(
                "HIDDEN publishes the title-wide configuration, which every sandbox uses, RETAIL included.");
        }

        if (!string.Equals(request.SourceSandbox, request.Sandbox, StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add(
                $"Publishing from {request.SourceSandbox} first replaces {request.Sandbox}'s working " +
                "documents that have the same names.");
        }

        return reasons;
    }

    /// <summary>
    /// Whether <see cref="ConfirmAsync"/> describes <paramref name="request"/> and asks, which it does for a
    /// publish <see cref="DescribeHighImpact"/> flags unless it's already <paramref name="confirmed"/>.
    /// </summary>
    internal static bool Prompts(XboxLivePublishRequest request, bool confirmed) =>
        !confirmed && DescribeHighImpact(request).Count > 0;

    /// <summary>
    /// Asks before a publish that <see cref="DescribeHighImpact"/> flags. Returns whether to go ahead:
    /// always when nothing is flagged or <paramref name="confirmed"/>, otherwise only when the user says
    /// yes at a terminal. Without a terminal it refuses. Cancellation ends a wait for the answer.
    /// </summary>
    internal static async Task<bool> ConfirmAsync(
        XboxLivePublishRequest request,
        bool confirmed,
        PublishConfirmationDependencies dependencies,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> reasons = DescribeHighImpact(request);
        if (!Prompts(request, confirmed))
        {
            return true;
        }

        string? version = request.ConfigSetVersion?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string configuration =
            string.Equals(request.Sandbox, TitleWideSandbox, StringComparison.OrdinalIgnoreCase)
                ? "the latest title-wide configuration (the working documents in ALL)"
                : version is null
                    ? $"{request.SourceSandbox}'s latest working configuration"
                    : $"working version {version} of {request.SourceSandbox}'s configuration";
        stderr.WriteLine(
            $"This publishes {configuration} to {request.Sandbox} for SCID {request.Scid:D}.");
        foreach (string reason in reasons)
        {
            stderr.WriteLine(reason);
        }

        if (request.Republish)
        {
            stderr.WriteLine(
                "--republish publishes in full even if nothing changed, and forces the selected configuration, by default RETAIL's latest working one, onto a locked RETAIL.");
        }

        // Shown here, before the answer, so RunAsync doesn't repeat it after a prompt.
        if (FormatGlobalStorageWarning(request) is string globalStorageWarning)
        {
            stderr.WriteLine(globalStorageWarning);
        }

        if (!dependencies.IsInteractive())
        {
            stderr.WriteLine(
                "Re-run with --confirm (--yes, -y) to publish without an interactive prompt.");
            return false;
        }

        stderr.Write($"Publish to {request.Sandbox}? [y/N]: ");
        stderr.Flush();
        string? answer = (await dependencies.ReadLineAsync(cancellationToken).ConfigureAwait(false))?.Trim();
        if (string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        stderr.WriteLine("Publish canceled.");
        return false;
    }

    /// <summary>
    /// Writes the validation diagnostics from XBOX services, such as the warning for a first RETAIL
    /// publish without an embargo, to stderr. The JSON output keeps them too.
    /// </summary>
    private static void WriteDiagnostics(XboxLivePublishResponse result, TextWriter stderr)
    {
        if (result.AdditionalProperties is null)
        {
            return;
        }

        JsonElement diagnostics = result.AdditionalProperties
            .FirstOrDefault(property => string.Equals(
                property.Key,
                "ValidationResult",
                StringComparison.OrdinalIgnoreCase))
            .Value;
        if (diagnostics.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement diagnostic in diagnostics.EnumerateArray())
        {
            if (diagnostic.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string severity = ReadString(diagnostic, "Severity") ?? "Diagnostic";
            string? error = ReadString(diagnostic, "Error");
            string? document = ReadString(diagnostic, "DocumentName");
            string? message = ReadString(diagnostic, "Message");
            var line = new System.Text.StringBuilder("XBOX services ")
                .Append(XboxPartnerCommandSupport.ForTerminal(severity).ToLowerInvariant());
            if (error is not null)
            {
                line.Append(" (").Append(XboxPartnerCommandSupport.ForTerminal(error)).Append(')');
            }

            if (document is not null)
            {
                line.Append(" in ").Append(XboxPartnerCommandSupport.ForTerminal(document));
            }

            if (message is not null)
            {
                line.Append(": ").Append(XboxPartnerCommandSupport.ForTerminal(message));
            }

            stderr.WriteLine(line.ToString());
        }
    }

    private static string? ReadString(JsonElement element, string name)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(property.Value.GetString()))
            {
                return property.Value.GetString();
            }
        }

        return null;
    }

    private static Option<Guid> CreateRequiredGuidOption(
        string name,
        string alias,
        string description)
    {
        var option = new Option<Guid>(name, alias)
        {
            Description = description,
            Required = true,
        };
        option.Validators.Add(result =>
        {
            if (result.Tokens.Count > 0 &&
                (!Guid.TryParse(result.Tokens[0].Value, out Guid value) ||
                 value == Guid.Empty))
            {
                result.AddError($"{name} must be a non-empty GUID.");
            }
        });
        return option;
    }

    private static Option<string> CreateRequiredSandboxOption(
        string name,
        string description)
    {
        var option = new Option<string>(name)
        {
            HelpName = "id",
            Description = description,
            Required = true,
        };
        option.Validators.Add(result =>
        {
            string? value = result.GetValueOrDefault<string>();
            if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace))
            {
                result.AddError($"{name} must be non-empty and cannot contain whitespace.");
            }
        });
        return option;
    }

    private static Option<string?> CreateOptionalSandboxOption(
        string name,
        string description)
    {
        var option = new Option<string?>(name)
        {
            HelpName = "id",
            Description = description,
        };
        option.Validators.Add(result =>
        {
            string? value = result.GetValueOrDefault<string>();
            if (value is not null &&
                (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace)))
            {
                result.AddError($"{name} must be non-empty and cannot contain whitespace.");
            }
        });
        return option;
    }
}

/// <summary>The terminal seams <c>xbl publish start</c> asks for confirmation through.</summary>
internal sealed record PublishConfirmationDependencies(
    Func<bool> IsInteractive,
    Func<CancellationToken, Task<string?>> ReadLineAsync)
{
    internal static PublishConfirmationDependencies Default { get; } = new(
        XboxPartnerCommandSupport.IsInteractiveTerminal,
        FeedbackDependencies.CancelableReadLine(Console.ReadLine));
}
