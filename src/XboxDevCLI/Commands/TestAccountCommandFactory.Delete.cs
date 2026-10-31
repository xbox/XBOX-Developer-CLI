// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Output;
using XboxDevCLI.Services;

namespace XboxDevCLI.Commands;

internal static partial class TestAccountCommandFactory
{
    /// <summary>The <c>state</c> the CLI reports once it has seen the deleted batch read as gone.</summary>
    internal const string DeletedState = "deleted";

    private static Command CreateDeleteCommand()
    {
        var sellerOption = new XdtsSellerOption();
        var batchIdArgument = CreateBatchIdArgument();
        var allowNotMineOption = new Option<bool>("--allow-not-mine")
        {
            Description =
                "Allow deleting a batch another developer created. Without it, the service refuses " +
                "batches you did not create. The test account service may still refuse, depending on " +
                "your permissions. Your own batches are deleted either way.",
        };
        var confirmOption = new Option<bool>("--confirm", "--yes", "-y")
        {
            Description = "Confirm deleting the batch without prompting.",
        };
        var noWaitOption = new Option<bool>("--no-wait")
        {
            Description = "Return as soon as the service accepts the delete, without waiting for it.",
        };
        var timeoutOption = CreateTimeoutOption();

        var command = new Command("delete", "Delete an XBOX test account batch and wait until it is gone");
        command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.RemoteMutation,
            Scope:
                "Deletes one test account batch for --seller-id, XBOX_SELLER_ID, or the seller saved by " +
                "'context set'. With none of them, the service uses your only seller and refuses when you " +
                "have more than one.",
            Output:
                "The delete as JSON: batchId, jobId, state, emailTemplate, and batchSize. state is deleted " +
                "once the batch is gone, or deleting with --no-wait or when --timeout passes first. stdout " +
                "is empty after Ctrl+C or a failed read while waiting. Table " +
                "output shows one row. Progress, recovery commands, and errors go to stderr.",
            SafetyDetails:
                "Removes every account in the batch from its sandboxes and deletes the test account " +
                "service's records of them; their Microsoft accounts and XBOX profiles are not closed. " +
                "Cannot be undone. Requires --confirm (--yes, -y) or terminal confirmation. The delete " +
                "is sent once and never retried. Agent approval is still required.",
            LearnMore:
            [
                "Reads the batch while waiting, until the service reports " +
                    $"404 {TestAccountBatchDeletionWaiter.BatchNotFoundCode}.",
                "Waits up to 5 minutes by default, polling as the service's Retry-After asks. Exit codes: 0 " +
                "deleted or accepted with --no-wait; 1 nothing was deleted; 2 the delete stopped and the " +
                "batch still exists; 3 the outcome is unknown, so read the batch before deleting again; 4 " +
                "the wait ended while the batch was still being deleted.",
                "There is no bulk delete. To remove several batches, delete them one at a time; every " +
                "seller shares the test account service's rate limit.",
            ],
            Examples:
            [
                "xbox test-account batch delete 123456 --confirm",
                "xbox test-account batch delete 123456 --allow-not-mine --no-wait",
            ],
            Table: XboxPartnerCommandSupport.TestAccountBatchDeletionView));
        command.Arguments.Add(batchIdArgument);
        command.Options.Add(allowNotMineOption);
        sellerOption.AddTo(command);
        command.Options.Add(confirmOption);
        command.Options.Add(noWaitOption);
        command.Options.Add(timeoutOption);
        command.Validators.Add(result =>
        {
            if (result.GetValue(noWaitOption) && result.GetResult(timeoutOption) is { Implicit: false })
            {
                result.AddError("--no-wait and --timeout cannot be combined.");
            }
        });
        command.SetAction((ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var dependencies = Dependencies;
            long batchId = parseResult.GetValue(batchIdArgument);
            bool allowNotMine = parseResult.GetValue(allowNotMineOption);
            bool confirmed = parseResult.GetResult(confirmOption) is { Implicit: false }
                ? parseResult.GetValue(confirmOption)
                : parseResult.GetValue(Program.ConfirmOption);
            bool noWait = parseResult.GetValue(noWaitOption);
            TimeSpan timeout = ReadTimeout(parseResult, timeoutOption);
            return InvokeAsync(
                parseResult,
                sellerOption,
                (client, sellerId, token) => DeleteAsync(
                    parseResult, client, sellerId, batchId, allowNotMine, confirmed, noWait, timeout, dependencies, token),
                cancellationToken);
        });
        return command;
    }

    private static async Task<int> DeleteAsync(
        ParseResult parseResult,
        XboxPartnerClient client,
        long? sellerId,
        long batchId,
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

        if (!confirmed && !await ConfirmDeleteAsync(
                client, sellerId, batchId, output, dependencies, stderr, cancellationToken).ConfigureAwait(false))
        {
            return 1;
        }

        Program.WriteVerbose(verbose, () => $"Deleting test account batch {id} using the signed in Entra ID token");
        stderr.WriteLine($"Deleting test account batch {id}...");
        bool sent = false;
        TestAccountBatchDeletionResult accepted;
        try
        {
            // Sent once. Every failure that could follow the request leaving the process is
            // reported as an unknown outcome rather than retried.
            accepted = await client.DeleteTestAccountBatchAsync(
                    sellerId, batchId, allowNotMine, cancellationToken, () => sent = true)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (!sent)
        {
            stderr.WriteLine(exception is OperationCanceledException
                ? "Canceled before the delete was sent. Nothing was deleted."
                : $"Request failed: {exception.Message} The delete was not sent.");
            return 1;
        }
        catch (XboxPartnerApiException exception) when (IsOutcomeUnknown(exception))
        {
            XboxPartnerCommandSupport.WriteCorrelationId(exception.CorrelationId, stderr);
            stderr.WriteLine(FormatError(exception, output.IsCompact));
            WriteDeleteOutcomeUnknown(id, getBatch, stderr);
            return OutcomeUnknownExitCode;
        }
        catch (XboxPartnerApiException exception)
        {
            XboxPartnerCommandSupport.WriteCorrelationId(exception.CorrelationId, stderr);
            stderr.WriteLine(FormatError(
                exception,
                output.IsCompact,
                allowNotMine && ReadProblemString(exception.ResponseBody, "code") == AccessDeniedCode
                    ? "The test account service refused to delete a batch you did not create. --allow-not-mine " +
                        "bypasses only the creator check; ask the batch's creator or a developer with permission " +
                        "to delete it."
                    : DescribeFailure(exception.StatusCode, exception.ResponseBody, TestAccountRequest.Delete)));
            return 1;
        }
        catch (XboxPartnerResponseException exception)
        {
            XboxPartnerCommandSupport.WriteCorrelationId(exception.CorrelationId, stderr);
            stderr.WriteLine($"The service accepted the request, but its response could not be read: {exception.Message}");
            WriteDeleteOutcomeUnknown(id, getBatch, stderr);
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
                    "Canceled while waiting for the service to answer the delete.",
                OperationCanceledException => "Timed out waiting for the service to answer the delete.",
                HttpRequestException or IOException =>
                    $"The connection failed while waiting for the service to answer the delete: {exception.Message}",
                _ => $"The delete was sent, but failed before its answer could be read: {exception.Message}",
            });
            WriteDeleteOutcomeUnknown(id, getBatch, stderr);
            return OutcomeUnknownExitCode;
        }

        TestAccountBatchDeletion deletion = accepted.Deletion;
        string jobId = Clean(deletion.JobId);
        XboxPartnerCommandSupport.WriteCorrelationId(accepted.CorrelationId, stderr);
        stderr.WriteLine($"Test account batch delete accepted: batch {id}, job {jobId}.");
        string checkBatch =
            $"Check it with: {getBatch} " +
            $"It answers {TestAccountBatchDeletionWaiter.BatchNotFoundCode} once the batch is gone.";
        string stillDeleting = $"Deletion may still be running on the service. {checkBatch}";
        if (noWait)
        {
            WriteDeletion(accepted, state: null, output, stdout, stderr);
            stderr.WriteLine($"Deletion continues on the service. {checkBatch}");
            return 0;
        }

        stderr.WriteLine($"Waiting up to {FormatDuration(timeout)} for batch {id} to be deleted...");
        try
        {
            TestAccountBatchDeletionWait wait = await TestAccountBatchDeletionWaiter.WaitAsync(
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
            switch (wait.Outcome)
            {
                case TestAccountBatchDeletionOutcome.Deleted:
                    WriteDeletion(accepted, DeletedState, output, stdout, stderr);
                    stderr.WriteLine($"Batch {id} deleted.");
                    return 0;
                case TestAccountBatchDeletionOutcome.Stopped:
                    stderr.WriteLine(
                        $"Delete job {jobId} stopped, but batch {id} still exists. Inspect it with: {getBatch}");
                    stderr.WriteLine("Then delete it again.");
                    return JobFailedExitCode;
                default:
                    WriteDeletion(accepted, state: null, output, stdout, stderr);
                    stderr.WriteLine(DescribeNotYetDeleted(id, jobId, timeout, wait.LastBatch));
                    stderr.WriteLine(stillDeleting);
                    return JobNotFinishedExitCode;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stderr.WriteLine($"Stopped waiting. {stillDeleting}");
        }
        // Any read failure leaves the delete running on the service; report it with the way to
        // check rather than crash.
        catch (Exception exception)
        {
            WriteJobReadError(exception, output, stderr, TestAccountRequest.Get);
            stderr.WriteLine($"Could not read batch {id}. {stillDeleting}");
        }

        return JobNotFinishedExitCode;
    }

    /// <summary>
    /// Asks before deleting. In a terminal the batch is read first, so the prompt names what will
    /// be deleted; a batch that cannot be read is not deleted. Outside a terminal the CLI refuses
    /// without reading anything.
    /// </summary>
    private static async Task<bool> ConfirmDeleteAsync(
        XboxPartnerClient client,
        long? sellerId,
        long batchId,
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
        const string effect =
            "Its accounts lose access to their sandboxes and the test account service deletes its records " +
            "of them. Their Microsoft accounts and XBOX profiles are not closed. This cannot be undone.";
        if (!dependencies.IsInteractive())
        {
            stderr.WriteLine($"This deletes test account batch {id} for {seller}. {effect}");
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
            stderr.WriteLine("Canceled. Nothing was deleted.");
            return false;
        }
        catch (Exception exception)
        {
            WriteJobReadError(exception, output, stderr, TestAccountRequest.Get);
            stderr.WriteLine($"Could not read batch {id}, so nothing was deleted.");
            return false;
        }

        string creator = batch.CreatedBy > 0
            ? $"user {batch.CreatedBy.ToString(CultureInfo.InvariantCulture)}"
            : "an unknown user";
        stderr.WriteLine(FormattableString.Invariant(
            $"This deletes test account batch {id} ({Clean(XboxPartnerCommandSupport.TestAccountText(batch.EmailTemplate))}, ") +
            FormattableString.Invariant($"{batch.BatchSize} accounts, created by {creator}) for {seller}. {effect}"));
        if (!string.IsNullOrWhiteSpace(batch.JobId))
        {
            stderr.WriteLine(
                $"  A job is running on this batch ({Clean(batch.JobId)}); the service refuses the delete until it stops.");
        }

        stderr.Write("Delete this batch? [y/N]: ");
        stderr.Flush();
        string? answer = dependencies.ReadConfirmation()?.Trim();
        if (string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        stderr.WriteLine("Test account batch deletion canceled.");
        return false;
    }

    /// <summary>
    /// Describes a batch the wait left behind. A delete job can fail or stall and keep its jobId on
    /// the batch indefinitely, so this reports what the last read showed rather than claiming the
    /// delete is still progressing.
    /// </summary>
    private static string DescribeNotYetDeleted(string batchId, string jobId, TimeSpan timeout, TestAccountBatch? last)
    {
        string waited = FormatDuration(timeout);
        if (last is null)
        {
            return $"Batch {batchId} was not deleted yet after {waited}; delete job {jobId} was not seen ending.";
        }

        string lastJob = string.IsNullOrWhiteSpace(last.JobId) ? "none" : Clean(last.JobId);
        string progress = string.IsNullOrWhiteSpace(last.Progress) ? "unknown" : Clean(last.Progress);
        return $"Batch {batchId} was not deleted yet after {waited}. Its jobId is {lastJob} and its progress is " +
            $"{progress}. If the jobId stays set and progress stops changing, the delete job may have stalled.";
    }

    private static void WriteDeleteOutcomeUnknown(string batchId, string getBatch, TextWriter stderr)
    {
        stderr.WriteLine(
            $"The delete may have reached the service, so it is unknown whether batch {batchId} is being deleted.");
        stderr.WriteLine($"To find out, run: {getBatch}");
        stderr.WriteLine($"  404 {TestAccountBatchDeletionWaiter.BatchNotFoundCode}: the batch was deleted.");
        stderr.WriteLine("  jobId set: a job, likely the delete, is still running; check again later.");
        stderr.WriteLine(
            "  jobId null: the delete may not have started yet; wait a minute and check again. " +
            "If jobId is still null, delete it again.");
    }

    /// <summary>
    /// Writes the delete answer, with <paramref name="state"/> in place of the service's when the
    /// CLI knows more than the answer did. Other fields pass through as the service sent them.
    /// </summary>
    private static void WriteDeletion(
        TestAccountBatchDeletionResult result,
        string? state,
        OutputOptions output,
        TextWriter stdout,
        TextWriter stderr)
    {
        if (output.TableView is not null)
        {
            var table = XboxPartnerTableFormatter.FormatTestAccountBatchDeletion(
                state is null ? result.Deletion : result.Deletion with { State = state });
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

    /// <summary>Hints for delete failures, keyed on the problem <c>code</c> only, never the detail.</summary>
    private static string? DescribeDeletionFailure(HttpStatusCode statusCode, string? responseBody) =>
        ReadProblemString(responseBody, "code") switch
        {
            InteractionRequiredCode => null,
            "test_account_batch_not_owned" =>
                "Another developer created this batch. To delete it anyway, run again with --allow-not-mine; " +
                "the test account service may still refuse, depending on your permissions.",
            "test_account_batch_busy" =>
                "A job is running on this batch, possibly a delete that is already in progress. Read the batch " +
                "with 'xbox test-account batch get'; once its jobId is null, try again. If the jobId never " +
                "clears, the job may have stalled.",
            TestAccountBatchDeletionWaiter.BatchNotFoundCode =>
                "No such batch for this seller. Check the batch ID and --seller-id. A batch that was already " +
                "deleted is not found either.",
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
                "Deleting test account batches may be unavailable.",
            _ => null,
        };
}
