// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Output;
using XboxDevCLI.Services;

namespace XboxDevCLI.Commands;

internal static partial class TestAccountCommandFactory
{

    /// <summary>The create returned, or was interrupted, without telling us whether it created a batch.</summary>
    internal const int OutcomeUnknownExitCode = 3;

    /// <summary>The job reached the failed state.</summary>
    internal const int JobFailedExitCode = 2;

    /// <summary>
    /// A wait ended without seeing the job stop: it timed out with the job still running, or, after
    /// a create was accepted, it was interrupted. The batch exists and the job may still be running.
    /// </summary>
    internal const int JobNotFinishedExitCode = 4;

    /// <summary>
    /// The question sent when --security-question is omitted. XboxPartner requires a question and
    /// applies no default of its own, so the CLI always sends one.
    /// </summary>
    internal const string DefaultSecurityQuestion = "What is your favorite hobby?";

    private static readonly string[] SecurityQuestionSuggestions =
    [
        DefaultSecurityQuestion,
        "Who is your favorite athlete?",
        "What is your maternal grandmother's first name?",
        "What was your childhood best friend's first name?",
        "What was the name of your first pet?",
        "What is your current best friend's first name?",
        "What was your favorite city to visit as a child?",
    ];

    /// <summary>The subscriptions the service accepts, spelled as it spells them.</summary>
    internal static readonly string[] Subscriptions = ["Silver", "Gold"];

    /// <summary>Returns the service's spelling of a subscription, ignoring case, or null when it is not one.</summary>
    internal static string? NormalizeSubscription(string? value) =>
        Trim(value) is { } trimmed
            ? Array.Find(Subscriptions, name => string.Equals(name, trimmed, StringComparison.OrdinalIgnoreCase))
            : null;

    internal static readonly TimeSpan DefaultWaitTimeout = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan MaximumWaitTimeout = TimeSpan.FromDays(7);

