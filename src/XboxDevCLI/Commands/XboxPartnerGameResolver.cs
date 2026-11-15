// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using XboxDevCLI.Services;

namespace XboxDevCLI.Commands;

/// <summary>
/// Turns what a command was given, a Store ID or an XBOX title ID, into the 12-character Store ID
/// that Partner Center's package and playtest routes need. A title ID is looked up with the same
/// call as <c>xbox game get</c>.
/// </summary>
internal static partial class XboxPartnerGameResolver
{
    /// <summary>The <c>--store-id</c> and <c>--xbox-title-id</c> options, exactly one of which chooses the game.</summary>
    internal sealed record GameOptions(Option<string?> StoreId, Option<string?> TitleId);

    internal static GameOptions CreateGameOptions()
    {
        var storeId = new Option<string?>("--store-id")
        {
            Description = $"The game's Store ID. {StoreIdFormat} Use this or --xbox-title-id.",
        };
        storeId.Validators.Add(result =>
        {
            if (result.Tokens.Count > 0
                && !TryNormalizeStoreId(result.Tokens[0].Value, out _))
            {
                result.AddError(
                    $"Invalid Store ID '{result.Tokens[0].Value}'. {StoreIdFormat}");
            }
        });
        var titleId = new Option<string?>("--xbox-title-id")
        {
            Description = "The game's XBOX title ID, not its PlayFab title ID. Resolved to its Store ID "
                + "with the same lookup as 'xbox game get'. Use this or --store-id.",
        };
        titleId.Validators.Add(result =>
        {
            if (result.Tokens.Count > 0
                && !XboxTitleId.TryNormalize(result.Tokens[0].Value, out _))
            {
                result.AddError(
                    $"Invalid XBOX title ID '{result.Tokens[0].Value}'. {TitleIdFormat}");
            }
        });
        return new GameOptions(storeId, titleId);
    }

    internal static void RequireOneGame(CommandResult result, GameOptions game)
    {
        var hasStore = result.GetResult(game.StoreId) is not null;
        var hasTitle = result.GetResult(game.TitleId) is not null;
        if (hasStore == hasTitle)
        {
            result.AddError("Pass exactly one of --store-id or --xbox-title-id.");
        }
    }

    /// <summary>
    /// The Store ID given with <c>--store-id</c>, or the one game of <paramref name="sellerId"/> with
    /// the <c>--xbox-title-id</c> given.
    /// </summary>
    internal static async Task<string> ResolveStoreIdAsync(
        ParseResult result,
        GameOptions game,
        IXboxPartnerClient client,
        string baseUrl,
        string sellerId,
        CancellationToken cancellationToken)
    {
        if (result.GetValue(game.StoreId) is { } given)
        {
            TryNormalizeStoreId(given, out var storeId);
            return storeId;
        }

        XboxTitleId.TryNormalize(result.GetValue(game.TitleId)!, out var titleId);
        return await ResolveTitleIdAsync(client, baseUrl, sellerId, titleId!, cancellationToken);
    }

    internal const string StoreIdFormat =
        "A Store ID is 12 letters and digits, such as 9N0000000000.";

    [GeneratedRegex("^[0-9A-Za-z]{12}$")]
    private static partial Regex StoreIdPattern();

    internal static bool TryNormalizeStoreId(string? value, out string storeId)
    {
        var trimmed = value?.Trim();
        if (trimmed is not null && StoreIdPattern().IsMatch(trimmed))
        {
            storeId = trimmed.ToUpperInvariant();
            return true;
        }

        storeId = string.Empty;
        return false;
    }

    internal const string TitleIdFormat =
        "An XBOX title ID is 1 to 8 hex digits, with an optional 0x prefix, such as 1234ABCD.";

    /// <summary>
    /// The one Store ID of the seller's game with <paramref name="titleId"/>. A title ID can
    /// identify several games, and none of them is guessed: the failure names them so the caller
    /// can pick one with <c>--store-id</c>.
    /// </summary>
    internal static async Task<string> ResolveTitleIdAsync(
        IXboxPartnerClient client,
        string baseUrl,
        string sellerId,
        string titleId,
        CancellationToken cancellationToken)
    {
        string json;
        using (RequestProgress.Start($"Finding the game for XBOX title ID {titleId}"))
        {
            json = await client.GetGamesAsync(
                baseUrl, sellerId, titleId: titleId, cancellationToken: cancellationToken);
        }

        return PickStoreId(json, titleId);
    }

    internal static string PickStoreId(string gamesJson, string titleId)
    {
        var storeIds = new SortedSet<string>(StringComparer.Ordinal);
        using var document = JsonDocument.Parse(gamesJson);
        if (document.RootElement.TryGetProperty("games", out var games)
            && games.ValueKind == JsonValueKind.Array)
        {
            foreach (var game in games.EnumerateArray())
            {
                if (game.ValueKind == JsonValueKind.Object
                    && game.TryGetProperty("xboxTitleId", out var title)
                    && string.Equals(title.GetString(), titleId, StringComparison.OrdinalIgnoreCase)
                    && game.TryGetProperty("storeId", out var store)
                    && store.GetString() is { Length: > 0 } id)
                {
                    // The Store ID goes into request paths and recovery commands, so a malformed one is refused.
                    if (!TryNormalizeStoreId(id, out var storeId))
                    {
                        throw new InvalidOperationException(
                            $"The game with XBOX title ID {titleId} has a Store ID that isn't valid: "
                            + $"'{XboxPartnerCommandSupport.ForTerminal(id)}'. {StoreIdFormat} Pass --store-id instead.");
                    }

                    storeIds.Add(storeId);
                }
            }
        }

        switch (storeIds.Count)
        {
            case 1:
                return storeIds.Min!;
            case 0:
                throw new InvalidOperationException(
                    $"No game with XBOX title ID {titleId} was found for this seller. "
                    + "Check the XBOX title ID and --seller-id, or pass --store-id.");
            default:
                throw new InvalidOperationException(
                    $"XBOX title ID {titleId} identifies {storeIds.Count} games "
                    + $"({string.Join(", ", storeIds)}). Pass one of them with --store-id.");
        }
    }
}
