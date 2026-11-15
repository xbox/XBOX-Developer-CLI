// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Output;
using XboxDevCLI.Services;

namespace XboxDevCLI.Commands;

/// <summary>
/// Builds <c>xbox package</c>: list and get the packages of a game in Partner Center.
/// </summary>
internal static class PackageCommandFactory
{
    /// <summary>The group Help's pointer to the GDK's makepkg, which uploads packages.</summary>
    internal const string UploadLearnMore =
        "Upload packages with makepkg from the Microsoft GDK: " +
        "https://learn.microsoft.com/gaming/gdk/docs/features/common/packaging/deployment/makepkg";

    public static Command Create() => Create(clientFactory: null);

    internal static Command Create(Func<IConfiguration, XboxPartnerClient>? clientFactory)
    {
        var command = new Command("package", "List and get game packages");
        command.Subcommands.Add(CreateListCommand(clientFactory));
        command.Subcommands.Add(CreateGetCommand(clientFactory));
        return command.Requires(CommandRequirement.Authentication).Calls(ServiceEndpoints.XboxPartner);
    }

    private static string Camel(string name) => JsonNamingPolicy.CamelCase.ConvertName(name);

    private static IReadOnlyList<TableColumn> PackageColumns { get; } =
    [
        new("PACKAGE ID", Camel(nameof(XboxPartnerPackage.Id)), 38),
        new("VERSION", Camel(nameof(XboxPartnerPackage.Version)), 16),
        new("PLATFORM", Camel(nameof(XboxPartnerPackage.Platform)), 12),
        new("STATE", Camel(nameof(XboxPartnerPackage.ProcessingState)), 14),
        new("PLAYTEST", Camel(nameof(XboxPartnerPackage.IsAvailableForPlaytest)), 8),
        new("CREATED", Camel(nameof(XboxPartnerPackage.CreatedAt)), 22),
    ];

    internal static TableView PackageListTableView { get; } = new(XboxPartnerPackage.Collection, PackageColumns);

    internal static TableView PackageTableView { get; } = TableView.Record(PackageColumns);

