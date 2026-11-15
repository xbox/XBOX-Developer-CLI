// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using System.Text.Json;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Output;
using XboxDevCLI.Presentation;
using XboxDevCLI.Services;
using XboxDevCLI.Telemetry;

namespace XboxDevCLI.Commands;

internal static class PublishCommandFactory
{
    /// <summary>The write was sent, but whether it took effect is unknown.</summary>
    internal const int OutcomeUnknownExitCode = 3;

    /// <summary>Global sandboxes where a publish reaches certification or players.</summary>
    private static readonly HashSet<string> GlobalDestinations =
        new(["RETAIL", "CERT", "CERT.DEBUG"], StringComparer.OrdinalIgnoreCase);

    /// <summary>The sandbox XBOX services publish the title-wide configuration to.</summary>
    private const string TitleWideSandbox = "HIDDEN";

    public static Command Create(PublishConfirmationDependencies? confirmationDependencies = null)
    {
        var dependencies = confirmationDependencies ?? PublishConfirmationDependencies.Default;
        var sellerOption = new XdtsSellerOption();
        var target = new XboxLiveConfigurationTarget();
        var sandboxOption = CreateRequiredSandboxOption(
            "--sandbox-id",
            "The destination sandbox ID whose published configuration will be updated.")
            .AsContextValue(ContextEndpoint.XboxPartner);
        var sourceSandboxOption = CreateOptionalSandboxOption(
            "--source-sandbox-id",
            "The source sandbox ID. Default: --sandbox-id.");
        var validateOnlyOption = new Option<bool>("--validate-only")
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
            // Read the token rather than the value, which throws when it didn't parse; the
            // parser reports that itself.
            if (result.Tokens.Count > 0 &&
                ulong.TryParse(result.Tokens[0].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong value) &&
                value == 0)
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
        var allowSharedOption = XboxLiveConfigurationTarget.CreateAllowSharedConfigurationOption();

        var command = new Command(
            "publish",
            "Publish XBOX services working configuration to a sandbox");
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
                "XBOX services, such as warnings, go to stderr. A --validate-only to RETAIL also lists " +
                "the achievements the publish adds, on stderr and in achievementChanges.",
            Examples:
            [
                "xbox xbl config publish --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id XDKS.1",
                "xbox xbl config publish --title-id 1234ABCD --sandbox-id XDKS.1",
                "xbox xbl config publish --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --source-sandbox-id STAGE.0 --sandbox-id RETAIL --validate-only",
                "xbox xbl config publish --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --source-sandbox-id STAGE.0 --sandbox-id RETAIL --confirm",
                "xbox xbl config publish --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --source-sandbox-id XDKS.1 --sandbox-id TESTSB.42 --validate-only --output table",
                "xbox xbl config publish --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --source-sandbox-id STAGE.0 --sandbox-id RETAIL --configset 42 --confirm",
                "xbox xbl config publish --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id XDKS.1 --republish",
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
                "Before asking at a terminal, a RETAIL publish is validated and the achievements it adds " +
                "are listed; the publish then sends the same working version. " +
                "Every title bound to a primary title shares its configuration. A --title-id that selects a " +
                "bound title is refused, even with --validate-only; rerun with the primary's --scid. A publish by " +
                "--title-id, validation included, of configuration other titles are bound to lists them and " +
                "asks before changing it; without a terminal it's refused unless you pass " +
                "--allow-shared-configuration. A RETAIL publish that asks first is refused without the list " +
                "unless you pass --allow-shared-configuration. A publish by --scid isn't checked. " +
                "Agent approval is still required.",
            Table: XboxPartnerCommandSupport.PublishTableView));
        sellerOption.AddTo(command);
        target.AddTo(command);
        command.Options.Add(sandboxOption);
        command.Options.Add(sourceSandboxOption);
        command.Options.Add(validateOnlyOption);
        command.Options.Add(configSetOption);
        command.Options.Add(republishOption);
        command.Options.Add(confirmOption);
        command.Options.Add(allowSharedOption);
        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var output = OutputOptions.From(parseResult);
            try
            {
                string sandbox = parseResult.GetValue(sandboxOption)!;
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
                var selected = await target.SelectAsync(parseResult, sellerId);
                var request = new XboxLivePublishRequest(
                    selected.Scid,
                    sandbox,
                    parseResult.GetValue(sourceSandboxOption) ?? sandbox,
                    parseResult.GetValue(validateOnlyOption),
                    parseResult.GetValue(configSetOption),
                    parseResult.GetValue(republishOption),
                    selected.TitleId,
                    parseResult.GetValue(allowSharedOption));
                bool confirmed = parseResult.GetResult(confirmOption) is { Implicit: false }
                    ? parseResult.GetValue(confirmOption)
                    : parseResult.GetValue(Program.ConfirmOption);
                bool proceed;
                try
                {
                    // The service lists the achievements a RETAIL publish adds only when validating, so the
                    // prompt validates first and then pins the publish to the working version it checked.
                    if (IsRetail(sandbox) && Prompts(request, confirmed) && dependencies.IsInteractive())
                    {
                        XboxLivePublishRequest validation = request with { ValidateOnly = true, Republish = false };
                        XboxLivePublishResponse result;
                        XboxLiveResolvedConfigurationTarget? resolved = null;
                        try
                        {
                            using (RequestProgress.Start(DescribePublish(validation), Console.Error))
                            {
                                result = (await client.PublishXboxLiveConfigurationAsync(
                                    selected.SellerId, validation, cancellationToken,
                                    target => resolved = target).ConfigureAwait(false)).Value;
                            }
                        }
                        finally
                        {
                            if (resolved is not null)
                            {
                                XboxLiveConfigurationTarget.WriteResolved(resolved, selected.SellerId, Console.Error);
                            }
                        }

                        WriteDiagnostics(result, Console.Error);
                        Console.Error.WriteLine(FormatAchievementChanges(result, validateOnly: false));
                        if (FormatSourceWarning(request, result) is { } sourceWarning)
                        {
                            Console.Error.WriteLine(sourceWarning);
                        }

                        request = request with { ConfigSetVersion = request.ConfigSetVersion ?? result.SourceVersionNumber };
                        if (request.ConfigSetVersion is null)
                        {
                            Console.Error.WriteLine(
                                "Nothing was published. The validation didn't say which working version it checked, so " +
                                "the publish can't be limited to what you were shown. Pass --configset, or try again.");
                            return 1;
                        }
                    }

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
                Program.WriteVerbose(parseResult.GetValue(Program.VerboseOption), () =>
                    $"{(request.ValidateOnly ? "Validate" : request.Republish ? "Republish" : "Publish")} " +
                        $"{selected.Describe()} " +
                        $"from {request.SourceSandbox} " +
                        $"(working version {request.ConfigSetVersion?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "latest"}) " +
                        $"to {request.Sandbox}");
                return await RunAsync(
                    client,
                    selected.SellerId,
                    request,
                    output,
                    Console.Out,
                    Console.Error,
                    cancellationToken,
                    writeGlobalStorageWarning: !Prompts(request, confirmed),
                    sharedConfirmation: dependencies,
                    recoveryPrefix: string.Join(' ', RecoveryCommandArguments.Create(parseResult))).ConfigureAwait(false);
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

    /// <summary>
    /// Sends the publish and writes its result. <paramref name="writeGlobalStorageWarning"/> is false when the
    /// confirmation prompt already showed the global title storage warning. When the service refuses to change
    /// configuration other titles share, <paramref name="sharedConfirmation"/> asks whether to resend with the opt-in.
    /// </summary>
    internal static async Task<int> RunAsync(
        XboxPartnerClient client,
        long? sellerId,
        XboxLivePublishRequest request,
        OutputOptions output,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken,
        bool writeGlobalStorageWarning = true,
        PublishConfirmationDependencies? sharedConfirmation = null,
        string? recoveryPrefix = null)
    {
        bool compact = output.IsCompact;
        TableView? tableView = output.TableView;
        string? globalStorageWarning = writeGlobalStorageWarning ? FormatGlobalStorageWarning(request) : null;
        if (globalStorageWarning is not null)
        {
            stderr.WriteLine(globalStorageWarning);
        }

        var sent = false;
        string? sentCorrelationId = null;
        var answered = false;
        XboxLiveResolvedConfigurationTarget? resolved = null;
        try
        {
            XboxPartnerOperationResult<XboxLivePublishResponse> response;
            try
            {
                response = await SendAsync(request).ConfigureAwait(false);
            }
            catch (XboxPartnerApiException ex) when (
                !request.AllowSharedConfiguration &&
                XboxLiveConfigurationTarget.ReadSharedConfiguration(ex) is not null)
            {
                // The service refused the first send, so nothing started.
                sent = false;
                if (!await XboxLiveConfigurationTarget.ConfirmSharedConfigurationAsync(
                    XboxLiveConfigurationTarget.ReadSharedConfiguration(ex)!, ex, compact, request.ValidateOnly,
                    sharedConfirmation, stderr,
                    cancellationToken).ConfigureAwait(false))
                {
                    return 1;
                }

                request = request with { AllowSharedConfiguration = true };
                response = await SendAsync(request).ConfigureAwait(false);
            }

            async Task<XboxPartnerOperationResult<XboxLivePublishResponse>> SendAsync(XboxLivePublishRequest body)
            {
                resolved = null;
                try
                {
                    using (RequestProgress.Start(DescribePublish(body), stderr))
                    {
                        return await client.PublishXboxLiveConfigurationAsync(
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
            XboxLivePublishResponse result = response.Value;
            WriteDiagnostics(result, stderr);
            if (request.ValidateOnly && IsRetail(request.Sandbox))
            {
                stderr.WriteLine(FormatAchievementChanges(result, validateOnly: true));
            }

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
                : PlayFabApiClient.FormatJson(response.Body, compact));
            if (FormatSourceWarning(request, result) is { } warning)
            {
                stderr.WriteLine(warning);
            }

            return 0;
        }
        catch (OperationCanceledException) when (!request.ValidateOnly && !sent && cancellationToken.IsCancellationRequested)
        {
            stderr.WriteLine(
                $"Canceled before the request was sent, so the publish to {TerminalText.SingleLine(request.Sandbox)} didn't start.");
            return 1;
        }
        catch (Exception ex) when (!request.ValidateOnly && sent && !answered
            && XboxPartnerCommandSupport.IsUncertainSubmission(ex))
        {
            XboxPartnerCommandSupport.WriteUncertainCause(ex, compact, stderr, cancellationToken, sentCorrelationId);
            stderr.WriteLine(
                $"It is unknown whether the publish to {TerminalText.SingleLine(request.Sandbox)} started. " +
                "Don't resubmit it.");
            stderr.WriteLine(
                $"Check with: {recoveryPrefix ?? CliIdentity.CommandName} xbl config status " +
                XboxPartnerCommandSupport.FormatConfigurationTarget(resolved, request.Scid, request.TitleId, sellerId) +
                $" --sandbox-id {ShellQuoting.Quote(request.Sandbox)}");
            return OutcomeUnknownExitCode;
        }
        catch (XboxPartnerApiException ex)
        {
            stderr.WriteLine(XboxPartnerCommandErrorFormatter.Format(ex, compact));
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

    internal static string DescribePublish(XboxLivePublishRequest request)
    {
        string verb = request.ValidateOnly ? "Validating publish"
            : request.Republish ? "Republishing"
            : "Publishing";
        return string.Equals(request.SourceSandbox, request.Sandbox, StringComparison.OrdinalIgnoreCase)
            ? $"{verb} to {request.Sandbox}"
            : $"{verb} from {request.SourceSandbox} to {request.Sandbox}";
    }

    // A cross-sandbox publish also copies global title storage, mirroring the source's
    // global title storage files into the destination. XBOX services compare
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

    /// <summary>The status XboxPartner reports when nothing changed since the last publish.</summary>
    internal const string AlreadyPublishedStatus = "AlreadyPublished";

    private static bool IsRetail(string sandbox) =>
        string.Equals(sandbox, "RETAIL", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Says which achievements a publish to RETAIL adds, from a validation's response. Once they're live,
    /// achievements can't be removed and their unlock rules and gamerscore can't change, so this is what
    /// to check before publishing.
    /// </summary>
    internal static string FormatAchievementChanges(XboxLivePublishResponse result, bool validateOnly)
    {
        if (result.AchievementChanges?.Added is not { } added)
        {
            return "The service didn't report which achievements this publish adds to RETAIL.";
        }

        string[] ids = added
            .Where(id => id is not null)
            .Select(id => XboxPartnerCommandSupport.ForTerminal(id))
            .ToArray();
        string lead = validateOnly ? "This publish would add" : "This publish adds";
        if (ids.Length == 0)
        {
            return $"{lead} no achievements to RETAIL.";
        }

        return $"{lead} {ids.Length} achievement{(ids.Length == 1 ? "" : "s")} to RETAIL: {string.Join(", ", ids)}. " +
            "Once they're live in RETAIL, achievements can't be removed, and their unlock rules and gamerscore can't change.";
    }

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
        string target = request.Scid is { } scid ? $"SCID {scid:D}" : $"title {request.TitleId}";
        stderr.WriteLine(
            $"This publishes {configuration} to {request.Sandbox} for {target}.");
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
            if (IsRetail(request.Sandbox))
            {
                stderr.WriteLine(
                    "Run with --validate-only first to see which achievements this publish adds to RETAIL.");
            }

            TelemetryRecorder.RecordError(TelemetryErrorTypes.NotConfirmed);
            return false;
        }

        ConfirmationPrompt.Write(stderr, $"Publish to {request.Sandbox}? [y/N]: ");
        string? answer = (await dependencies.ReadLineAsync(cancellationToken).ConfigureAwait(false))?.Trim();
        if (string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        stderr.WriteLine("Publish canceled.");
        TelemetryRecorder.RecordError(TelemetryErrorTypes.NotConfirmed);
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
        string description)
    {
        var option = new Option<Guid>(name)
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

/// <summary>The terminal seams <c>xbl config publish</c> asks for confirmation through.</summary>
internal sealed record PublishConfirmationDependencies(
    Func<bool> IsInteractive,
    Func<CancellationToken, Task<string?>> ReadLineAsync)
{
    internal static PublishConfirmationDependencies Default { get; } = new(
        XboxPartnerCommandSupport.IsInteractiveTerminal,
        FeedbackDependencies.CancelableReadLine(Console.ReadLine));
}
