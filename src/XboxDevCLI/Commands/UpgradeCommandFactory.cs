// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Text.Json;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Output;
using XboxDevCLI.Services;

namespace XboxDevCLI.Commands;

internal static class UpgradeCommandFactory
{
    internal static Command Create(Func<HttpClient>? createClient = null, Func<string>? currentVersion = null)
    {
        var command = new Command("upgrade", "Check for a newer CLI release and show manual update instructions");
        command.Options.Add(new Option<bool>("--check")
        {
            Description = "Explicitly request a version check (also the default). Never installs an update.",
        });
        command.WithMetadata(new CommandMetadata(
            Category: HelpCategory.Support,
            Safety: CommandSafety.ReadOnly,
            Scope: "The running CLI version and the latest public stable GitHub release. No sign-in or title ID is required.",
            SafetyDetails: "Only reads release metadata. Does not download binaries, prompt, run an installer, or modify the installation.",
            Output: "JSON object (schemaVersion 1) with currentVersion, latestVersion, updateAvailable, releaseNotesUrl, message and manual instructions. Exit 0 means the check succeeded, even when an update is available.",
            Examples: ["xbox upgrade", "xbox upgrade --check --output json-compact"],
            LearnMore: [ProjectLinks.Releases, ProjectLinks.InstallationGuide]));
        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var output = OutputOptions.From(parseResult);
            if (output.IsTable)
            {
                Console.Error.WriteLine(Program.TableUnavailableMessage);
                return 1;
            }
            if (parseResult.GetValue(Program.ConfirmOption))
            {
                Console.Error.WriteLine("This command only checks for updates. Omit --confirm/--yes/-y; nothing will be installed.");
                return 1;
            }
            try
            {
                using var client = createClient?.Invoke() ?? new HttpClient(
                    new HttpClientHandler { AllowAutoRedirect = false })
                {
                    Timeout = CliUpdateChecker.CheckTimeout,
                };
                var result = await new CliUpdateChecker(client).CheckAsync(
                    currentVersion?.Invoke() ?? CliUpdateChecker.CurrentVersion, cancellationToken);
                Console.Out.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)
                {
                    WriteIndented = !output.IsCompact,
                }));
                return 0;
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine(cancellationToken.IsCancellationRequested
                    ? "Update check cancelled. No installation changes were made."
                    : "Update check timed out. Try again later; no installation changes were made.");
                return 1;
            }
            // JsonDocument.Parse(byte[]) accepts a well-formed JSON document even when a
            // string value contains invalid UTF-8 bytes; GetString() on that value is what
            // throws (InvalidOperationException), not the earlier parse call. ArgumentException
            // is included defensively, matching PlayFabApiClient's string-based JsonDocument.Parse
            // call, though the byte-array overload used here does not raise it in practice.
            catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException
                or ArgumentException or InvalidOperationException or InvalidDataException)
            {
                Console.Error.WriteLine($"Update check failed: {exception.Message}");
                return 1;
            }
        });
        return command;
    }
}
