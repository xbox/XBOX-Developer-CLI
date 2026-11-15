// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Net;
using Microsoft.Extensions.Configuration;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Output;
using XboxDevCLI.Services;

namespace XboxDevCLI.Commands;

internal static partial class PlaytestCommandFactory
{
    /// <summary>Exit code when the publish job finished, but the playtest's status is PublishFailed.</summary>
    internal const int PublishFailedExitCode = 2;

    /// <summary>Exit code when the publish was queued, but the wait ended before it finished.</summary>
    internal const int PublishNotFinishedExitCode = 4;

    internal static readonly TimeSpan PublishPollInterval = TimeSpan.FromSeconds(10);

    internal const string AgeRatingHint =
        "To fix this, complete the age-rating questionnaire in Partner Center.";

    /// <summary>Exit code when the delete was queued, but the wait ended before it finished.</summary>
    internal const int DeleteNotFinishedExitCode = PublishNotFinishedExitCode;

    private const string PublishFailedStatus = "PublishFailed";
    private const string DeletedStatus = "Deleted";

    private static Option<bool> CreateConfirmOption(string description) =>
        new("--confirm", "--yes", "-y") { Description = description };

    private static bool IsConfirmed(ParseResult result, Option<bool> confirm) =>
        result.GetResult(confirm) is { Implicit: false }
            ? result.GetValue(confirm)
            : result.GetValue(Program.ConfirmOption);

