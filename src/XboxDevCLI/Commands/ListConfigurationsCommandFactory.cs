// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Output;
using XboxDevCLI.Services;

namespace XboxDevCLI.Commands;

internal static class ListConfigurationsCommandFactory
{
    public static Command Create()
    {
        var sellerOption = new XdtsSellerOption();
        var scidOption = new Option<Guid>("--scid", "-s")
        {
            Description = "The service configuration ID. No default.",
            Required = true,
        };
        scidOption.Validators.Add(result =>
        {
            if (result.Tokens.Count > 0 &&
                (!Guid.TryParse(result.Tokens[0].Value, out Guid value) ||
                 value == Guid.Empty))
            {
                result.AddError("--scid must be a non-empty GUID.");
            }
        });

        var command = new Command(
            "list",
            "List working and published configuration state for each accessible sandbox");
        command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.ReadOnly,
            Scope: "One XBOX title identified by SCID across all accessible sandboxes.",
            Output:
                "A JSON array by default. Table output shows working and published versions, " +
                "their associated commit times, and whether working changes are unpublished. " +
                ListProgress.HelpOutput,
            Examples:
            [
                "xbox xbl configuration list --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
                "xbox xbl configuration list --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --output table",
                "xbox xbl configuration list --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --output json-compact",
            ],
            LearnMore:
            [
                "Reads the sandboxes a page at a time and prints them all; a failed request prints no partial results.",
                "Published commit time identifies the configuration commit, not the exact time publishing completed.",
            ],
            Table: XboxPartnerCommandSupport.ConfigurationTableView));
        sellerOption.AddTo(command);
        command.Options.Add(scidOption);
        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var output = OutputOptions.From(parseResult);
            var verbose = parseResult.GetValue(Program.VerboseOption);
            try
            {
                Guid scid = parseResult.GetValue(scidOption);
                var configuration = Program.BuildConfiguration(parseResult);
                using var client = Program.XboxPartnerClientFactory is null
                    ? new XboxPartnerClient(
                        configuration,
                        authenticationService: Program.GetAuthentication(parseResult))
                    : Program.XboxPartnerClientFactory(configuration);
                string baseUrl = XboxPartnerCommandSupport.ResolveBaseUrl(configuration);
                long? sellerId = await sellerOption.GetSellerIdAsync(
                    parseResult,
                    configuration,
                    client,
                    baseUrl,
                    cancellationToken);

                Program.WriteVerbose(
                    verbose,
                    () => $"List XBOX services configuration state for SCID {scid:D}");
                return await RunAsync(
                    client,
                    sellerId,
                    scid,
                    output,
                    Console.Out,
                    Console.Error,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Request failed: {ex.Message}");
                return 1;
            }
        });

        return command;
    }

    internal static async Task<int> RunAsync(
        XboxPartnerClient client,
        long? sellerId,
        Guid scid,
        OutputOptions output,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        try
        {
            string response;
            using (var progress = ListProgress.Start("Reading configurations", stderr))
            {
                response = await client
                    .GetXboxLiveConfigurationsAsync(sellerId, scid, cancellationToken, progress.PageRead)
                    .ConfigureAwait(false);
            }

            stdout.WriteLine(output.TableView is not null
                ? XboxPartnerTableFormatter.FormatConfigurations(response)
                : PlayFabApiClient.FormatJson(response, output.IsCompact));
            return 0;
        }
        catch (XboxPartnerApiException ex)
        {
            XboxPartnerCommandSupport.WriteCorrelationId(ex.CorrelationId, stderr);
            stderr.WriteLine(XboxPartnerCommandErrorFormatter.Format(ex, output.IsCompact));
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
}
