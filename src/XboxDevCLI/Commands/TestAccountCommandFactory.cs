// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Net;
using System.Text.Json;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Output;
using XboxDevCLI.Services;

namespace XboxDevCLI.Commands;

/// <summary>
/// Commands for XBOX test account batches and their creation jobs, served by XboxPartner.
/// </summary>
/// <remarks>
/// <c>test-account</c> sits at the root rather than under <c>xbl</c> for the same reason
/// <c>sandbox</c> does: test accounts belong to the Partner Center seller, not to one service,
/// and are used across Store, XBOX Live, and sandbox testing. Nesting them under one service
/// would suggest a scope they do not have.
/// </remarks>
internal static partial class TestAccountCommandFactory
{
    private const string GetEndpoint = $"/{XboxPartnerClient.TestAccountBatchesPath}/{{batchId}}";
    private const string InteractionRequiredCode = "authentication_interaction_required";
    private const string AccessDeniedCode = "test_account_access_denied";

    // --include-sandboxes reads each listed batch's details from the test account service, which
    // allows a developer 60 calls in 15 seconds, so without --limit the list stops at this many.
    internal const int IncludeSandboxesDefaultLimit = 10;

    /// <summary>Test seam for prompts, stdin, the clock, and waiting.</summary>
    internal static TestAccountCommandDependencies Dependencies { get; set; } = TestAccountCommandDependencies.Default;

    public static Command Create()
    {
        var batch = new Command("batch", "Create, list, inspect, update, and delete XBOX test account batches");
        batch.Subcommands.Add(CreateCreateCommand());
        batch.Subcommands.Add(CreateListCommand());
        batch.Subcommands.Add(CreateGetCommand());
        batch.Subcommands.Add(CreateUpdateCommand());
        batch.Subcommands.Add(CreateDeleteCommand());

        var command = new Command("test-account", "Create, inspect, update, and delete XBOX test accounts");
        command.Subcommands.Add(CreateLookupCommand());
        command.Subcommands.Add(batch);
        command.Subcommands.Add(CreateJobCommand());
        return command;
    }