    private static Command CreateListCommand(Func<IConfiguration, XboxPartnerClient>? clientFactory)
    {
        var game = XboxPartnerGameResolver.CreateGameOptions();
        var seller = new XdtsSellerOption();
        var baseUrl = XboxPartnerCommandSupport.CreateBaseUrlOption();
        var command = new Command("list", "List the packages of a game");
        command.Options.Add(game.StoreId);
        command.Options.Add(game.TitleId);
        command.Validators.Add(result => XboxPartnerGameResolver.RequireOneGame(result, game));
        seller.AddTo(command);
        command.Options.Add(baseUrl);
        command.SetAction((ParseResult result, CancellationToken cancellationToken) =>
            ExecuteAsync(
                result, seller, baseUrl, clientFactory,
                async (client, url, sellerId) =>
                {
                    string storeId = await ResolveGameAsync(result, game, client, url, sellerId, cancellationToken);
                    Program.WriteVerbose(result.GetValue(Program.VerboseOption),
                        () => $"Listing the packages on the Main branch of game {storeId}{DescribeSeller(sellerId)}.");
                    XboxPartnerPackageListing listing;
                    using (RequestProgress.Start("Reading packages"))
                    {
                        listing = await client.ListPackagesAsync(url, sellerId, storeId, cancellationToken);
                    }

                    if (listing.PackageSetId is null)
                    {
                        Console.Error.WriteLine($"Game {storeId} has no Main branch, so it has no packages.");
                    }

                    foreach (string missing in listing.MissingPackageIds)
                    {
                        Console.Error.WriteLine(
                            $"Package {XboxPartnerCommandSupport.ForTerminal(missing)} is on the Main branch but couldn't be found, so it's left out.");
                    }

                    WriteOutput(listing.Json, OutputOptions.From(result));
                    return 0;
                },
                cancellationToken));
        return command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.ReadOnly,
            Scope: "The packages on the Main branch of the game chosen by --store-id or --xbox-title-id, for the seller selected by " +
                "--seller-id, XBOX_SELLER_ID, or 'context set', or your only seller.",
            Output: "A JSON object with the Main branch's packageSetId and a packages array holding every package with every field the service returns, " +
                "including id, version, platform, processingState, isAvailableForPlaytest, and validationItems. " +
                "Table output has PACKAGE ID, VERSION, PLATFORM, STATE, PLAYTEST, and CREATED columns.",
            Examples:
            [
                "xbox package list --store-id 9N0000000000",
                "xbox package list --xbox-title-id 1234ABCD --output table",
                "xbox package list --store-id 9N0000000000 --seller-id 1234567 --output json-compact",
            ],
            LearnMore:
            [
                "A package is ready for a playtest when processingState is Processed, validationItems contains no item with severity Error, " +
                "and isAvailableForPlaytest is true. Table output doesn't show validationItems.",
            ],
            Table: PackageListTableView));
    }

    private static Command CreateGetCommand(Func<IConfiguration, XboxPartnerClient>? clientFactory)
    {
        var packageId = PackageIdArgument();
        var game = XboxPartnerGameResolver.CreateGameOptions();

        var seller = new XdtsSellerOption();
        var baseUrl = XboxPartnerCommandSupport.CreateBaseUrlOption();
        var command = new Command("get", "Get one package of a game") { packageId };
        command.Options.Add(game.StoreId);
        command.Options.Add(game.TitleId);
        command.Validators.Add(result => XboxPartnerGameResolver.RequireOneGame(result, game));
        seller.AddTo(command);
        command.Options.Add(baseUrl);
        command.SetAction((ParseResult result, CancellationToken cancellationToken) =>
            ExecuteAsync(
                result, seller, baseUrl, clientFactory,
                async (client, url, sellerId) =>
                {
                    string storeId = await ResolveGameAsync(result, game, client, url, sellerId, cancellationToken);
                    string id = result.GetValue(packageId)!.Trim();
                    Program.WriteVerbose(result.GetValue(Program.VerboseOption),
                        () => $"Reading package {XboxPartnerCommandSupport.ForTerminal(id)} of game {storeId}{DescribeSeller(sellerId)}.");
                    XboxPartnerPackageResult package;
                    using (RequestProgress.Start("Reading package"))
                    {
                        package = await client.GetPackageAsync(url, sellerId, storeId, id, cancellationToken);
                    }

                    WriteOutput(package.RawResponse, OutputOptions.From(result));
                    return 0;
                },
                cancellationToken));
        return command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.ReadOnly,
            Scope: "One package of the game chosen by --store-id or --xbox-title-id, for the seller selected by " +
                "--seller-id, XBOX_SELLER_ID, or 'context set', or your only seller.",
            Output: "The package as a JSON object with every field the service returns. Table output has one row " +
                "with PACKAGE ID, VERSION, PLATFORM, STATE, PLAYTEST, and CREATED columns.",
            Examples:
            [
                "xbox package get 11111111-2222-3333-4444-555555555555 --store-id 9N0000000000",
                "xbox package get 11111111-2222-3333-4444-555555555555 --xbox-title-id 1234ABCD --output table",
            ],
            Table: PackageTableView));
    }

    private static void WriteOutput(string json, OutputOptions output)
    {
        if (output.TableView is { } view)
        {
            using var document = JsonDocument.Parse(json);
            Console.WriteLine(TableFormatter.Format(document.RootElement, view));
            return;
        }

        Console.WriteLine(Program.FormatOutput(json, output.IsCompact, dataOnly: false));
    }

    private static async Task<int> ExecuteAsync(
        ParseResult result,
        XdtsSellerOption sellerOption,
        Option<string?> baseUrlOption,
        Func<IConfiguration, XboxPartnerClient>? clientFactory,
        Func<XboxPartnerClient, string, long?, Task<int>> action,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var configuration = Program.GetConfiguration(result);
            var baseUrl = XboxPartnerCommandSupport.ResolveBaseUrl(result, baseUrlOption, configuration);
            using var client = clientFactory is null
                ? new XboxPartnerClient(configuration, authenticationService: Program.GetAuthentication(result))
                : clientFactory(configuration);
            long? sellerId = await sellerOption.GetSellerIdAsync(result, configuration, client, baseUrl, cancellationToken);
            return await action(client, baseUrl, sellerId);
        }
        // Only the service's failures and a request timeout, which cancels without the caller, are reported
        // here. Everything else, including the caller's cancellation, reaches the shared error boundary.
        catch (Exception exception) when (exception is XboxPartnerApiException or XboxPartnerResponseException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            WriteError(exception, result, cancellationToken);
            return 1;
        }
    }

    private static Argument<string> PackageIdArgument()
    {
        var packageId = new Argument<string>("package-id") { Description = "ID of the package, from 'xbox package list'." };
        packageId.Validators.Add(result =>
        {
            if (result.Tokens.Count > 0 && string.IsNullOrWhiteSpace(result.Tokens[0].Value))
            {
                result.AddError("The package ID cannot be empty.");
            }
            // The action trims the ID, so validate what it will send.
            else if (result.Tokens.Count > 0 && !XboxPartnerPackageRoutes.IsValidPackageId(result.Tokens[0].Value.Trim()))
            {
                result.AddError("The package ID can't be '.' or '..'.");
            }
        });
        return packageId;
    }

    /// <summary>Writes a request error, with what to do in place of the general guidance when the package routes say.</summary>
    internal static void WriteError(Exception exception, ParseResult result, CancellationToken cancellationToken)
    {
        if (exception is XboxPartnerApiException api && Hint(api) is { } hint)
        {
            XboxPartnerCommandSupport.WriteCorrelationId(api.CorrelationId, Console.Error);
            XboxPartnerCommandSupport.WriteGatewayError(api, result, hint);
            return;
        }

        XboxPartnerCommandSupport.WriteRequestError(exception, result, cancellationToken);
    }

    internal static string? Hint(XboxPartnerApiException exception) =>
        TestAccountCommandFactory.ReadProblemString(exception.ResponseBody, "code") switch
        {
            XboxPartnerErrorGuidance.GameNotFoundCode => XboxPartnerErrorGuidance.DescribeGameNotFound(exception.RequestUri),
            XboxPartnerPackageErrors.PermissionDenied =>
                "You don't have Partner Center's Packages Read permission on this game for this seller. " +
                "Ask a manager of the Partner Center account to give you the Developer role, which includes it, " +
                "or pass --seller-id if the game belongs to another seller.",
            XboxPartnerPackageErrors.SellerSelectionRequired =>
                "You belong to more than one seller. Pass --seller-id, or save one with 'xbox context set seller-id <id>'.",
            XboxPartnerPackageErrors.IdentityOverrideNotAuthorized =>
                "You don't belong to that seller. Check --seller-id, XBOX_SELLER_ID, or the seller saved with " +
                "'xbox context set', and find your sellers with 'xbox seller list'.",
            XboxPartnerPackageErrors.MainPackageSetNotFound => "The game has no Main branch in Partner Center.",
            _ when exception.StatusCode == HttpStatusCode.Forbidden =>
                "Check that --store-id or --xbox-title-id names a game of this seller, and that you have " +
                "Partner Center's Packages Read permission on it.",
            _ => null,
        };

    /// <summary>
    /// The game's Store ID. Looking up an XBOX title ID needs a seller, which a package command can
    /// otherwise leave to the service.
    /// </summary>
    private static Task<string> ResolveGameAsync(
        ParseResult result,
        XboxPartnerGameResolver.GameOptions game,
        XboxPartnerClient client,
        string baseUrl,
        long? sellerId,
        CancellationToken cancellationToken)
    {
        if (sellerId is null && result.GetValue(game.TitleId) is not null)
        {
            throw new InvalidOperationException(
                "Finding a game by --xbox-title-id needs a seller. Pass --seller-id, or choose the game with --store-id.");
        }

        return XboxPartnerGameResolver.ResolveStoreIdAsync(
            result, game, client, baseUrl, sellerId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, cancellationToken);
    }

    private static string DescribeSeller(long? sellerId) =>
        sellerId is { } id ? $" for seller {id.ToString(CultureInfo.InvariantCulture)}" : string.Empty;
}
