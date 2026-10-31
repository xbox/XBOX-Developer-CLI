// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using XboxDevCLI.Output;
using XboxDevCLI.CommandLine;
using System.Text.Json;
using XboxDevCLI.PlayFab.Generated.Admin;
using XboxDevCLI.Services;
using static XboxDevCLI.PlayFab.Commands.PlayFabCommandHelper;

namespace XboxDevCLI.PlayFab.Commands;

/// <summary>
/// Creates the <c>playfab player-data</c> command group: reading and writing a player's custom
/// data. The <c>get</c>, <c>set</c>, and <c>delete</c> commands together cover all twelve
/// underlying Admin user-data endpoints via <c>--visibility</c> (read/write tier) and
/// <c>--publisher</c> (title vs. publisher scope):
/// <list type="bullet">
///   <item><c>client</c> — readable and writable by the game client (<c>GetUserData</c>).</item>
///   <item><c>read-only</c> — client-readable, server-writable (<c>GetUserReadOnlyData</c>).</item>
///   <item><c>internal</c> — server/admin only (<c>GetUserInternalData</c>).</item>
/// </list>
/// The commands do more than wrap the API: <c>set</c> accepts a single <c>--key K --value V</c>
/// pair (no JSON needed, value stored verbatim), <c>get --key K --value-only</c> writes just the
/// bare value to stdout, and <c>delete</c> is a dedicated verb for removing keys.
/// </summary>
internal static class PlayerDataCommandFactory
{
    /// <summary>
    /// The <c>--visibility</c> values and the API-path fragment each selects. One table rather
    /// than a list plus a parallel switch, so the accepted values and the paths they reach cannot
    /// disagree.
    /// </summary>
    private static readonly (string Visibility, string Tier)[] VisibilityTiers =
    [
        ("client", ""),
        ("read-only", "ReadOnly"),
        ("internal", "Internal"),
    ];

    /// <summary>
    /// The API-path fragment <c>--publisher</c> selects: the empty string for the title's own
    /// data, and <c>Publisher</c> for the data shared across the publisher's titles.
    /// </summary>
    private static readonly string[] Scopes = ["", "Publisher"];

    private static readonly string[] Visibilities =
        [.. VisibilityTiers.Select(entry => entry.Visibility)];

    /// <summary>
    /// Every path <c>get</c> can reach across <c>--visibility</c> and <c>--publisher</c>. Built
    /// from the same shape <see cref="TryResolvePaths"/> uses, so the paths Help advertises are
    /// the paths the command calls; the guard test then checks each one against the bundled
    /// PlayFab metadata, which is what catches a wrong shape.
    /// </summary>
    internal static readonly IReadOnlyList<string> GetPaths = BuildPaths("Get");

    /// <summary>
    /// Every path the writing verbs can reach. <c>delete</c> shares this list because it removes
    /// keys through <c>UpdateUser*Data</c> rather than a <c>Delete*</c> operation.
    /// </summary>
    internal static readonly IReadOnlyList<string> SetPaths = BuildPaths("Update");

    private const string TierLearnMore =
        "--visibility and --publisher select which of these the command calls.";

    private static string BuildPath(string verb, string scope, string tier) =>
        $"/Admin/{verb}User{scope}{tier}Data";

    private static IReadOnlyList<string> BuildPaths(string verb) =>
        [.. Scopes.SelectMany(scope =>
            VisibilityTiers.Select(entry => BuildPath(verb, scope, entry.Tier)))];

    public static Command Create()
    {
        var data = new Command("player-data", "Manage title- or publisher-scoped player data");
        data.Subcommands.Add(CreateGetCommand());
        data.Subcommands.Add(CreateSetCommand());
        data.Subcommands.Add(CreateDeleteCommand());
        return data;
    }

    private static Option<string?> VisibilityOption() =>
        OptionalOption("--visibility", "Data tier: 'client' (default), 'read-only', or 'internal'.");

    private static Option<bool> PublisherOption() =>
        new("--publisher") { Description = "Target publisher-scoped data (shared across the publisher's titles) instead of title data." };

    #region get

