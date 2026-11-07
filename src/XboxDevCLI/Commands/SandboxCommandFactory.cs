// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Output;
using XboxDevCLI.Services;

namespace XboxDevCLI.Commands;

/// <summary>
/// Builds the root sandbox namespace. Sandboxes belong to the Partner Center account and isolate
/// Store products as well as XBOX services, so they are not nested under <c>xbl</c>.
/// </summary>
internal static class SandboxCommandFactory
{
    /// <summary>The creation job failed, so the sandbox was not created.</summary>
    internal const int JobFailedExitCode = 2;

    /// <summary>
    /// The request was sent but its outcome could not be confirmed, so the sandbox may or may not
    /// exist. Matches <c>test-account batch create</c>.
    /// </summary>
    internal const int OutcomeUnknownExitCode = 3;

    /// <summary>
    /// The creation job was accepted but the command stopped before confirming the sandbox: the wait
    /// timed out or was canceled, polling failed, or the created sandbox could not be read back.
    /// Matches <c>test-account batch create</c>.
    /// </summary>
    internal const int WaitEndedExitCode = 4;

    public static Command Create() =>
        Create(clientFactory: null);

    internal static Command Create(
        Func<IConfiguration, XboxPartnerClient>? clientFactory,
        XboxPartnerJobDependencies? creationDependencies = null)
    {
        var sandbox = new Command("sandbox", "List, get, and create XBOX sandboxes");
        sandbox.Subcommands.Add(CreateListCommand(clientFactory));
        sandbox.Subcommands.Add(CreateGetCommand(clientFactory));
        sandbox.Subcommands.Add(CreateCreateCommand(
            clientFactory, creationDependencies ?? XboxPartnerJobDependencies.Default));
        return sandbox.Requires(CommandRequirement.Authentication).Calls(ServiceEndpoints.XboxPartner);
    }

    private delegate bool SandboxIdNormalizer(string? value, out string normalized);

    private static Argument<string> CreateSandboxIdArgument(
        string description, SandboxIdNormalizer normalize, string formatDescription)
    {
        var argument = new Argument<string>("sandbox-id") { Description = description };
        argument.Validators.Add(result =>
        {
            if (result.Tokens.Count > 0 && !normalize(result.Tokens[0].Value, out _))
            {
                result.AddError($"Invalid sandbox ID '{result.Tokens[0].Value}'. {formatDescription}");
            }
        });
        return argument;
    }

