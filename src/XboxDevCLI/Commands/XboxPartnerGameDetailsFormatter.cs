// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using XboxDevCLI.Output;

namespace XboxDevCLI.Commands;

internal static class XboxPartnerGameDetailsFormatter
{
    internal static DetailsFormatResult Format(string responseBody)
    {
        using var document = XboxPartnerCommandSupport.ParseGameResponse(responseBody);
        var games = document.RootElement
            .GetProperty("games")
            .EnumerateArray()
            .Select((game, index) => new GameEntry(game, index))
            .ToArray();
        if (games.Length == 0)
        {
            return new(TableFormatter.EmptyNotice, 0);
        }

        var groups = games
            .GroupBy(
                entry => entry.TitleId is not null
                    ? $"title:{entry.TitleId}"
                    : $"game:{entry.Index}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => new GameGroup(
                group.First().TitleId,
                group.Select(entry => entry.Game).ToArray()))
            .OrderBy(group => FormatTitleId(group.TitleId), StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => ReadDisplayValue(group.Games[0], "name"), StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var builder = new StringBuilder();
        foreach (var (group, groupIndex) in groups.Select((group, index) => (group, index)))
        {
            if (groupIndex > 0)
            {
                builder.AppendLine();
            }

            builder.Append("TITLE ID: ");
            builder.AppendLine(FormatTitleId(group.TitleId));

            var numbered = group.Games.Count > 1;
            foreach (var (game, gameIndex) in group.Games.Select((game, index) => (game, index)))
            {
                if (numbered)
                {
                    builder.Append(gameIndex + 1);
                    builder.Append(". ");
                }

                builder.AppendLine(ReadDisplayValue(game, "name"));
                AppendDetail(builder, "Store ID", game, "storeId");
                AppendDetail(builder, "SCID", game, "SCID");
                AppendDetail(builder, "Primary Development Sandbox", game, "primaryDevelopmentSandbox");
                AppendDetail(builder, "Package Identity Name", game, "packageIdentityName");
                AppendDetail(
                    builder,
                    "Package Identity Publisher",
                    game,
                    "packageIdentityPublisher");
                AppendDetail(builder, "Package Family Name", game, "packageFamilyName");
                AppendDetail(builder, "MSA App ID", game, "msaAppId");
            }
        }

        return new(builder.ToString().TrimEnd(), games.Length);
    }

    private static void AppendDetail(
        StringBuilder builder,
        string label,
        JsonElement game,
        string propertyName)
    {
        builder.Append("   ");
        builder.Append(label);
        builder.Append(": ");
        builder.AppendLine(ReadDisplayValue(game, propertyName));
    }

    private static string ReadDisplayValue(JsonElement game, string propertyName)
    {
        var value = TableFormatter.NormalizeCell(
            XboxPartnerCommandSupport.ReadString(game, propertyName)).Trim();
        return value.Length == 0 ? "-" : value;
    }

    private static string FormatTitleId(string? titleId) => titleId ?? "-";

    private sealed record GameEntry(JsonElement Game, int Index)
    {
        public string? TitleId { get; } = XboxPartnerTableFormatter.ReadTitleId(Game);
    }

    private sealed record GameGroup(string? TitleId, IReadOnlyList<JsonElement> Games);

    internal sealed record DetailsFormatResult(string Text, int ResultCount);
}