    private static Command CreateGetCommand()
    {
        var playFabIdOption = PlayFabIdRequired();
        var visibilityOption = VisibilityOption();
        var publisherOption = PublisherOption();
        var keysOption = OptionalOption("--keys", "Optional: comma-separated list of specific keys to return (defaults to all). Mutually exclusive with --key.");
        var keyOption = OptionalOption("--key", "Optional: a single key to return. Use with --value-only to print just the value.");
        var valueOnlyOption = new Option<bool>("--value-only")
        {
            Description = "With --key, write just that key's value to stdout (no JSON envelope). Pipe-friendly. Cannot be combined with --if-changed-from-version.",
        };
        var ifChangedOption = new Option<double?>("--if-changed-from-version")
        {
            Description = "Optional: only return data if the current version differs from this value.",
        };

        var command = new Command("get", "Get a player's custom data.")
        {
            playFabIdOption,
            visibilityOption,
            publisherOption,
            keysOption,
            keyOption,
            valueOnlyOption,
            ifChangedOption,
        };

        command.SetAction((ParseResult parseResult, CancellationToken cancellationToken) =>
            WithClientAsync(parseResult, cancellationToken, (client, output, stdout, stderr, cancellationToken) =>
                RunGetAsync(
                    client,
                    parseResult.GetValue(playFabIdOption)!,
                    parseResult.GetValue(visibilityOption),
                    parseResult.GetValue(publisherOption),
                    parseResult.GetValue(keysOption),
                    parseResult.GetValue(keyOption),
                    parseResult.GetValue(valueOnlyOption),
                    parseResult.GetValue(ifChangedOption),
                    output, stdout, stderr,
                    cancellationToken)));

        return command.WithMetadata(new CommandMetadata(
            ApiPaths: GetPaths,
            LearnMore: [TierLearnMore]));
    }

    /// <summary>
    /// Action for <c>player-data get</c>. Resolves the endpoint from <c>--visibility</c>/
    /// <c>--publisher</c>; with <c>--value-only</c> it returns the bare value for a single
    /// <c>--key</c>, otherwise it returns the full response for the requested key(s) (or all keys).
    /// </summary>
    internal static async Task<int> RunGetAsync(
        PlayFabApiClient client, string playFabId, string? visibility, bool publisher,
        string? keysCsv, string? key, bool valueOnly, double? ifChanged,
        OutputOptions output, TextWriter stdout, TextWriter stderr,
        CancellationToken cancellationToken)
    {
        if (!TryResolvePaths(visibility, publisher, out var getPath, out _, out _, out var error))
        {
            stderr.WriteLine(error);
            return 1;
        }

        // Normalize blank values to "absent": --key "" and --keys "  " are treated as not
        // provided (and the key is trimmed), so we never send an empty key to PlayFab.
        var normalizedKey = string.IsNullOrWhiteSpace(key) ? null : key.Trim();
        var hasKeys = !string.IsNullOrWhiteSpace(keysCsv);

        if (normalizedKey is not null && hasKeys)
        {
            stderr.WriteLine("Use --key or --keys, not both.");
            return 1;
        }

        if (valueOnly)
        {
            if (normalizedKey is null)
            {
                stderr.WriteLine("--value-only requires --key.");
                return 1;
            }

            // --if-changed-from-version can legitimately make PlayFab withhold the data (when the
            // version matches), which value-only can't distinguish from a missing key. Reject the
            // combination rather than silently dropping the filter or printing a misleading error.
            if (ifChanged.HasValue)
            {
                stderr.WriteLine("--value-only cannot be combined with --if-changed-from-version.");
                return 1;
            }

            return await RunGetValueAsync(client, getPath, playFabId, normalizedKey, output.Format, stdout, stderr, cancellationToken);
        }

        var keys = normalizedKey is not null ? new List<string> { normalizedKey } : ParseCsv(keysCsv);
        var request = BuildGetRequest(playFabId, keys, ifChanged);
        return await RunAsync(client, getPath, request, output, stdout, stderr, cancellationToken: cancellationToken);
    }

