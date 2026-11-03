// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;

namespace XboxDevCLI.E2ETest;

public class GameTests
{
    private static E2ESettings Settings => E2ESettings.Current;

    // With a Store ID, the CLI stops at the first page that has the game, and with only a title ID the
    // service usually looks the game up directly. 'game list' and 'game get --keyword' read every page
    // of the seller's games, 100 at a time, so only one test runs each.
    [LiveFact]
    public async Task Get_ByStoreId_ReturnsTheGame() =>
        CheckIsTheGame(await Cli.RunForSellerAsync("game", "get", "--store-id", Settings.StoreId));

    [LiveFact]
    public async Task Get_ByTitleIdAndStoreId_ReturnsTheGame() =>
        CheckIsTheGame(await Cli.RunForSellerAsync("game", "get", Settings.TitleId, "--store-id", Settings.StoreId));

    [LiveFact]
    public async Task Get_ByTitleId_ReturnsTheGame() =>
        CheckIsTheGame(await Cli.RunForSellerAsync("game", "get", Settings.TitleId));

    [LiveFact]
    public async Task Get_ByKeyword_ReturnsTheGame() =>
        CheckIsTheGame(await Cli.RunForSellerAsync("game", "get", "--keyword", Settings.GameKeyword));

    [LiveFact]
    public async Task Get_ByStoreId_ShowsTheGameInATable()
    {
        CliResult result = await Cli.RunAsync(
            "game", "get", "--store-id", Settings.StoreId, "--seller-id", Settings.SellerId, "--output", "table");

        result.Check(result.ExitCode == 0, "Expected exit code 0.");
        string[] lines = result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        result.Check(
            lines.Length == 3 && lines[0].StartsWith("TITLE ID", StringComparison.Ordinal) && lines[1].StartsWith('-'),
            "Expected a header, its underline, and one row.");
        result.Check(
            lines[2].StartsWith(Settings.TitleId, StringComparison.Ordinal) &&
                lines[2].Contains(Settings.StoreId, StringComparison.OrdinalIgnoreCase),
            $"Expected a row for title ID {Settings.TitleId} with Store ID {Settings.StoreId}.");
    }

    [LiveFact]
    public async Task List_ReadsEveryPage()
    {
        CliResult result = await Cli.RunForSellerAsync("game", "list");

        JsonElement[] games = result.Json().Items("games");
        string[] repeated =
        [
            .. games.Select(game => game.Text("storeId")).OfType<string>()
                .GroupBy(storeId => storeId, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key),
        ];
        result.Check(repeated.Length == 0, $"Expected each game once, but these Store IDs repeat: {string.Join(", ", repeated)}.");
        CheckIsTheGame(result, [.. games.Where(game => game.Is("storeId", Settings.StoreId))]);
    }

    [LiveFact]
    public async Task JobGet_ShowsTheSucceededCreationJob()
    {
        CliResult result = await Cli.RunForSellerAsync("game", "job", "get", Settings.GameJobId);

        JsonElement job = result.Json();
        result.Check(job.Is("jobId", Settings.GameJobId), $"Expected job {Settings.GameJobId}.");
        result.Check(job.Is("status", "completed") && job.Is("result", "succeeded"), "Expected a job that succeeded.");
    }

    private static void CheckIsTheGame(CliResult result) => CheckIsTheGame(result, result.Json().Items("games"));

    private static void CheckIsTheGame(CliResult result, JsonElement[] games)
    {
        result.Check(games.Length == 1, "Expected exactly one game.");
        result.Check(games[0].Is("storeId", Settings.StoreId), $"Expected Store ID {Settings.StoreId}.");
        result.Check(
            games[0].Text("xboxTitleId") == Settings.TitleId,
            $"Expected XBOX title ID {Settings.TitleId}, as eight uppercase hexadecimal digits.");
        result.Check(games[0].IsGuid("SCID", Settings.Scid), $"Expected SCID {Settings.Scid}.");
    }
}
