// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using XboxDevCLI.Output;
using XboxDevCLI.CommandLine;
using XboxDevCLI.PlayFab.Generated.Admin;
using XboxDevCLI.Services;
using static XboxDevCLI.PlayFab.Commands.PlayFabCommandHelper;

namespace XboxDevCLI.PlayFab.Commands;

/// <summary>
/// Creates the <c>playfab player</c> command group. Today it exposes a single read verb,
/// <c>account get</c>, which reads a player's account from their PlayFab ID.
/// </summary>
/// <remarks>
/// <para>
/// <c>/Admin/GetUserAccountInfo</c> also matches on <c>Email</c>, <c>Username</c> and
/// <c>TitleDisplayName</c>, but only the PlayFab ID is exposed, because each of the other three is
/// unreliable in a different way:
/// </para>
/// <list type="bullet">
///   <item>
///     <c>Email</c> and <c>Username</c> exist only on accounts registered with PlayFab credentials
///     (<c>RegisterPlayFabUser</c>/<c>AddUsernamePassword</c>). A player who signed in through
///     XBOX, Steam or PSN has neither, so on most titles these would fail for every player while
///     appearing supported.
///   </item>
///   <item>
///     <c>TitleDisplayName</c> is not guaranteed unique — a title can permit duplicates — so the
///     lookup can return <em>a</em> player rather than <em>the</em> player. A confidently wrong
///     answer is worse than no answer.
///   </item>
///   <item>
///     <c>Email</c> is also PII, which PlayFab's own note on this API calls out. Accepting it as a
///     search key would make this an enumeration tool, which matters more for a CLI meant to be
///     driven by automation.
///   </item>
/// </list>
/// <para>
/// The PlayFab ID this takes is the <em>master</em> player account ID. PlayFab's entity model also
/// gives each player a <em>title</em> player account ID, which looks identical (16 hex characters)
/// but is a different value that this API rejects. Game Manager surfaces both, so the option help
/// says which one is wanted.
/// </para>
/// <para>
/// Looking a player up by a platform identifier — XBOX XUID, Steam ID, PSN account ID — is not
/// supported yet; see <c>docs/commands/playfab/player.md</c>. Note that the <em>response</em> already
/// carries every linked platform account the player has, so PlayFab ID → platform ID works today;
/// only the reverse is missing.
/// </para>
/// </remarks>
internal static class PlayerCommandFactory
{
    internal const string GetUserAccountInfoPath = "/Admin/GetUserAccountInfo";

    public static Command Create()
    {
        var player = new Command("player", "Look up player accounts");

        // The account is a distinct resource on the player, so the verb hangs off the noun rather
        // than being folded into a bare get-account action.
        var account = new Command("account", "Inspect a player's PlayFab account.");
        account.Subcommands.Add(CreateGetAccountCommand());
        player.Subcommands.Add(account);

        return player;
    }

    private static Command CreateGetAccountCommand()
    {
        var playFabIdOption = new Option<string>("--playfab-id")
        {
            Description =
                "The player's unique PlayFab ID. This is the master player account ID — not the " +
                "title player account ID, which looks the same but is a different value.",
            Required = true,
        };

        var command = new Command(
            "get",
            "Get a player's account info by PlayFab ID. The account returned includes the player's " +
            "linked Steam, PSN, Nintendo, XBOX and other platform accounts, but looking a player up " +
            "by those IDs is not supported.")
        {
            playFabIdOption,
        };

        command.SetAction((ParseResult parseResult, CancellationToken cancellationToken) =>
            WithClientAsync(parseResult, cancellationToken, (client, output, stdout, stderr, cancellationToken) =>
                RunGetAccountAsync(
                    client,
                    parseResult.GetValue(playFabIdOption),
                    output, stdout, stderr,
                    cancellationToken)));

        return command.WithMetadata(new CommandMetadata(ApiPaths: [GetUserAccountInfoPath]));
    }

    /// <summary>
    /// Action for <c>player account get</c>: reads the account for <paramref name="playFabId"/>
    /// via <c>/Admin/GetUserAccountInfo</c>.
    /// </summary>
    internal static async Task<int> RunGetAccountAsync(
        PlayFabApiClient client,
        string? playFabId,
        OutputOptions output,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        // Blank is treated as absent rather than forwarded, so an empty flag value is not sent to
        // PlayFab as a real lookup key.
        if (string.IsNullOrWhiteSpace(playFabId))
        {
            stderr.WriteLine("Provide the player's PlayFab ID with --playfab-id.");
            return 1;
        }

        var request = new LookupUserAccountInfoRequest { PlayFabId = playFabId.Trim() };

        return await RunAsync(client, GetUserAccountInfoPath, request, output, stdout, stderr, cancellationToken: cancellationToken);
    }
}