    private static Command CreateListCommand(
        Func<IConfiguration, XboxPartnerClient>? clientFactory)
    {
        var seller = XboxPartnerCommandSupport.CreateSellerIdOption();
        var baseUrl = XboxPartnerCommandSupport.CreateBaseUrlOption();
        var command = new Command("list", "List the XBOX sandboxes available to a Partner Center seller")
        {
            seller, baseUrl,
        };
        command.SetAction((ParseResult result, CancellationToken cancellationToken) =>
            ExecuteAsync(
                result, seller, baseUrl, clientFactory,
                async (client, url, sellerId) =>
                {
                    Program.WriteVerbose(result.GetValue(Program.VerboseOption),
                        () => $"Listing sandboxes for seller {sellerId}, following every page.");
                    string json;
                    using (var progress = ListProgress.Start("Reading sandboxes"))
                    {
                        json = await client.ListSandboxesAsync(url, sellerId, cancellationToken, progress.PageRead);
                    }

                    json = $"{{\"sandboxes\":{json}}}";
                    var output = OutputOptions.From(result);
                    if (output.TableView is { } view)
                    {
                        using var document = JsonDocument.Parse(json);
                        Console.WriteLine(TableFormatter.Format(document.RootElement, view));
                    }
                    else
                    {
                        Console.WriteLine(Program.FormatOutput(json, output.IsCompact, dataOnly: false));
                    }

                    return 0;
                },
                cancellationToken));
        return command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.ReadOnly,
            Scope: "Sandboxes visible to the seller selected by --seller-id, XBOX_SELLER_ID, or 'context set', including global sandboxes.",
            Output: "A JSON object with a sandboxes array of sandbox objects with sandboxId, type (Shared, Isolated, or Unknown), " +
                "sellerId, and global; table output has SANDBOX, TYPE, and GLOBAL columns. " + ListProgress.HelpOutput,
            Examples:
            [
                "xbox sandbox list",
                "xbox sandbox list --output table",
                "xbox sandbox list --seller-id 1234567 --output json-compact",
            ],
            LearnMore:
            [
                "Lists the same sandboxes as the Sandboxes page in Partner Center.",
                "Pass any listed sandboxId to 'xbox sandbox get'.",
            ],
            Table: XboxPartnerCommandSupport.SandboxTableView));
    }

    private static Command CreateGetCommand(
        Func<IConfiguration, XboxPartnerClient>? clientFactory)
    {
        var sandboxId = CreateSandboxIdArgument(
            "Sandbox ID, such as ABCDEF.1 or RETAIL.",
            XboxSandboxId.TryNormalizeForRead,
            XboxSandboxId.ReadFormatDescription);
        var seller = XboxPartnerCommandSupport.CreateSellerIdOption();
        var baseUrl = XboxPartnerCommandSupport.CreateBaseUrlOption();
        var command = new Command("get", "Get one XBOX sandbox by ID")
        {
            sandboxId, seller, baseUrl,
        };
        command.SetAction((ParseResult result, CancellationToken cancellationToken) =>
            ExecuteAsync(
                result, seller, baseUrl, clientFactory,
                async (client, url, sellerId) =>
                {
                    XboxSandboxId.TryNormalizeForRead(result.GetValue(sandboxId), out var id);
                    Program.WriteVerbose(result.GetValue(Program.VerboseOption),
                        () => $"Reading sandbox {id} for seller {sellerId}.");
                    var json = await client.GetSandboxAsync(url, sellerId, id, cancellationToken);
                    WriteSandbox(json, OutputOptions.From(result));
                    return 0;
                },
                cancellationToken));
        return command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.ReadOnly,
            Scope: "One sandbox, read as the seller selected by --seller-id, XBOX_SELLER_ID, or 'context set'.",
            Output: "The sandbox as a JSON object with sandboxId, type (Shared, Isolated, or Unknown), " +
                "sellerId (null for a global sandbox), and global; " +
                "table output has SANDBOX ID, TYPE, and SELLER ID columns.",
            Examples:
            [
                "xbox sandbox get ABCDEF.1",
                "xbox sandbox get RETAIL",
                "xbox sandbox get ABCDEF.1 --seller-id 1234567 --output table",
            ],
            LearnMore:
            [
                "Sandbox IDs are case-insensitive and normalized to upper case. " +
                XboxSandboxId.ReadFormatDescription,
            ],
            Table: XboxPartnerCommandSupport.SandboxRecordTableView));
    }

    private static Command CreateCreateCommand(
        Func<IConfiguration, XboxPartnerClient>? clientFactory,
        XboxPartnerJobDependencies dependencies)
    {
        var sandboxId = CreateSandboxIdArgument(
            "ID of the new development sandbox, such as ABCDEF.1.",
            XboxSandboxId.TryNormalizeForCreate,
            XboxSandboxId.CreateFormatDescription);
        var seller = XboxPartnerCommandSupport.CreateSellerIdOption();
        var baseUrl = XboxPartnerCommandSupport.CreateBaseUrlOption();
        var confirm = new Option<bool>("--confirm", "--yes", "-y")
        {
            Description = "Confirm production sandbox creation without prompting.",
        };
        var command = new Command("create", "Create a Shared XBOX development sandbox and wait for it")
        {
            sandboxId, seller, baseUrl, confirm,
        };
        command.SetAction((ParseResult result, CancellationToken cancellationToken) =>
            ExecuteAsync(
                result, seller, baseUrl, clientFactory,
                async (client, url, sellerId) =>
                {
                    XboxSandboxId.TryNormalizeForCreate(result.GetValue(sandboxId), out var id);
                    var confirmed = result.GetResult(confirm) is { Implicit: false }
                        ? result.GetValue(confirm)
                        : result.GetValue(Program.ConfirmOption);
                    if (!ConfirmCreation(confirmed, dependencies, id, sellerId))
                    {
                        return 1;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    var recovery = XboxPartnerCommandSupport.BuildRecoveryCommand(
                        result, $"sandbox get {ShellQuoting.Quote(id)}", sellerId, url);
                    Program.WriteVerbose(result.GetValue(Program.VerboseOption),
                        () => $"Submitting sandbox creation for seller {sellerId}.");
                    XboxPartnerSandboxCreationJob job;
                    var sent = false;
                    try
                    {
                        Console.Error.WriteLine("Submitting sandbox creation job...");
                        // Do not retry an uncertain submission after a request or polling failure.
                        job = await client.CreateSandboxAsync(
                            url, sellerId,
                            new CreateXboxSandboxRequest(id, XboxSandboxId.SharedType),
                            cancellationToken,
                            onSending: () => sent = true);
                    }
                    catch (Exception exception) when (sent && IsUncertainSubmission(exception))
                    {
                        Console.Error.WriteLine(
                            "Sandbox creation was interrupted or its response could not be verified; " +
                            $"it is unknown whether sandbox {id} was created. Do not resubmit.");
                        Console.Error.WriteLine($"Check whether it exists with: {recovery}");
                        XboxPartnerCommandSupport.WriteRequestError(exception, result, cancellationToken);
                        return OutcomeUnknownExitCode;
                    }


                    var correlation = string.IsNullOrWhiteSpace(job.CorrelationId)
                        ? string.Empty
                        : $" (Correlation ID: {XboxPartnerCommandSupport.ForTerminal(job.CorrelationId)})";
                    Console.Error.WriteLine($"Sandbox creation job submitted: {XboxPartnerCommandSupport.ForTerminal(job.JobId)}{correlation}");
                    // The gateway's job route only accepts a GUID, so any other ID can't be followed. A
                    // job that is already complete but has no recognized result can't be classified.
                    if (!Guid.TryParse(job.JobId, out _) || (job.Completed && !job.Succeeded && !job.Failed))
                    {
                        Console.Error.WriteLine(
                            "The sandbox creation job response was invalid; " +
                            $"it is unknown whether sandbox {id} was created. Do not resubmit.");
                        Console.Error.WriteLine($"Check whether it exists with: {recovery}");
                        return OutcomeUnknownExitCode;
                    }

                    return await WaitForJobAsync(
                        result, client, url, sellerId, id, job, recovery, dependencies, cancellationToken);
                },
                cancellationToken));
        return command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.RemoteMutation,
            Scope: "Creates a Shared sandbox for --seller-id, XBOX_SELLER_ID, or the seller saved by 'context set'.",
            Output: "The created sandbox as a JSON object, or the failed job object when the creation job " +
                "fails. Nothing is written to stdout when the outcome is unknown or the job is still running. " +
                "Submission progress, job ID, correlation ID, and recovery commands go to stderr.",
            SafetyDetails: "Creates production state and cannot be undone from the CLI. Requires " +
                "--confirm (--yes, -y) or terminal confirmation. " +
                "Submission is never retried. Agent approval is still required.",
            LearnMore:
            [
                "Only Shared sandboxes can be created. " + XboxSandboxId.CreateFormatDescription,
                "Waits up to 120 seconds, polling every 5 seconds unless the service supplies Retry-After. " +
                "Exit codes: 0 created; 1 not created (rejected, or failed before the request was sent); " +
                "2 the creation job failed; 3 the request was sent but its outcome is unknown; " +
                "4 the job was accepted but the wait ended (timeout, Ctrl+C, polling failure, or the new " +
                "sandbox could not be read back).",
                "After exit 3 or 4, do not resubmit: use 'xbox sandbox get <sandbox-id>' to check whether the sandbox exists.",
            ],
            Examples:
            [
                "xbox sandbox create ABCDEF.1 --seller-id 1234567 --confirm",
                "xbox sandbox create ABCDEF.2 --confirm --output json-compact",
            ]));
    }

    private static async Task<int> WaitForJobAsync(
        ParseResult result,
        XboxPartnerClient client,
        string baseUrl,
        string sellerId,
        string sandboxId,
        XboxPartnerSandboxCreationJob initial,
        string recovery,
        XboxPartnerJobDependencies dependencies,
        CancellationToken cancellationToken)
    {
        var verbose = result.GetValue(Program.VerboseOption);
        var compact = OutputOptions.From(result).IsCompact;
        var jobId = XboxPartnerCommandSupport.ForTerminal(initial.JobId);
        if (!initial.Completed)
        {
            Console.Error.WriteLine($"Waiting for sandbox creation job {jobId}...");
        }

        XboxPartnerSandboxCreationJob? completed;
        try
        {
            completed = await XboxPartnerJobPoller.PollAsync(
                initial,
                requestToken =>
                {
                    Program.WriteVerbose(verbose,
                        () => $"Reading sandbox creation job {jobId}.");
                    return client.GetSandboxCreationJobAsync(baseUrl, sellerId, initial.JobId, requestToken);
                },
                dependencies.DelayAsync, cancellationToken, dependencies.GetUtcNow);
        }
        catch (Exception exception) when (XboxPartnerCommandSupport.IsRequestError(exception))
        {
            Console.Error.WriteLine(
                exception switch
                {
                    OperationCanceledException =>
                        $"Stopped waiting for job {jobId}; it may still be running.",
                    XboxPartnerApiException =>
                        "The sandbox was submitted, but its status could not be read.",
                    _ =>
                        $"The sandbox was submitted, but its status could not be read: {XboxPartnerCommandSupport.ForTerminal(exception.Message)}",
                });
            Console.Error.WriteLine($"Check whether it exists with: {recovery}");
            XboxPartnerCommandSupport.WriteRequestError(exception, result, cancellationToken);
            return WaitEndedExitCode;
        }

        if (completed is null)
        {
            Console.Error.WriteLine(
                $"Sandbox creation job {jobId} is still running after " +
                $"{XboxPartnerJobPoller.DefaultTimeout.TotalSeconds:0} seconds. Do not resubmit.");
            Console.Error.WriteLine($"Check whether it exists with: {recovery}");
            return WaitEndedExitCode;
        }

        if (!completed.Succeeded && !completed.Failed)
        {
            Console.Error.WriteLine(
                $"Sandbox creation job {jobId} completed without a recognized result " +
                $"('{XboxPartnerCommandSupport.ForTerminal(completed.Result)}'); it is unknown whether " +
                $"sandbox {sandboxId} was created. Do not resubmit.");
            Console.Error.WriteLine($"Check whether it exists with: {recovery}");
            return WaitEndedExitCode;
        }

        if (completed.Failed)
        {
            Console.WriteLine(Program.FormatOutput(completed.RawResponse, compact, dataOnly: false));
            Console.Error.WriteLine($"Sandbox creation job {jobId} completed without creating sandbox {sandboxId}.");
            return JobFailedExitCode;
        }

        Console.Error.WriteLine($"Sandbox creation job {jobId} completed.");
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (attempt > 1)
                {
                    // A just-created sandbox can take a moment to become readable. The delay is inside
                    // the try so cancelling it still reports that the sandbox was created.
                    await dependencies.DelayAsync(XboxPartnerJobPoller.DefaultInterval, cancellationToken);
                }

                Program.WriteVerbose(verbose,
                    () => $"Reading sandbox {sandboxId}.");
                var json = await client.GetSandboxAsync(baseUrl, sellerId, sandboxId, cancellationToken);
                WriteSandbox(json, compact ? OutputOptions.JsonCompact : OutputOptions.Json);
                return 0;
            }
            catch (Exception exception) when (attempt == 1
                && !cancellationToken.IsCancellationRequested
                && XboxPartnerCommandSupport.IsRequestError(exception))
            {
                Program.WriteVerbose(verbose, () => $"Reading sandbox {sandboxId} failed; retrying once.");
            }
            catch (Exception exception) when (XboxPartnerCommandSupport.IsRequestError(exception))
            {
                Console.Error.WriteLine($"Sandbox {sandboxId} was created, but it could not be read back.");
                Console.Error.WriteLine($"Check it with: {recovery}");
                XboxPartnerCommandSupport.WriteRequestError(exception, result, cancellationToken);
                return WaitEndedExitCode;
            }
        }
    }

    private static async Task<int> ExecuteAsync(
        ParseResult result,
        Option<string?> sellerOption,
        Option<string?> baseUrlOption,
        Func<IConfiguration, XboxPartnerClient>? clientFactory,
        Func<XboxPartnerClient, string, string, Task<int>> action,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var configuration = Program.GetConfiguration(result);
            var baseUrl = XboxPartnerCommandSupport.ResolveBaseUrl(result, baseUrlOption, configuration);
            using var client = clientFactory is null
                ? new XboxPartnerClient(
                    configuration,
                    authenticationService: Program.GetAuthentication(result))
                : clientFactory(configuration);
            var sellerId = await XboxPartnerCommandSupport.GetSellerIdAsync(result, sellerOption)
                .WaitAsync(cancellationToken);
            return await action(client, baseUrl, sellerId);
        }
        catch (Exception exception) when (XboxPartnerCommandSupport.IsRequestError(exception))
        {
            XboxPartnerCommandSupport.WriteRequestError(exception, result, cancellationToken);
            return 1;
        }
    }

    /// <summary>
    /// Whether a failure after the request was sent leaves the outcome unknown. Only a client error
    /// (4xx other than 408) from the gateway is a definite rejection; transport failures, timeouts,
    /// unreadable responses, unfollowed redirects, and server errors are all uncertain. Failures
    /// before sending, such as a missing sign-in, are never uncertain and are not passed here.
    /// </summary>
    private static bool IsUncertainSubmission(Exception exception) =>
        exception is not XboxPartnerApiException api
            || (int)api.StatusCode is < 400 or >= 500
            || api.StatusCode == HttpStatusCode.RequestTimeout;

    private static void WriteSandbox(string json, OutputOptions output)
    {
        var sandbox = XboxPartnerCommandSupport.DeserializeResponse<XboxPartnerSandbox>(json, "sandbox");
        if (string.IsNullOrWhiteSpace(sandbox.SandboxId))
        {
            throw new InvalidDataException("The sandbox response did not contain a 'sandboxId'.");
        }

        if (output.TableView is { } view)
        {
            using var document = JsonDocument.Parse(json);
            Console.WriteLine(TableFormatter.Format(document.RootElement, view));
            return;
        }

        Console.WriteLine(Program.FormatOutput(json, output.IsCompact, dataOnly: false));
    }

    private static bool ConfirmCreation(
        bool confirmed, XboxPartnerJobDependencies dependencies, string sandboxId, string sellerId)
    {
        if (confirmed)
        {
            return true;
        }

        Console.Error.WriteLine(
            $"Creating the Shared sandbox '{sandboxId}' under seller {sellerId} cannot be undone from this CLI.");
        if (!dependencies.IsInteractive())
        {
            Console.Error.WriteLine(
                "Re-run with --confirm (--yes, -y) to proceed without an interactive prompt.");
            return false;
        }

        Console.Error.Write("Create this sandbox? [y/N]: ");
        Console.Error.Flush();
        var answer = Console.ReadLine()?.Trim();
        if (string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        Console.Error.WriteLine("Sandbox creation canceled.");
        return false;
    }
}
