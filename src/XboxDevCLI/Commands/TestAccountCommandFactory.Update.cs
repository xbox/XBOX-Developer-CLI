// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Output;
using XboxDevCLI.Services;

namespace XboxDevCLI.Commands;

internal static partial class TestAccountCommandFactory
{
    /// <summary>The <c>state</c> the CLI reports once the batch shows the update's values.</summary>
    internal const string UpdatedState = "updated";

    private static Command CreateUpdateCommand()
    {
        var sellerOption = new XdtsSellerOption();
        var batchIdArgument = CreateBatchIdArgument();
        var addSandboxOption = new Option<string[]>("--add-sandbox-id")
        {
            Description = "Sandbox to give the accounts access to. Repeat for more sandboxes.",
            HelpName = "id",
            AllowMultipleArgumentsPerToken = false,
        };
        addSandboxOption.Validators.Add(result => RequireNonBlankValues(result, "--add-sandbox-id"));
        var removeSandboxOption = new Option<string[]>("--remove-sandbox-id")
        {
            Description = "Sandbox to take the accounts' access away from. Repeat for more sandboxes.",
            HelpName = "id",
            AllowMultipleArgumentsPerToken = false,
        };
        removeSandboxOption.Validators.Add(result => RequireNonBlankValues(result, "--remove-sandbox-id"));
        var accessExpirationOption = new Option<string?>("--access-expiration")
        {
            Description =
                "New date the accounts' sandbox access expires, such as 2026-12-31. This is not when the " +
                "accounts themselves expire. The service keeps only the day.",
            HelpName = "date",
        };
        accessExpirationOption.Validators.Add(result =>
        {
            if (result.Tokens.Count > 0 && ParseExpiration(result.Tokens[0].Value) is null)
            {
                result.AddError("--access-expiration must be a date such as 2026-12-31 or an ISO 8601 date and time.");
            }
        });
        var tagOption = new Option<string[]>("--tag")
        {
            Description =
                "Tag to add to the batch, shown as keywords in JSON output. Tags the batch already has are " +
                "kept; repeat for more tags.",
            AllowMultipleArgumentsPerToken = false,
        };
        tagOption.Validators.Add(result =>
        {
            RequireNonBlankValues(result, "--tag");
            if (result.Tokens.Any(token => token.Value.Trim().Length > MaxTagLength))
            {
                result.AddError($"Each --tag must be at most {MaxTagLength} characters.");
            }
        });
        var replaceTagsOption = new Option<bool>("--replace-tags")
        {
            Description = "Replace every tag on the batch with the --tag values instead of adding them.",
        };
        var clearTagsOption = new Option<bool>("--clear-tags")
        {
            Description = "Remove every tag from the batch.",
        };
        var allowNotMineOption = new Option<bool>("--allow-not-mine")
        {
            Description =
                "Allow changing a batch another developer created. Without it, the service refuses " +
                "batches you did not create. The test account service may still refuse, depending on " +
                "your permissions. Your own batches are changed either way.",
        };
        var confirmOption = new Option<bool>("--confirm", "--yes", "-y")
        {
            Description = "Confirm changing the batch without prompting.",
        };
        var noWaitOption = new Option<bool>("--no-wait")
        {
            Description = "Return as soon as the service accepts the update, without waiting for it.",
        };
        var timeoutOption = CreateTimeoutOption();

        var command = new Command(
            "update",
            "Change an XBOX test account batch's sandboxes, sandbox access expiry, or tags and wait until it shows them");
        command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.RemoteMutation,
            Scope:
                "Changes one test account batch for --seller-id, else XBOX_SELLER_ID, else the seller saved " +
                "with 'xbox context set seller-id <id>'. With none, the service uses your only seller and " +
                "refuses when you have more than one.",
            Output:
                "The update as JSON: batchId, jobId, state, sandboxes, accessExpirationDate, and keywords, " +
                "the values the batch has once the update completes. state is updated once the batch shows " +
                "them, unchanged when nothing needed to change, or updating with --no-wait or when " +
                "--timeout passes first. stdout is empty after Ctrl+C or a failed read while waiting. " +
                "Table output shows one row, with sandboxes and keywords on stderr. Progress, recovery " +
                "commands, and errors go to stderr.",
            SafetyDetails:
                "Gives or takes away the accounts' access to sandboxes, moves when that access expires, or " +
                "adds to or replaces the batch's tags. Removing a sandbox takes effect for every account in the batch. " +
                "Requires --confirm (--yes, -y) or terminal confirmation. The update is sent once and never " +
                "retried; it sets values, so sending it again after reading the batch is safe. Agent " +
                "approval is still required.",
            LearnMore:
            [
                "Reads the batch while waiting, until the batch's jobId clears. " +
                    "'xbox test-account job get' cannot read update jobs.",
                "Waits up to 5 minutes by default, polling as the service's Retry-After asks. Exit codes: 0 " +
                "updated, unchanged, or accepted with --no-wait; 1 nothing was changed; 2 the job ended but " +
                "the batch does not show the change; 3 the outcome is unknown, so read the batch before " +
                "updating again; 4 the wait ended while the batch was still being updated.",
                "Pass at least one change. --tag adds tags and keeps the batch's other tags; with " +
                "--replace-tags it replaces them, and --clear-tags removes them all. The service keeps " +
                "the marker that records which tool created the batch, and refuses a batch that would " +
                "end with more than 20 tags. Changing sandboxes " +
                "or the expiry needs sandbox access that your XBOX developer token grants; a sandbox it " +
                "does not grant is refused.",
            ],
            Examples:
            [
                "xbox test-account batch update 123456 --tag regression --tag nightly --confirm",
                "xbox test-account batch update 123456 --tag nightly --replace-tags --confirm",
                "xbox test-account batch update 123456 --add-sandbox-id ABCD.1 --access-expiration 2026-12-31",
                "xbox test-account batch update 123456 --remove-sandbox-id ABCD.2 --no-wait --confirm",
            ],
            Table: XboxPartnerCommandSupport.TestAccountBatchUpdateView));
        command.Arguments.Add(batchIdArgument);
        command.Options.Add(addSandboxOption);
        command.Options.Add(removeSandboxOption);
        command.Options.Add(accessExpirationOption);
        command.Options.Add(tagOption);
        command.Options.Add(replaceTagsOption);
        command.Options.Add(clearTagsOption);
        command.Options.Add(allowNotMineOption);
        sellerOption.AddTo(command);
        command.Options.Add(confirmOption);
        command.Options.Add(noWaitOption);
        command.Options.Add(timeoutOption);
        command.Validators.Add(result =>
        {
            string[] added = result.GetValue(addSandboxOption) ?? [];
            string[] removed = result.GetValue(removeSandboxOption) ?? [];
            bool clearTags = result.GetValue(clearTagsOption);
            bool tags = result.GetResult(tagOption) is { Implicit: false };
            if (added.Length == 0 && removed.Length == 0 && !tags && !clearTags &&
                result.GetResult(accessExpirationOption) is not { Implicit: false })
            {
                result.AddError(
                    "Nothing to change. Pass --add-sandbox-id, --remove-sandbox-id, --access-expiration, " +
                    "--tag, or --clear-tags.");
            }

            if (tags && clearTags)
            {
                result.AddError("--tag and --clear-tags cannot be combined.");
            }

            if (result.GetValue(replaceTagsOption) && clearTags)
            {
                result.AddError("--replace-tags and --clear-tags cannot be combined. To remove every tag, pass only --clear-tags.");
            }
            else if (result.GetValue(replaceTagsOption) && !tags)
            {
                result.AddError("--replace-tags needs --tag. To remove every tag, pass --clear-tags.");
            }

            var both = added.Select(value => value.Trim())
                .Intersect(removed.Select(value => value.Trim()), StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (both.Length > 0)
            {
                result.AddError(
                    $"Sandbox {string.Join(", ", both)} cannot be both added and removed.");
            }

            if (result.GetValue(noWaitOption) && result.GetResult(timeoutOption) is { Implicit: false })
            {
                result.AddError("--no-wait and --timeout cannot be combined.");
            }
        });
        command.SetAction((ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var dependencies = Dependencies;
            long batchId = parseResult.GetValue(batchIdArgument);
            string? expiration = parseResult.GetValue(accessExpirationOption);
            bool clearTags = parseResult.GetValue(clearTagsOption);
            var request = new UpdateTestAccountBatchRequest
            {
                AddSandboxes = Trimmed(parseResult.GetValue(addSandboxOption)),
                RemoveSandboxes = Trimmed(parseResult.GetValue(removeSandboxOption)),
                AccessExpirationDate = string.IsNullOrWhiteSpace(expiration) ? null : ParseExpiration(expiration),
                Keywords = clearTags ? [] : Trimmed(parseResult.GetValue(tagOption)),
                ReplaceKeywords = clearTags || parseResult.GetValue(replaceTagsOption) ? true : null,
            };
            bool allowNotMine = parseResult.GetValue(allowNotMineOption);
            bool confirmed = parseResult.GetResult(confirmOption) is { Implicit: false }
                ? parseResult.GetValue(confirmOption)
                : parseResult.GetValue(Program.ConfirmOption);
            bool noWait = parseResult.GetValue(noWaitOption);
            TimeSpan timeout = ReadTimeout(parseResult, timeoutOption);
            return InvokeAsync(
                parseResult,
                sellerOption,
                (client, sellerId, token) => UpdateAsync(
                    parseResult, client, sellerId, batchId, request, allowNotMine, confirmed, noWait, timeout, dependencies, token),
                cancellationToken);
        });
        return command;
    }

    private const int MaxTagLength = 100;

    // Trimming also keeps a trailing newline from a script out of the request, which the service rejects.
    private static string[]? Trimmed(string[]? values) =>
        values is { Length: > 0 } ? [.. values.Select(value => value.Trim())] : null;

    private static async Task<int> UpdateAsync(
        ParseResult parseResult,
        XboxPartnerClient client,
        long? sellerId,
        long batchId,
        UpdateTestAccountBatchRequest request,
        bool allowNotMine,
        bool confirmed,
        bool noWait,
        TimeSpan timeout,
        TestAccountCommandDependencies dependencies,
        CancellationToken cancellationToken)
    {
        var output = OutputOptions.From(parseResult);
        var verbose = parseResult.GetValue(Program.VerboseOption);
        TextWriter stdout = Console.Out;
        TextWriter stderr = Console.Error;
        var recovery = new RecoveryCommands(parseResult, sellerId);
        string id = batchId.ToString(CultureInfo.InvariantCulture);
        string getBatch = recovery.Build("batch", "get", id);

        if (!confirmed && !await ConfirmUpdateAsync(
                client, sellerId, batchId, request, output, dependencies, stderr, cancellationToken).ConfigureAwait(false))
        {
            return 1;
        }

        Program.WriteVerbose(verbose, () => $"Updating test account batch {id} using the signed in Entra ID token");
        stderr.WriteLine($"Updating test account batch {id}...");
        bool sent = false;
        TestAccountBatchUpdateResult accepted;
        try
        {
            // Sent once. Every failure that could follow the request leaving the process is
            // reported as an unknown outcome rather than retried.
            accepted = await client.UpdateTestAccountBatchAsync(
                    sellerId, batchId, request, allowNotMine, cancellationToken, () => sent = true)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (!sent)
        {
            stderr.WriteLine(exception is OperationCanceledException
                ? "Canceled before the update was sent. Nothing was changed."
                : $"Request failed: {exception.Message} The update was not sent.");
            return 1;
        }
        catch (XboxPartnerApiException exception) when (IsOutcomeUnknown(exception))
        {
            XboxPartnerCommandSupport.WriteCorrelationId(exception.CorrelationId, stderr);
            stderr.WriteLine(FormatError(exception, output.IsCompact));
            WriteUpdateOutcomeUnknown(id, getBatch, stderr);
            return OutcomeUnknownExitCode;
        }
        catch (XboxPartnerApiException exception)
        {
            XboxPartnerCommandSupport.WriteCorrelationId(exception.CorrelationId, stderr);
            stderr.WriteLine(FormatError(
                exception,
                output.IsCompact,
                allowNotMine && ReadProblemString(exception.ResponseBody, "code") == AccessDeniedCode
                    ? "The test account service refused to change a batch you did not create. --allow-not-mine " +
                        "bypasses only the creator check; ask the batch's creator or a developer with permission " +
                        "to change it."
                    : DescribeFailure(exception.StatusCode, exception.ResponseBody, TestAccountRequest.Update)));
            return 1;
        }
        catch (XboxPartnerResponseException exception)
        {
            XboxPartnerCommandSupport.WriteCorrelationId(exception.CorrelationId, stderr);
            stderr.WriteLine($"The service accepted the request, but its response could not be read: {exception.Message}");
            WriteUpdateOutcomeUnknown(id, getBatch, stderr);
            return OutcomeUnknownExitCode;
        }
        // ConnectionError is left out for the same reason as the create: a failed connect does not
        // prove an earlier attempt on a pooled connection never reached the service.
        catch (HttpRequestException exception) when (exception.HttpRequestError is
            HttpRequestError.NameResolutionError or HttpRequestError.SecureConnectionError)
        {
            stderr.WriteLine($"Request failed: {exception.Message} The request did not reach the service.");
            return 1;
        }
        catch (Exception exception)
        {
            stderr.WriteLine(exception switch
            {
                OperationCanceledException when cancellationToken.IsCancellationRequested =>
                    "Canceled while waiting for the service to answer the update.",
                OperationCanceledException => "Timed out waiting for the service to answer the update.",
                HttpRequestException or IOException =>
                    $"The connection failed while waiting for the service to answer the update: {exception.Message}",
                _ => $"The update was sent, but failed before its answer could be read: {exception.Message}",
            });
            WriteUpdateOutcomeUnknown(id, getBatch, stderr);
            return OutcomeUnknownExitCode;
        }

        TestAccountBatchUpdate update = accepted.Update;
        XboxPartnerCommandSupport.WriteCorrelationId(accepted.CorrelationId, stderr);
        if (update.IsUnchanged)
        {
            WriteUpdate(accepted, state: null, output, stdout, stderr);
            stderr.WriteLine($"Batch {id} already has these values. Nothing was changed.");
            return 0;
        }

        string jobId = Clean(update.JobId ?? "-");
        stderr.WriteLine($"Test account batch update accepted: batch {id}, job {jobId}.");
        string checkBatch = $"Check it with: {getBatch} Its jobId clears once the update completes.";
        string stillUpdating = $"The update may still be running on the service. {checkBatch}";
        if (noWait)
        {
            WriteUpdate(accepted, state: null, output, stdout, stderr);
            stderr.WriteLine($"The update continues on the service. {checkBatch}");
            return 0;
        }

        stderr.WriteLine($"Waiting up to {FormatDuration(timeout)} for batch {id} to be updated...");
        try
        {
            TestAccountBatchUpdateWait wait = await TestAccountBatchUpdateWaiter.WaitAsync(
                accepted.RetryAfter,
                token =>
                {
                    Program.WriteVerbose(verbose, () => $"Reading test account batch {id}");
                    return client.GetTestAccountBatchAsync(sellerId, batchId, token);
                },
                timeout,
                dependencies.DelayAsync,
                dependencies.GetUtcNow,
                cancellationToken).ConfigureAwait(false);
            if (wait.Outcome == TestAccountBatchUpdateOutcome.Finished && wait.LastBatch is { } batch)
            {
                IReadOnlyList<string> mismatches = TestAccountBatchUpdateWaiter.FindMismatches(request, update, batch);
                if (mismatches.Count > 0)
                {
                    stderr.WriteLine(
                        $"Update job {jobId} ended, but batch {id} does not show the new " +
                        $"{string.Join(" or ", mismatches)}. Inspect it with: {getBatch}");
                    stderr.WriteLine("Then update it again; sending the same change again is safe.");
                    return JobFailedExitCode;
                }

                WriteUpdate(accepted, UpdatedState, output, stdout, stderr);
                stderr.WriteLine($"Batch {id} updated.");
                return 0;
            }

            WriteUpdate(accepted, state: null, output, stdout, stderr);
            stderr.WriteLine(DescribeNotYetUpdated(id, jobId, timeout, wait.LastBatch));
            stderr.WriteLine(stillUpdating);
            return JobNotFinishedExitCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stderr.WriteLine($"Stopped waiting. {stillUpdating}");
        }
        // Any read failure leaves the update running on the service; report it with the way to
        // check rather than crash.
        catch (Exception exception)
        {
            WriteJobReadError(exception, output, stderr, TestAccountRequest.Get);
            stderr.WriteLine($"Could not read batch {id}. {stillUpdating}");
        }

        return JobNotFinishedExitCode;
    }

    /// <summary>
    /// Asks before changing a batch. In a terminal the batch is read first, so the prompt shows its
    /// current values beside the requested ones; a batch that cannot be read is not changed. Outside
    /// a terminal the CLI refuses without reading anything.
    /// </summary>
    private static async Task<bool> ConfirmUpdateAsync(
        XboxPartnerClient client,
        long? sellerId,
        long batchId,
        UpdateTestAccountBatchRequest request,
        OutputOptions output,
        TestAccountCommandDependencies dependencies,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        string id = batchId.ToString(CultureInfo.InvariantCulture);
        string seller = sellerId is { } sellerValue
            ? $"seller {sellerValue.ToString(CultureInfo.InvariantCulture)}"
            : "your only seller (no seller was given, so the service uses your only one and refuses if " +
                "you have several; pass --seller-id to choose)";
        if (!dependencies.IsInteractive())
        {
            stderr.WriteLine($"This changes test account batch {id} for {seller}:");
            WriteRequestedChanges(request, current: null, stderr);
            stderr.WriteLine("Run again with --confirm (--yes, -y) to proceed without an interactive prompt.");
            return false;
        }

        TestAccountBatch batch;
        try
        {
            string body = await client.GetTestAccountBatchAsync(sellerId, batchId, cancellationToken).ConfigureAwait(false);
            batch = TestAccountBatchDeletionWaiter.ReadBatch(body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stderr.WriteLine("Canceled. Nothing was changed.");
            return false;
        }
        catch (Exception exception)
        {
            WriteJobReadError(exception, output, stderr, TestAccountRequest.Get);
            stderr.WriteLine($"Could not read batch {id}, so nothing was changed.");
            return false;
        }

        stderr.WriteLine(FormattableString.Invariant(
            $"This changes test account batch {id} ({Clean(XboxPartnerCommandSupport.TestAccountText(batch.EmailTemplate))}, ") +
            FormattableString.Invariant($"{batch.BatchSize} accounts) for {seller}:"));
        WriteRequestedChanges(request, batch, stderr);
        if (!string.IsNullOrWhiteSpace(batch.JobId))
        {
            stderr.WriteLine(
                $"  A job is running on this batch ({Clean(batch.JobId)}); the service refuses the update until it stops.");
        }

        stderr.Write("Update this batch? [y/N]: ");
        stderr.Flush();
        string? answer = dependencies.ReadConfirmation()?.Trim();
        if (string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        stderr.WriteLine("Test account batch update canceled.");
        return false;
    }

    /// <summary>One line per requested change, with the batch's current value when it was read.</summary>
    private static void WriteRequestedChanges(UpdateTestAccountBatchRequest request, TestAccountBatch? current, TextWriter stderr)
    {
        static string List(IReadOnlyList<string>? values) =>
            Clean(XboxPartnerCommandSupport.TestAccountList(values));
        static string Day(DateTimeOffset? value) =>
            value?.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "-";

        if (request.AddSandboxes is { Count: > 0 } || request.RemoveSandboxes is { Count: > 0 })
        {
            string now = current is null ? string.Empty : $" (now {List(current.SandboxAccess)})";
            if (request.AddSandboxes is { Count: > 0 } added)
            {
                stderr.WriteLine($"  Give access to sandboxes: {List(added)}{now}");
            }

            if (request.RemoveSandboxes is { Count: > 0 } removed)
            {
                stderr.WriteLine($"  Take away access to sandboxes: {List(removed)}{now}");
            }
        }

        if (request.AccessExpirationDate is { } expiry)
        {
            string now = current is null ? string.Empty : $" (now {Day(current.AccessExpirationDate)})";
            stderr.WriteLine($"  Sandbox access expires: {Day(expiry)}{now}");
        }

        if (request.Keywords is { } keywords)
        {
            string now = current is null ? string.Empty : $" (now {List(current.Keywords)})";
            stderr.WriteLine(keywords.Count == 0
                ? $"  Remove every tag{now}"
                : request.ReplaceKeywords == true
                    ? $"  Replace tags with: {List(keywords)}{now}"
                    : $"  Add tags: {List(keywords)}{now}");
        }
    }

    /// <summary>
    /// Describes a batch the wait left behind. A failed update job keeps its jobId on the batch
    /// while the test account service retries it for days, so this reports what the last read
    /// showed rather than claiming the update is still progressing.
    /// </summary>
    private static string DescribeNotYetUpdated(string batchId, string jobId, TimeSpan timeout, TestAccountBatch? last)
    {
        string waited = FormatDuration(timeout);
        if (last is null)
        {
            return $"Batch {batchId} was not updated yet after {waited}; update job {jobId} was not seen ending.";
        }

        string lastJob = string.IsNullOrWhiteSpace(last.JobId) ? "none" : Clean(last.JobId);
        return $"Batch {batchId} was not updated yet after {waited}. Its jobId is {lastJob}. If the jobId stays " +
            "set, the update job may have stalled; the batch refuses other changes until it clears.";
    }

    private static void WriteUpdateOutcomeUnknown(string batchId, string getBatch, TextWriter stderr)
    {
        stderr.WriteLine(
            $"The update may have reached the service, so it is unknown whether batch {batchId} is being changed.");
        stderr.WriteLine($"To find out, run: {getBatch}");
        stderr.WriteLine("  jobId set: a job, likely the update, is still running; check again later.");
        stderr.WriteLine("  jobId null and the batch shows the new values: the update is done.");
        stderr.WriteLine(
            "  jobId null and the old values: the update did not happen. Sending it again is safe, " +
            "since tags the batch already has are skipped and the other changes set values.");
    }

    /// <summary>
    /// Writes the update answer, with <paramref name="state"/> in place of the service's when the
    /// CLI knows more than the answer did. Other fields pass through as the service sent them.
    /// </summary>
    private static void WriteUpdate(
        TestAccountBatchUpdateResult result,
        string? state,
        OutputOptions output,
        TextWriter stdout,
        TextWriter stderr)
    {
        if (output.TableView is not null)
        {
            var table = XboxPartnerTableFormatter.FormatTestAccountBatchUpdate(
                state is null ? result.Update : result.Update with { State = state });
            stdout.WriteLine(table.Text);
            foreach (string note in table.Notes)
            {
                stderr.WriteLine(note);
            }

            return;
        }

        string body = result.RawResponse;
        if (state is not null && JsonNode.Parse(body) is JsonObject json)
        {
            json["state"] = state;
            body = json.ToJsonString();
        }

        stdout.WriteLine(PlayFabApiClient.FormatJson(body, output.IsCompact));
    }

    /// <summary>Hints for update failures, keyed on the problem <c>code</c> only, never the detail.</summary>
    private static string? DescribeUpdateFailure(HttpStatusCode statusCode, string? responseBody) =>
        ReadProblemString(responseBody, "code") switch
        {
            InteractionRequiredCode => null,
            "test_account_batch_not_owned" =>
                "Another developer created this batch. To change it anyway, run again with --allow-not-mine; " +
                "the test account service may still refuse, depending on your permissions.",
            "test_account_batch_busy" =>
                "A job is running on this batch. Read the batch with 'xbox test-account batch get'; once its " +
                "jobId is null, try again. If the jobId never clears, the job may have stalled.",
            "test_account_sandbox_access_denied" =>
                "Your XBOX developer token does not grant access to a sandbox in the change. Extending the " +
                "expiry needs access to every sandbox on the batch. Check the sandbox IDs and your access.",
            "test_account_keyword_invalid" => "Check the --tag values.",
            TestAccountBatchDeletionWaiter.BatchNotFoundCode =>
                "No such batch for this seller. Check the batch ID and --seller-id.",
            AccessDeniedCode =>
                "Your account lacks the Partner Center role to manage test accounts for this seller, or the " +
                "seller is wrong; pass --seller-id.",
            "downstream_throttled" =>
                "The test account service is throttling requests. Wait, then try again.",
            "feature_disabled" =>
                "Test accounts are disabled.",
            "service_configuration_invalid" =>
                "The service is not configured for test accounts. Contact support.",
            null when statusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed =>
                "Updating test account batches may be unavailable.",
            _ => null,
        };
}
