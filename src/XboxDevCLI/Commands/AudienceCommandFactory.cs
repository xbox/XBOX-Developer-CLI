// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Output;
using XboxDevCLI.Services;

namespace XboxDevCLI.Commands;

/// <summary>
/// Builds the <c>audience</c> namespace: the seller's audiences, which are groups of players or
/// devices used to target content to them. Partner Center calls these customer groups. Known-user
/// audiences can also be created, renamed, and have their member lists replaced.
/// </summary>
internal static partial class AudienceCommandFactory
{
    private const string StoredMembersNote =
        "The members field is the stored member list. It isn't proof that every member has been resolved to an " +
        "account or can see targeted content.";

    public static Command Create() => Create(clientFactory: null);

    internal static Command Create(
        Func<IConfiguration, XboxPartnerClient>? clientFactory,
        XboxPartnerJobDependencies? dependencies = null)
    {
        dependencies ??= XboxPartnerJobDependencies.Default;
        var audience = new Command("audience", "List, get, create, and update the audiences of a Partner Center seller");
        audience.Subcommands.Add(CreateListCommand(clientFactory));
        audience.Subcommands.Add(CreateGetCommand(clientFactory));
        audience.Subcommands.Add(CreateCreateCommand(clientFactory, dependencies));
        audience.Subcommands.Add(CreateUpdateCommand(clientFactory, dependencies));
        return audience.Requires(CommandRequirement.Authentication).Calls(ServiceEndpoints.XboxPartner);
    }

