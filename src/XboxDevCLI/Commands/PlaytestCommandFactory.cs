// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Output;
using XboxDevCLI.Presentation;
using XboxDevCLI.Services;
using XboxDevCLI.Telemetry;

namespace XboxDevCLI.Commands;

/// <summary>
/// Builds <c>xbox playtest</c>: read only commands over the gateway's playtest routes. A playtest
/// belongs to one game, so each command takes the game as <c>--store-id</c> or <c>--xbox-title-id</c>.
/// </summary>
internal static partial class PlaytestCommandFactory
{
    public static Command Create() => Create(clientFactory: null);

    internal static Command Create(
        Func<IConfiguration, XboxPartnerClient>? clientFactory,
        XboxPartnerJobDependencies? dependencies = null)
    {
        dependencies ??= XboxPartnerJobDependencies.Default;
        var playtest = new Command("playtest", "List, get, diff, create, update, publish, and delete the playtests of a game");
        playtest.Subcommands.Add(CreateListCommand(clientFactory));
        playtest.Subcommands.Add(CreateGetCommand(clientFactory));
        playtest.Subcommands.Add(CreateDiffCommand(clientFactory));
        playtest.Subcommands.Add(CreateCreateCommand(clientFactory, dependencies));
        playtest.Subcommands.Add(CreateUpdateCommand(clientFactory, dependencies));
        playtest.Subcommands.Add(CreatePublishCommand(clientFactory, dependencies));
        playtest.Subcommands.Add(CreateDeleteCommand(clientFactory, dependencies));
        return playtest.Requires(CommandRequirement.Authentication).Calls(ServiceEndpoints.XboxPartner);
    }

    /// <summary>Exit code when a create, update, publish, or delete was sent but its outcome is unknown.</summary>
    internal const int OutcomeUnknownExitCode = 3;

    internal const string DateFormat =
        "Dates are ISO 8601, such as 2026-11-01T17:00:00-07:00. A date or time without an offset "
        + "is UTC, so 2026-11-01 is midnight UTC.";

    internal const string PlaytestIdFormat =
        "A playtest ID is a GUID, such as 11111111-2222-3333-4444-555555555555, as shown by 'xbox playtest list'.";

    internal static bool IsPlaytestId(string value) =>
        Guid.TryParse(value.Trim(), out var id) && id != Guid.Empty;

    private static Argument<string> CreatePlaytestIdArgument(string name, string description)
    {
        var argument = new Argument<string>(name) { Description = description };
        argument.Validators.Add(result =>
        {
            if (result.Tokens.Count > 0 && !IsPlaytestId(result.Tokens[0].Value))
            {
                result.AddError($"Invalid {name} '{result.Tokens[0].Value}'. {PlaytestIdFormat}");
            }
        });
        return argument;
    }

    /// <summary>
    /// Explains the playtest refusals whose HTTP status alone misleads: playtests that aren't
    /// enabled answer 403 like a missing permission, and the service's own detail names systems
    /// that mean nothing to the reader. Other failures keep the general guidance.
    /// <paramref name="listCommand"/> is the <c>playtest list</c> command for the same game and
    /// seller, which a missing playtest points to. <paramref name="writing"/> is true for create,
    /// update, publish, and delete, which need permission to change the game's playtests.
    /// <paramref name="audienceListCommand"/> is the <c>audience list</c> command for the same seller,
    /// which an unknown audience points to.
    /// </summary>
    internal static string? DescribeRefusal(
        XboxPartnerApiException exception,
        bool comparing,
        string listCommand,
        bool writing = false,
        string audienceListCommand = "xbox audience list") =>
        TestAccountCommandFactory.ReadProblemString(exception.ResponseBody, "code") switch
        {
            XboxPartnerErrorGuidance.GameNotFoundCode => XboxPartnerErrorGuidance.DescribeGameNotFound(exception.RequestUri),
            "package_permission_denied" when writing =>
                "You don't have permission to change this game's playtests for this seller. "
                + "Ask a manager of the Partner Center account for access, "
                + "or pass --seller-id if the game belongs to another seller.",
            "package_permission_denied" =>
                "You don't have Partner Center's Packages Read permission on this game for this seller. "
                + "Ask a manager of the Partner Center account to give you the Developer role, which includes it, "
                + "or pass --seller-id if the game belongs to another seller.",
            "playtest_not_enabled" =>
                "Playtests aren't enabled for this game and seller, or the service couldn't check right now. "
                + "If they should be enabled, try again later.",
            "playtest_not_found" when comparing =>
                $"The game has no playtest with one of these IDs. To see its playtests, run: {listCommand}",
            "playtest_not_found" =>
                $"The game has no playtest with that ID. To see its playtests, run: {listCommand}",
            "playtest_not_editable" =>
                "The playtest's status doesn't allow changes right now. "
                + $"To check it, run '{CliIdentity.CommandName} playtest get'.",
            "playtest_ended" =>
                "The playtest's end date has passed, so it can't be changed unless you also pass a future --end.",
            "playtest_not_publishable" =>
                "The playtest's status doesn't allow publishing it now, or it changed after it was read. "
                + $"Check its status with '{CliIdentity.CommandName} playtest get'.",
            "playtest_not_deletable" =>
                $"The playtest's status doesn't allow deleting it now. Check its status with '{CliIdentity.CommandName} playtest get'.",
            "playtest_audience_not_found" =>
                "At least one --audience-id isn't one of the seller's audiences. "
                + $"To see them, run: {audienceListCommand}",
            "playtest_package_platform_mismatch" =>
                "A package doesn't target the platform of the option it was passed in: --pc-package-id takes a PC package "
                + "and --console-package-id takes a console package. "
                + $"Check each package's platform with '{CliIdentity.CommandName} package list'.",
            _ => null,
        };