    private static async Task<int> RunGetValueAsync(
        PlayFabApiClient client, string getPath, string playFabId, string key,
        string outputFormat, TextWriter stdout, TextWriter stderr,
        CancellationToken cancellationToken)
    {
        var request = BuildGetRequest(playFabId, new List<string> { key }, null);
        var body = JsonSerializer.Serialize(request, RequestOptions);

        try
        {
            var response = await client.CallApiAsync(getPath, body, cancellationToken: cancellationToken);
            if (TryExtractDataValue(response, key, out var value))
            {
                stdout.WriteLine(value);
                return 0;
            }

            stderr.WriteLine($"Key '{key}' not found for the player.");
            return 1;
        }
        catch (PlayFabApiException ex)
        {
            var compact = outputFormat.Equals("json-compact", StringComparison.OrdinalIgnoreCase);
            stderr.WriteLine(Program.FormatErrorOutput(ex, compact));
            return 1;
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"Request failed: {ex.Message}");
            return 1;
        }
    }

    #endregion

    #region set

    private static Command CreateSetCommand()
    {
        var playFabIdOption = PlayFabIdRequired();
        var visibilityOption = VisibilityOption();
        var publisherOption = PublisherOption();
        var keyOption = OptionalOption("--key", "The single custom data key to set (use with --value).");
        var valueOption = OptionalOption("--value", "The value to set for --key. Stored verbatim (may contain any characters).");
        var dataOption = OptionalOption("--data", "A JSON object to set multiple keys at once, e.g. '{\"level\":\"5\",\"profile\":{\"name\":\"Bob\"}}'. Object/array values are stored as JSON strings. Mutually exclusive with --key/--value.");
        var permissionOption = OptionalOption("--permission",
            "Required, except with '--visibility internal', where it isn't valid. Who can read the keys: " +
            "'Private' (only the player) or 'Public' (other players too). Replaces the permission of every key written.");

        var command = new Command("set", "Set a player's custom data from a --key/--value pair or a JSON object.")
        {
            playFabIdOption,
            visibilityOption,
            publisherOption,
            keyOption,
            valueOption,
            dataOption,
            permissionOption,
        };

        command.SetAction((ParseResult parseResult, CancellationToken cancellationToken) =>
            WithClientAsync(parseResult, cancellationToken, (client, output, stdout, stderr, cancellationToken) =>
                RunSetAsync(
                    client,
                    parseResult.GetValue(playFabIdOption)!,
                    parseResult.GetValue(visibilityOption),
                    parseResult.GetValue(publisherOption),
                    parseResult.GetValue(keyOption),
                    parseResult.GetValue(valueOption),
                    parseResult.GetValue(dataOption),
                    parseResult.GetValue(permissionOption),
                    output, stdout, stderr,
                    cancellationToken)));

        return command.WithMetadata(new CommandMetadata(
            ApiPaths: SetPaths,
            SafetyDetails:
                "PlayFab gives every key the request writes the same permission, replacing the one it had, " +
                "so a Public key set again with '--permission Private' can no longer be read by other players.",
            LearnMore: [TierLearnMore]));
    }

    /// <summary>
    /// Action for <c>player-data set</c>. Takes either a single <c>--key</c>/<c>--value</c> pair
    /// (value stored verbatim) or a <c>--data</c> JSON object (the two are mutually exclusive),
    /// and writes the data. Returns 1 (with an error on stderr) on any invalid input.
    /// </summary>
    /// <remarks>
    /// <c>--permission</c> is required for the <c>client</c> and <c>read-only</c> tiers and
    /// rejected for <c>internal</c>, which has no permission. PlayFab applies the request's
    /// <c>Permission</c> to every key it writes and treats an absent one as <c>Private</c>, so
    /// leaving it optional would turn a Public key Private whenever its value was set again.
    /// </remarks>
    internal static async Task<int> RunSetAsync(
        PlayFabApiClient client, string playFabId, string? visibility, bool publisher,
        string? key, string? value, string? dataJson, string? permissionRaw,
        OutputOptions output, TextWriter stdout, TextWriter stderr,
        CancellationToken cancellationToken)
    {
        if (!TryResolvePaths(visibility, publisher, out _, out var setPath, out var isInternal, out var pathError))
        {
            stderr.WriteLine(pathError);
            return 1;
        }

        var keyProvided = key is not null;
        var valueProvided = value is not null;
        var dataProvided = !string.IsNullOrWhiteSpace(dataJson);

        if ((keyProvided || valueProvided) && dataProvided)
        {
            stderr.WriteLine("Use --key/--value or --data, not both.");
            return 1;
        }

        if (keyProvided && string.IsNullOrWhiteSpace(key))
        {
            stderr.WriteLine("--key must not be blank.");
            return 1;
        }

        if (keyProvided != valueProvided)
        {
            stderr.WriteLine("--key and --value must be used together.");
            return 1;
        }

        Dictionary<string, object>? data;
        if (dataProvided)
        {
            if (!TryParseDataObject(dataJson, out data, out var dataError))
            {
                stderr.WriteLine(dataError);
                return 1;
            }
        }
        else if (keyProvided)
        {
            data = new Dictionary<string, object>(StringComparer.Ordinal) { [key!.Trim()] = value! };
        }
        else
        {
            data = null;
        }

        if (data is null || data.Count == 0)
        {
            stderr.WriteLine("Nothing to set: provide --key and --value, or --data.");
            return 1;
        }

        if (!TryParseEnum<UserDataPermission>("--permission", permissionRaw, out var permission, out var permError))
        {
            stderr.WriteLine(permError);
            return 1;
        }

        if (permission.HasValue && isInternal)
        {
            stderr.WriteLine("--permission is not valid for '--visibility internal' (internal data is server-only).");
            return 1;
        }

        if (!permission.HasValue && !isInternal)
        {
            stderr.WriteLine(
                "--permission is required: PlayFab replaces the permission of every key it writes. " +
                "Add '--permission Private' so only the player can read the keys, or '--permission Public' " +
                "so other players can read them too.");
            return 1;
        }

        if (isInternal)
        {
            var internalRequest = BuildUpdateInternalRequest(playFabId, data, null);
            return await RunAsync(client, setPath, internalRequest, output, stdout, stderr, cancellationToken: cancellationToken);
        }

        var request = BuildUpdateRequest(playFabId, data, null, permission);
        return await RunAsync(client, setPath, request, output, stdout, stderr, cancellationToken: cancellationToken);
    }

    #endregion

    #region delete

    private static Command CreateDeleteCommand()
    {
        var playFabIdOption = PlayFabIdRequired();
        var visibilityOption = VisibilityOption();
        var publisherOption = PublisherOption();
        var keysOption = RequiredOption("--keys", "Comma-separated list of keys to delete.");

        var command = new Command("delete", "Delete one or more keys from a player's custom data.")
        {
            playFabIdOption,
            visibilityOption,
            publisherOption,
            keysOption,
        };

        command.SetAction((ParseResult parseResult, CancellationToken cancellationToken) =>
            WithClientAsync(parseResult, cancellationToken, (client, output, stdout, stderr, cancellationToken) =>
                RunDeleteAsync(
                    client,
                    parseResult.GetValue(playFabIdOption)!,
                    parseResult.GetValue(visibilityOption),
                    parseResult.GetValue(publisherOption),
                    parseResult.GetValue(keysOption)!,
                    output, stdout, stderr,
                    cancellationToken)));

        return command.WithMetadata(new CommandMetadata(
            ApiPaths: SetPaths,
            SafetyDetails:
                "The named keys are removed through the update endpoint's KeysToRemove, so " +
                "--confirm does not apply; only the keys named in --keys are affected.",
            LearnMore: [TierLearnMore]));
    }

    /// <summary>
    /// Action for <c>player-data delete</c>: removes the given keys via the update endpoint's
    /// <c>KeysToRemove</c>. Returns 1 (with an error on stderr) on invalid input.
    /// </summary>
    /// <remarks>
    /// This verb is intentionally <b>not</b> gated behind <c>--confirm</c>: it routes through
    /// <c>UpdateUser*Data</c> (not a <c>Delete*</c>/<c>Shutdown*</c> path), so the catastrophic
    /// hard-gate in <see cref="PlayFabApiClient.CallApiAsync"/> does not apply. Removing explicitly
    /// named keys is a targeted operation, no more destructive than <c>set</c> overwriting an
    /// existing value (also ungated), so it deliberately relies on the read-only convention rather
    /// than requiring confirmation.
    /// </remarks>
    internal static async Task<int> RunDeleteAsync(
        PlayFabApiClient client, string playFabId, string? visibility, bool publisher, string keysCsv,
        OutputOptions output, TextWriter stdout, TextWriter stderr,
        CancellationToken cancellationToken)
    {
        if (!TryResolvePaths(visibility, publisher, out _, out var setPath, out var isInternal, out var pathError))
        {
            stderr.WriteLine(pathError);
            return 1;
        }

        var keys = ParseCsv(keysCsv);
        if (keys is null || keys.Count == 0)
        {
            stderr.WriteLine("--keys must contain at least one key.");
            return 1;
        }

        if (isInternal)
        {
            var internalRequest = BuildUpdateInternalRequest(playFabId, null, keys);
            return await RunAsync(client, setPath, internalRequest, output, stdout, stderr, cancellationToken: cancellationToken);
        }

        var request = BuildUpdateRequest(playFabId, null, keys, null);
        return await RunAsync(client, setPath, request, output, stdout, stderr, cancellationToken: cancellationToken);
    }

    #endregion

    #region Build helpers

    /// <summary>
    /// Resolves the get/set endpoint paths for a (<paramref name="visibility"/>,
    /// <paramref name="publisher"/>) combination. <paramref name="visibility"/> defaults to
    /// <c>client</c> when absent. Returns <c>false</c> with an <paramref name="error"/> for an
    /// unknown visibility value.
    /// </summary>
    internal static bool TryResolvePaths(
        string? visibility, bool publisher,
        out string getPath, out string setPath, out bool isInternal, out string? error)
    {
        getPath = string.Empty;
        setPath = string.Empty;
        isInternal = false;
        error = null;

        var value = string.IsNullOrWhiteSpace(visibility) ? "client" : visibility.Trim().ToLowerInvariant();
        var match = VisibilityTiers.FirstOrDefault(
            entry => string.Equals(entry.Visibility, value, StringComparison.Ordinal));

        if (match.Visibility is null)
        {
            error = $"Invalid value '{visibility}' for option '--visibility'. Allowed values: {string.Join(", ", Visibilities)}.";
            return false;
        }

        var scope = publisher ? "Publisher" : string.Empty;
        getPath = BuildPath("Get", scope, match.Tier);
        setPath = BuildPath("Update", scope, match.Tier);
        isInternal = value == "internal";
        return true;
    }

    /// <summary>
    /// Parses the <c>--data</c> JSON object into a string-valued dictionary. PlayFab custom-data
    /// values are strings, so each value is serialized to a string: JSON strings are stored
    /// verbatim, scalars (number/boolean) as their literal form, and complex values (objects and
    /// arrays) as their JSON text. A <c>null</c> value is rejected (ambiguous in a strings-only
    /// map — use <c>player-data delete</c> to remove keys). Returns <c>true</c> with a <c>null</c>
    /// result when the value is absent.
    /// </summary>
    internal static bool TryParseDataObject(
        string? json, out Dictionary<string, object>? result, out string? error)
    {
        result = null;
        error = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            return true;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            error = $"Invalid JSON for --data: {ex.Message}";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "Invalid --data: expected a JSON object of key/value pairs, e.g. '{\"level\":\"5\"}'.";
                return false;
            }

            var dict = new Dictionary<string, object>();
            foreach (var property in root.EnumerateObject())
            {
                var dataKey = property.Name;
                var element = property.Value;
                switch (element.ValueKind)
                {
                    case JsonValueKind.String:
                        // Stored unquoted (the raw text would keep the surrounding JSON quotes).
                        dict[dataKey] = element.GetString()!;
                        break;
                    case JsonValueKind.Null or JsonValueKind.Undefined:
                        error = $"Invalid --data: the value for key '{dataKey}' is null. Player data values are strings — " +
                                "provide a value, or use 'player-data delete' to remove the key.";
                        return false;
                    default:
                        // number/boolean -> literal (5, true); object/array -> JSON text ({...}, [...]).
                        dict[dataKey] = element.GetRawText();
                        break;
                }
            }

            result = dict;
            return true;
        }
    }

    /// <summary>
    /// Extracts <c>data.Data[key].Value</c> from a raw PlayFab response envelope. Returns
    /// <c>false</c> when the key is absent; the extracted value may itself be <c>null</c>.
    /// </summary>
    internal static bool TryExtractDataValue(string responseJson, string key, out string? value)
    {
        value = null;

        using var doc = JsonDocument.Parse(responseJson);
        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("Data", out var dataObj) ||
            dataObj.ValueKind != JsonValueKind.Object ||
            !dataObj.TryGetProperty(key, out var record) ||
            record.ValueKind != JsonValueKind.Object ||
            !record.TryGetProperty("Value", out var valueElement))
        {
            return false;
        }

        value = valueElement.GetString();
        return true;
    }

    internal static GetUserDataRequest BuildGetRequest(string playFabId, List<string>? keys, double? ifChanged) =>
        new()
        {
            PlayFabId = playFabId,
            Keys = keys,
            IfChangedFromDataVersion = ifChanged,
        };

    internal static UpdateUserDataRequest BuildUpdateRequest(
        string playFabId, Dictionary<string, object>? data, List<string>? keysToRemove, UserDataPermission? permission) =>
        new()
        {
            PlayFabId = playFabId,
            Data = data,
            KeysToRemove = keysToRemove,
            Permission = permission,
        };

    internal static UpdateUserInternalDataRequest BuildUpdateInternalRequest(
        string playFabId, Dictionary<string, object>? data, List<string>? keysToRemove) =>
        new()
        {
            PlayFabId = playFabId,
            Data = data,
            KeysToRemove = keysToRemove,
        };

    #endregion
}