    private static Command CreateListCommand(Func<IConfiguration, XboxPartnerClient>? clientFactory)
    {
        var name = new Option<string?>("--name")
        {
            HelpName = "text",
            Description = "Only list audiences whose name contains this text, ignoring case.",
        };
        name.Validators.Add(option =>
        {
            if (option.Tokens.Count > 0 && string.IsNullOrWhiteSpace(option.Tokens[0].Value))
            {
                option.AddError("Invalid --name: it can't be empty.");
            }
        });
        var seller = XboxPartnerCommandSupport.CreateSellerIdOption();
        var baseUrl = XboxPartnerCommandSupport.CreateBaseUrlOption();
        var command = new Command("list", "List the audiences of a Partner Center seller")
        {
            name, seller, baseUrl,
        };
        command.SetAction((ParseResult result, CancellationToken cancellationToken) =>
            ExecuteAsync(
                result, seller, baseUrl, clientFactory,
                async (client, url, sellerId) =>
                {
                    Program.WriteVerbose(result.GetValue(Program.VerboseOption),
                        () => $"Listing audiences for seller {XboxPartnerCommandSupport.ForTerminal(sellerId)}.");
                    IReadOnlyList<JsonObject> audiences;
                    using (RequestProgress.Start("Reading audiences"))
                    {
                        audiences = await client.ListAudiencesAsync(url, sellerId, cancellationToken);
                    }

                    if (result.GetValue(name) is { } nameFilter)
                    {
                        audiences = audiences.Where(audience => NameContains(audience, nameFilter)).ToList();
                    }

                    var items = new JsonArray(audiences.Select(audience => (JsonNode?)ForOutput(audience, "memberCount", "members")).ToArray());
                    Write(new JsonObject { ["audiences"] = items }, OutputOptions.From(result));
                    return 0;
                },
                cancellationToken));
        return command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.ReadOnly,
            Scope: "Audiences of the seller selected by --seller-id, XBOX_SELLER_ID, or 'context set'.",
            Output: "A JSON object with an audiences array of audience objects, each as the service returns " +
                "it, such as name, id, dnaTags, sellerId, groupType, and groupUsage, but without members. A " +
                "list doesn't read members; use 'xbox audience get' for them. With --name, only audiences " +
                "whose name contains the text, ignoring case; none matching is an empty array. Table " +
                "output has ID, NAME, and TYPE columns.",
            Examples:
            [
                "xbox audience list",
                "xbox audience list --name testers",
                "xbox audience list --output table",
                "xbox audience list --seller-id 1234567 --output json-compact",
            ],
            LearnMore:
            [
                "Pass a listed id to 'xbox audience get', unchanged, to read the audience's members.",
            ],
            Table: XboxPartnerCommandSupport.AudienceTableView));
    }

    private static Command CreateGetCommand(Func<IConfiguration, XboxPartnerClient>? clientFactory)
    {
        var audienceId = CreateAudienceIdArgument();
        var seller = XboxPartnerCommandSupport.CreateSellerIdOption();
        var baseUrl = XboxPartnerCommandSupport.CreateBaseUrlOption();
        var command = new Command("get", "Get one audience by ID")
        {
            audienceId, seller, baseUrl,
        };
        command.SetAction((ParseResult result, CancellationToken cancellationToken) =>
            ExecuteAsync(
                result, seller, baseUrl, clientFactory,
                async (client, url, sellerId) =>
                {
                    var id = result.GetValue(audienceId)!;
                    Program.WriteVerbose(result.GetValue(Program.VerboseOption),
                        () => $"Reading audience {XboxPartnerCommandSupport.ForTerminal(id)} " +
                            $"for seller {XboxPartnerCommandSupport.ForTerminal(sellerId)}.");
                    JsonObject audience;
                    using (RequestProgress.Start("Reading audience"))
                    {
                        audience = await client.GetAudienceAsync(url, sellerId, id, cancellationToken);
                    }

                    Write(ForOutput(audience, "memberCount"), OutputOptions.From(result));
                    return 0;
                },
                cancellationToken));
        return command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.ReadOnly,
            Scope: "One audience of the seller selected by --seller-id, XBOX_SELLER_ID, or 'context set'.",
            Output: "The audience as a JSON object as the service returns it, such as name, id, members, " +
                "dnaTags, sellerId, groupType, and groupUsage. Table output has ID, NAME, and TYPE " +
                "columns.",
            Examples:
            [
                "xbox audience get 1152921504606846976",
                "xbox audience get 1152921504606846976 --seller-id 1234567 --output table",
            ],
            LearnMore:
            [
                StoredMembersNote,
                "An audience ID is opaque: pass it exactly as 'xbox audience list' returns it.",
            ],
            Table: XboxPartnerCommandSupport.AudienceRecordTableView));
    }

    // memberCount counts only the members in the response, which a list never reads, so it isn't output.
    private static JsonObject ForOutput(JsonObject audience, params string[] omitted)
    {
        var json = audience.DeepClone().AsObject();
        foreach (var field in omitted)
        {
            json.Remove(field);
        }

        return json;
    }

    private static bool NameContains(JsonObject audience, string text) =>
        audience["name"] is JsonValue value
        && value.TryGetValue<string>(out var name)
        && name.Contains(text, StringComparison.OrdinalIgnoreCase);

    private static void Write(JsonObject json, OutputOptions output)
    {
        if (output.TableView is { } view)
        {
            using var document = JsonDocument.Parse(json.ToJsonString());
            Console.WriteLine(TableFormatter.Format(document.RootElement, view));
            return;
        }

        Console.WriteLine(Program.FormatOutput(json.ToJsonString(), output.IsCompact, dataOnly: false));
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
                ? new XboxPartnerClient(configuration, authenticationService: Program.GetAuthentication(result))
                : clientFactory(configuration);
            var sellerId = await XboxPartnerCommandSupport.GetSellerIdAsync(result, sellerOption)
                .WaitAsync(cancellationToken);
            return await action(client, baseUrl, sellerId);
        }
        // Only the service's failures and a request timeout, which cancels without the caller, are
        // reported here. Everything else, including the caller's cancellation, reaches the shared boundary.
        catch (Exception exception) when (exception is XboxPartnerApiException or XboxPartnerResponseException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            XboxPartnerCommandSupport.WriteRequestError(exception, result, cancellationToken);
            return 1;
        }
    }
}
