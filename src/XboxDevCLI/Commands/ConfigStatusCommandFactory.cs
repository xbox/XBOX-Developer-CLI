// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Output;
using XboxDevCLI.Services;

namespace XboxDevCLI.Commands;

/// <summary>Builds <c>xbox xbl config status</c>: one sandbox's configuration state and its last publish.</summary>
internal static class ConfigStatusCommandFactory
{
    /// <summary>The title-wide working set, which is published through HIDDEN and never publishes itself.</summary>
    private const string TitleWideWorkingSandbox = "ALL";

    public static Command Create()
    {
        var sellerOption = new XdtsSellerOption();
        var target = new XboxLiveConfigurationTarget();
        var sandboxOption = new Option<string>("--sandbox-id")
        {
            HelpName = "id",
            Description = "The case-sensitive sandbox ID.",
            Required = true,
        }.AsContextValue(ContextEndpoint.XboxPartner);
        sandboxOption.Validators.Add(result =>
        {
            string? value = result.GetValueOrDefault<string>();
            if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace))
            {
                result.AddError(
                    "--sandbox-id must be non-empty and cannot contain whitespace.");
            }
        });

        var command = new Command(
            "status",
            "Get one sandbox's working and published configuration state and its last publish");
        command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.ReadOnly,
            Scope: "One XBOX title and sandbox.",
            Output:
                "One JSON object by default, with the last publish under lastPublish as " +
                "status, jobId, and message, null when there's none. Table output shows working and published versions, " +
                "their associated commit times, whether working changes are unpublished, and " +
                "the last publish's status.",
            Examples:
            [
                "xbox xbl config status --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id XDKS.1",
                "xbox xbl config status --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id XDKS.1 --output table",
                "xbox xbl config status --title-id 1234ABCD --sandbox-id XDKS.1",
            ],
            LearnMore:
            [
                "Published commit time identifies the configuration commit, not the exact time publishing completed.",
                "Publishing is asynchronous: after config publish, run config status until lastPublish.status settles.",
            ],
            Table: XboxPartnerCommandSupport.ConfigurationStatusTableView));
        sellerOption.AddTo(command);
        target.AddTo(command);
        command.Options.Add(sandboxOption);
        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var output = OutputOptions.From(parseResult);
            var verbose = parseResult.GetValue(Program.VerboseOption);
            try
            {
                string sandbox = parseResult.GetValue(sandboxOption)!;
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
                    () => $"Get XBOX services configuration status for {selected.Describe()} in {sandbox}");
                return await RunAsync(
                    client,
                    selected.SellerId,
                    selected.Scid,
                    sandbox,
                    output,
                    Console.Out,
                    Console.Error,
                    cancellationToken,
                    selected.TitleId,
                    verbose).ConfigureAwait(false);
            }
            catch (XboxPartnerApiException ex) when (!cancellationToken.IsCancellationRequested)
            {
                Console.Error.WriteLine(XboxPartnerCommandErrorFormatter.Format(ex, output.IsCompact));
                return 1;
            }
            catch (XboxPartnerResponseException ex) when (!cancellationToken.IsCancellationRequested)
            {
                XboxPartnerCommandSupport.WriteCorrelationId(ex.CorrelationId, Console.Error);
                return XboxPartnerCommandSupport.WriteLocalError(ex);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
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
        string sandbox,
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
            string configurationResponse;
            string? publishResponse;
            using (RequestProgress.Start($"Getting configuration status for {sandbox}", stderr))
            {
                configurationResponse = await client
                    .GetXboxLiveConfigurationAsync(
                        sellerId,
                        scid,
                        sandbox,
                        cancellationToken,
                        titleId,
                        target => resolved = target)
                    .ConfigureAwait(false);
                // Pin the last-publish read to the target the configuration read resolved, as config
                // list pins later pages, so a binding change between the reads can't mix two configurations.
                publishResponse = resolved is { } pinned
                    ? await GetLastPublishAsync(
                        client,
                        sellerId ?? pinned.SellerId,
                        pinned.Scid,
                        null,
                        sandbox,
                        cancellationToken).ConfigureAwait(false)
                    : await GetLastPublishAsync(
                        client,
                        sellerId,
                        scid,
                        titleId,
                        sandbox,
                        cancellationToken).ConfigureAwait(false);
            }

            if (verbose && resolved is not null)
            {
                XboxLiveConfigurationTarget.WriteResolved(resolved, sellerId, stderr);
            }

            JsonObject? lastPublish = publishResponse is null ? null : ToLastPublish(publishResponse);
            stdout.WriteLine(output.TableView is not null
                ? XboxPartnerTableFormatter.FormatConfigurationStatus(
                    configurationResponse,
                    lastPublish?["status"]?.GetValue<string>())
                : PlayFabApiClient.FormatJson(
                    Combine(configurationResponse, lastPublish),
                    output.IsCompact));
            return 0;
        }
        catch (XboxPartnerApiException ex) when (!cancellationToken.IsCancellationRequested)
        {
            stderr.WriteLine(XboxPartnerCommandErrorFormatter.Format(ex, output.IsCompact));
            return 1;
        }
        catch (XboxPartnerResponseException ex) when (!cancellationToken.IsCancellationRequested)
        {
            XboxPartnerCommandSupport.WriteCorrelationId(ex.CorrelationId, stderr);
            stderr.WriteLine($"Request failed: {ex.Message}");
            return 1;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            stderr.WriteLine($"Request failed: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Reads the sandbox's last publish, or null when it has none: the title-wide working set
    /// never publishes itself, and a 404 means nothing was published to the sandbox.
    /// </summary>
    private static async Task<string?> GetLastPublishAsync(
        XboxPartnerClient client,
        long? sellerId,
        Guid? scid,
        string? titleId,
        string sandbox,
        CancellationToken cancellationToken)
    {
        if (string.Equals(sandbox, TitleWideWorkingSandbox, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            return await client
                .GetXboxLivePublishStatusAsync(sellerId, scid, sandbox, cancellationToken, titleId)
                .ConfigureAwait(false);
        }
        catch (XboxPartnerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static string Combine(string configurationResponse, JsonObject? lastPublish)
    {
        JsonObject status;
        try
        {
            status = JsonNode.Parse(configurationResponse) as JsonObject
                ?? throw new JsonException("The response was not an object.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The configuration response was not a valid JSON object.",
                exception);
        }

        status[XboxPartnerCommandSupport.ConfigurationStatusLastPublish] = lastPublish;
        return status.ToJsonString();
    }

    /// <summary>
    /// The last publish as <c>status</c>, <c>jobId</c>, and <c>message</c>, read from the publish
    /// status's <c>Status</c>, <c>JobId</c>, and <c>StatusMessage</c>. A blank field is null.
    /// </summary>
    private static JsonObject ToLastPublish(string publishResponse)
    {
        var publish = XboxPartnerCommandSupport.DeserializeResponse<PublishStatus>(
            publishResponse,
            "publish status");
        return new JsonObject
        {
            ["status"] = NullIfBlank(publish.Status),
            ["jobId"] = NullIfBlank(publish.JobId),
            ["message"] = NullIfBlank(publish.StatusMessage),
        };

        static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private sealed record PublishStatus(string? Status, string? JobId, string? StatusMessage);
}
