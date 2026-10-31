// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using XboxDevCLI.CommandLine;

namespace XboxDevCLI.PlayFab.Commands;

/// <summary>
/// Creates <c>playfab api</c>, the raw passthrough to any PlayFab API operation. Like
/// <c>gh api &lt;endpoint&gt;</c>, it takes the path directly rather than through an action.
/// </summary>
/// <remarks>
/// Finding an operation and its request object is left to PlayFab's own REST reference and
/// Swagger specs, which Help points to.
/// </remarks>
internal static class ApiCommandFactory
{
    private const string RestReferenceUrl = "https://learn.microsoft.com/rest/api/playfab/";
    private const string SwaggerSpecsUrl = "https://github.com/PlayFab/API_Specs/tree/master/Swagger/PlayFab";

    /// <param name="isMsysRoot">
    /// Whether a directory is an MSYS install directory, for explaining a path Git Bash converted.
    /// Defaults to <see cref="MsysPathConversion.IsMsysRoot"/>; tests substitute their own.
    /// </param>
    public static Command Create(Func<string, bool>? isMsysRoot = null)
    {
        isMsysRoot ??= MsysPathConversion.IsMsysRoot;

        var apiPathArgument = new Argument<string>("api-path")
        {
            Description = "The PlayFab API path, /<Prefix>/<Operation>, e.g. /Admin/GetTitleData or /Catalog/SearchItems",
        };
        apiPathArgument.Validators.Add(result =>
        {
            if (result.Tokens.Count > 0 && !result.Tokens[0].Value.StartsWith('/'))
            {
                var value = result.Tokens[0].Value;
                var message = $"The API path must start with '/', such as /Admin/GetTitleData; got '{value}'.";

                // Still refused: the conversion can't always be reversed, so the path the user
                // meant is only suggested, never called.
                if (MsysPathConversion.TryGetOriginalPath(value, isMsysRoot, out var originalPath))
                {
                    message += Environment.NewLine
                        + $"That looks like {originalPath} after Git Bash converted it to a Windows path. "
                        + "Turn the conversion off for the command, for example:" + Environment.NewLine
                        + $"  MSYS_NO_PATHCONV=1 xbox playfab api {ShellQuoting.Quote(originalPath, powerShell: false)}";
                }

                result.AddError(message);
            }
        });

        var bodyOption = new Option<string?>("--body")
        {
            Description = "The operation's JSON request object (inline). Defaults to {} if neither --body nor --body-file is provided.",
        };

        var bodyFileOption = new Option<string?>("--body-file")
        {
            Description = "Path to a file containing the JSON request object, or - to read UTF-8 from stdin until EOF.",
        };

        var command = new Command("api", "Call any PlayFab API endpoint directly")
        {
            apiPathArgument,
            bodyOption,
            bodyFileOption,
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var apiPath = parseResult.GetValue(apiPathArgument)!;
            var body = parseResult.GetValue(bodyOption);
            var bodyFile = parseResult.GetValue(bodyFileOption);

            return await Program.InvokeApiAsync(parseResult, apiPath, body, bodyFile, cancellationToken);
        });

        // The path is an argument, so there is no effect to state in advance. Saying so is more
        // use than saying nothing: this is the one command that can reach any operation in the
        // API, including the Delete and Shutdown paths the runtime gate gets involved in, and it
        // is the command an agent exploring the surface is most likely to reach for.
        return command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.DependsOnApiPath,
            Scope: "Any PlayFab API operation on the selected title. The CLI calls with your Entra ID "
                + "credentials in place of a title secret key or title entity token, so operations "
                + "that accept only player or game-server entity tokens aren't authorized.",
            Output: "The PlayFab response envelope ({code, status, data}) as JSON; --data-only prints only data. "
                + "Errors go to stderr with exit code 1. --output table isn't available.",
            // Read-only operations with no dedicated command, and no inline JSON, which Windows
            // PowerShell 5.1 and cmd don't pass through intact.
            Examples: [
                "xbox playfab api /Catalog/GetCatalogConfig",
                "xbox playfab api /Match/ListMatchmakingQueues --data-only --output json-compact",
                "xbox playfab api /Catalog/SearchItems --body-file search.json",
            ],
            LearnMore: [
                "Find operations, request and response schemas, and required fields in",
                "PlayFab's documentation:",
                $"  REST API reference: {RestReferenceUrl}",
                "  Swagger specs:",
                $"    {SwaggerSpecsUrl}",
                "Spec files are named for API groups, not path segments, so search their",
                "paths: /Catalog is in Economy.swagger.json, and /Match is in",
                "Multiplayer.swagger.json.",
                "An operation's security field names the credential it accepts, or None if it",
                "needs none. The CLI stands in for SecretKey and a title EntityToken, not a",
                "player SessionTicket.",
            ]));
    }
}
