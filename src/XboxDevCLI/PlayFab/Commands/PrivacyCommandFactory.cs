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
/// Creates the <c>playfab privacy</c> command group for master-player-account privacy operations:
/// <list type="bullet">
///   <item><c>export</c> — queues a full data export of a master player account via
///   <c>/Admin/ExportMasterPlayerData</c> and returns a job receipt id.</item>
///   <item><c>played-titles</c> — lists the titles a player has played via
///   <c>/Admin/GetPlayedTitleList</c>.</item>
/// </list>
/// Both endpoints take only a <c>--playfab-id</c>. <c>export</c> merely <em>queues</em> the export
/// (PlayFab emails a download link to the title's notification address when the dump is ready); the
/// bundled PlayFab metadata exposes no job-status endpoint, so there is no <c>--wait</c>/poll option.
/// </summary>
internal static class PrivacyCommandFactory
{
    private const string PlayFabExportGuideUrl =
        "https://learn.microsoft.com/gaming/playfab/data-analytics/privacy-compliance/gdpr-exporting-player-data";

    public static Command Create()
    {
        var privacy = new Command(
            "privacy",
            "Master-player-account privacy operations (data export, played-title list).");
        privacy.Subcommands.Add(CreateExportCommand());
        privacy.Subcommands.Add(CreatePlayedTitlesCommand());
        return privacy;
    }

    #region export

    private static Command CreateExportCommand()
    {
        var playFabIdOption = PlayFabIdRequired();

        var command = new Command(
            "export",
            "Queue a full export of a master player account's associated data; returns a job receipt id.")
        {
            playFabIdOption,
        };

        command.SetAction((ParseResult parseResult, CancellationToken cancellationToken) =>
            WithClientAsync(parseResult, cancellationToken, (client, output, stdout, stderr, cancellationToken) =>
                RunExportAsync(
                    client,
                    parseResult.GetValue(playFabIdOption)!,
                    output, stdout, stderr,
                    cancellationToken)));

        return command.WithMetadata(new CommandMetadata(
            ApiPaths: ["/Admin/ExportMasterPlayerData"],
            SafetyDetails:
                "The CLI doesn't return the data. When the export is ready, PlayFab emails a " +
                "download link to the notification email address configured for the title. The " +
                "export holds this player's data from every title of the publisher they played. " +
                "Once queued, the export and its email can't be recalled.",
            LearnMore:
            [
                "What the export holds, and who PlayFab sends it to: " + PlayFabExportGuideUrl,
            ]));
    }

    /// <summary>
    /// Action for <c>privacy export</c>: queues a master-player-data export via
    /// <c>/Admin/ExportMasterPlayerData</c> and writes the <c>JobReceiptId</c> result.
    /// </summary>
    /// <remarks>
    /// <c>ExportMasterPlayerData</c> is not in the catastrophic hard-gated set (only
    /// <c>Delete*</c>/<c>Shutdown*</c> are), so this deliberately does <b>not</b> pass
    /// <c>confirmedDestructive: true</c>; the export only queues a read-out of existing data and
    /// does not delete or mutate the account.
    /// </remarks>
    internal static async Task<int> RunExportAsync(
        PlayFabApiClient client, string playFabId,
        OutputOptions output, TextWriter stdout, TextWriter stderr,
        CancellationToken cancellationToken)
    {
        var request = new ExportMasterPlayerDataRequest { PlayFabId = playFabId };
        return await RunAsync(client, "/Admin/ExportMasterPlayerData", request, output, stdout, stderr, cancellationToken: cancellationToken);
    }

    #endregion

    #region played-titles

    private static Command CreatePlayedTitlesCommand()
    {
        var playFabIdOption = PlayFabIdRequired();

        var command = new Command(
            "played-titles",
            "List the titles a player has played.")
        {
            playFabIdOption,
        };

        command.SetAction((ParseResult parseResult, CancellationToken cancellationToken) =>
            WithClientAsync(parseResult, cancellationToken, (client, output, stdout, stderr, cancellationToken) =>
                RunPlayedTitlesAsync(
                    client,
                    parseResult.GetValue(playFabIdOption)!,
                    output, stdout, stderr,
                    cancellationToken)));

        return command.WithMetadata(new CommandMetadata(ApiPaths: ["/Admin/GetPlayedTitleList"]));
    }

    /// <summary>
    /// Action for <c>privacy played-titles</c>: lists the titles the player has played via
    /// <c>/Admin/GetPlayedTitleList</c>.
    /// </summary>
    internal static async Task<int> RunPlayedTitlesAsync(
        PlayFabApiClient client, string playFabId,
        OutputOptions output, TextWriter stdout, TextWriter stderr,
        CancellationToken cancellationToken)
    {
        var request = new GetPlayedTitleListRequest { PlayFabId = playFabId };
        return await RunAsync(client, "/Admin/GetPlayedTitleList", request, output, stdout, stderr, cancellationToken: cancellationToken);
    }

    #endregion
}