    private static Command CreateListCommand(Func<IConfiguration, XboxPartnerClient>? clientFactory)
    {
        var game = XboxPartnerGameResolver.CreateGameOptions();
        var seller = XboxPartnerCommandSupport.CreateSellerIdOption();
        var baseUrl = XboxPartnerCommandSupport.CreateBaseUrlOption();
        var command = new Command("list", "List the playtests of a game")
        {
            game.StoreId, game.TitleId, seller, baseUrl,
        };
        command.Validators.Add(result => XboxPartnerGameResolver.RequireOneGame(result, game));
        command.SetAction((ParseResult result, CancellationToken cancellationToken) =>
            ExecuteAsync(
                result, game, seller, baseUrl, clientFactory,
                async (client, url, sellerId, storeId) =>
                {
                    string json;
                    using (var progress = ListProgress.Start("Reading playtests"))
                    {
                        json = await client.ListPlaytestsAsync(
                            url, sellerId, storeId, cancellationToken, progress.PageRead);
                    }

                    WriteOutput(result, json, json);
                    return 0;
                },
                cancellationToken));
        return command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.ReadOnly,
            Scope: "Playtests of one game owned by the seller selected by --seller-id, XBOX_SELLER_ID, or 'context set'.",
            Output: "A JSON object with a playtests array. Each playtest has playtestId, name, status, type, "
                + "startDate, endDate, and errorCode. Table output has PLAYTEST ID, NAME, "
                + "STATUS, TYPE, START, and END columns. " + ListProgress.HelpOutput,
            Examples:
            [
                "xbox playtest list --store-id 9N0000000000",
                "xbox playtest list --xbox-title-id 1234ABCD --output table",
            ],
            LearnMore:
            [
                "Follows every page, so the list is complete.",
                "Pass any playtestId to 'xbox playtest get' or 'xbox playtest diff'.",
                "Fails (HTTP 403) when playtests aren't enabled for the seller and game, or the service couldn't check right now; try again later if they should be enabled.",
            ],
            Table: PlaytestTableViews.List));
    }

    private static Command CreateGetCommand(Func<IConfiguration, XboxPartnerClient>? clientFactory)
    {
        var playtestId = CreatePlaytestIdArgument("playtest-id", "The playtest ID, as shown by 'xbox playtest list'.");
        var game = XboxPartnerGameResolver.CreateGameOptions();
        var seller = XboxPartnerCommandSupport.CreateSellerIdOption();
        var baseUrl = XboxPartnerCommandSupport.CreateBaseUrlOption();
        var command = new Command("get", "Get one playtest with its ETag")
        {
            playtestId, game.StoreId, game.TitleId, seller, baseUrl,
        };
        command.Validators.Add(result => XboxPartnerGameResolver.RequireOneGame(result, game));
        command.SetAction((ParseResult result, CancellationToken cancellationToken) =>
            ExecuteAsync(
                result, game, seller, baseUrl, clientFactory,
                async (client, url, sellerId, storeId) =>
                {
                    string json;
                    using (RequestProgress.Start("Reading playtest"))
                    {
                        json = await client.GetPlaytestAsync(
                            url, sellerId, storeId, result.GetRequiredValue(playtestId).Trim(), cancellationToken);
                    }

                    PlaytestFields.RequirePlaytest(json);
                    WriteOutput(result, json, PlaytestFields.ToGetFields(json));
                    return 0;
                },
                cancellationToken));
        return command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.ReadOnly,
            Scope: "One playtest of a game owned by the seller selected by --seller-id, XBOX_SELLER_ID, or 'context set'.",
            Output: "A JSON object with details, packages, images, audiences, and etag. The etag is the value "
                + "to send back when updating the playtest, or null when the service did not return one. "
                + "Table output has FIELD and VALUE columns.",
            Examples:
            [
                "xbox playtest get 11111111-2222-3333-4444-555555555555 --store-id 9N0000000000",
                "xbox playtest get 11111111-2222-3333-4444-555555555555 --xbox-title-id 1234ABCD --output table",
            ],
            LearnMore: ["Fails (HTTP 404) when the game has no playtest with that ID."],
            Table: PlaytestTableViews.Fields));
    }

    private static Command CreateDiffCommand(Func<IConfiguration, XboxPartnerClient>? clientFactory)
    {
        var playtestId = CreatePlaytestIdArgument("playtest-id", "The playtest ID, as shown by 'xbox playtest list'.");
        var compareTo = new Option<string?>("--compare-to")
        {
            Description = "Another playtest ID to compare with. Without it, the playtest is compared "
                + "with its last published version.",
        };
        compareTo.Validators.Add(result =>
        {
            if (result.Tokens.Count > 0 && !IsPlaytestId(result.Tokens[0].Value))
            {
                result.AddError($"Invalid --compare-to '{result.Tokens[0].Value}'. {PlaytestIdFormat}");
            }
        });
        var game = XboxPartnerGameResolver.CreateGameOptions();
        var seller = XboxPartnerCommandSupport.CreateSellerIdOption();
        var baseUrl = XboxPartnerCommandSupport.CreateBaseUrlOption();
        var command = new Command("diff", "Compare a playtest with its last published version")
        {
            playtestId, compareTo, game.StoreId, game.TitleId, seller, baseUrl,
        };
        command.Validators.Add(result => XboxPartnerGameResolver.RequireOneGame(result, game));
        command.SetAction((ParseResult result, CancellationToken cancellationToken) =>
            ExecuteAsync(
                result, game, seller, baseUrl, clientFactory,
                async (client, url, sellerId, storeId) =>
                {
                    string json;
                    using (RequestProgress.Start("Comparing playtest"))
                    {
                        json = await client.GetPlaytestDiffAsync(
                            url,
                            sellerId,
                            storeId,
                            result.GetRequiredValue(playtestId).Trim(),
                            result.GetValue(compareTo)?.Trim(),
                            cancellationToken);
                    }

                    if (!PlaytestFields.HasDiffData(json))
                    {
                        Console.Error.WriteLine(
                            "There is nothing to compare with: the playtest has no published version.");
                    }

                    WriteOutput(result, json, PlaytestFields.ToDiffFields(json));
                    return 0;
                },
                cancellationToken,
                comparing: result.GetValue(compareTo) is not null));
        return command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.ReadOnly,
            Scope: "One playtest of a game owned by the seller selected by --seller-id, XBOX_SELLER_ID, or 'context set'.",
            Output: "A JSON object with hasDiffData, current, and compared (omitted when hasDiffData is false). "
                + "Table output has FIELD, CURRENT, and COMPARED columns for the fields that differ. "
                + "Art URIs are left out because they change on every request.",
            Examples:
            [
                "xbox playtest diff 11111111-2222-3333-4444-555555555555 --store-id 9N0000000000",
                "xbox playtest diff 11111111-2222-3333-4444-555555555555 --xbox-title-id 1234ABCD --compare-to 22222222-3333-4444-5555-666666666666 --output table",
            ],
            LearnMore:
            [
                "When hasDiffData is false there is nothing to compare, and the table is empty.",
                "Images are not compared.",
            ],
            Table: PlaytestTableViews.Diff));
    }

    private sealed record DetailOptions(
        Option<string?> Name,
        Option<string?> Description,
        Option<string?> LongDescription,
        Option<string?> Start,
        Option<string?> End,
        Option<string?> PcPackage,
        Option<string?> ConsolePackage,
        Option<string[]> AudienceIds,
        Option<bool> Confirm)
    {
        public IEnumerable<Option> All => [.. Fields, Confirm];

        public IEnumerable<Option> Fields => [.. TextFields, AudienceIds];

        public IEnumerable<Option<string?>> TextFields => [Name, Description, LongDescription, Start, End, PcPackage, ConsolePackage];

        public PlaytestWrite Read(ParseResult result) => new(
            result.GetValue(Name)?.Trim(),
            result.GetValue(Description),
            result.GetValue(LongDescription),
            ReadDate(result, Start),
            ReadDate(result, End),
            ReadAudienceIds(result, AudienceIds),
            result.GetValue(PcPackage)?.Trim(),
            result.GetValue(ConsolePackage)?.Trim());

        public bool IsConfirmed(ParseResult result) =>
            result.GetResult(Confirm) is { Implicit: false }
                ? result.GetValue(Confirm)
                : result.GetValue(Program.ConfirmOption);
    }

    private static DetailOptions CreateDetailOptions(bool required, string confirmDescription)
    {
        var requirement = required ? " Required." : string.Empty;
        var name = new Option<string?>("--name")
        {
            Description = "The playtest's name, shown to players." + requirement,
            Required = required,
        };
        name.Validators.Add(result =>
        {
            if (result.Tokens.Count > 0 && string.IsNullOrWhiteSpace(result.Tokens[0].Value))
            {
                result.AddError("--name cannot be empty.");
            }
        });
        var description = new Option<string?>("--description")
        {
            Description = required
                ? "A short description of the playtest."
                : "A new short description. Pass \"\" to clear it.",
        };
        var longDescription = new Option<string?>("--long-description")
        {
            Description = required
                ? "A long description of the playtest."
                : "A new long description. Pass \"\" to clear it.",
        };
        var start = CreateDateOption("--start", "When the playtest starts." + requirement, required);
        var end = CreateDateOption(
            "--end", "When the playtest ends; must be after the start." + requirement, required);
        var audienceIds = CreateAudienceIdOption(clearable: !required);
        var pcPackage = new Option<string?>("--pc-package-id")
        {
            Description = required
                ? "The ID of the PC package players get, from 'xbox package list'."
                : "The ID of a new PC package, from 'xbox package list'. Pass \"\" to remove the PC package.",
        };
        var consolePackage = new Option<string?>("--console-package-id")
        {
            Description = required
                ? "The ID of the console package players get, from 'xbox package list'."
                : "The ID of a new console package, from 'xbox package list'. Pass \"\" to remove the console package.",
        };
        foreach (var package in new[] { pcPackage, consolePackage })
        {
            package.Validators.Add(result =>
            {
                if (result.Tokens.Count > 0 && result.Tokens[0].Value.Length > 0
                    && string.IsNullOrWhiteSpace(result.Tokens[0].Value))
                {
                    result.AddError($"{package.Name} cannot be only whitespace.");
                }
            });
        }

        var confirm = new Option<bool>("--confirm", "--yes", "-y") { Description = confirmDescription };
        return new DetailOptions(
            name, description, longDescription, start, end, pcPackage, consolePackage, audienceIds, confirm);
    }

    internal const string AudienceIdFormat =
        "An audience ID is up to 128 letters, digits, '_', '.', or '-', but not '.' or '..', "
        + "such as 1152921504606846976, as shown by 'xbox audience list'.";

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,128}$")]
    private static partial Regex AudienceIdPattern();

    internal static bool IsAudienceId(string value) =>
        AudienceIdPattern().IsMatch(value) && !XboxPartnerClient.IsDotSegment(value);

    private static Option<string[]> CreateAudienceIdOption(bool clearable)
    {
        var option = new Option<string[]>("--audience-id")
        {
            Description = clearable
                ? "An audience that can join the playtest, from 'xbox audience list'. Repeat for more audiences; "
                    + "the IDs replace the playtest's audiences. Pass \"\" alone to clear them."
                : "An audience that can join the playtest, from 'xbox audience list'. Repeat for more audiences.",
            HelpName = "id",
            Arity = ArgumentArity.OneOrMore,
            AllowMultipleArgumentsPerToken = false,
        };
        option.Validators.Add(result =>
        {
            var values = result.Tokens.Select(token => token.Value.Trim()).ToList();
            if (values.Any(value => value.Length == 0))
            {
                if (!clearable)
                {
                    result.AddError("--audience-id cannot be empty. " + AudienceIdFormat);
                }
                else if (values.Count > 1)
                {
                    result.AddError(
                        "Pass --audience-id \"\" alone to clear the audiences; it can't be combined with audience IDs.");
                }

                return;
            }

            foreach (var value in values.Where(value => !IsAudienceId(value)))
            {
                result.AddError($"Invalid --audience-id '{XboxPartnerCommandSupport.ForTerminal(value)}'. {AudienceIdFormat}");
            }
        });
        return option;
    }

    // Null when the option isn't passed, empty when it clears the audiences, and otherwise the
    // trimmed IDs in the order given, without repeats.
    private static IReadOnlyList<string>? ReadAudienceIds(ParseResult result, Option<string[]> option)
    {
        if (result.GetResult(option) is null)
        {
            return null;
        }

        return (result.GetValue(option) ?? [])
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static Option<string?> CreateDateOption(string name, string description, bool required)
    {
        var option = new Option<string?>(name)
        {
            Description = $"{description} {DateFormat}",
            Required = required,
        };
        option.Validators.Add(result =>
        {
            if (result.Tokens.Count > 0 && !TryParseDate(result.Tokens[0].Value, out _))
            {
                result.AddError($"Invalid {name} '{result.Tokens[0].Value}'. {DateFormat}");
            }
        });
        return option;
    }

    // ISO 8601 only, so a culture dependent date such as 01/11/2026 or a bare time is refused.
    private static readonly string[] DateFormats =
    [
        "yyyy-MM-dd",
        "yyyy-MM-dd'T'HH:mmK",
        "yyyy-MM-dd'T'HH:mm:ssK",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
    ];

    internal static bool TryParseDate(string? value, out DateTimeOffset date) =>
        DateTimeOffset.TryParseExact(
            value?.Trim(),
            DateFormats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out date);

    private static DateTimeOffset? ReadDate(ParseResult result, Option<string?> option) =>
        TryParseDate(result.GetValue(option), out var date) ? date : null;

    // Only checked when both dates are given; an update's merged dates are checked by the service.
    private static void RequireEndAfterStart(CommandResult result, DetailOptions details)
    {
        if (result.GetResult(details.Start) is { Tokens.Count: > 0 } start
            && result.GetResult(details.End) is { Tokens.Count: > 0 } end
            && TryParseDate(start.Tokens[0].Value, out var startDate)
            && TryParseDate(end.Tokens[0].Value, out var endDate)
            && endDate <= startDate)
        {
            result.AddError("--end must be after --start.");
        }
    }

    private static Command CreateCreateCommand(
        Func<IConfiguration, XboxPartnerClient>? clientFactory,
        XboxPartnerJobDependencies dependencies)
    {
        var details = CreateDetailOptions(required: true, "Confirm creating the playtest without prompting.");
        var idempotencyKeyOption = new Option<string?>("--idempotency-key")
        {
            Description = "The idempotency key to send, 1 to 50 printable ASCII characters, instead of a new GUID. "
                + "Repeating the key of an earlier create returns the playtest it created instead of creating another.",
        };
        idempotencyKeyOption.Validators.Add(result =>
        {
            // The service trims the key and takes 1 to 50 characters. Printable ASCII only: the HTTP stack would send a
            // line break as it is, and refuses a non-ASCII character only after the request has started, which reads as exit 3.
            if (result.Tokens.Count > 0
                && result.Tokens[0].Value.Trim() is var key
                && (key.Length is 0 or > 50 || !key.All(character => character is >= ' ' and <= '~')))
            {
                result.AddError("--idempotency-key must be 1 to 50 printable ASCII characters, such as a GUID.");
            }
        });
        var game = XboxPartnerGameResolver.CreateGameOptions();
        var seller = XboxPartnerCommandSupport.CreateSellerIdOption();
        var baseUrl = XboxPartnerCommandSupport.CreateBaseUrlOption();
        var command = new Command("create", "Create a private draft playtest for a game");
        foreach (var option in details.All.Concat([idempotencyKeyOption, game.StoreId, game.TitleId, seller, baseUrl]))
        {
            command.Options.Add(option);
        }

        command.Validators.Add(result =>
        {
            XboxPartnerGameResolver.RequireOneGame(result, game);
            RequireEndAfterStart(result, details);
        });
        command.SetAction((ParseResult result, CancellationToken cancellationToken) =>
            ExecuteAsync(
                result, game, seller, baseUrl, clientFactory,
                async (client, url, sellerId, storeId) =>
                {
                    var request = details.Read(result);
                    var audiences = request.AudienceIds is { Count: > 0 } ids
                        ? $", for audiences {FormatAudienceIds(ids)}"
                        : string.Empty;
                    if (!await ConfirmAsync(
                        details.IsConfirmed(result),
                        dependencies,
                        $"Creating the playtest '{XboxPartnerCommandSupport.ForTerminal(request.Name)}' for game {storeId} "
                            + $"under seller {sellerId}, from {FormatUtc(request.StartDate)} to {FormatUtc(request.EndDate)}"
                            + $"{audiences}{DescribePackages(request)}. Remove it later with 'xbox playtest delete'.",
                        "Create this playtest?",
                        "Playtest creation canceled.",
                        cancellationToken))
                    {
                        return 1;
                    }

                    // A new key per invocation unless --idempotency-key repeats one. The request is never retried.
                    var idempotencyKey = result.GetValue(idempotencyKeyOption)?.Trim() ?? Guid.NewGuid().ToString();
                    var correlationId = Guid.NewGuid().ToString();
                    Program.WriteVerbose(result.GetValue(Program.VerboseOption),
                        () => $"Creating a playtest for game {storeId} with Idempotency-Key {idempotencyKey} "
                            + $"and correlation ID {correlationId}.");
                    var recovery = XboxPartnerCommandSupport.BuildRecoveryCommand(
                        result, $"playtest list --store-id {ShellQuoting.Quote(storeId)}", sellerId);
                    var sent = false;
                    string json;
                    try
                    {
                        using (RequestProgress.Start($"Creating playtest '{request.Name}'"))
                        {
                            json = await client.CreatePlaytestAsync(
                                url, sellerId, storeId, request, idempotencyKey, correlationId, cancellationToken,
                                onSending: () => sent = true);
                        }
                    }
                    catch (OperationCanceledException) when (!sent && cancellationToken.IsCancellationRequested)
                    {
                        Console.Error.WriteLine("Canceled before the request was sent, so no playtest was created.");
                        return 1;
                    }
                    catch (Exception exception) when (sent && IsUncertainSubmission(exception))
                    {
                        Console.Error.WriteLine(
                            "Playtest creation was interrupted or its response could not be verified; "
                            + $"it is unknown whether the playtest '{XboxPartnerCommandSupport.ForTerminal(request.Name)}' was created.");
                        Console.Error.WriteLine(
                            "To retry without creating a second playtest, "
                            + (CanPrintDetails(result, details)
                                ? "send the same idempotency key: "
                                    + BuildCreateRetryCommand(result, details, storeId, idempotencyKey, sellerId)
                                : $"run the same command again with --idempotency-key {ShellQuoting.Quote(idempotencyKey)}. "
                                    + "The full command isn't shown because an option has a line break or another control character."));
                        Console.Error.WriteLine(
                            $"If the retry is refused, check whether the playtest exists before creating it with a new key: {recovery}");
                        XboxPartnerCommandSupport.WriteRequestError(exception, result, cancellationToken, correlationId);
                        return OutcomeUnknownExitCode;
                    }

                    WriteOutput(result, json, json);
                    return 0;
                },
                cancellationToken,
                writing: true));
        return command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.RemoteMutation,
            Stage: CommandStage.Experimental,
            Scope: "Creates a playtest for one game owned by the seller selected by --seller-id, XBOX_SELLER_ID, or 'context set'.",
            Output: "The created playtest, as 'xbox playtest get' writes it: a JSON object with details, packages, "
                + "images, audiences, and etag. Always JSON, so --output table is refused. Nothing is written to stdout "
                + "when the create fails or its outcome is unknown.",
            SafetyDetails: "Creates a playtest in Partner Center, which 'xbox playtest delete' removes. Requires "
                + "--confirm (--yes, -y) or terminal confirmation. The request is sent once and never retried. "
                + "Agent approval is still required.",
            Examples:
            [
                "xbox playtest create --store-id 9N0000000000 --name \"My sample playtest\" --start 2026-11-01 --end 2026-11-15 --confirm",
                "xbox playtest create --xbox-title-id 1234ABCD --name \"My sample playtest\" --description \"Try the new arena.\" --start 2026-11-01T17:00:00-07:00 --end 2026-11-15T17:00:00-07:00 --audience-id 1152921504606846976 --confirm",
                "xbox playtest create --store-id 9N0000000000 --name \"My sample playtest\" --start 2026-11-01 --end 2026-11-15 --pc-package-id 11111111-2222-3333-4444-555555555555 --confirm",
            ],
            LearnMore:
            [
                "The playtest starts as a private draft.",
                "Pass --audience-id once for each audience that can join the playtest, using IDs from "
                    + "'xbox audience list'. Without it, the playtest has no audiences, and can't be published until it has one. "
                    + "Fails (HTTP 400) when an ID isn't one of the seller's audiences.",
                PackageHelp,
                DateFormat,
                "To remove the playtest later, run 'xbox playtest delete'. "
                    + "Exit codes: 0 created; 1 not created (rejected, or failed before the request was sent); "
                    + "3 the request was sent but its outcome is unknown, and stderr shows its correlation ID.",
                "Each run sends a new idempotency key unless you pass --idempotency-key. A create that repeats a key "
                    + "returns the playtest that key created, without applying any options that differ; change it with "
                    + "'xbox playtest update'.",
                "After exit 3, stderr shows the command that retries with the same options and key, or only the key "
                    + "when an option has a line break or another control character. If the retry is refused, run "
                    + "'xbox playtest list' to check whether the playtest exists before creating it with a new key.",
            ]));
    }

    // A value that would change when printed safely, such as one with a line break, can't be shown as it was typed.
    private static bool CanPrintDetails(ParseResult result, DetailOptions details) =>
        details.TextFields.All(option =>
            result.GetValue(option) is not { } value || XboxPartnerCommandSupport.ForTerminal(value) == value);

    /// <summary>
    /// The <c>playtest create</c> command that repeats this one's details with <paramref name="idempotencyKey"/>,
    /// so it returns the playtest if this request created it.
    /// </summary>
    private static string BuildCreateRetryCommand(
        ParseResult result, DetailOptions details, string storeId, string idempotencyKey, string sellerId)
    {
        var command = $"playtest create --store-id {ShellQuoting.Quote(storeId)}";
        foreach (var option in details.TextFields)
        {
            if (result.GetValue(option) is { } value)
            {
                command += $" {option.Name} {ShellQuoting.Quote(value)}";
            }
        }

        foreach (var audienceId in ReadAudienceIds(result, details.AudienceIds) ?? [])
        {
            command += $" {details.AudienceIds.Name} {ShellQuoting.Quote(audienceId)}";
        }

        return XboxPartnerCommandSupport.BuildRecoveryCommand(
            result, $"{command} --idempotency-key {ShellQuoting.Quote(idempotencyKey)}", sellerId);
    }

    private static Command CreateUpdateCommand(
        Func<IConfiguration, XboxPartnerClient>? clientFactory,
        XboxPartnerJobDependencies dependencies)
    {
        var playtestId = CreatePlaytestIdArgument("playtest-id", "The playtest ID, as shown by 'xbox playtest list'.");
        var details = CreateDetailOptions(required: false, "Confirm updating the playtest without prompting.");
        var ifMatch = new Option<string?>("--if-match")
        {
            Description = "Update only if the playtest still has this etag, as shown by 'xbox playtest get'. "
                + "Without it, the update applies to the current version.",
        };
        ifMatch.Validators.Add(result =>
        {
            if (result.Tokens.Count > 0 && string.IsNullOrWhiteSpace(result.Tokens[0].Value))
            {
                result.AddError("--if-match cannot be empty.");
            }
            // One strong etag only: '*' or a list would turn the precondition into "any version", and the
            // service never matches a weak one. Printable ASCII only, because the HTTP stack drops or
            // refuses other header values.
            else if (result.Tokens.Count > 0 && QuoteEtag(result.Tokens[0].Value) is null)
            {
                result.AddError(
                    "--if-match must be one etag as 'xbox playtest get' shows it, such as \"0x8DD1234ABCD5678\"; "
                    + "the double quotes are optional. '*', weak (W/) etags, and lists are refused.");
            }
        });
        var game = XboxPartnerGameResolver.CreateGameOptions();
        var seller = XboxPartnerCommandSupport.CreateSellerIdOption();
        var baseUrl = XboxPartnerCommandSupport.CreateBaseUrlOption();
        var command = new Command("update", "Change the name, descriptions, dates, audiences, or packages of a playtest")
        {
            playtestId,
        };
        foreach (var option in details.All.Concat([ifMatch, game.StoreId, game.TitleId, seller, baseUrl]))
        {
            command.Options.Add(option);
        }

        command.Validators.Add(result =>
        {
            XboxPartnerGameResolver.RequireOneGame(result, game);
            if (details.Fields.All(option => result.GetResult(option) is null))
            {
                result.AddError(
                    "Pass at least one of --name, --description, --long-description, --start, --end, --audience-id, "
                    + "--pc-package-id, or --console-package-id.");
            }

            RequireEndAfterStart(result, details);
        });
        command.SetAction((ParseResult result, CancellationToken cancellationToken) =>
            ExecuteAsync(
                result, game, seller, baseUrl, clientFactory,
                async (client, url, sellerId, storeId) =>
                {
                    var id = result.GetRequiredValue(playtestId).Trim();
                    var request = details.Read(result);
                    if (!await ConfirmAsync(
                        details.IsConfirmed(result),
                        dependencies,
                        DescribeUpdate(id, storeId, sellerId, request),
                        "Update this playtest?",
                        "Playtest update canceled.",
                        cancellationToken))
                    {
                        return 1;
                    }

                    var correlationId = Guid.NewGuid().ToString();
                    Program.WriteVerbose(result.GetValue(Program.VerboseOption),
                        () => $"Updating playtest {id} with correlation ID {correlationId}.");
                    var recovery = XboxPartnerCommandSupport.BuildRecoveryCommand(
                        result, $"playtest get {ShellQuoting.Quote(id)} --store-id {ShellQuoting.Quote(storeId)}", sellerId);
                    var sent = false;
                    string json;
                    try
                    {
                        using (RequestProgress.Start($"Updating playtest {id}"))
                        {
                            json = await client.UpdatePlaytestAsync(
                                url, sellerId, storeId, id, request, QuoteEtag(result.GetValue(ifMatch)),
                                correlationId, cancellationToken, onSending: () => sent = true);
                        }
                    }
                    catch (OperationCanceledException) when (!sent && cancellationToken.IsCancellationRequested)
                    {
                        Console.Error.WriteLine($"Canceled before the request was sent, so playtest {id} wasn't changed.");
                        return 1;
                    }
                    catch (XboxPartnerApiException exception)
                        when (exception.StatusCode == HttpStatusCode.PreconditionFailed)
                    {
                        XboxPartnerCommandSupport.WriteRequestError(exception, result, cancellationToken);
                        Console.Error.WriteLine(result.GetValue(ifMatch) is null
                            ? "The playtest changed while this update ran. Read it again, check that your change still applies, and retry. To read it, run:"
                            : "The etag in --if-match is no longer current. Read the playtest again, check that your change still applies, and retry with its new etag. To read it, run:");
                        Console.Error.WriteLine(recovery);
                        return 1;
                    }
                    catch (Exception exception) when (sent && IsUncertainSubmission(exception))
                    {
                        Console.Error.WriteLine(
                            "The playtest update was interrupted or its response could not be verified; "
                            + "it is unknown whether it was applied.");
                        Console.Error.WriteLine($"Check the playtest with: {recovery}");
                        XboxPartnerCommandSupport.WriteRequestError(exception, result, cancellationToken, correlationId);
                        return OutcomeUnknownExitCode;
                    }

                    WriteOutput(result, json, json);
                    return 0;
                },
                cancellationToken,
                writing: true));
        return command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.RemoteMutation,
            Stage: CommandStage.Experimental,
            Scope: "One playtest of a game owned by the seller selected by --seller-id, XBOX_SELLER_ID, or 'context set'.",
            Output: "The updated playtest, as 'xbox playtest get' writes it: a JSON object with details, packages, "
                + "images, audiences, and its new etag. Always JSON, so --output table is refused. Nothing is written to "
                + "stdout when the update fails or its outcome is unknown.",
            SafetyDetails: "Changes a playtest in Partner Center, and a published playtest returns to draft "
                        + "until it's published again, for example with 'xbox playtest publish'. "
                + "Requires --confirm (--yes, -y) or terminal confirmation. The request is sent once and never "
                + "retried. Agent approval is still required.",
            Examples:
            [
                "xbox playtest update 11111111-2222-3333-4444-555555555555 --store-id 9N0000000000 --end 2026-11-30 --confirm",
                "xbox playtest update 11111111-2222-3333-4444-555555555555 --xbox-title-id 1234ABCD --name \"My sample playtest 2\" --description \"\" --if-match 0x8DD1234ABCD5678 --confirm",
                "xbox playtest update 11111111-2222-3333-4444-555555555555 --store-id 9N0000000000 --audience-id 1152921504606846976 --audience-id 1152921504606846977 --confirm",
                "xbox playtest update 11111111-2222-3333-4444-555555555555 --store-id 9N0000000000 --audience-id \"\" --confirm",
                "xbox playtest update 11111111-2222-3333-4444-555555555555 --store-id 9N0000000000 --pc-package-id 22222222-3333-4444-5555-666666666666 --console-package-id \"\" --confirm",
            ],
            LearnMore:
            [
                "Only the options you pass change; the playtest's other details, packages, and audiences are kept.",
                "--audience-id replaces all of the playtest's audiences with the IDs you pass, from 'xbox audience list'; "
                    + "repeat it for more audiences. Pass --audience-id \"\" alone to remove them all. A playtest without "
                    + "audiences can't be published. Fails (HTTP 400) when an ID isn't one of the seller's audiences.",
                PackageHelp + " Pass \"\" to --pc-package-id or --console-package-id to remove that package.",
                "A published playtest returns to draft; publish it again with 'xbox playtest publish'.",
                DateFormat,
                "Pass --if-match the etag 'xbox playtest get' shows, with or without its double quotes, "
                    + "such as 0x8DD1234ABCD5678. '*', weak (W/) etags, and lists of etags are refused.",
                "Fails (HTTP 412) when the playtest changed since the etag in --if-match "
                    + "was read, or while this update ran; read it again with 'xbox playtest get' and retry.",
                "Fails (HTTP 409) when the playtest's status doesn't allow changes right now; run 'xbox playtest get' to check it.",
                "Fails (HTTP 409) when the playtest's end date is more than 5 minutes in the past and --end isn't passed; pass a future --end with the change.",
                "Exit codes: 0 updated; 1 not updated; 3 the request was sent but its outcome is unknown, "
                    + "and stderr shows its correlation ID.",
            ]));
    }

    internal static async Task<bool> ConfirmAsync(
        bool confirmed,
        XboxPartnerJobDependencies dependencies,
        string summary,
        string question,
        string canceled,
        CancellationToken cancellationToken)
    {
        if (confirmed)
        {
            return true;
        }

        Console.Error.WriteLine(summary);
        if (!dependencies.IsInteractive())
        {
            Console.Error.WriteLine(
                "Run again with --confirm (--yes, -y) to proceed without an interactive prompt.");
            TelemetryRecorder.RecordError(TelemetryErrorTypes.NotConfirmed);
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        ConfirmationPrompt.Write(Console.Error, $"{question} [y/N]: ");
        var readLineAsync = FeedbackDependencies.CancelableReadLine(Console.ReadLine);
        var answer = (await readLineAsync(cancellationToken).ConfigureAwait(false))?.Trim();
        cancellationToken.ThrowIfCancellationRequested();
        if (string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        Console.Error.WriteLine(canceled);
        TelemetryRecorder.RecordError(TelemetryErrorTypes.NotConfirmed);
        return false;
    }

    /// <summary>
    /// Whether a failure after the request was sent leaves the outcome unknown: anything but a
    /// client error (4xx other than 408) from the gateway.
    /// </summary>
    private static bool IsUncertainSubmission(Exception exception) =>
        exception is not XboxPartnerApiException api
            || (int)api.StatusCode is < 400 or >= 500
            || api.StatusCode == HttpStatusCode.RequestTimeout;

    [GeneratedRegex("^(\"[\\x21\\x23-\\x7E]+\"|[\\x21\\x23-\\x2B\\x2D-\\x7E]+)$")]
    private static partial Regex EtagPattern();

    /// <summary>
    /// Returns one strong etag in double quotes, adding them when <paramref name="value"/> has none,
    /// or null when it isn't one strong etag.
    /// </summary>
    internal static string? QuoteEtag(string? value)
    {
        var etag = value?.Trim();
        if (etag is null or "*"
            || etag.StartsWith("W/", StringComparison.OrdinalIgnoreCase)
            || !EtagPattern().IsMatch(etag))
        {
            return null;
        }

        return etag.StartsWith('"') ? etag : $"\"{etag}\"";
    }

    private static string FormatUtc(DateTimeOffset? date) =>
        date?.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) ?? "(unchanged)";

    private static string FormatAudienceIds(IEnumerable<string> ids) =>
        string.Join(", ", ids.Select(XboxPartnerCommandSupport.ForTerminal));

    private static string DescribeUpdate(string id, string storeId, string sellerId, PlaytestWrite request)
    {
        var changes = new List<string>();
        if (request.Name is not null)
        {
            changes.Add($"name to '{XboxPartnerCommandSupport.ForTerminal(request.Name)}'");
        }

        if (request.Description is not null)
        {
            changes.Add(request.Description.Length == 0 ? "clear the description" : "description");
        }

        if (request.LongDescription is not null)
        {
            changes.Add(request.LongDescription.Length == 0 ? "clear the long description" : "long description");
        }

        if (request.StartDate is not null)
        {
            changes.Add($"start to {FormatUtc(request.StartDate)}");
        }

        if (request.EndDate is not null)
        {
            changes.Add($"end to {FormatUtc(request.EndDate)}");
        }

        if (request.AudienceIds is not null)
        {
            changes.Add(request.AudienceIds.Count == 0
                ? "clear the audiences, so it can't be published until it has one"
                : $"audiences to {FormatAudienceIds(request.AudienceIds)}");
        }

        AddPackageChange(changes, "PC", request.PcPackageId);
        AddPackageChange(changes, "console", request.ConsolePackageId);

        return $"Updating playtest {id} of game {storeId} under seller {sellerId}: {string.Join(", ", changes)}. "
            + "A published playtest returns to draft, and must be published again with 'xbox playtest publish'.";
    }

    private const string PackageHelp =
        "A playtest has at most one PC package and one console package. Pass a package's id from 'xbox package list' "
        + "(PACKAGE ID with --output table) that's ready for a playtest: processingState (STATE) is Processed, validationItems contains no item with "
        + "severity Error, and isAvailableForPlaytest (PLAYTEST) is true. 'xbox playtest get' shows a playtest's current packages. "
        + "When the package is in more than one package set, the one on the game's Main branch is used, and in more than one market group of that set, the default one. "
        + "The request is refused, and nothing is written, when the game has no package with that ID, the ID matches more than one package set "
        + "with none of them on the Main branch or more than one market group with none of them the default one, the package doesn't target that option's platform, or it isn't "
        + "available for playtests yet.";

    private static void AddPackageChange(List<string> changes, string platform, string? packageId)
    {
        if (packageId is not null)
        {
            changes.Add(packageId.Length == 0
                ? $"remove the {platform} package"
                : $"{platform} package to {XboxPartnerCommandSupport.ForTerminal(packageId)}");
        }
    }

    private static string DescribePackages(PlaytestWrite request)
    {
        var packages = new List<string>();
        if (!string.IsNullOrEmpty(request.PcPackageId))
        {
            packages.Add($"PC package {XboxPartnerCommandSupport.ForTerminal(request.PcPackageId)}");
        }

        if (!string.IsNullOrEmpty(request.ConsolePackageId))
        {
            packages.Add($"console package {XboxPartnerCommandSupport.ForTerminal(request.ConsolePackageId)}");
        }

        return packages.Count == 0 ? string.Empty : $", with {string.Join(" and ", packages)}";
    }

    private static void WriteOutput(ParseResult result, string json, string tableJson)
    {
        var output = OutputOptions.From(result);
        if (output.TableView is { } view)
        {
            using var document = JsonDocument.Parse(tableJson);
            Console.WriteLine(TableFormatter.Format(document.RootElement, view));
        }
        else
        {
            Console.WriteLine(Program.FormatOutput(json, output.IsCompact, dataOnly: false));
        }
    }

    private static async Task<int> ExecuteAsync(
        ParseResult result,
        XboxPartnerGameResolver.GameOptions game,
        Option<string?> sellerOption,
        Option<string?> baseUrlOption,
        Func<IConfiguration, XboxPartnerClient>? clientFactory,
        Func<XboxPartnerClient, string, string, string, Task<int>> action,
        CancellationToken cancellationToken,
        bool comparing = false,
        bool writing = false)
    {
        // Set once the game is known, so a missing playtest can point to its list.
        var listCommand = "xbox playtest list";
        var audienceListCommand = "xbox audience list";
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
            audienceListCommand = XboxPartnerCommandSupport.BuildRecoveryCommand(result, "audience list", sellerId);
            string storeId = await XboxPartnerGameResolver.ResolveStoreIdAsync(
                result, game, client, baseUrl, sellerId, cancellationToken);

            listCommand = XboxPartnerCommandSupport.BuildRecoveryCommand(
                result, $"playtest list --store-id {ShellQuoting.Quote(storeId)}", sellerId);
            return await action(client, baseUrl, sellerId, storeId);
        }
        catch (XboxPartnerApiException exception)
            when (DescribeRefusal(exception, comparing, listCommand, writing, audienceListCommand) is { } diagnosis)
        {
            XboxPartnerCommandSupport.WriteCorrelationId(exception.CorrelationId, Console.Error);
            XboxPartnerCommandSupport.WriteGatewayError(exception, result, diagnosis);
            return 1;
        }
        // Caller cancellation and bugs reach the shared boundary, which lets them through. A request
        // timeout, which cancels without the caller, is still reported.
        catch (Exception exception) when (XboxPartnerCommandSupport.IsRequestError(exception)
            && exception is not (ArgumentException or ObjectDisposedException)
            && !(exception is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            XboxPartnerCommandSupport.WriteRequestError(exception, result, cancellationToken);
            return 1;
        }
    }
}
