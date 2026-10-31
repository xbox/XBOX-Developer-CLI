// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Output;
using XboxDevCLI.Services;

namespace XboxDevCLI.Commands;

internal static class GetConfigurationCommandFactory
{
    public static Command Create()
    {
        var sellerOption = new XdtsSellerOption();
        var scidOption = CreateRequiredGuidOption();
        var sandboxOption = new Option<string>("--sandbox", "-d")
        {
            Description = "The case-sensitive sandbox name.",
            Required = true,
        }.AsContextValue(ContextEndpoint.XboxPartner);
        sandboxOption.Validators.Add(result =>
        {
            string? value = result.GetValueOrDefault<string>();
            if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace))
            {
                result.AddError(
                    "--sandbox must be non-empty and cannot contain whitespace.");
            }
        });

        var command = new Command(
            "get",
            "Get working and published configuration state for one sandbox");
        command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.ReadOnly,
            Scope: "One XBOX Live title and sandbox.",
            Output:
                "One JSON object by default. Table output shows working and published versions, " +
                "their associated commit times, and whether working changes are unpublished.",
            Examples:
            [
                "xbox xbl configuration get --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox STAGE.0",
                "xbox xbl configuration get --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox STAGE.0 --output table",
            ],
            LearnMore:
            [
                "Published commit time identifies the configuration commit, not the exact time publishing completed.",
            ],
            Table: XboxPartnerCommandSupport.ConfigurationRecordTableView));
        sellerOption.AddTo(command);
        command.Options.Add(scidOption);
        command.Options.Add(sandboxOption);
        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var output = OutputOptions.From(parseResult);
            var verbose = parseResult.GetValue(Program.VerboseOption);
            try
            {
                Guid scid = parseResult.GetValue(scidOption);
                string sandbox = parseResult.GetValue(sandboxOption)!;
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
                    () => $"Get XBOX Live configuration state for {scid:D} in {sandbox}");
                return await RunAsync(
                    client,
                    sellerId,
                    scid,
                    sandbox,
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
        string sandbox,
        OutputOptions output,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        try
        {
            string response = await client
                .GetXboxLiveConfigurationAsync(
                    sellerId,
                    scid,
                    sandbox,
                    cancellationToken)
                .ConfigureAwait(false);
            stdout.WriteLine(output.TableView is not null
                ? XboxPartnerTableFormatter.FormatConfiguration(response)
                : PlayFabApiClient.FormatJson(response, output.IsCompact));
            return 0;
        }
        catch (XboxPartnerApiException ex)
        {
            stderr.WriteLine(XboxPartnerCommandErrorFormatter.Format(ex, output.IsCompact));
            return 1;
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"Request failed: {ex.Message}");
            return 1;
        }
    }

    private static Option<Guid> CreateRequiredGuidOption()
    {
        var option = new Option<Guid>("--scid", "-s")
        {
            Description = "The service configuration ID. No default.",
            Required = true,
        };
        option.Validators.Add(result =>
        {
            if (result.Tokens.Count > 0 &&
                (!Guid.TryParse(result.Tokens[0].Value, out Guid value) ||
                 value == Guid.Empty))
            {
                result.AddError("--scid must be a non-empty GUID.");
            }
        });
        return option;
    }
}
