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
        var target = new XboxLiveConfigurationTarget();

        var command = new Command(
            "list",
            "List working and published configuration state for each accessible sandbox");
        command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.ReadOnly,
            Scope: "One effective shared XBOX services configuration across all accessible sandboxes.",
            Output:
                "A JSON array by default. Table output shows working and published versions, " +
                "their associated commit times, and whether working changes are unpublished. " +
                ListProgress.HelpOutput,
            Examples:
            [
                "xbox xbl config list --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
                "xbox xbl config list --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --output table",
                "xbox xbl config list --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --output json-compact",
                "xbox xbl config list --title-id 1234ABCD --output table",
            ],
            LearnMore:
            [
                "Reads the sandboxes a page at a time and prints them all; a failed request prints no partial results.",
                "Published commit time identifies the configuration commit, not the exact time publishing completed.",
            ],
            Table: XboxPartnerCommandSupport.ConfigurationTableView));
        sellerOption.AddTo(command);
        target.AddTo(command);
        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var output = OutputOptions.From(parseResult);
            var verbose = parseResult.GetValue(Program.VerboseOption);
            try
            {
                var configuration = Program.GetConfiguration(parseResult);
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
                var selected = await target.SelectAsync(parseResult, sellerId);

                Program.WriteVerbose(
                    verbose,
                    () => $"List XBOX services configuration state for {selected.Describe()}");
                return await RunAsync(
                    client,
                    selected.SellerId,
                    selected.Scid,
                    output,
                    Console.Out,
                    Console.Error,
                    cancellationToken,
                    selected.TitleId,
                    verbose).ConfigureAwait(false);
            }
            catch (XboxPartnerApiException ex)
            {
                Console.Error.WriteLine(XboxPartnerCommandErrorFormatter.Format(ex, output.IsCompact));
                return 1;
            }
            catch (XboxPartnerResponseException ex)
            {
                XboxPartnerCommandSupport.WriteCorrelationId(ex.CorrelationId, Console.Error);
                return XboxPartnerCommandSupport.WriteLocalError(ex);
            }
            catch (Exception ex) when (!XboxPartnerCommandSupport.IsCallerCancellation(ex, cancellationToken))
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
        Guid? scid,
        OutputOptions output,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken,
        string? titleId = null,
        bool verbose = false)
    {
        try
        {
            XboxLiveResolvedConfigurationTarget? resolved = null;
            string response;
            using (var progress = ListProgress.Start("Reading configurations", stderr))
            {
                response = await client
                    .GetXboxLiveConfigurationsAsync(
                        sellerId, scid, cancellationToken, titleId,
                        target => resolved = target, progress.PageRead)
                    .ConfigureAwait(false);
            }

            if (verbose && resolved is not null)
            {
                XboxLiveConfigurationTarget.WriteResolved(resolved, sellerId, stderr);
            }
            stdout.WriteLine(output.TableView is not null
                ? XboxPartnerTableFormatter.FormatConfigurations(response)
                : PlayFabApiClient.FormatJson(response, output.IsCompact));
            return 0;
        }
        catch (XboxPartnerApiException ex)
        {
            stderr.WriteLine(XboxPartnerCommandErrorFormatter.Format(ex, output.IsCompact));
            return 1;
        }
        catch (XboxPartnerResponseException ex)
        {
            XboxPartnerCommandSupport.WriteCorrelationId(ex.CorrelationId, stderr);
            stderr.WriteLine($"Request failed: {ex.Message}");
            return 1;
        }
        catch (Exception ex) when (!XboxPartnerCommandSupport.IsCallerCancellation(ex, cancellationToken))
        {
            stderr.WriteLine($"Request failed: {ex.Message}");
            return 1;
        }
    }
}
