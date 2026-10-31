// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Globalization;
using System.Net;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Output;
using XboxDevCLI.Services;

namespace XboxDevCLI.Commands;

internal static partial class TestAccountCommandFactory
{
    private static Command CreateJobCommand()
    {
        var group = new Command("job", "Follow XBOX test account creation jobs");
        group.Subcommands.Add(CreateJobGetCommand());
        group.Subcommands.Add(CreateJobWaitCommand());
        return group.WithMetadata(new CommandMetadata(
            Scope: "Creation jobs started by 'xbox test-account batch create'."));
    }

    private static Command CreateJobGetCommand()
    {
        var (jobIdArgument, batchIdOption, sellerOption) = CreateJobInputs();
        var command = new Command("get", "Show the state of an XBOX test account creation job");
        command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.ReadOnly,
            Scope: "One test account creation job for the selected seller.",
            Output:
                "The job as JSON: batchId, jobId, state, total, succeeded, failed, errorCode, errorMessage, " +
                "and progress. Table output shows one row; progress and errors go to stderr.",
            SafetyDetails:
                "Read only; never creates accounts. Exits 0 for a running or succeeded job, 2 for a failed " +
                "job, and 1 for request errors.",
            Examples:
            [
                "xbox test-account job get JOB_ID --batch-id 123456",
                "xbox test-account job get JOB_ID --batch-id 123456 --output table",
            ],
            LearnMore:
            [
                "Reads the job once. Use 'xbox test-account job wait' to poll until it stops.",
            ],
            Table: XboxPartnerCommandSupport.TestAccountJobView));
        command.Arguments.Add(jobIdArgument);
        command.Options.Add(batchIdOption);
        sellerOption.AddTo(command);
        command.SetAction((ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            string jobId = parseResult.GetValue(jobIdArgument)!.Trim();
            long batchId = parseResult.GetValue(batchIdOption);
            return InvokeAsync(
                parseResult,
                sellerOption,
                $"Reading test account job {jobId} of batch {batchId} using the signed in Entra ID token",
                async (client, sellerId, stdout, stderr, output, token) =>
                {
                    var recovery = new RecoveryCommands(parseResult, sellerId);
                    TestAccountJobResult? result = await ReadJobAsync(
                        client, sellerId, batchId, jobId, output, stderr, token).ConfigureAwait(false);
                    if (result is null)
                    {
                        return 1;
                    }

                    string? running = result.RetryAfter is { } retryAfter
                        ? $"Check again in {FormatDuration(retryAfter)}, or wait for it with: {recovery.Wait(result.Job)}"
                        : $"Wait for it with: {recovery.Wait(result.Job)}";
                    return Finish(result, recovery, output, stdout, stderr, $"Job {Clean(result.Job.JobId)} is still running. {running}");
                },
                cancellationToken);
        });
        return command;
    }

    private static Command CreateJobWaitCommand()
    {
        var (jobIdArgument, batchIdOption, sellerOption) = CreateJobInputs();
        var timeoutOption = CreateTimeoutOption();
        var command = new Command("wait", "Wait for an XBOX test account creation job to stop running");
        command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.ReadOnly,
            Scope: "One test account creation job for the selected seller.",
            Output:
                "The last job read as JSON. Table output shows one row. Progress, errors, and the command " +
                "to resume waiting go to stderr.",
            SafetyDetails:
                "Read only; never creates accounts. Exits 0 when the job succeeded, 2 when it failed, 4 " +
                "when the wait ends (timeout, Ctrl+C, or a read failure after waiting began) before the job " +
                "stops, and 1 when the first read fails.",
            Examples:
            [
                "xbox test-account job wait JOB_ID --batch-id 123456",
                "xbox test-account job wait JOB_ID --batch-id 123456 --timeout 2h",
            ],
            LearnMore:
            [
                "Polls as the service's Retry-After asks, 10 seconds when it does not say. A job can run " +
                "for days; stopping the wait never stops the job.",
            ],
            Table: XboxPartnerCommandSupport.TestAccountJobView));
        command.Arguments.Add(jobIdArgument);
        command.Options.Add(batchIdOption);
        sellerOption.AddTo(command);
        command.Options.Add(timeoutOption);
        command.SetAction((ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            string jobId = parseResult.GetValue(jobIdArgument)!.Trim();
            long batchId = parseResult.GetValue(batchIdOption);
            TimeSpan timeout = ReadTimeout(parseResult, timeoutOption);
            var dependencies = Dependencies;
            var verbose = parseResult.GetValue(Program.VerboseOption);
            return InvokeAsync(
                parseResult,
                sellerOption,
                $"Reading test account job {jobId} of batch {batchId} using the signed in Entra ID token",
                async (client, sellerId, stdout, stderr, output, token) =>
                {
                    var recovery = new RecoveryCommands(parseResult, sellerId);
                    TestAccountJobResult? initial = await ReadJobAsync(
                        client, sellerId, batchId, jobId, output, stderr, token).ConfigureAwait(false);
                    if (initial is null)
                    {
                        return 1;
                    }

                    if (!initial.Job.IsRunning)
                    {
                        return Finish(initial, recovery, output, stdout, stderr, runningMessage: null);
                    }

                    return await WaitAndFinishAsync(
                        client, initial, sellerId, timeout, recovery, output, verbose, dependencies, stdout, stderr,
                        token).ConfigureAwait(false);
                },
                cancellationToken);
        });
        return command;
    }

    private static (Argument<string> JobId, Option<long> BatchId, XdtsSellerOption Seller) CreateJobInputs()
    {
        var jobIdArgument = new Argument<string>("job-id")
        {
            Description = "The creation job ID, as returned by 'xbox test-account batch create' or 'batch list'.",
        };
        jobIdArgument.Validators.Add(result =>
        {
            if (result.Tokens.Count > 0 && string.IsNullOrWhiteSpace(result.Tokens[0].Value))
            {
                result.AddError("job-id must not be empty.");
            }
        });
        var batchIdOption = new Option<long>("--batch-id")
        {
            Description = "The numeric batch ID the job belongs to.",
            Required = true,
        };
        batchIdOption.Validators.Add(result =>
        {
            if (result.Tokens.Count > 0 &&
                long.TryParse(result.Tokens[0].Value, out long value) &&
                value <= 0)
            {
                result.AddError("--batch-id must be a positive number.");
            }
        });
        return (jobIdArgument, batchIdOption, new XdtsSellerOption());
    }

    private static async Task<TestAccountJobResult?> ReadJobAsync(
        XboxPartnerClient client,
        long? sellerId,
        long batchId,
        string jobId,
        OutputOptions output,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        try
        {
            return await client.GetTestAccountJobAsync(sellerId, batchId, jobId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is XboxPartnerApiException or XboxPartnerResponseException)
        {
            WriteJobReadError(exception, output, stderr);
            return null;
        }
    }

    /// <summary>
    /// Polls until the job stops or <paramref name="timeout"/> passes. A timeout, cancel, or read
    /// failure with the job possibly still running exits <see cref="JobNotFinishedExitCode"/>, so a
    /// script can tell "resume with job wait" apart from a request that failed before waiting began.
    /// </summary>
    private static async Task<int> WaitAndFinishAsync(
        XboxPartnerClient client,
        TestAccountJobResult initial,
        long? sellerId,
        TimeSpan timeout,
        RecoveryCommands recovery,
        OutputOptions output,
        bool verbose,
        TestAccountCommandDependencies dependencies,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        TestAccountJob job = initial.Job;
        string jobId = Clean(job.JobId);
        stderr.WriteLine($"Waiting up to {FormatDuration(timeout)} for job {jobId}...");
        TestAccountJobResult last = initial;
        try
        {
            TestAccountJobResult final = await TestAccountJobWaiter.WaitAsync(
                initial,
                token =>
                {
                    Program.WriteVerbose(verbose, () => FormattableString.Invariant(
                        $"Reading test account job {jobId} of batch {job.BatchId}"));
                    return client.GetTestAccountJobAsync(sellerId, job.BatchId, job.JobId, token);
                },
                timeout,
                dependencies.DelayAsync,
                dependencies.GetUtcNow,
                result =>
                {
                    if (result.Job.IsRunning &&
                        (result.Job.Succeeded != last.Job.Succeeded || result.Job.Failed != last.Job.Failed))
                    {
                        stderr.WriteLine(FormattableString.Invariant(
                            $"Job {jobId}: {result.Job.Succeeded} of {result.Job.Total} created, {result.Job.Failed} failed."));
                    }

                    last = result;
                },
                cancellationToken).ConfigureAwait(false);
            return Finish(
                final,
                recovery,
                output,
                stdout,
                stderr,
                FormattableString.Invariant(
                    $"Job {jobId} is still running after {FormatDuration(timeout)} ({final.Job.Succeeded} of {final.Job.Total} created). ") +
                    $"It keeps running on the service; resume waiting with: {recovery.Wait(final.Job)}",
                runningExitCode: JobNotFinishedExitCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stderr.WriteLine(
                $"Stopped waiting; job {jobId} keeps running on the service. Resume waiting with: {recovery.Wait(job)}");
        }
        // Any read failure, including a token that can no longer be refreshed, leaves the job running
        // on the service; report it with the resume command rather than crash.
        catch (Exception exception)
        {
            WriteJobReadError(exception, output, stderr);
            stderr.WriteLine(
                $"Could not read job {jobId}; it may still be running. Resume waiting with: {recovery.Wait(job)}");
        }

        // No job JSON on stdout: the last read is stale, and the resume command on stderr is the
        // way forward. After a create, the "accepted" line already told the user a batch exists.
        return JobNotFinishedExitCode;
    }

    private static int Finish(
        TestAccountJobResult result,
        RecoveryCommands recovery,
        OutputOptions output,
        TextWriter stdout,
        TextWriter stderr,
        string? runningMessage,
        int runningExitCode = 0)
    {
        WriteJob(result, output, stdout, stderr);
        TestAccountJob job = result.Job;
        string jobId = Clean(job.JobId);
        if (job.IsFailed)
        {
            string reason = string.IsNullOrWhiteSpace(job.ErrorCode) && string.IsNullOrWhiteSpace(job.ErrorMessage)
                ? string.Empty
                : $": {Clean(XboxPartnerCommandSupport.TestAccountText(job.ErrorCode))}: " +
                    Clean(XboxPartnerCommandSupport.TestAccountText(job.ErrorMessage));
            stderr.WriteLine(FormattableString.Invariant(
                $"Job {jobId} failed{reason}. {job.Succeeded} of {job.Total} accounts were created."));
            stderr.WriteLine($"Inspect the batch with: {recovery.GetBatch(job)}");
            return JobFailedExitCode;
        }

        if (job.IsSucceeded)
        {
            stderr.WriteLine(FormattableString.Invariant(
                $"Job {jobId} succeeded: {job.Succeeded} of {job.Total} accounts created."));
            stderr.WriteLine($"List the accounts with: {recovery.GetBatch(job)}");
            return 0;
        }

        stderr.WriteLine(runningMessage ?? FormattableString.Invariant(
            $"Job {jobId} is running ({job.Succeeded} of {job.Total} created). Follow it with: ") + recovery.Wait(job));
        return runningExitCode;
    }

    private static void WriteJob(TestAccountJobResult result, OutputOptions output, TextWriter stdout, TextWriter stderr)
    {
        if (output.TableView is not null)
        {
            var table = XboxPartnerTableFormatter.FormatTestAccountJob(result.Job);
            stdout.WriteLine(table.Text);
            foreach (string note in table.Notes)
            {
                stderr.WriteLine(note);
            }

            return;
        }

        stdout.WriteLine(PlayFabApiClient.FormatJson(result.RawResponse, output.IsCompact));
    }

    private static void WriteJobReadError(
        Exception exception,
        OutputOptions output,
        TextWriter stderr,
        TestAccountRequest request = TestAccountRequest.Job)
    {
        switch (exception)
        {
            case XboxPartnerApiException api:
                XboxPartnerCommandSupport.WriteCorrelationId(api.CorrelationId, stderr);
                stderr.WriteLine(FormatError(
                    api,
                    output.IsCompact,
                    DescribeFailure(api.StatusCode, api.ResponseBody, request)));
                break;
            case XboxPartnerResponseException response:
                XboxPartnerCommandSupport.WriteCorrelationId(response.CorrelationId, stderr);
                stderr.WriteLine($"Request failed: {response.Message}");
                break;
            case OperationCanceledException:
                stderr.WriteLine("Request timed out.");
                break;
            default:
                stderr.WriteLine($"Request failed: {exception.Message}");
                break;
        }
    }


    private static string Clean(string value) => XboxPartnerTableFormatter.NormalizeCell(value);

    internal static string FormatDuration(TimeSpan value)
    {
        static string Unit(double amount, string name) =>
            amount.ToString("0.#", CultureInfo.InvariantCulture) + " " + (amount == 1 ? name : name + "s");

        return value.TotalDays >= 1 && value.TotalHours % 24 == 0 ? Unit(value.TotalDays, "day")
            : value.TotalHours >= 1 && value.TotalMinutes % 60 == 0 ? Unit(value.TotalHours, "hour")
            : value.TotalMinutes >= 1 && value.TotalSeconds % 60 == 0 ? Unit(value.TotalMinutes, "minute")
            : Unit(Math.Ceiling(value.TotalSeconds), "second");
    }

    /// <summary>Hints for create and job failures, keyed on the problem <c>code</c> only, never the detail.</summary>
    private static string? DescribeCreationFailure(
        HttpStatusCode statusCode,
        string? responseBody,
        TestAccountRequest request)
    {
        bool isCreate = request == TestAccountRequest.Create;
        return ReadProblemString(responseBody, "code") switch
        {
            InteractionRequiredCode => null,
            "test_account_sandbox_access_denied" =>
                "You lack access to one or more of the --sandbox-id values for this seller. Check the sandbox " +
                "IDs and --seller-id. If every sandbox is refused, your developer token for this seller may " +
                "grant no sandboxes at all: your account may not be linked to the seller, the seller may have " +
                "no sandboxes, or Partner Center may not allow you to manage test accounts.",
            "test_account_count_invalid" =>
                "Check --count against the limit this service allows.",
            "test_account_child_count_invalid" =>
                "Check --child-count against the limit this service allows.",
            "test_account_email_prefix_invalid" =>
                "--email-prefix must be the sandbox group, a hyphen, and a template, for example ABCD-tester.",
            "test_account_name_invalid" => "Check --first-name and --last-name.",
            "test_account_password_invalid" =>
                "The password does not meet the test account password rules.",
            "test_account_security_question_invalid" => "Check --security-question.",
            "test_account_security_answer_invalid" =>
                "Choose a security answer that differs from the password and does not appear in the security " +
                "question.",
            "test_account_alternate_email_invalid" =>
                "Check --alternate-email, or omit it to use your developer email.",
            "test_account_keyword_invalid" => "Check the --tag values.",
            "test_account_locale_invalid" => "Check --locale, for example en-US.",
            "test_account_subscription_unavailable" =>
                "That subscription is not available in this environment. Try another --subscription; some " +
                "environments offer only Silver.",
            "test_account_limit_exceeded" =>
                "The request would exceed the seller's test account limit.",
            AccessDeniedCode when isCreate =>
                "Your account lacks the Partner Center role to create test accounts for this seller, or the " +
                "seller is wrong; pass --seller-id.",
            AccessDeniedCode =>
                "Another developer or another seller owns this job; pass --seller-id, or ask its creator.",
            "downstream_request_rejected" when isCreate =>
                "The test account service rejected the request. Check that the --email-prefix group is one of " +
                "your sandbox groups (the part of a sandbox ID before the dot), and the length of the security " +
                "question and answer.",
            "downstream_request_rejected" when statusCode == HttpStatusCode.NotFound =>
                "No such job in this batch. Check the job ID and --batch-id.",
            "invalid_request" when isCreate =>
                "The service rejected a field; see detail and errors.",
            null when statusCode == HttpStatusCode.BadRequest && isCreate =>
                "The service rejected a field; see errors. A field sent by the CLI may not be supported.",
            "downstream_throttled" =>
                "The test account service is throttling requests. Wait, then try again.",
            "downstream_gateway_timeout" or "downstream_service_failed" when !isCreate =>
                "The test account service did not answer. Retry.",
            "feature_disabled" =>
                "Test account creation is disabled.",
            "service_configuration_invalid" =>
                "The service is not configured for test accounts. Contact support.",
            null when statusCode == HttpStatusCode.NotFound && !isCreate =>
                "Check the job ID and --batch-id. Test account jobs may be unavailable.",
            null when statusCode == HttpStatusCode.NotFound =>
                "Creating test accounts may be unavailable.",
            _ => null,
        };
    }
}
