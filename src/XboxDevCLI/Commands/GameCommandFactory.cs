// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Safety;
using XboxDevCLI.Services;

namespace XboxDevCLI.Commands;

internal static class GameCommandFactory
{
    public static Command Create() =>
        Create(clientFactory: null);

    internal static Command Create(
        Func<IConfiguration, IXboxPartnerClient>? clientFactory,
        XboxPartnerJobDependencies? creationDependencies = null)
    {
        var command = new Command("game", "List, get, and create Partner Center games");
        command.Subcommands.Add(CreateListCommand(clientFactory));
        command.Subcommands.Add(CreateGetCommand(clientFactory));
        var dependencies = creationDependencies ?? XboxPartnerJobDependencies.Default;
        command.Subcommands.Add(CreateCreateCommand(clientFactory, dependencies));
        command.Subcommands.Add(CreateJobCommand(clientFactory));
        return command;
    }

    private static Command CreateListCommand(
        Func<IConfiguration, IXboxPartnerClient>? clientFactory)
    {
        var detailsOption = CreateDetailsOption();
        var sellerIdOption = XboxPartnerCommandSupport.CreateSellerIdOption();
        var baseUrlOption = XboxPartnerCommandSupport.CreateBaseUrlOption();
        var command = new Command("list", "List games for a Partner Center seller");
        command.Options.Add(detailsOption);
        command.Options.Add(sellerIdOption);
        command.Options.Add(baseUrlOption);
        command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.ReadOnly,
            Scope:
                "All games belonging to the seller selected by --seller-id, XBOX_SELLER_ID, or 'context set'.",
            Output:
                "JSON by default; --output table groups games by TITLE ID, while --details shows one indented block per game. Result count and query duration are written to stderr for both human-readable views. " +
                ListProgress.HelpOutput,
            Examples:
            [
                "xbox game list --seller-id 1234567",
                "xbox game list --seller-id 1234567 --output table",
                "xbox game list --seller-id 1234567 --details",
            ],
            LearnMore:
            [
                "Reads the catalog a page at a time and prints the complete list.",
                "A failed request prints no partial results and is not retried.",
            ],
            Table: XboxPartnerCommandSupport.GameTableView));

        command.SetAction((ParseResult parseResult, CancellationToken cancellationToken) =>
            ListAsync(
                parseResult,
                detailsOption,
                sellerIdOption,
                baseUrlOption,
                clientFactory,
                cancellationToken));
        return command;
    }

    private static Command CreateGetCommand(
        Func<IConfiguration, IXboxPartnerClient>? clientFactory)
    {
        var titleIdArgument = new Argument<string?>("title-id")
        {
            Description =
                "Optional one-to-eight-digit hexadecimal XBOX title ID, with optional 0x prefix.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var storeIdOption = new Option<string?>("--store-id")
        {
            Description = "Microsoft Store ID of the game to retrieve.",
            HelpName = "id",
        };
        var keywordOption = new Option<string?>("--keyword")
        {
            Description =
                "Case-insensitive game-name substring. Cannot be combined with title-id or --store-id.",
            HelpName = "text",
        };
        var detailsOption = CreateDetailsOption();
        var sellerIdOption = XboxPartnerCommandSupport.CreateSellerIdOption();
        var baseUrlOption = XboxPartnerCommandSupport.CreateBaseUrlOption();
        var command = new Command(
            "get",
            "Get Partner Center games by Store ID, XBOX title ID, or name keyword");
        command.Arguments.Add(titleIdArgument);
        command.Options.Add(storeIdOption);
        command.Options.Add(keywordOption);
        command.Options.Add(detailsOption);
        command.Options.Add(sellerIdOption);
        command.Options.Add(baseUrlOption);
        command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.ReadOnly,
            Scope:
                "Games belonging to the seller selected by --seller-id, XBOX_SELLER_ID, or 'context set'.",
            Output:
                "A games collection in JSON; --output table groups by TITLE ID, while --details shows one indented block per game. Result count and query duration are written to stderr for both human-readable views. " +
                ListProgress.HelpOutput,
            Examples:
            [
                "xbox game get --store-id 9N0000000000 --seller-id 1234567",
                "xbox game get 1234ABCD --seller-id 1234567",
                "xbox game get --keyword headless --seller-id 1234567",
                "xbox game get 42 --store-id 9N0000000000 --seller-id 1234567",
                "xbox game get 1234ABCD --details --seller-id 1234567",
            ],
            LearnMore:
            [
                "At least one of title-id, --store-id, or --keyword is required.",
                "--keyword cannot be combined with title-id or --store-id.",
                "When title-id is supplied, --store-id can disambiguate the matching game.",
                "A title ID can identify multiple games.",
                "Partner Center can't search, so lookups read the catalog a page at a time; a Store ID lookup stops at its first match.",
            ],
            Table: XboxPartnerCommandSupport.GameTableView));

        command.SetAction((ParseResult parseResult, CancellationToken cancellationToken) =>
            GetAsync(
                parseResult,
                titleIdArgument,
                storeIdOption,
                keywordOption,
                detailsOption,
                sellerIdOption,
                baseUrlOption,
                clientFactory,
                cancellationToken));
        return command;
    }

    private static Option<bool> CreateDetailsOption() =>
        new("--details", "-d")
        {
            Description =
                "Show a multiline human-readable view. Cannot be combined with --output.",
        };

    private static async Task<int> ListAsync(
        ParseResult parseResult,
        Option<bool> detailsOption,
        Option<string?> sellerIdOption,
        Option<string?> baseUrlOption,
        Func<IConfiguration, IXboxPartnerClient>? clientFactory,
        CancellationToken cancellationToken)
    {
        if (!ValidateDetailsOutput(parseResult, detailsOption))
        {
            return 1;
        }

        return await ExecuteAsync(
            parseResult,
            sellerIdOption,
            baseUrlOption,
            clientFactory,
            (client, baseUrl, sellerId, pageRead) =>
                client.GetGamesAsync(
                    baseUrl,
                    sellerId,
                    cancellationToken: cancellationToken,
                    pageRead: pageRead),
            (json, result, elapsed) =>
                XboxPartnerCommandSupport.WriteGameOutput(
                    json,
                    result,
                    elapsed,
                    detailsOption),
            cancellationToken);
    }

    private static async Task<int> GetAsync(
        ParseResult parseResult,
        Argument<string?> titleIdArgument,
        Option<string?> storeIdOption,
        Option<string?> keywordOption,
        Option<bool> detailsOption,
        Option<string?> sellerIdOption,
        Option<string?> baseUrlOption,
        Func<IConfiguration, IXboxPartnerClient>? clientFactory,
        CancellationToken cancellationToken)
    {
        if (!ValidateDetailsOutput(parseResult, detailsOption))
        {
            return 1;
        }

        var rawTitleId = Normalize(parseResult.GetValue(titleIdArgument));
        string? titleId = null;
        if (rawTitleId != null && !TryNormalizeTitleId(rawTitleId, out titleId))
        {
            Console.Error.WriteLine(
                "title-id must be one to eight hexadecimal digits, with an optional 0x prefix.");
            return 1;
        }

        var rawStoreId = parseResult.GetValue(storeIdOption);
        var storeId = Normalize(rawStoreId);
        if (rawStoreId != null && storeId == null)
        {
            Console.Error.WriteLine(
                "--store-id must contain at least one non-whitespace character.");
            return 1;
        }

        var rawKeyword = parseResult.GetValue(keywordOption);
        var keyword = Normalize(rawKeyword);
        if (rawKeyword != null && keyword == null)
        {
            Console.Error.WriteLine(
                "--keyword must contain at least one non-whitespace character.");
            return 1;
        }

        if (titleId == null && storeId == null && keyword == null)
        {
            Console.Error.WriteLine(
                "At least one of title-id, --store-id, or --keyword is required.");
            return 1;
        }

        if (keyword != null && (titleId != null || storeId != null))
        {
            Console.Error.WriteLine(
                "--keyword cannot be combined with title-id or --store-id.");
            return 1;
        }

        return await ExecuteAsync(
            parseResult,
            sellerIdOption,
            baseUrlOption,
            clientFactory,
            (client, baseUrl, sellerId, pageRead) =>
                client.GetGamesAsync(
                    baseUrl,
                    sellerId,
                    titleId: titleId,
                    storeId: storeId,
                    keyword: keyword,
                    cancellationToken: cancellationToken,
                    pageRead: pageRead),
            (json, result, elapsed) =>
                XboxPartnerCommandSupport.WriteGameOutput(
                    json,
                    result,
                    elapsed,
                    detailsOption),
            cancellationToken);
    }

    private static bool ValidateDetailsOutput(
        ParseResult parseResult,
        Option<bool> detailsOption)
    {
        if (!parseResult.GetValue(detailsOption))
        {
            return true;
        }

        if (parseResult.GetResult(Program.OutputOption) is { Implicit: false })
        {
            Console.Error.WriteLine("--details cannot be combined with --output.");
            return false;
        }

        return true;
    }

    private static bool TryNormalizeTitleId(string value, out string? normalized)
    {
        var digits = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? value[2..]
            : value;

        if (digits.Length is < 1 or > 8 ||
            !uint.TryParse(
                digits,
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out var titleId))
        {
            normalized = null;
            return false;
        }

        normalized = titleId.ToString("X8", CultureInfo.InvariantCulture);
        return true;
    }

    private static async Task<int> ExecuteAsync(
        ParseResult parseResult,
        Option<string?> sellerIdOption,
        Option<string?> baseUrlOption,
        Func<IConfiguration, IXboxPartnerClient>? clientFactory,
        Func<IXboxPartnerClient, string, string, Action<int>, Task<string>> request,
        Action<string, ParseResult, TimeSpan> writeOutput,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var configuration = Program.BuildConfiguration(parseResult);
            var baseUrl = XboxPartnerCommandSupport.ResolveBaseUrl(
                parseResult,
                baseUrlOption,
                configuration);
            using var client = clientFactory is null
                ? new XboxPartnerClient(configuration, authenticationService: Program.GetAuthentication(parseResult))
                : clientFactory(configuration);
            var sellerId = await XboxPartnerCommandSupport.GetSellerIdAsync(parseResult, sellerIdOption);
            string response;
            using (var progress = ListProgress.Start("Reading games"))
            {
                response = await request(client, baseUrl, sellerId, progress.PageRead);
            }

            stopwatch.Stop();
            writeOutput(response, parseResult, stopwatch.Elapsed);
            return 0;
        }
        catch (XboxPartnerApiException exception)
        {
            XboxPartnerCommandSupport.WriteCorrelationId(exception.CorrelationId, Console.Error);
            XboxPartnerCommandSupport.WriteGatewayError(exception, parseResult);
            return 1;
        }
        catch (XboxPartnerResponseException exception)
        {
            XboxPartnerCommandSupport.WriteCorrelationId(exception.CorrelationId, Console.Error);
            return XboxPartnerCommandSupport.WriteLocalError(exception);
        }
        catch (InvalidOperationException exception)
        {
            return XboxPartnerCommandSupport.WriteLocalError(exception);
        }
        catch (InvalidDataException exception)
        {
            return XboxPartnerCommandSupport.WriteLocalError(exception);
        }
        catch (IOException exception)
        {
            return XboxPartnerCommandSupport.WriteLocalError(exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            return XboxPartnerCommandSupport.WriteLocalError(exception);
        }
        catch (HttpRequestException exception)
        {
            return XboxPartnerCommandSupport.WriteLocalError(exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            return XboxPartnerCommandSupport.WriteLocalError(
                new TimeoutException("The request timed out.", exception));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return XboxPartnerCommandSupport.WriteLocalError(exception);
        }
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Command CreateCreateCommand(
        Func<IConfiguration, IXboxPartnerClient>? clientFactory,
        XboxPartnerJobDependencies dependencies)
    {
        var name = new Option<string>("--name")
        {
            Description = "Primary game name.",
            Required = true,
        };
        var devices = new Option<XboxPartnerDeviceFamily[]>("--supported-device")
        {
            Description = "Supported device family: xbox or desktop. Repeat for both.",
            Required = true,
            Arity = ArgumentArity.OneOrMore,
            AllowMultipleArgumentsPerToken = false,
        };
        var sandbox = new Option<string?>("--sandbox-id")
        {
            Description = "Primary development sandbox ID.",
            HelpName = "id",
        };
        var seller = XboxPartnerCommandSupport.CreateSellerIdOption();
        var baseUrl = XboxPartnerCommandSupport.CreateBaseUrlOption();
        var confirm = new Option<bool>("--confirm", "--yes", "-y")
        {
            Description = "Confirm production game creation without prompting.",
        };
        var command = new Command("create", "Create a Partner Center game and wait for its creation job")
        {
            name, devices, sandbox, seller, baseUrl, confirm,
        };
        command.SetAction((ParseResult result, CancellationToken cancellationToken) =>
            ExecuteCreationAsync(
                result, seller, baseUrl, clientFactory,
                async (client, url, sellerId) =>
                {
                    var gameName = result.GetValue(name)!.Trim();
                    var deviceFamilies = result.GetValue(devices)!;
                    var confirmed = result.GetResult(confirm) is { Implicit: false }
                        ? result.GetValue(confirm)
                        : result.GetValue(Program.ConfirmOption);
                    if (!ConfirmGameCreation(confirmed, dependencies, gameName, sellerId))
                    {
                        return 1;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    Program.WriteVerbose(result.GetValue(Program.VerboseOption),
                        () => $"Submitting game creation for seller {sellerId}.");
                    XboxPartnerGameCreationJob job;
                    try
                    {
                        Console.Error.WriteLine("Submitting game creation job...");
                        // Do not retry an uncertain submission after a request or polling failure.
                        job = await client.CreateGameAsync(
                            url, sellerId,
                            new CreateXboxGameRequest(gameName, deviceFamilies, result.GetValue(sandbox)),
                            cancellationToken);
                    }
                    catch (Exception exception) when (exception is OperationCanceledException
                        or HttpRequestException or IOException or InvalidOperationException
                        or XboxPartnerResponseException or XboxPartnerApiException)
                    {
                        Console.Error.WriteLine(
                            "Game creation was interrupted or its response could not be verified; " +
                            "it is unknown whether the game was created. Do not resubmit.");
                        var recovery = BuildGameRecoveryCommand(result, sellerId, url);
                        Console.Error.WriteLine($"Check its status with: {recovery}");
                        throw;
                    }

                    var correlationId = XboxPartnerCommandSupport.ForTerminal(job.CorrelationId);
                    var correlation = string.IsNullOrWhiteSpace(correlationId)
                        ? string.Empty
                        : $" (Correlation ID: {correlationId})";
                    Console.Error.WriteLine(
                        $"Game creation job submitted: {XboxPartnerCommandSupport.ForTerminal(job.JobId)}{correlation}");
                    return await WaitForJobAsync(
                        result, client, url, sellerId, job, dependencies, cancellationToken);
                },
                cancellationToken));
        return command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.RemoteMutation,
            Scope: "Creates a game for --seller-id, XBOX_SELLER_ID, or the seller saved by 'context set'.",
            Output: "The game creation job object as JSON. Submission progress, job ID, correlation ID, " +
                "and recovery commands go to stderr.",
            SafetyDetails: "Creates production state and cannot be undone from the CLI. Requires " +
                "--confirm (--yes, -y) or terminal confirmation. " +
                "Submission is never retried. Agent approval is still required.",
            LearnMore:
            [
                "Use 'xbox game job get <job-id>' to check an existing creation job without submitting again.",
                "Waits up to 120 seconds, polling every 5 seconds unless the service supplies Retry-After. " +
                "Timeout returns the accepted job and exits 0; " +
                "terminal failure exits 2, request or polling failure exits 1.",
            ],
            Examples:
            [
                "xbox game create --name \"Nod Arena\" --supported-device xbox --supported-device desktop " +
                    "--sandbox-id TESTSB.42 --seller-id 123 --confirm",
                "xbox game create --name \"Nod Arena\" --supported-device xbox --seller-id 123 --confirm",
            ]));
    }

    private static Command CreateJobCommand(
        Func<IConfiguration, IXboxPartnerClient>? clientFactory)
    {
        var group = new Command("job", "Inspect game-creation jobs");
        var jobId = new Argument<string>("job-id") { Description = "Game-creation job ID." };
        jobId.Validators.Add(result =>
        {
            if (result.Tokens.Count > 0 && string.IsNullOrWhiteSpace(result.Tokens[0].Value))
            {
                result.AddError("The job ID cannot be empty.");
            }
        });
        var seller = XboxPartnerCommandSupport.CreateSellerIdOption();
        var baseUrl = XboxPartnerCommandSupport.CreateBaseUrlOption();
        var command = new Command("get", "Get the state of a game-creation job")
        {
            jobId, seller, baseUrl,
        };

        command.SetAction((ParseResult result, CancellationToken cancellationToken) =>
            ExecuteCreationAsync(
                result, seller, baseUrl, clientFactory,
                async (client, url, sellerId) =>
                {
                    var id = result.GetValue(jobId)!;
                    Program.WriteVerbose(result.GetValue(Program.VerboseOption),
                        () => $"Reading game creation job {XboxPartnerCommandSupport.ForTerminal(id)} " +
                            $"for seller {sellerId}.");
                    var job = await client.GetGameCreationJobAsync(url, sellerId, id, cancellationToken);

                    Program.WriteVerbose(result.GetValue(Program.VerboseOption), () => "Request succeeded.");
                    WriteJobResponse(job.RawResponse, result);
                    return job.Completed && !job.Succeeded ? 2 : 0;
                },
                cancellationToken));
        command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.ReadOnly,
            Scope: "One game-creation job for --seller-id, XBOX_SELLER_ID, or the seller saved by 'context set'.",
            Output: "The game creation job object as JSON. Diagnostics go to stderr.",
            SafetyDetails: "Read-only; never resubmits creation. Exits 0 for a running or succeeded job, " +
                "2 for terminal job failure, and 1 for request or cancellation errors.",
            LearnMore: ["Use the job ID returned by 'xbox game create'. Checking a job never creates another game."],
            Examples: ["xbox game job get JOB_ID --seller-id 123"]));
        group.Subcommands.Add(command);
        return group.WithMetadata(new CommandMetadata(
            Scope: "Asynchronous jobs started by 'xbox game create'."));
    }

    private static async Task<int> WaitForJobAsync(
        ParseResult result,
        IXboxPartnerClient client,
        string baseUrl,
        string sellerId,
        XboxPartnerGameCreationJob initial,
        XboxPartnerJobDependencies dependencies,
        CancellationToken cancellationToken)
    {
        var recovery = BuildGameRecoveryCommand(result, sellerId, baseUrl, initial.JobId);
        var displayJobId = XboxPartnerCommandSupport.ForTerminal(initial.JobId);
        string RecoveryLine(string lead, string unsafeIdAdvice) => CanPrintJobId(initial.JobId)
            ? $"{lead} with: {recovery}"
            : $"The job ID can't be shown safely, so {unsafeIdAdvice} Run: {recovery}";
        const string UnknownOutcomeAdvice = "list games to see whether the game was created. Don't resubmit.";
        var verbose = result.GetValue(Program.VerboseOption);
        if (!initial.Completed)
        {
            Console.Error.WriteLine($"Waiting for game creation job {displayJobId}...");
        }

        XboxPartnerGameCreationJob? completed;
        try
        {
            completed = await XboxPartnerJobPoller.PollAsync(
                initial,
                requestToken =>
                {
                    Program.WriteVerbose(verbose,
                        () => $"Reading game creation job {displayJobId}.");
                    return client.GetGameCreationJobAsync(baseUrl, sellerId, initial.JobId, requestToken);
                },
                dependencies.DelayAsync, cancellationToken, dependencies.GetUtcNow);
        }
        catch (Exception exception) when (XboxPartnerCommandSupport.IsRequestError(exception))
        {
            Console.Error.WriteLine(
                exception switch
                {
                    OperationCanceledException =>
                        $"Stopped waiting for job {displayJobId}; it may still be running.",
                    XboxPartnerApiException =>
                        "The game was submitted, but its status could not be read.",
                    _ =>
                        $"The game was submitted, but its status could not be read: {XboxPartnerCommandSupport.ForTerminal(exception.Message)}",
                });
            Console.Error.WriteLine(RecoveryLine("Check its status", UnknownOutcomeAdvice));
            XboxPartnerCommandSupport.WriteRequestError(exception, result, cancellationToken);
            return 1;
        }

        if (completed is null)
        {
            WriteJobResponse(initial.RawResponse, result);
            Console.Error.WriteLine(
                $"Job {displayJobId} is still running after {XboxPartnerJobPoller.DefaultTimeout.TotalSeconds:0} seconds.");
            Console.Error.WriteLine(RecoveryLine("Check its status", UnknownOutcomeAdvice));
            return 0;
        }

        WriteJobResponse(completed.RawResponse, result);
        var completedJobId = XboxPartnerCommandSupport.ForTerminal(completed.JobId);
        Console.Error.WriteLine(completed.Succeeded
            ? $"Game creation job {completedJobId} completed."
            : $"Game creation job {completedJobId} completed without creating the game.");
        if (!completed.Succeeded)
        {
            Console.Error.WriteLine(RecoveryLine("Review the job", "list recent games to review it."));
        }

        return completed.Succeeded ? 0 : 2;
    }

    private static async Task<int> ExecuteCreationAsync(
        ParseResult result,
        Option<string?> sellerOption,
        Option<string?> baseUrlOption,
        Func<IConfiguration, IXboxPartnerClient>? clientFactory,
        Func<IXboxPartnerClient, string, string, Task<int>> action,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var configuration = Program.BuildConfiguration(result);
            var baseUrl = XboxPartnerCommandSupport.ResolveBaseUrl(result, baseUrlOption, configuration);
            using var client = clientFactory is null
                ? new XboxPartnerClient(configuration, authenticationService: Program.GetAuthentication(result))
                : clientFactory(configuration);
            var sellerId = await XboxPartnerCommandSupport.GetSellerIdAsync(result, sellerOption).WaitAsync(cancellationToken);
            return await action(client, baseUrl, sellerId);
        }
        catch (Exception exception) when (XboxPartnerCommandSupport.IsRequestError(exception))
        {
            XboxPartnerCommandSupport.WriteRequestError(exception, result, cancellationToken);
            return 1;
        }
    }

    private static bool ConfirmGameCreation(
        bool confirmed, XboxPartnerJobDependencies dependencies,
        string name, string sellerId)
    {
        if (confirmed)
        {
            return true;
        }

        Console.Error.WriteLine(
            $"Creating the game '{name}' under seller {sellerId} is irreversible from this CLI.");
        if (!dependencies.IsInteractive())
        {
            Console.Error.WriteLine(
                "Re-run with --confirm (--yes, -y) to proceed without an interactive prompt.");
            return false;
        }

        Console.Error.Write("Create this game? [y/N]: ");
        Console.Error.Flush();
        var answer = Console.ReadLine()?.Trim();
        if (string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        Console.Error.WriteLine("Game creation canceled.");
        return false;
    }

    private static void WriteJobResponse(string json, ParseResult result)
    {
        var job = JsonNode.Parse(json)!.AsObject();
        if (job.ContainsKey("game") && job.TryGetPropertyValue("errors", out var errors))
        {
            job.Remove("errors");
            job.Add("errors", errors);
        }

        Console.WriteLine(Program.FormatOutput(
            job.ToJsonString(),
            string.Equals(result.GetValue(Program.OutputOption), "json-compact", StringComparison.OrdinalIgnoreCase),
            dataOnly: false));
    }

    private static bool CanPrintJobId(string jobId) => XboxPartnerCommandSupport.ForTerminal(jobId) == jobId;

    // A job ID that would change when printed safely can't be pasted back as the same ID, so
    // recovery lists games instead of naming a different job.
    private static string BuildGameRecoveryCommand(
        ParseResult result, string sellerId, string baseUrl, string? jobId = null) =>
        XboxPartnerCommandSupport.BuildRecoveryCommand(
            result,
            jobId is null || !CanPrintJobId(jobId)
                ? "game list"
                : $"game job get {ShellQuoting.Quote(jobId)}",
            sellerId,
            baseUrl);
}