    private static Command CreateCreateCommand()
    {
        var sellerOption = new XdtsSellerOption();
        var emailPrefixOption = CreateTextOption(
            "--email-prefix",
            "Email prefix for the new accounts: the sandbox group, a hyphen, and a template, for example ABCD-tester.");
        emailPrefixOption.Required = true;
        var countOption = new Option<int>("--count")
        {
            Description = "Number of test accounts to create. The service sets the maximum.",
            Required = true,
        };
        countOption.Validators.Add(result => RequireAtLeast(result, "--count", 1));
        var sandboxOption = new Option<string[]>("--sandbox-id")
        {
            Description = "Sandbox the accounts can access. Repeat for more sandboxes.",
            HelpName = "id",
            Required = true,
            Arity = ArgumentArity.OneOrMore,
            AllowMultipleArgumentsPerToken = false,
        };
        sandboxOption.Validators.Add(result => RequireNonBlankValues(result, "--sandbox-id"));
        var passwordFileOption = new Option<string?>("--password-file")
        {
            Description =
                "File holding the account password, or - to read it from stdin. Without it, the CLI " +
                "prompts in a terminal and never echoes the value.",
            HelpName = "path",
        };
        var securityAnswerFileOption = new Option<string?>("--security-answer-file")
        {
            Description =
                "File holding the security answer, or - to read it from stdin. Without it, the CLI " +
                "prompts in a terminal and never echoes the value.",
            HelpName = "path",
        };
        var childCountOption = new Option<int?>("--child-count")
        {
            Description =
                "Total number of child accounts in the batch, all linked to the first account, not a " +
                "number per account. The service sets the maximum.",
        };
        childCountOption.Validators.Add(result => RequireAtLeast(result, "--child-count", 0));
        var firstNameOption = CreateTextOption("--first-name", "First name for the accounts. Defaults to Test.");
        var lastNameOption = CreateTextOption("--last-name", "Last name for the accounts. Defaults to Account.");
        var securityQuestionOption = CreateTextOption(
            "--security-question",
            "Security question for the accounts. Without it, a terminal offers a choice unless " +
                $"--security-answer-file is supplied. Otherwise defaults to \"{DefaultSecurityQuestion}\".");
        var alternateEmailOption = CreateTextOption(
            "--alternate-email",
            "Alternate email for the accounts. Defaults to your developer email.");
        var accessExpirationOption = new Option<string?>("--access-expiration")
        {
            Description = "Date the accounts' sandbox access expires, such as 2026-12-31. Defaults to three months from now.",
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
            Description = "Tag to store on the batch, shown as keywords in JSON output. Repeat for more tags.",
            AllowMultipleArgumentsPerToken = false,
        };
        tagOption.Validators.Add(result => RequireNonBlankValues(result, "--tag"));
        var localeOption = CreateTextOption("--locale", "Locale for the accounts. Defaults to en-US.");
        var subscriptionOption = CreateTextOption(
            "--subscription",
            "Subscription for the accounts: Silver or Gold. Defaults to Gold.");
        subscriptionOption.CompletionSources.Add(Subscriptions);
        subscriptionOption.Validators.Add(result =>
        {
            if (result.Tokens.Count > 0 && NormalizeSubscription(result.Tokens[0].Value) is null)
            {
                result.AddError("--subscription must be Silver or Gold.");
            }
        });
        var confirmOption = new Option<bool>("--confirm", "--yes", "-y")
        {
            Description = "Confirm creating the test accounts without prompting.",
        };
        var noWaitOption = new Option<bool>("--no-wait")
        {
            Description = "Return as soon as the service accepts the job, without waiting for it.",
        };
        var timeoutOption = CreateTimeoutOption();

        var command = new Command("create", "Create a batch of XBOX test accounts and wait for its creation job");
        command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.RemoteMutation,
            Scope:
                "Creates test accounts for --seller-id, XBOX_SELLER_ID, or the seller saved by 'context set'. " +
                "With none of them, the service uses your only seller and refuses when you have more than one.",
            Output:
                "The job as JSON: batchId, jobId, state, total, succeeded, failed, errorCode, errorMessage, " +
                "and progress. The job may still be running with --no-wait or when --timeout passes first. " +
                "stdout is empty when the create was refused or never sent (exit 1), when the outcome is " +
                "unknown (exit 3), and after Ctrl+C or a failed read while waiting (exit 4). Table output " +
                "shows one row. Progress, recovery commands, and errors go to stderr.",
            SafetyDetails:
                "Creates test accounts that count against the seller's test account limit until the batch " +
                "is deleted with 'xbox test-account batch delete'. Requires --confirm (--yes, -y) or " +
                "terminal confirmation. The create is never retried, because it is not idempotent. Agent " +
                "approval is still required.",
            LearnMore:
            [
                "When --security-question and --security-answer-file are omitted, a terminal offers a " +
                "numbered choice before confirmation. Enter selects the default question. --confirm " +
                "skips only confirmation, not the question or secret prompts.",
                "The password and security answer are read from --password-file, --security-answer-file, " +
                "stdin, or a prompt; never from an option value or an environment variable.",
                TestAccountSecretReader.PasswordRequirements +
                " The password must not exactly match the security answer.",
                "Waits up to 5 minutes by default, polling as the service's Retry-After asks. Exit codes: 0 " +
                "succeeded or accepted with --no-wait; 1 nothing was created; 2 the job failed; 3 the " +
                "outcome is unknown, so check before creating again; 4 the batch exists but the wait ended " +
                "before the job stopped, so resume with the command on stderr.",
            ],
            Examples:
            [
                "xbox test-account batch create --email-prefix ABCD-tester --count 5 --sandbox-id ABCD.1 " +
                    "--password-file ./password.txt --security-answer-file ./answer.txt --confirm",
                "xbox test-account batch create --email-prefix ABCD-smoke --count 2 --sandbox-id ABCD.1 " +
                    "--subscription Silver --tag smoke --no-wait",
            ],
            Table: XboxPartnerCommandSupport.TestAccountJobView));
        command.Options.Add(emailPrefixOption);
        command.Options.Add(countOption);
        command.Options.Add(sandboxOption);
        command.Options.Add(passwordFileOption);
        command.Options.Add(securityAnswerFileOption);
        command.Options.Add(childCountOption);
        command.Options.Add(firstNameOption);
        command.Options.Add(lastNameOption);
        command.Options.Add(securityQuestionOption);
        command.Options.Add(alternateEmailOption);
        command.Options.Add(accessExpirationOption);
        command.Options.Add(tagOption);
        command.Options.Add(localeOption);
        command.Options.Add(subscriptionOption);
        sellerOption.AddTo(command);
        command.Options.Add(confirmOption);
        command.Options.Add(noWaitOption);
        command.Options.Add(timeoutOption);
        command.Validators.Add(result =>
        {
            if (result.GetValue(passwordFileOption) == TestAccountSecretReader.StandardInput &&
                result.GetValue(securityAnswerFileOption) == TestAccountSecretReader.StandardInput)
            {
                result.AddError("--password-file and --security-answer-file cannot both read stdin.");
            }

            if (result.GetValue(noWaitOption) && result.GetResult(timeoutOption) is { Implicit: false })
            {
                result.AddError("--no-wait and --timeout cannot be combined.");
            }
        });
        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var dependencies = Dependencies;
            string? passwordFile = parseResult.GetValue(passwordFileOption);
            string? securityAnswerFile = parseResult.GetValue(securityAnswerFileOption);
            string? suppliedQuestion = Trim(parseResult.GetValue(securityQuestionOption));
            string securityQuestion = suppliedQuestion ?? DefaultSecurityQuestion;
            try
            {
                // Fail before sign in and the seller lookup when a secret cannot be read at all.
                // The secrets themselves are read only after the confirmation.
                TestAccountSecretReader.EnsureSource(passwordFile, "--password-file", "Password", dependencies);
                TestAccountSecretReader.EnsureSource(
                    securityAnswerFile, "--security-answer-file", "Security answer", dependencies);
                if (suppliedQuestion is null && securityAnswerFile is null && dependencies.IsInteractive())
                {
                    securityQuestion = await SelectSecurityQuestionAsync(
                        dependencies, Console.Error, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (InvalidOperationException exception)
            {
                Console.Error.WriteLine(exception.Message);
                return 1;
            }
            catch (IOException exception)
            {
                Console.Error.WriteLine($"Could not read a security question selection: {exception.Message}");
                return 1;
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("Test account creation canceled. Nothing was created.");
                return 1;
            }

            string emailPrefix = parseResult.GetValue(emailPrefixOption)!.Trim();
            return await ExecuteCreateAsync(
                parseResult,
                sellerOption,
                new CreateTestAccountBatchRequest
                {
                    Count = parseResult.GetValue(countOption),
                    ChildCount = parseResult.GetValue(childCountOption),
                    EmailPrefix = emailPrefix,
                    FirstName = Trim(parseResult.GetValue(firstNameOption)),
                    LastName = Trim(parseResult.GetValue(lastNameOption)),
                    // Read after the confirmation, so a declined create never asks for them.
                    Password = string.Empty,
                    SecurityQuestion = securityQuestion,
                    SecurityAnswer = string.Empty,
                    Sandboxes = [.. parseResult.GetValue(sandboxOption)!.Select(value => value.Trim())],
                    AlternateEmail = Trim(parseResult.GetValue(alternateEmailOption)),
                    AccessExpirationDate = parseResult.GetValue(accessExpirationOption) is { } expiration
                        ? ParseExpiration(expiration)
                        : null,
                    Keywords = parseResult.GetValue(tagOption) is { Length: > 0 } tags
                        ? [.. tags.Select(value => value.Trim())]
                        : null,
                    Locale = Trim(parseResult.GetValue(localeOption)),
                    Subscription = NormalizeSubscription(parseResult.GetValue(subscriptionOption)),
                },
                request =>
                {
                    request.Password = TestAccountSecretReader.ReadPassword(passwordFile, dependencies);
                    request.SecurityAnswer = TestAccountSecretReader.Read(
                        securityAnswerFile,
                        "--security-answer-file",
                        "Security answer",
                        dependencies,
                        prompt: $"Answer to \"{securityQuestion}\"");
                },
                parseResult.GetResult(confirmOption) is { Implicit: false }
                    ? parseResult.GetValue(confirmOption)
                    : parseResult.GetValue(Program.ConfirmOption),
                parseResult.GetValue(noWaitOption),
                ReadTimeout(parseResult, timeoutOption),
                dependencies,
                cancellationToken).ConfigureAwait(false);
        });
        return command;
    }

    private static async Task<string> SelectSecurityQuestionAsync(
        TestAccountCommandDependencies dependencies,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        stderr.WriteLine("Security question for the accounts:");
        for (int index = 0; index < SecurityQuestionSuggestions.Length; index++)
        {
            stderr.WriteLine(FormattableString.Invariant($"  {index + 1}. {SecurityQuestionSuggestions[index]}"));
        }

        var readLineAsync = FeedbackDependencies.CancelableReadLine(dependencies.ReadConfirmation);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stderr.Write(FormattableString.Invariant($"Choose 1 to {SecurityQuestionSuggestions.Length} [1]: "));
            stderr.Flush();
            string? answer = await readLineAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (answer is null)
            {
                throw new InvalidOperationException("Test account creation canceled. Nothing was created.");
            }

            if (string.IsNullOrWhiteSpace(answer))
            {
                return DefaultSecurityQuestion;
            }

            if (int.TryParse(answer.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int choice) &&
                choice >= 1 && choice <= SecurityQuestionSuggestions.Length)
            {
                return SecurityQuestionSuggestions[choice - 1];
            }

            stderr.WriteLine(FormattableString.Invariant(
                $"Enter a number from 1 to {SecurityQuestionSuggestions.Length}, or press Enter for 1."));
        }
    }

    private static Task<int> ExecuteCreateAsync(
        ParseResult parseResult,
        XdtsSellerOption sellerOption,
        CreateTestAccountBatchRequest request,
        Action<CreateTestAccountBatchRequest> readSecrets,
        bool confirmed,
        bool noWait,
        TimeSpan timeout,
        TestAccountCommandDependencies dependencies,
        CancellationToken cancellationToken) =>
        InvokeAsync(
            parseResult,
            sellerOption,
            (client, sellerId, token) => CreateAsync(
                parseResult, client, sellerId, request, readSecrets, confirmed, noWait, timeout, dependencies, token),
            cancellationToken);

    private static async Task<int> CreateAsync(
        ParseResult parseResult,
        XboxPartnerClient client,
        long? sellerId,
        CreateTestAccountBatchRequest request,
        Action<CreateTestAccountBatchRequest> readSecrets,
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
        string emailPrefix = request.EmailPrefix;
        request.SellerId = sellerId;
        if (!ConfirmCreate(confirmed, dependencies, request, stderr))
        {
            return 1;
        }

        try
        {
            readSecrets(request);
        }
        catch (InvalidOperationException exception)
        {
            stderr.WriteLine(exception.Message);
            return 1;
        }

        var recovery = new RecoveryCommands(parseResult, sellerId);
        DateTimeOffset requestStarted = dependencies.GetUtcNow();
        Program.WriteVerbose(verbose, () => "Creating a test account batch using the signed in Entra ID token");
        stderr.WriteLine("Submitting test account creation job...");
        bool sent = false;
        TestAccountJobResult accepted;
        try
        {
            // Sent once. Every failure that could follow the request leaving the process is
            // reported as an unknown outcome rather than retried.
            accepted = await client.CreateTestAccountBatchAsync(request, cancellationToken, () => sent = true)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (!sent)
        {
            stderr.WriteLine(exception is OperationCanceledException
                ? "Canceled before the create was sent. Nothing was created."
                : $"Request failed: {exception.Message} The create was not sent.");
            return 1;
        }
        catch (XboxPartnerApiException exception) when (IsOutcomeUnknown(exception))
        {
            XboxPartnerCommandSupport.WriteCorrelationId(exception.CorrelationId, stderr);
            stderr.WriteLine(FormatError(exception, output.IsCompact));
            WriteOutcomeUnknown(emailPrefix, requestStarted, recovery, stderr);
            return OutcomeUnknownExitCode;
        }
        catch (XboxPartnerApiException exception)
        {
            XboxPartnerCommandSupport.WriteCorrelationId(exception.CorrelationId, stderr);
            stderr.WriteLine(FormatError(
                exception,
                output.IsCompact,
                DescribeFailure(exception.StatusCode, exception.ResponseBody, TestAccountRequest.Create),
                includeValidationErrors: true));
            return 1;
        }
        catch (XboxPartnerResponseException exception)
        {
            XboxPartnerCommandSupport.WriteCorrelationId(exception.CorrelationId, stderr);
            stderr.WriteLine($"The service accepted the request, but its response could not be read: {exception.Message}");
            WriteOutcomeUnknown(emailPrefix, requestStarted, recovery, stderr);
            return OutcomeUnknownExitCode;
        }
        // ConnectionError is left out: the handler can resend on a new connection after a pooled one
        // drops, so a failed connect does not prove an earlier attempt never reached the service.
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
                    "Canceled while waiting for the service to answer the create.",
                OperationCanceledException => "Timed out waiting for the service to answer the create.",
                HttpRequestException or IOException =>
                    $"The connection failed while waiting for the service to answer the create: {exception.Message}",
                _ => $"The create was sent, but failed before its answer could be read: {exception.Message}",
            });
            WriteOutcomeUnknown(emailPrefix, requestStarted, recovery, stderr);
            return OutcomeUnknownExitCode;
        }

        TestAccountJob job = accepted.Job;
        XboxPartnerCommandSupport.WriteCorrelationId(accepted.CorrelationId, stderr);
        stderr.WriteLine(FormattableString.Invariant(
            $"Test account creation job accepted: batch {job.BatchId}, job {Clean(job.JobId)}."));
        if (noWait || !job.IsRunning)
        {
            return Finish(accepted, recovery, output, stdout, stderr, runningMessage: null);
        }

        return await WaitAndFinishAsync(
            client, accepted, sellerId, timeout, recovery, output, verbose, dependencies, stdout, stderr,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// True when the create may have created a batch: any 3xx, a redirect the client doesn't follow
    /// and that doesn't say whether the request was acted on, and any 5xx, except a 503 whose code says
    /// XboxPartner refused it before any upstream call (<c>feature_disabled</c> or
    /// <c>service_configuration_invalid</c>). XboxPartner can answer 502 after the batch was
    /// accepted, and the network in front of XboxPartner answers 503 or 504 with no code both when
    /// no backend was reached and when the connection reset after the request was forwarded, so a
    /// 5xx without one of those codes says nothing about how far the request got.
    /// </summary>
    internal static bool IsOutcomeUnknown(XboxPartnerApiException exception) =>
        (int)exception.StatusCode is < 400 or >= 500 &&
        !(exception.StatusCode == HttpStatusCode.ServiceUnavailable &&
            ReadProblemString(exception.ResponseBody, "code") is "feature_disabled" or "service_configuration_invalid");

    private static void WriteOutcomeUnknown(
        string emailPrefix,
        DateTimeOffset requestStarted,
        RecoveryCommands recovery,
        TextWriter stderr)
    {
        string started = requestStarted.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
        stderr.WriteLine(
            "The create may have reached the service, so it is unknown whether a batch was created. " +
            "Do not run it again yet; the create is not safe to retry blindly.");
        stderr.WriteLine("To find out:");
        stderr.WriteLine("  1. Wait a few minutes; a batch still being created may not be listed yet.");
        stderr.WriteLine($"  2. Run: {recovery.Build("batch", "list", "--mine", "--email-prefix", emailPrefix)}");
        stderr.WriteLine($"     and look for a batch created at or after {started}.");
        stderr.WriteLine("  3. If that batch's jobId is set, follow it: " + recovery.Template(
            "job", "wait", RecoveryCommands.JobIdPlaceholder, "--batch-id", RecoveryCommands.BatchIdPlaceholder));
        stderr.WriteLine("     If its jobId is null, the job has stopped; inspect it: " +
            recovery.Template("batch", "get", RecoveryCommands.BatchIdPlaceholder));
        stderr.WriteLine("  4. Create again only if no such batch exists.");
    }

    internal static bool ConfirmCreate(
        bool confirmed,
        TestAccountCommandDependencies dependencies,
        CreateTestAccountBatchRequest request,
        TextWriter stderr)
    {
        if (confirmed)
        {
            return true;
        }

        string children = request.ChildCount is > 0
            ? FormattableString.Invariant($", plus {request.ChildCount} child accounts linked to the first one,")
            : string.Empty;
        string seller = request.SellerId is { } id
            ? $"seller {id.ToString(CultureInfo.InvariantCulture)}"
            : "your only seller (no seller was given, so the service uses your only one and refuses if " +
                "you have several; pass --seller-id to choose)";
        stderr.WriteLine(FormattableString.Invariant(
            $"This creates {request.Count} test accounts{children} with email prefix {request.EmailPrefix} ") +
            $"in sandboxes {string.Join(", ", request.Sandboxes)} for {seller}. They count against " +
            "the seller's test account limit until the batch is deleted.");
        stderr.WriteLine($"  Security question: {request.SecurityQuestion}");
        stderr.WriteLine($"  First name: {request.FirstName ?? "Test (service default)"}");
        stderr.WriteLine($"  Last name: {request.LastName ?? "Account (service default)"}");
        stderr.WriteLine($"  Subscription: {request.Subscription ?? "Gold (service default)"}");
        stderr.WriteLine($"  Locale: {request.Locale ?? "en-US (service default)"}");
        stderr.WriteLine($"  Alternate email: {request.AlternateEmail ?? "your developer email (service default)"}");
        stderr.WriteLine("  Access expires: " + (request.AccessExpirationDate is { } expires
            ? expires.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : "three months from now (service default)"));
        if (request.Keywords is { Count: > 0 } keywords)
        {
            stderr.WriteLine($"  Tags: {string.Join(", ", keywords)}");
        }
        if (!dependencies.IsInteractive())
        {
            stderr.WriteLine("Run again with --confirm (--yes, -y) to proceed without an interactive prompt.");
            return false;
        }

        stderr.Write("Create these test accounts? [y/N]: ");
        stderr.Flush();
        string? answer = dependencies.ReadConfirmation()?.Trim();
        if (string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        stderr.WriteLine("Test account creation canceled.");
        return false;
    }

    private static string? Trim(string? value) => value?.Trim();

    private static DateTimeOffset? ParseExpiration(string value) =>
        DateTimeOffset.TryParse(
            value.Trim(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset parsed)
            ? parsed
            : null;

    private static void RequireAtLeast(OptionResult result, string name, int minimum)
    {
        if (result.Tokens.Count > 0 &&
            int.TryParse(result.Tokens[0].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) &&
            value < minimum)
        {
            result.AddError(minimum == 0
                ? $"{name} must not be negative."
                : FormattableString.Invariant($"{name} must be at least {minimum}."));
        }
    }

    private static void RequireNonBlankValues(OptionResult result, string name)
    {
        // Collection arity applies across occurrences, so a bare repeat would otherwise pass.
        if (result.Tokens.Count != result.IdentifierTokenCount ||
            result.Tokens.Any(token => string.IsNullOrWhiteSpace(token.Value)))
        {
            result.AddError($"Each {name} requires a non-empty value.");
        }
    }

    private static Option<string?> CreateTimeoutOption()
    {
        var option = new Option<string?>("--timeout")
        {
            Description = "How long to wait for the job, such as 90s, 10m, 2h, or 1d. Defaults to 5m; at most 7d.",
            HelpName = "duration",
        };
        option.Validators.Add(result =>
        {
            if (result.Tokens.Count > 0 && ParseDuration(result.Tokens[0].Value) is null)
            {
                result.AddError("--timeout must be a positive duration of at most 7d, such as 90s, 10m, 2h, or 1d.");
            }
        });
        return option;
    }

    private static TimeSpan ReadTimeout(ParseResult parseResult, Option<string?> timeoutOption) =>
        parseResult.GetValue(timeoutOption) is { } value ? ParseDuration(value) ?? DefaultWaitTimeout : DefaultWaitTimeout;

    internal static TimeSpan? ParseDuration(string value)
    {
        Match match = DurationPattern().Match(value.Trim());
        if (!match.Success ||
            !long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long amount) ||
            amount <= 0 ||
            amount > (long)MaximumWaitTimeout.TotalSeconds)
        {
            return null;
        }

        TimeSpan duration = match.Groups[2].Value.ToLowerInvariant() switch
        {
            "s" => TimeSpan.FromSeconds(amount),
            "m" => TimeSpan.FromMinutes(amount),
            "h" => TimeSpan.FromHours(amount),
            _ => TimeSpan.FromDays(amount),
        };
        return duration <= MaximumWaitTimeout ? duration : null;
    }

    [GeneratedRegex("^([0-9]{1,9})([smhdSMHD])$")]
    private static partial Regex DurationPattern();

    /// <summary>Builds copyable follow up commands that keep the caller's environment options.</summary>
    private sealed class RecoveryCommands(ParseResult parseResult, long? sellerId)
    {
        /// <summary>Left unquoted so the reader sees what to substitute; nothing else is.</summary>
        public const string JobIdPlaceholder = "<job-id>";
        public const string BatchIdPlaceholder = "<batch-id>";

        /// <summary>Builds a runnable command; every argument is quoted.</summary>
        public string Build(params string[] arguments) => Compose(keepPlaceholders: false, arguments);

        /// <summary>
        /// Builds a command for the reader to fill in. Only call it with placeholders and fixed
        /// text, never values from a response, since placeholders are left unquoted.
        /// </summary>
        public string Template(params string[] arguments) => Compose(keepPlaceholders: true, arguments);

        private string Compose(bool keepPlaceholders, string[] arguments)
        {
            var parts = RecoveryCommandArguments.Create(parseResult);
            parts.Add("test-account");
            parts.AddRange(arguments.Select(argument =>
                keepPlaceholders && argument is JobIdPlaceholder or BatchIdPlaceholder
                    ? argument
                    : ShellQuoting.Quote(argument)));
            if (sellerId is { } id)
            {
                parts.Add("--seller-id");
                parts.Add(id.ToString(CultureInfo.InvariantCulture));
            }

            return string.Join(' ', parts);
        }

        public string Wait(TestAccountJob job) =>
            Build("job", "wait", Clean(job.JobId), "--batch-id", job.BatchId.ToString(CultureInfo.InvariantCulture));

        public string GetBatch(TestAccountJob job) =>
            Build("batch", "get", job.BatchId.ToString(CultureInfo.InvariantCulture));
    }
}