    private static Command CreateListCommand()
    {
        var sellerOption = new XdtsSellerOption();
        var mineOption = new Option<bool>("--mine")
        {
            Description = "Only batches created by the signed-in developer.",
        };
        var createdByOption = new Option<long?>("--created-by-id")
        {
            Description = "Only batches created by this numeric Partner Center user ID. Cannot be combined with --mine.",
        };
        createdByOption.Validators.Add(result =>
        {
            if (result.Tokens.Count > 0 &&
                long.TryParse(result.Tokens[0].Value, out long value) &&
                value <= 0)
            {
                result.AddError("--created-by-id must be a positive numeric user ID.");
            }
        });
        var emailPrefixOption = CreateTextOption(
            "--email-prefix",
            "Only batches whose email template starts with this text, ignoring case.");
        var tagOption = CreateTextOption(
            "--tag",
            "Only batches with this exact tag.");
        var stateOption = new Option<string?>("--state")
        {
            Description = "Only complete or pending batches. Complete means the job stopped, not that it succeeded.",
        };
        stateOption.AcceptOnlyFromAmong("complete", "pending");
        var limitOption = new Option<int?>("--limit")
        {
            Description = "Return at most this many of the newest matching batches.",
        };
        limitOption.Validators.Add(result =>
        {
            if (result.Tokens.Count > 0 &&
                int.TryParse(result.Tokens[0].Value, out int value) &&
                value <= 0)
            {
                result.AddError("--limit must be a positive number.");
            }
        });
        var includeSandboxesOption = new Option<bool>("--include-sandboxes")
        {
            Description =
                "Also return each batch's tags and sandbox access. Reads each batch's details separately, so " +
                FormattableString.Invariant(
                    $"without --limit it lists at most the newest {IncludeSandboxesDefaultLimit} matching batches."),
        };
        var expiredOption = new Option<bool>("--expired")
        {
            Description =
                "Only batches whose access expiry has passed. --limit then applies to the expired batches.",
        };

        var command = new Command("list", "List XBOX test account batches for the selected seller, newest first");
        command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.ReadOnly,
            Scope: "XBOX test account batches for the selected seller.",
            Output:
                "A JSON object with a batches array by default. Table output shows one row per batch and adds " +
                "SANDBOXES with --include-sandboxes; the batch count goes to stderr.",
            Examples:
            [
                "xbox test-account batch list --mine --limit 5",
                "xbox test-account batch list --state pending --output table",
                "xbox test-account batch list --email-prefix ABCDEF-smoke --include-sandboxes",
                "xbox test-account batch list --expired --limit 10",
            ],
            LearnMore:
            [
                "Reads the list a page at a time and prints every matching batch; a failed request prints no " +
                    "partial results.",
            ],
            Table: XboxPartnerCommandSupport.TestAccountBatchesView));
        sellerOption.AddTo(command);
        command.Options.Add(mineOption);
        command.Options.Add(createdByOption);
        command.Options.Add(emailPrefixOption);
        command.Options.Add(tagOption);
        command.Options.Add(stateOption);
        command.Options.Add(limitOption);
        command.Options.Add(includeSandboxesOption);
        command.Options.Add(expiredOption);
        command.Validators.Add(result =>
        {
            if (result.GetValue(mineOption) && result.GetValue(createdByOption).HasValue)
            {
                result.AddError("--mine and --created-by-id cannot be combined.");
            }

            if (result.GetValue(expiredOption) && result.GetValue(includeSandboxesOption))
            {
                result.AddError(
                    "--expired and --include-sandboxes cannot be combined: --expired reads every batch, and " +
                    "--include-sandboxes reads each batch's details. List them with " +
                    "'xbox test-account batch list --expired', then run 'xbox test-account batch get <batch-id>' " +
                    "to see a batch's sandbox access.");
            }
        });
        command.SetAction((ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var filter = new TestAccountBatchFilter(
                Mine: parseResult.GetValue(mineOption),
                CreatedBy: parseResult.GetValue(createdByOption),
                EmailPrefix: parseResult.GetValue(emailPrefixOption)?.Trim(),
                Keyword: parseResult.GetValue(tagOption)?.Trim(),
                State: parseResult.GetValue(stateOption),
                Top: parseResult.GetValue(limitOption),
                IncludeSandboxes: parseResult.GetValue(includeSandboxesOption));
            bool expired = parseResult.GetValue(expiredOption);
            return InvokeAsync(
                parseResult,
                sellerOption,
                "Listing test account batches using the signed in Entra ID token",
                (client, sellerId, stdout, stderr, output, token) =>
                    RunListAsync(client, sellerId, filter, expired, output, stdout, stderr, token),
                cancellationToken);
        });
        return command;
    }

    private static Command CreateGetCommand()
    {
        var sellerOption = new XdtsSellerOption();
        var batchIdArgument = CreateBatchIdArgument();

        var command = new Command("get", "Show one XBOX test account batch and its accounts");
        command.WithMetadata(XboxPartnerCommandSupport.ReadOnlyMetadata(
            scope: "One XBOX test account batch for the selected seller.",
            output:
                "A JSON object with batch and accounts by default. Each account has xuid, email, gamertag, " +
                "and isDeleted; passwords are never returned. Table output shows one row per account, " +
                "and a summary of the batch goes to stderr.",
            examples:
            [
                "xbox test-account batch get 123456",
                "xbox test-account batch get 123456 --output table",
            ],
            endpoint: GetEndpoint,
            table: XboxPartnerCommandSupport.TestAccountBatchView,
            learnMore: []));
        command.Arguments.Add(batchIdArgument);
        sellerOption.AddTo(command);
        command.SetAction((ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            long batchId = parseResult.GetValue(batchIdArgument);
            return InvokeAsync(
                parseResult,
                sellerOption,
                $"Reading test account batch {batchId} using the signed in Entra ID token",
                (client, sellerId, stdout, stderr, output, token) =>
                    RunGetAsync(client, sellerId, batchId, output, stdout, stderr, token),
                cancellationToken);
        });
        return command;
    }

    private static Argument<long> CreateBatchIdArgument()
    {
        var argument = new Argument<long>("batch-id")
        {
            Description = "The numeric test account batch ID, as shown by 'xbox test-account batch list'.",
        };
        argument.Validators.Add(result =>
        {
            if (result.Tokens.Count > 0 &&
                long.TryParse(result.Tokens[0].Value, out long value) &&
                value <= 0)
            {
                result.AddError("batch-id must be a positive number.");
            }
        });
        return argument;
    }

    private static Option<string?> CreateTextOption(string name, string description)
    {
        var option = new Option<string?>(name) { Description = description };
        option.Validators.Add(result =>
        {
            if (result.Tokens.Count > 0 && string.IsNullOrWhiteSpace(result.Tokens[0].Value))
            {
                result.AddError($"{name} must not be empty.");
            }
        });
        return option;
    }

    private static async Task<int> InvokeAsync(
        ParseResult parseResult,
        XdtsSellerOption sellerOption,
        string verboseMessage,
        Func<XboxPartnerClient, long?, TextWriter, TextWriter, OutputOptions, CancellationToken, Task<int>> run,
        CancellationToken cancellationToken)
    {
        var output = OutputOptions.From(parseResult);
        var verbose = parseResult.GetValue(Program.VerboseOption);
        return await InvokeAsync(
            parseResult,
            sellerOption,
            async (client, sellerId, token) =>
            {
                try
                {
                    Program.WriteVerbose(verbose, () => verboseMessage);
                    return await run(client, sellerId, Console.Out, Console.Error, output, token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Request failed: {ex.Message}");
                    return 1;
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the client and resolves the seller, then hands both to <paramref name="run"/>. A setup
    /// failure exits 1; failures inside <paramref name="run"/> are its own to report.
    /// </summary>
    private static async Task<int> InvokeAsync(
        ParseResult parseResult,
        XdtsSellerOption sellerOption,
        Func<XboxPartnerClient, long?, CancellationToken, Task<int>> run,
        CancellationToken cancellationToken)
    {
        XboxPartnerClient client;
        long? sellerId;
        try
        {
            var configuration = Program.BuildConfiguration(parseResult);
            client = Program.XboxPartnerClientFactory is null
                ? new XboxPartnerClient(
                    configuration,
                    authenticationService: Program.GetAuthentication(parseResult))
                : Program.XboxPartnerClientFactory(configuration);
            try
            {
                sellerId = await sellerOption.GetSellerIdAsync(
                    parseResult,
                    configuration,
                    client,
                    XboxPartnerCommandSupport.ResolveBaseUrl(
                        configuration, serviceName: "service", includeDefaultUrl: false),
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Request failed: {ex.Message}");
            return 1;
        }

        using (client)
        {
            return await run(client, sellerId, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Lists batches. With <paramref name="expired"/>, the whole list is read and the CLI keeps
    /// the expired batches, so <see cref="TestAccountBatchFilter.Top"/> limits what is printed
    /// rather than what the gateway returns.
    /// </summary>
    internal static async Task<int> RunListAsync(
        XboxPartnerClient client,
        long? sellerId,
        TestAccountBatchFilter filter,
        bool expired,
        OutputOptions output,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        bool bounded = filter.IncludeSandboxes && filter.Top is null;
        if (bounded)
        {
            filter = filter with { Top = IncludeSandboxesDefaultLimit };
        }

        int exitCode = await RunAsync(
            async () =>
            {
                TestAccountBatchFilter request = expired ? filter with { Top = null } : filter;
                string response = await client.ListTestAccountBatchesAsync(sellerId, request, cancellationToken)
                    .ConfigureAwait(false);
                return expired
                    ? TestAccountLookup.FilterExpired(response, Dependencies.GetUtcNow(), filter.Top)
                    : response;
            },
            response => XboxPartnerTableFormatter.FormatTestAccountBatches(response, filter, expired),
            ex => DescribeFailure(ex.StatusCode, ex.ResponseBody, TestAccountRequest.List),
            output,
            stdout,
            stderr).ConfigureAwait(false);
        if (exitCode == 0 && bounded)
        {
            stderr.WriteLine(
                FormattableString.Invariant($"Listed at most the newest {IncludeSandboxesDefaultLimit} matching batches: ") +
                "--include-sandboxes reads each batch's details, so set --limit to list more.");
        }

        return exitCode;
    }

    internal static Task<int> RunGetAsync(
        XboxPartnerClient client,
        long? sellerId,
        long batchId,
        OutputOptions output,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken) =>
        RunAsync(
            () => client.GetTestAccountBatchAsync(sellerId, batchId, cancellationToken),
            XboxPartnerTableFormatter.FormatTestAccountBatch,
            ex => DescribeFailure(ex.StatusCode, ex.ResponseBody, TestAccountRequest.Get),
            output,
            stdout,
            stderr);

    private static async Task<int> RunAsync(
        Func<Task<string>> send,
        Func<string, XboxPartnerTableFormatter.TestAccountTable> formatTable,
        Func<XboxPartnerApiException, string?> describeFailure,
        OutputOptions output,
        TextWriter stdout,
        TextWriter stderr)
    {
        try
        {
            string response = await send().ConfigureAwait(false);
            if (output.TableView is not null)
            {
                var table = formatTable(response);
                stdout.WriteLine(table.Text);
                foreach (string note in table.Notes)
                {
                    stderr.WriteLine(note);
                }
            }
            else
            {
                using (JsonDocument.Parse(response))
                {
                    stdout.WriteLine(PlayFabApiClient.FormatJson(response, output.IsCompact));
                }
            }

            return 0;
        }
        catch (XboxPartnerApiException ex)
        {
            XboxPartnerCommandSupport.WriteCorrelationId(ex.CorrelationId, stderr);
            stderr.WriteLine(FormatError(
                ex,
                output.IsCompact,
                describeFailure(ex)));
            return 1;
        }
        catch (XboxPartnerResponseException ex)
        {
            XboxPartnerCommandSupport.WriteCorrelationId(ex.CorrelationId, stderr);
            stderr.WriteLine($"Request failed: {ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"Request failed: {ex.Message}");
            return 1;
        }
    }

    internal enum TestAccountRequest
    {
        List,
        Get,
        Create,
        Job,
        Delete,
        Update,
        Lookup,
    }

    private static string FormatError(
        XboxPartnerApiException exception,
        bool compact,
        string? additionalDetail = null,
        bool includeValidationErrors = false)
    {
        string detail = $"The test account request failed with HTTP {(int)exception.StatusCode}" +
            (string.IsNullOrWhiteSpace(exception.ReasonPhrase) ? "." : $" ({exception.ReasonPhrase}).");
        if (exception.InnerException is not null)
        {
            detail += " The response body could not be read.";
            if (exception.InnerException is InvalidOperationException)
            {
                detail += $" {exception.InnerException.Message}";
            }
        }

        return XboxPartnerCommandErrorFormatter.Format(
            exception,
            compact,
            additionalDetail,
            includeValidationErrors: includeValidationErrors,
            fallbackTitle: "Test account request failed",
            fallbackDetail: detail);
    }

    /// <summary>
    /// Adds what the gateway's status means for test accounts, since XboxPartner maps several
    /// test account service failures onto gateway statuses that do not explain themselves.
    /// </summary>
    /// <remarks>
    /// The status alone is ambiguous, so the response body narrows some hints: a 403 that already
    /// tells the user to sign in again gets no hint that would contradict it.
    /// </remarks>
    internal static string? DescribeFailure(
        HttpStatusCode statusCode,
        string? responseBody,
        TestAccountRequest request)
    {
        if (request is TestAccountRequest.Create or TestAccountRequest.Job)
        {
            return DescribeCreationFailure(statusCode, responseBody, request);
        }

        if (request == TestAccountRequest.Delete)
        {
            return DescribeDeletionFailure(statusCode, responseBody);
        }

        if (request == TestAccountRequest.Update)
        {
            return DescribeUpdateFailure(statusCode, responseBody);
        }

        bool isLookup = request == TestAccountRequest.Lookup;
        bool isGet = request == TestAccountRequest.Get;
        return statusCode switch
        {
            HttpStatusCode.Forbidden when ReadProblemString(responseBody, "code") == InteractionRequiredCode => null,
            HttpStatusCode.Forbidden when isLookup =>
                "You are not a member of this seller, or lack the Partner Center test account role for it. " +
                "If the account belongs to another seller, pass --seller-id.",
            HttpStatusCode.Forbidden when isGet && ReadProblemString(responseBody, "code") == AccessDeniedCode =>
                "The batch belongs to a different seller (pass --seller-id), another developer created it " +
                "and you lack seller wide test account read permission, or your account lacks the Partner " +
                "Center test account role for this seller.",
            HttpStatusCode.Forbidden when isGet =>
                "The batch may belong to a different seller; pass --seller-id. Otherwise you are not a " +
                "member of this seller.",
            HttpStatusCode.Forbidden =>
                "You are not a member of this seller, or lack access to its test accounts.",
            HttpStatusCode.NotFound when isLookup =>
                "Test accounts may be unavailable, or the batch was deleted " +
                "while it was being read. Retry, or pass --seller-id if the account belongs to another seller.",
            HttpStatusCode.NotFound when isGet =>
                "Check the batch ID and --seller-id. Test accounts may be unavailable.",
            HttpStatusCode.NotFound =>
                "Test accounts may be unavailable.",
            HttpStatusCode.BadGateway =>
                "The test account request could not be completed. Retry first. If it keeps failing, check " +
                "that your account has the Partner Center test account role for this seller" +
                (isGet
                    ? " and, if another developer created the batch, seller wide test account read permission."
                    : ".") +
                " Signing in again does not help.",
            HttpStatusCode.ServiceUnavailable =>
                "Test accounts are unavailable because they are disabled or the service is not configured for them.",
            HttpStatusCode.GatewayTimeout =>
                "The test account service did not respond in time. Retry.",
            _ => null,
        };
    }

    internal static string? ReadProblemString(string? responseBody, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty(propertyName, out JsonElement value) &&
                value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
