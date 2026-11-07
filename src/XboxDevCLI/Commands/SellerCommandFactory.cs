// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using Microsoft.Extensions.Configuration;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Services;

namespace XboxDevCLI.Commands;

internal static class SellerCommandFactory
{
    public static Command Create() =>
        Create(clientFactory: null);

    internal static Command Create(Func<IConfiguration, IXboxPartnerClient>? clientFactory)
    {
        var command = new Command(
            "seller",
            "List the Partner Center sellers available to the signed-in developer");
        command.Subcommands.Add(CreateListCommand(clientFactory));
        return command.Requires(CommandRequirement.Authentication).Calls(ServiceEndpoints.XboxPartner);
    }

    private static Command CreateListCommand(
        Func<IConfiguration, IXboxPartnerClient>? clientFactory)
    {
        var baseUrlOption = XboxPartnerCommandSupport.CreateBaseUrlOption();
        var command = new Command("list", "List sellers available to the signed-in developer");
        command.Options.Add(baseUrlOption);
        command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.ReadOnly,
            Scope: "The sellers associated with the signed-in Partner Center developer.",
            Output: "JSON by default; table output contains SELLER ID and NAME columns.",
            Examples:
            [
                "xbox seller list",
                "xbox seller list --output table",
            ],
            LearnMore:
            [
                "Save one for later commands with 'xbox context set seller-id <id>'.",
            ],
            Table: XboxPartnerCommandSupport.SellerTableView));

        command.SetAction((ParseResult parseResult, CancellationToken cancellationToken) =>
            ListAsync(parseResult, baseUrlOption, clientFactory, cancellationToken));
        return command;
    }

    private static async Task<int> ListAsync(
        ParseResult parseResult,
        Option<string?> baseUrlOption,
        Func<IConfiguration, IXboxPartnerClient>? clientFactory,
        CancellationToken cancellationToken)
    {
        try
        {
            var configuration = Program.GetConfiguration(parseResult);
            var baseUrl = XboxPartnerCommandSupport.ResolveBaseUrl(
                parseResult,
                baseUrlOption,
                configuration);
            using var client = clientFactory is null
                ? new XboxPartnerClient(configuration, authenticationService: Program.GetAuthentication(parseResult))
                : clientFactory(configuration);
            var result = await client.GetSellersAsync(baseUrl, cancellationToken);
            XboxPartnerCommandSupport.WriteSellerOutput(result.ResponseBody, parseResult);
            return 0;
        }
        catch (XboxPartnerApiException exception)
        {
            XboxPartnerCommandSupport.WriteGatewayError(
                exception,
                parseResult);
            return 1;
        }
        catch (InvalidOperationException exception)
        {
            return XboxPartnerCommandSupport.WriteLocalError(exception);
        }
        catch (InvalidDataException exception)
        {
            return XboxPartnerCommandSupport.WriteLocalError(exception);
        }
        catch (IOException exception)
        {
            return XboxPartnerCommandSupport.WriteLocalError(exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            return XboxPartnerCommandSupport.WriteLocalError(exception);
        }
        catch (HttpRequestException exception)
        {
            return XboxPartnerCommandSupport.WriteLocalError(exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            return XboxPartnerCommandSupport.WriteLocalError(
                new TimeoutException("The request timed out.", exception));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return XboxPartnerCommandSupport.WriteLocalError(exception);
        }
    }
}