    private static Command CreatePublishCommand(
        Func<IConfiguration, XboxPartnerClient>? clientFactory,
        XboxPartnerJobDependencies dependencies)
    {
        var playtestId = CreatePlaytestIdArgument("playtest-id", "The playtest ID, as shown by 'xbox playtest list'.");
        var confirm = CreateConfirmOption("Confirm publishing the playtest without prompting.");
        var noWait = new Option<bool>("--no-wait")
        {
            Description = "Return as soon as the publish is queued, without waiting for it to finish.",
        };
        var timeout = TestAccountCommandFactory.CreateTimeoutOption("the publish to finish");
        var game = XboxPartnerGameResolver.CreateGameOptions();
        var seller = XboxPartnerCommandSupport.CreateSellerIdOption();
        var baseUrl = XboxPartnerCommandSupport.CreateBaseUrlOption();
        var command = new Command("publish", "Publish a playtest and wait until it's scheduled or live")
        {
            playtestId, confirm, noWait, timeout, game.StoreId, game.TitleId, seller, baseUrl,
        };
        command.Validators.Add(result =>
        {
            XboxPartnerGameResolver.RequireOneGame(result, game);
            if (result.GetValue(noWait) && result.GetResult(timeout) is { Implicit: false })
            {
                result.AddError("--no-wait and --timeout cannot be combined.");
            }
        });
        command.SetAction((ParseResult result, CancellationToken cancellationToken) =>
            ExecuteAsync(
                result, game, seller, baseUrl, clientFactory,
                async (client, url, sellerId, storeId) =>
                {
                    var id = result.GetRequiredValue(playtestId).Trim();
                    var recovery = XboxPartnerCommandSupport.BuildRecoveryCommand(
                        result, $"playtest get {ShellQuoting.Quote(id)} --store-id {ShellQuoting.Quote(storeId)}", sellerId);
                    PlaytestFields.PlaytestSummary summary;
                    using (RequestProgress.Start("Reading playtest"))
                    {
                        summary = PlaytestFields.ReadSummary(
                            await client.GetPlaytestAsync(url, sellerId, storeId, id, cancellationToken));
                    }

                    if (UsableEtag(summary.Etag) is not { } etag)
                    {
                        return 1;
                    }

                    var initialStatus = summary.Status;
                    var readinessWarnings = await ReadReadinessWarningsAsync(
                        result, client, url, sellerId, storeId, id, cancellationToken);
                    if (readinessWarnings.Count > 0)
                    {
                        Console.Error.WriteLine("Review these before you publish:");
                        foreach (var warning in readinessWarnings)
                        {
                            foreach (var line in TerminalText.MultiLine(warning).Split('\n'))
                            {
                                Console.Error.WriteLine($"  {line}");
                            }
                        }
                    }

                    if (!await ConfirmAsync(
                        IsConfirmed(result, confirm),
                        dependencies,
                        $"Publishing {DescribePlaytest(summary, id)} of game {storeId} under seller {sellerId}. "
                            + "Once published, it's scheduled or live for its audiences.",
                        readinessWarnings.Count > 0 ? "Publish this playtest anyway?" : "Publish this playtest?",
                        "Playtest publish canceled.",
                        cancellationToken))
                    {
                        return 1;
                    }

                    var correlationId = Guid.NewGuid().ToString();
                    Program.WriteVerbose(result.GetValue(Program.VerboseOption),
                        () => $"Publishing playtest {id} with If-Match {etag} and correlation ID {correlationId}.");
                    var sent = false;
                    string json;
                    try
                    {
                        using (RequestProgress.Start($"Publishing playtest {id}"))
                        {
                            json = await client.PublishPlaytestAsync(
                                url, sellerId, storeId, id, etag, correlationId, cancellationToken,
                                onSending: () => sent = true);
                        }
                    }
                    catch (OperationCanceledException) when (!sent && cancellationToken.IsCancellationRequested)
                    {
                        Console.Error.WriteLine($"Canceled before the request was sent, so playtest {id} wasn't published.");
                        return 1;
                    }
                    catch (XboxPartnerApiException exception)
                        when (exception.StatusCode == HttpStatusCode.UnprocessableEntity
                            && TestAccountCommandFactory.ReadProblemString(exception.ResponseBody, "code") == "playtest_publish_blocked"
                            && PlaytestFields.ReadMessages(exception.ResponseBody, "validation") is { Count: > 0 } checks)
                    {
                        XboxPartnerCommandSupport.WriteCorrelationId(exception.CorrelationId, Console.Error);
                        Console.Error.WriteLine("The playtest wasn't published, because these checks failed:");
                        WriteChecks(checks);
                        return 1;
                    }
                    catch (XboxPartnerApiException exception)
                        when (exception.StatusCode == HttpStatusCode.PreconditionFailed)
                    {
                        XboxPartnerCommandSupport.WriteRequestError(exception, result, cancellationToken);
                        Console.Error.WriteLine(
                            "The playtest changed after it was read, so it wasn't published. Read it again, check it, and retry. To read it, run:");
                        Console.Error.WriteLine(recovery);
                        return 1;
                    }
                    catch (Exception exception) when (sent && IsUncertainSubmission(exception))
                    {
                        Console.Error.WriteLine(
                            "The playtest publish was interrupted or its response could not be verified; "
                            + "it is unknown whether it was queued.");
                        Console.Error.WriteLine($"Check the playtest's status before publishing again with: {recovery}");
                        XboxPartnerCommandSupport.WriteRequestError(exception, result, cancellationToken, correlationId);
                        return OutcomeUnknownExitCode;
                    }

                    var warnings = PlaytestFields.ReadMessages(json, "warnings");
                    if (warnings.Count > 0)
                    {
                        Console.Error.WriteLine("The publish was queued with these warnings:");
                        WriteChecks(warnings);
                    }

                    if (PlaytestFields.ReadResponseString(json, "publishJobId") is { Length: > 0 } jobId)
                    {
                        Console.Error.WriteLine($"Publish job: {XboxPartnerCommandSupport.ForTerminal(jobId)}");
                    }

                    if (result.GetValue(noWait))
                    {
                        WriteOutput(result, json, json);
                        Console.Error.WriteLine($"Publishing continues on the service. Check its status with: {recovery}");
                        return 0;
                    }

                    return await WaitForPlaytestAsync(
                        PublishWait(result, id), result, client, url, sellerId, storeId, id, initialStatus, etag,
                        TestAccountCommandFactory.ReadTimeout(result, timeout), recovery, dependencies,
                        cancellationToken);
                },
                cancellationToken,
                writing: true));
        return command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.RemoteMutation,
            Stage: CommandStage.Experimental,
            Scope: "One playtest of a game owned by the seller selected by --seller-id, XBOX_SELLER_ID, or 'context set'.",
            Output: "Once the playtest is Scheduled, Live, or PublishFailed, the playtest as 'xbox playtest get' writes it. "
                + "With --no-wait, the publish response: a JSON object with publishJobId and warnings. "
                + "Nothing is written to stdout when the publish is refused, its outcome is unknown, or the wait "
                + "ends first. Always JSON, so --output table is refused. Warnings, failed checks, progress, and "
                + "recovery commands go to stderr.",
            SafetyDetails: "Publishes a playtest in Partner Center, which offers it to its audiences from its start date. "
                + "Requires --confirm (--yes, -y) or terminal confirmation. The request is sent once and never "
                + "retried. Agent approval is still required.",
            Examples:
            [
                "xbox playtest publish 11111111-2222-3333-4444-555555555555 --store-id 9N0000000000 --confirm",
                "xbox playtest publish 11111111-2222-3333-4444-555555555555 --xbox-title-id 1234ABCD --no-wait --confirm",
            ],
            LearnMore:
            [
                "The playtest is read first, and the publish applies only to the version read. If the playtest "
                    + "changes in between, the publish fails (HTTP 412); read it again and retry.",
                "Before asking, the command checks the game's XBOX services setup, such as whether its embargo date is after "
                    + "the playtest end date, and lists anything to review on stderr. These checks only advise and never stop the publish; "
                    + "--confirm publishes without asking. If they can't run, the command says so and publishes as usual.",
                "When the service's own checks block the publish, nothing is queued, and stderr lists each failed check as an error "
                    + "or a warning. Fix the errors and publish again.",
                "Fails (HTTP 409) when the playtest's status doesn't allow publishing; "
                    + "check it with 'xbox playtest get'.",
                "Waits by checking the playtest's status every 10 seconds, until it's Scheduled, Live, or PublishFailed, "
                    + "or --timeout passes. Ctrl+C stops waiting; the publish continues on the service.",
                "Exit codes: 0 published, or queued with --no-wait; 1 not published; 2 the publish failed "
                    + "(status PublishFailed); 3 the request was sent but its outcome is unknown, and stderr shows "
                    + "its correlation ID; 4 the publish was queued, but waiting ended before it finished.",
            ]));
    }

    private static Command CreateDeleteCommand(
        Func<IConfiguration, XboxPartnerClient>? clientFactory,
        XboxPartnerJobDependencies dependencies)
    {
        var playtestId = CreatePlaytestIdArgument("playtest-id", "The playtest ID, as shown by 'xbox playtest list'.");
        var confirm = CreateConfirmOption("Confirm deleting the playtest without prompting.");
        var game = XboxPartnerGameResolver.CreateGameOptions();
        var seller = XboxPartnerCommandSupport.CreateSellerIdOption();
        var baseUrl = XboxPartnerCommandSupport.CreateBaseUrlOption();
        var noWait = new Option<bool>("--no-wait")
        {
            Description = "Return as soon as the delete is queued, without waiting for it to finish.",
        };
        var timeout = TestAccountCommandFactory.CreateTimeoutOption("the delete to finish");
        var command = new Command("delete", "Delete a playtest and wait for the delete to finish")
        {
            playtestId, confirm, noWait, timeout, game.StoreId, game.TitleId, seller, baseUrl,
        };
        command.Validators.Add(result =>
        {
            XboxPartnerGameResolver.RequireOneGame(result, game);
            if (result.GetValue(noWait) && result.GetResult(timeout) is { Implicit: false })
            {
                result.AddError("--no-wait and --timeout cannot be combined.");
            }
        });
        command.SetAction((ParseResult result, CancellationToken cancellationToken) =>
            ExecuteAsync(
                result, game, seller, baseUrl, clientFactory,
                async (client, url, sellerId, storeId) =>
                {
                    var id = result.GetRequiredValue(playtestId).Trim();
                    var recovery = XboxPartnerCommandSupport.BuildRecoveryCommand(
                        result, $"playtest get {ShellQuoting.Quote(id)} --store-id {ShellQuoting.Quote(storeId)}", sellerId);
                    PlaytestFields.PlaytestSummary summary;
                    using (RequestProgress.Start("Reading playtest"))
                    {
                        summary = PlaytestFields.ReadSummary(
                            await client.GetPlaytestAsync(url, sellerId, storeId, id, cancellationToken));
                    }

                    if (UsableEtag(summary.Etag) is not { } etag)
                    {
                        return 1;
                    }

                    if (!await ConfirmAsync(
                        IsConfirmed(result, confirm),
                        dependencies,
                        $"Deleting {DescribePlaytest(summary, id)} of game {storeId} under seller {sellerId}. "
                            + "This can't be undone."
                            + LivePlayersNote(summary, "Its players lose access to it."),
                        "Delete this playtest?",
                        "Playtest delete canceled.",
                        cancellationToken))
                    {
                        return 1;
                    }

                    var correlationId = Guid.NewGuid().ToString();
                    Program.WriteVerbose(result.GetValue(Program.VerboseOption),
                        () => $"Deleting playtest {id} with If-Match {etag} and correlation ID {correlationId}.");
                    var sent = false;
                    string json;
                    try
                    {
                        using (RequestProgress.Start($"Deleting playtest {id}"))
                        {
                            json = await client.DeletePlaytestAsync(
                                url, sellerId, storeId, id, etag, correlationId, cancellationToken,
                                onSending: () => sent = true);
                        }
                    }
                    catch (OperationCanceledException) when (!sent && cancellationToken.IsCancellationRequested)
                    {
                        Console.Error.WriteLine($"Canceled before the request was sent, so playtest {id} wasn't deleted.");
                        return 1;
                    }
                    catch (XboxPartnerApiException exception)
                        when (exception.StatusCode == HttpStatusCode.PreconditionFailed)
                    {
                        XboxPartnerCommandSupport.WriteRequestError(exception, result, cancellationToken);
                        Console.Error.WriteLine(
                            "The playtest changed after it was read, or a delete is already queued. "
                            + "Wait a minute, then read it again before deleting again. To read it, run:");
                        Console.Error.WriteLine(recovery);
                        return 1;
                    }
                    catch (Exception exception) when (sent && IsUncertainSubmission(exception))
                    {
                        Console.Error.WriteLine(
                            "The playtest delete was interrupted or its response could not be verified; "
                            + "it is unknown whether it was queued.");
                        Console.Error.WriteLine($"Check the playtest before deleting again with: {recovery}");
                        XboxPartnerCommandSupport.WriteRequestError(exception, result, cancellationToken, correlationId);
                        return OutcomeUnknownExitCode;
                    }

                    var status = PlaytestFields.ReadDeleteStatus(json);
                    if (string.Equals(status, DeletedStatus, StringComparison.OrdinalIgnoreCase))
                    {
                        WriteOutput(result, json, json);
                        Console.Error.WriteLine($"Playtest {id} deleted.");
                        return 0;
                    }

                    if (result.GetValue(noWait))
                    {
                        WriteOutput(result, json, json);
                        Console.Error.WriteLine($"The delete of playtest {id} was queued. Check it with: {recovery}");
                        return 0;
                    }

                    Console.Error.WriteLine($"The delete of playtest {id} was queued.");
                    return await WaitForPlaytestAsync(
                        DeleteWait(result, id, json), result, client, url, sellerId, storeId, id,
                        summary.Status, etag, TestAccountCommandFactory.ReadTimeout(result, timeout),
                        recovery, dependencies, cancellationToken);
                },
                cancellationToken,
                writing: true));
        return command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.RemoteMutation,
            Stage: CommandStage.Experimental,
            Scope: "One playtest of a game owned by the seller selected by --seller-id, XBOX_SELLER_ID, or 'context set'.",
            Output: "Once the playtest is deleted, the delete response: a JSON object with status, and deleteJobId "
                + "and error when the service returns them. With --no-wait, the delete response as soon as the "
                + "delete is queued; a status other than Deleted, such as Deleting, means it's still running. "
                + "Nothing is written to stdout when the delete is refused, its outcome is unknown, or the wait "
                + "ends first. Always JSON, so --output table is refused. Progress and recovery commands go to "
                + "stderr.",
            SafetyDetails: "Deletes a playtest in Partner Center, which can't be undone. Requires --confirm "
                + "(--yes, -y) or terminal confirmation. The request is sent once and never retried. Agent "
                + "approval is still required.",
            Examples:
            [
                "xbox playtest delete 11111111-2222-3333-4444-555555555555 --store-id 9N0000000000 --confirm",
                "xbox playtest delete 11111111-2222-3333-4444-555555555555 --xbox-title-id 1234ABCD --no-wait --confirm",
            ],
            LearnMore:
            [
                "The playtest is read first, and the delete applies only to the version read. A delete that fails "
                    + "with HTTP 412 may already be queued: wait, then read the playtest "
                    + "again with 'xbox playtest get' before deleting again.",
                "Fails (HTTP 409) when the playtest's status doesn't allow deleting; "
                    + "check it with 'xbox playtest get'.",
                "Waits by checking the playtest's status every 10 seconds, until it's Deleted or no longer found, "
                    + "or --timeout passes. Ctrl+C stops waiting; the delete continues on the service.",
                "Exit codes: 0 deleted, or queued with --no-wait; 1 not deleted; 3 the request was sent but its "
                    + "outcome is unknown, and stderr shows its correlation ID; 4 the delete was queued, but waiting "
                    + "ended before it finished.",
            ]));
    }

    // The checks only advise, so failing to read them, for example from a service that doesn't
    // offer them yet, never stops the publish.
    private static async Task<IReadOnlyList<string>> ReadReadinessWarningsAsync(
        ParseResult result,
        XboxPartnerClient client,
        string url,
        string sellerId,
        string storeId,
        string id,
        CancellationToken cancellationToken)
    {
        try
        {
            string body;
            using (RequestProgress.Start("Checking playtest before publishing"))
            {
                body = await client.GetPlaytestReadinessAsync(url, sellerId, storeId, id, cancellationToken);
            }

            if (PlaytestFields.ReadReadinessWarnings(body) is { } warnings)
            {
                return warnings;
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            Program.WriteVerbose(result.GetValue(Program.VerboseOption),
                () => $"Reading the checks before publishing failed: {XboxPartnerCommandSupport.ForTerminal(exception.Message)}");
        }

        Console.Error.WriteLine("Couldn't run the checks before publishing, so they were skipped.");
        return [];
    }

    // The etag goes into If-Match, so it must be one exact etag. Without one, the gateway would act
    // on whatever version it reads, which is not the version the user confirmed.
    private static string? UsableEtag(string? etag)
    {
        if (QuoteEtag(etag) is { } quoted)
        {
            return quoted;
        }

        Console.Error.WriteLine(
            "The playtest was read without a usable etag, so it wasn't changed. Try again later.");
        return null;
    }

    private static void WriteChecks(IReadOnlyList<XboxLiveValidationMessage> messages)
    {
        foreach (var message in messages)
        {
            Console.Error.WriteLine($"  {XboxLiveValidationMessages.Format(message)}");
            if (string.Equals(message.Error, PlaytestFields.MissingAgeRatingsCode, StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"    {AgeRatingHint}");
            }
        }
    }

    // The prompt names the version the etag will pin, so the confirmation and the request match.
    private static string DescribePlaytest(PlaytestFields.PlaytestSummary summary, string id)
    {
        var name = XboxPartnerCommandSupport.ForTerminal(summary.Name ?? "(unnamed)");
        var status = XboxPartnerCommandSupport.ForTerminal(summary.Status ?? "unknown");
        var start = XboxPartnerCommandSupport.ForTerminal(summary.StartDate ?? "(none)");
        var end = XboxPartnerCommandSupport.ForTerminal(summary.EndDate ?? "(none)");
        return $"playtest {id} '{name}' (status {status}, start {start}, end {end})";
    }

    private static string LivePlayersNote(PlaytestFields.PlaytestSummary summary, string consequence) =>
        string.Equals(summary.Status, "Live", StringComparison.OrdinalIgnoreCase) ? $" {consequence}" : string.Empty;

    /// <summary>What a playtest wait is waiting for, and what ends it.</summary>
    /// <param name="Goal">The finished state for messages, such as "published".</param>
    /// <param name="Activity">The running operation for messages, such as "Publishing".</param>
    /// <param name="Finish">Given a read, returns the exit code when the wait is over, or null to keep
    /// waiting.</param>
    /// <param name="SkipsOldVersion">Whether reads of the version from before the operation are skipped,
    /// because their status could be mistaken for its result.</param>
    /// <param name="OnNotFound">When set, a read that answers 404 ends the wait with its exit code.</param>
    private sealed record PlaytestWait(
        string Goal,
        string Activity,
        Func<string, PlaytestFields.PlaytestSummary, int?> Finish,
        bool SkipsOldVersion,
        Func<int>? OnNotFound = null);

    private static PlaytestWait PublishWait(ParseResult result, string id) => new(
        "published",
        "Publishing",
        (json, summary) =>
        {
            if (string.Equals(summary.Status, "Scheduled", StringComparison.OrdinalIgnoreCase)
                || string.Equals(summary.Status, "Live", StringComparison.OrdinalIgnoreCase))
            {
                WriteOutput(result, json, json);
                Console.Error.WriteLine($"Playtest {id} is published and {summary.Status}.");
                return 0;
            }

            if (string.Equals(summary.Status, PublishFailedStatus, StringComparison.OrdinalIgnoreCase))
            {
                WriteOutput(result, json, json);
                Console.Error.WriteLine($"Publishing playtest {id} failed.{ErrorCodeNote(summary)}");
                return PublishFailedExitCode;
            }

            return null;
        },
        SkipsOldVersion: true);

    // The playtest shows Deleted, then answers 404 once the record is gone. No status says a delete
    // failed, and a queued delete can still show the old status, so any other status keeps waiting.
    // Only a delete sets Deleted, so no read is skipped as old, even if the service keeps the etag.
    private static PlaytestWait DeleteWait(ParseResult result, string id, string deleteJson)
    {
        int Deleted()
        {
            WriteOutput(result, deleteJson, deleteJson);
            Console.Error.WriteLine($"Playtest {id} deleted.");
            return 0;
        }

        return new(
            "deleted",
            "Deleting",
            (_, summary) => string.Equals(summary.Status, DeletedStatus, StringComparison.OrdinalIgnoreCase)
                ? Deleted()
                : null,
            SkipsOldVersion: false,
            OnNotFound: Deleted);
    }

    private static string ErrorCodeNote(PlaytestFields.PlaytestSummary summary) =>
        summary.ErrorCode is { Length: > 0 } code
            ? $" Its error code is {XboxPartnerCommandSupport.ForTerminal(code)}."
            : string.Empty;

    /// <summary>
    /// Reads the playtest until <paramref name="wait"/> says the operation finished, or the timeout
    /// passes. Reads are safe to repeat, so a short run of transient failures is tolerated.
    /// </summary>
    private static async Task<int> WaitForPlaytestAsync(
        PlaytestWait wait,
        ParseResult result,
        XboxPartnerClient client,
        string url,
        string sellerId,
        string storeId,
        string id,
        string? initialStatus,
        string initialEtag,
        TimeSpan timeout,
        string recovery,
        XboxPartnerJobDependencies dependencies,
        CancellationToken cancellationToken)
    {
        var stillRunning = $"{wait.Activity} may still be running on the service. Check its status with: {recovery}";
        Console.Error.WriteLine(
            $"Waiting up to {TestAccountCommandFactory.FormatDuration(timeout)} for playtest {id} to be {wait.Goal}...");
        var deadline = dependencies.GetUtcNow() + timeout;

        var status = initialStatus;
        var changed = false;
        var failures = 0;
        TimeSpan? nextDelay = null;
        try
        {
            while (true)
            {
                var remaining = deadline - dependencies.GetUtcNow();
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                var delay = nextDelay ?? PublishPollInterval;
                await dependencies.DelayAsync(delay < remaining ? delay : remaining, cancellationToken);
                remaining = deadline - dependencies.GetUtcNow();
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                // The deadline bounds the read too, so a slow answer can't stretch the wait past --timeout.
                using var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                readCancellation.CancelAfter(remaining);
                string json;
                try
                {
                    json = await client.GetPlaytestAsync(url, sellerId, storeId, id, readCancellation.Token)
                        .WaitAsync(readCancellation.Token);
                    failures = 0;
                    nextDelay = null;
                }
                catch (Exception) when (readCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (XboxPartnerApiException exception)
                    when (exception.StatusCode == HttpStatusCode.NotFound && wait.OnNotFound is not null
                        && !XboxPartnerErrorGuidance.IsGameNotFound(exception))
                {
                    return wait.OnNotFound();
                }
                catch (Exception exception) when (
                    TestAccountJobWaiter.IsTransient(exception, cancellationToken)
                    && ++failures < TestAccountJobWaiter.MaxConsecutiveReadFailures)
                {
                    nextDelay = exception is XboxPartnerApiException { RetryAfter: { } retryAfter }
                        ? TestAccountJobWaiter.Clamp(retryAfter)
                        : null;
                    continue;
                }

                var summary = PlaytestFields.ReadSummary(json);

                // A read that still carries the etag from before the publish is the old version, whatever
                // its status, so it says nothing about this publish. A read without an etag proves a new
                // version only by a status other than the one read before publishing, so republishing a
                // PublishFailed playtest never reports the earlier failure.
                var readEtag = QuoteEtag(summary.Etag);
                if (wait.SkipsOldVersion
                    && (readEtag == initialEtag
                        || (readEtag is null && !changed
                            && string.Equals(summary.Status, initialStatus, StringComparison.OrdinalIgnoreCase))))
                {
                    continue;
                }

                changed = true;

                if (!string.Equals(summary.Status, status, StringComparison.OrdinalIgnoreCase))
                {
                    status = summary.Status;
                    Console.Error.WriteLine(
                        $"Playtest {id} is now {XboxPartnerCommandSupport.ForTerminal(status ?? "unknown")}.");
                }

                if (wait.Finish(json, summary) is { } exitCode)
                {
                    return exitCode;
                }
            }

            Console.Error.WriteLine(
                $"Playtest {id} wasn't {wait.Goal} within {TestAccountCommandFactory.FormatDuration(timeout)}; "
                + $"its status is {XboxPartnerCommandSupport.ForTerminal(status ?? "unknown")}.");
            Console.Error.WriteLine(stillRunning);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine($"Stopped waiting. {stillRunning}");
        }
        catch (XboxPartnerApiException exception) when (XboxPartnerErrorGuidance.IsGameNotFound(exception))
        {
            XboxPartnerCommandSupport.WriteCorrelationId(exception.CorrelationId, Console.Error);
            XboxPartnerCommandSupport.WriteGatewayError(
                exception, result, XboxPartnerErrorGuidance.DescribeGameNotFound(exception.RequestUri));
            Console.Error.WriteLine($"Could not read playtest {id}. {stillRunning}");
        }
        // Any read failure leaves the operation running on the service; report it with the way to
        // check rather than crash.
        catch (Exception exception) when (XboxPartnerCommandSupport.IsRequestError(exception))
        {
            XboxPartnerCommandSupport.WriteRequestError(exception, result, cancellationToken);
            Console.Error.WriteLine($"Could not read playtest {id}. {stillRunning}");
        }

        return PublishNotFinishedExitCode;
    }
}
