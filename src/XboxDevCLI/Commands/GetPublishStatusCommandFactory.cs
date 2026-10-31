// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Output;
using XboxDevCLI.Services;

namespace XboxDevCLI.Commands;

internal static class GetPublishStatusCommandFactory
{
    public static Command Create()
    {
        var sellerOption = new XdtsSellerOption();
        var scidOption = new Option<Guid>("--scid", "-s")
        {
            Description = "Service configuration ID. No default.",
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
        var sandboxOption = new Option<string>("--sandbox", "-d")
        {
            Description = "Case-sensitive destination sandbox.",
            Required = true,
        }.AsContextValue(ContextEndpoint.XboxPartner);
        sandboxOption.Validators.Add(result =>
        {
            string? value = result.GetValueOrDefault<string>();
            if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace))
            {
                result.AddError("--sandbox must be non-empty and cannot contain whitespace.");
            }
        });

        var command = new Command("status", "Get XBOX Live configuration publish status");
        command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.ReadOnly,
            Scope: "Publish status for one destination sandbox and SCID.",
            Output: "The publish status as JSON or a table.",
            Examples:
            [
                "xbox xbl publish status --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox RETAIL",
                "xbox xbl publish status --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox RETAIL --output table",
            ],
            Table: XboxPartnerCommandSupport.PublishStatusTableView));
        sellerOption.AddTo(command);
        command.Options.Add(scidOption);
        command.Options.Add(sandboxOption);
        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var output = OutputOptions.From(parseResult);
            var verbose = parseResult.GetValue(Program.VerboseOption);
            try
            {
                var scid = parseResult.GetValue(scidOption);
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
                    () => $"POST XboxPartner publish status endpoint for {scid:D}/{sandbox}");
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
        bool compact = output.IsCompact;
        TableView? tableView = output.TableView;
        try
        {
            string response = await client
                .GetXboxLivePublishStatusAsync(sellerId, scid, sandbox, cancellationToken)
                .ConfigureAwait(false);
            XboxLivePublishResponse result = ParseResponse(response);
            stdout.WriteLine(tableView is not null
                ? XboxPartnerTableFormatter.FormatRows(
                    tableView,
                    [
                        [
                            result.Status!,
                            sandbox,
                        ],
                    ])
                : PlayFabApiClient.FormatJson(response, compact));
            return 0;
        }
        catch (XboxPartnerApiException ex)
        {
            stderr.WriteLine(XboxPartnerCommandErrorFormatter.Format(ex, compact));
            return 1;
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"Request failed: {ex.Message}");
            return 1;
        }
    }

    internal static Task<int> RunAsync(
        XboxPartnerClient client,
        long? sellerId,
        Guid scid,
        string sandbox,
        string outputFormat,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken) =>
        RunAsync(
            client,
            sellerId,
            scid,
            sandbox,
            outputFormat.Equals("table", StringComparison.OrdinalIgnoreCase)
                ? OutputOptions.Table(XboxPartnerCommandSupport.PublishStatusTableView)
                : outputFormat.Equals("json-compact", StringComparison.OrdinalIgnoreCase)
                    ? OutputOptions.JsonCompact
                    : OutputOptions.Json,
            stdout,
            stderr,
            cancellationToken);

    private static XboxLivePublishResponse ParseResponse(string response)
    {
        XboxLivePublishResponse result =
            XboxPartnerCommandSupport.DeserializeResponse<XboxLivePublishResponse>(
                response,
                "publish status");
        if (string.IsNullOrWhiteSpace(result.Status))
        {
            throw new InvalidDataException(
                "The XboxPartner publish status response did not contain a status.");
        }

        return result;
    }
}
