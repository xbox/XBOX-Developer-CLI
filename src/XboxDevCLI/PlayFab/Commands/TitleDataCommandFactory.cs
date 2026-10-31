// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using XboxDevCLI.Output;
using XboxDevCLI.CommandLine;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using XboxDevCLI.PlayFab.Generated.Admin;
using XboxDevCLI.Services;

namespace XboxDevCLI.PlayFab.Commands;

/// <summary>
/// Creates the <c>playfab title-data</c> command group for reading, setting, and deleting
/// title-wide key/value data in the client-readable or internal tiers.
/// </summary>
internal static class TitleDataCommandFactory
{
    internal const string GetTitleDataPath = "/Admin/GetTitleData";
    internal const string GetTitleInternalDataPath = "/Admin/GetTitleInternalData";
    internal const string SetTitleDataPath = "/Admin/SetTitleData";
    internal const string SetTitleInternalDataPath = "/Admin/SetTitleInternalData";

    /// <summary>Every path <c>get</c> can reach, both tiers of <c>--internal</c>.</summary>
    internal static readonly IReadOnlyList<string> GetPaths =
        [GetTitleDataPath, GetTitleInternalDataPath];

    /// <summary>
    /// Every path the writing verbs can reach. <c>delete</c> shares this list because it removes
    /// keys by setting them to null rather than through a <c>Delete*</c> operation.
    /// </summary>
    internal static readonly IReadOnlyList<string> SetPaths =
        [SetTitleDataPath, SetTitleInternalDataPath];

    private const string TierLearnMore =
        "--internal selects the server-only internal tier; omit it for the client-readable tier.";

    private static readonly JsonSerializerOptions RequestOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions RequestOptionsIncludingNulls = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private static readonly JsonSerializerOptions SummaryOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static Command Create()
    {
        var command = new Command("title-data", "Manage title data key/value pairs");

        command.Subcommands.Add(CreateGetCommand());
        command.Subcommands.Add(CreateSetCommand());
        command.Subcommands.Add(CreateDeleteCommand());

        return command;
    }

    internal static (string apiPath, GetTitleDataRequest request) BuildGetCall(
        bool internalTier,
        List<string>? keys)
    {
        var request = new GetTitleDataRequest
        {
            Keys = keys,
        };

        return (internalTier ? GetTitleInternalDataPath : GetTitleDataPath, request);
    }

    internal static (string apiPath, SetTitleDataRequest request) BuildSetCall(
        bool internalTier,
        string key,
        string? value)
    {
        var request = new SetTitleDataRequest
        {
            Key = key,
            Value = value,
        };

        return (internalTier ? SetTitleInternalDataPath : SetTitleDataPath, request);
    }

    private static Option<bool> CreateInternalOption() => new("--internal")
    {
        Description = "Use the server-only internal title data tier.",
    };

    /// <summary>
    /// True when the caller explicitly asked for structured JSON instead of the single-key
    /// bare-value default. An explicit <c>-o</c>/<c>--output</c> selects the PlayFab envelope;
    /// <c>--data-only</c> selects the unwrapped <c>data</c> payload. An omitted <c>--output</c>
    /// is implicit and does not count.
    /// </summary>
    internal static bool RequestsExplicitStructuredOutput(ParseResult parseResult) =>
        parseResult.GetResult(Program.OutputOption) is { Implicit: false } ||
        parseResult.GetValue(Program.DataOnlyOption);

    private static Command CreateGetCommand()
    {
        var internalOption = CreateInternalOption();
        var keyArgument = new Argument<string?>("key")
        {
            Description = "Single title data key to retrieve. Prints only that key's raw value " +
                "unless -o/--output or --data-only asks for structured JSON.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var keyOption = new Option<string?>("--key")
        {
            Description = "Single title data key to retrieve (equivalent to the positional key).",
        };
        var keysOption = new Option<string?>("--keys")
        {
            Description = "Comma-separated title data keys to retrieve. Omit to fetch all keys.",
        };
        var valueOnlyOption = new Option<bool>("--value-only")
        {
            Description = "Deprecated alias for the single-key bare-value default. Does not change " +
                "output and does not override -o/--output or --data-only.",
        };

        var command = new Command("get", "Get title data values")
        {
            keyArgument,
            internalOption,
            keyOption,
            keysOption,
            valueOnlyOption,
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var output = OutputOptions.From(parseResult);

            // A single key prints its bare value by default. An explicit -o/--output request
            // returns the PlayFab envelope; --data-only returns the unwrapped data payload.
            // Either is structured JSON and wins over the bare-value default.
            var explicitStructuredOutput = RequestsExplicitStructuredOutput(parseResult);

            try
            {
                using var client = PlayFabCommandHelper.CreateClient(parseResult);
                return await RunGetAsync(
                    client,
                    parseResult.GetValue(internalOption),
                    parseResult.GetValue(keyArgument),
                    parseResult.GetValue(keyOption),
                    parseResult.GetValue(keysOption),
                    parseResult.GetValue(valueOnlyOption),
                    explicitStructuredOutput,
                    output,
                    Console.Out,
                    Console.Error,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Request failed: {ex.Message}");
                return 1;
            }
        });

        return command.WithMetadata(new CommandMetadata(
            ApiPaths: GetPaths,
            LearnMore: [TierLearnMore]));
    }

    internal static async Task<int> RunGetAsync(
        PlayFabApiClient client,
        bool internalTier,
        string? positionalKey,
        string? key,
        string? keysCsv,
        bool valueOnly,
        bool explicitStructuredOutput,
        OutputOptions output,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        // The positional key and --key are the same input; supplying both is ambiguous.
        if (positionalKey is not null && key is not null)
        {
            stderr.WriteLine("Use the positional key or --key, not both.");
            return 1;
        }

        var singleKey = positionalKey ?? key;

        // A present-but-blank key is user error (e.g. an empty shell variable); reject it rather
        // than silently treating it as "all keys", which would be a surprising wide read.
        if (singleKey is not null && string.IsNullOrWhiteSpace(singleKey))
        {
            stderr.WriteLine("Key must not be blank.");
            return 1;
        }

        if (!string.IsNullOrWhiteSpace(singleKey) && keysCsv is not null)
        {
            stderr.WriteLine("Use a single key or --keys, not both.");
            return 1;
        }

        if (valueOnly)
        {
            stderr.WriteLine(
                "Warning: --value-only is deprecated; omit it because single-key gets already print the bare value.");

            // --value-only does not select an output shape. It is a deprecated alias for the
            // single-key bare-value default, so it must not override -o/--output or --data-only.
            // A stray flag without a single key remains an error.
            if (string.IsNullOrWhiteSpace(singleKey))
            {
                stderr.WriteLine("--value-only requires a single key.");
                return 1;
            }
        }

        var keys = !string.IsNullOrWhiteSpace(singleKey)
            ? new List<string> { singleKey.Trim() }
            : ParseCsv(keysCsv);

        if (keysCsv is not null && (keys is null || keys.Count == 0))
        {
            stderr.WriteLine("--keys must contain at least one key.");
            return 1;
        }

        // valueOnly is intentionally not an input here. A single key is bare unless the caller
        // asked for structured JSON; the deprecated flag must not force the bare value back.
        var emitValueOnly = !string.IsNullOrWhiteSpace(singleKey) && !explicitStructuredOutput;

        var (apiPath, request) = BuildGetCall(internalTier, keys);
        var jsonBody = JsonSerializer.Serialize(request, RequestOptions);
        var compact = output.IsCompact;

        try
        {
            var response = await client.CallApiAsync(apiPath, jsonBody, cancellationToken: cancellationToken);
            if (!string.IsNullOrWhiteSpace(singleKey))
            {
                var trimmedKey = singleKey!.Trim();
                if (!TryExtractTitleDataValue(response, trimmedKey, out var value))
                {
                    stderr.WriteLine($"Key '{trimmedKey}' was not found.");
                    return 1;
                }

                if (emitValueOnly)
                {
                    stdout.WriteLine(value);
                    return 0;
                }
            }

            stdout.WriteLine(Program.FormatOutput(response, compact, output.DataOnly));
            return 0;
        }
        catch (PlayFabApiException ex)
        {
            stderr.WriteLine(Program.FormatErrorOutput(ex, compact));
            return 1;
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"Request failed: {ex.Message}");
            return 1;
        }
    }

    private static Command CreateSetCommand()
    {
        var internalOption = CreateInternalOption();
        var keyOption = new Option<string?>("--key")
        {
            Description = "The single title data key to set (use with --value).",
        };
        var valueOption = new Option<string?>("--value")
        {
            Description = "The value to set for --key. Stored verbatim (may contain any characters).",
        };
        var dataOption = new Option<string?>("--data")
        {
            Description = "JSON object of key/value pairs to set multiple keys at once. Mutually exclusive with --key/--value.",
        };

        var command = new Command("set", "Set one or more title data values")
        {
            internalOption,
            keyOption,
            valueOption,
            dataOption,
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var output = OutputOptions.From(parseResult);

            try
            {
                using var client = PlayFabCommandHelper.CreateClient(parseResult);
                return await RunSetAsync(
                    client,
                    parseResult.GetValue(internalOption),
                    parseResult.GetValue(keyOption),
                    parseResult.GetValue(valueOption),
                    parseResult.GetValue(dataOption),
                    output,
                    Console.Out,
                    Console.Error,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Request failed: {ex.Message}");
                return 1;
            }
        });

        return command.WithMetadata(new CommandMetadata(
            ApiPaths: SetPaths,
            LearnMore: [TierLearnMore]));
    }

    internal static async Task<int> RunSetAsync(
        PlayFabApiClient client,
        bool internalTier,
        string? key,
        string? value,
        string? dataJson,
        OutputOptions output,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
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

        Dictionary<string, string>? data;
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
            data = new Dictionary<string, string>(StringComparer.Ordinal) { [key!.Trim()] = value! };
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

        var nullableData = data.ToDictionary(pair => pair.Key, pair => (string?)pair.Value, StringComparer.Ordinal);
        return await RunMutationAsync(client, internalTier, nullableData, includeNullValues: false, output, stdout, stderr, cancellationToken: cancellationToken);
    }

    private static Command CreateDeleteCommand()
    {
        var internalOption = CreateInternalOption();
        var keysOption = new Option<string>("--keys")
        {
            Description = "Comma-separated title data keys to remove.",
            Required = true,
        };

        var command = new Command("delete", "Delete one or more title data keys")
        {
            internalOption,
            keysOption,
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var output = OutputOptions.From(parseResult);

            try
            {
                using var client = PlayFabCommandHelper.CreateClient(parseResult);
                return await RunDeleteAsync(
                    client,
                    parseResult.GetValue(internalOption),
                    parseResult.GetValue(keysOption)!,
                    output,
                    Console.Out,
                    Console.Error,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Request failed: {ex.Message}");
                return 1;
            }
        });

        return command.WithMetadata(new CommandMetadata(
            ApiPaths: SetPaths,
            SafetyDetails:
                "The named keys are removed by writing a null value through SetTitleData or " +
                "SetTitleInternalData, so --confirm does not apply; only the keys named in " +
                "--keys are affected.",
            LearnMore: [TierLearnMore]));
    }

    /// <summary>
    /// Removes title data keys by calling <c>SetTitleData</c> with a <c>null</c> value for each key.
    /// </summary>
    /// <remarks>
    /// This verb is intentionally <b>not</b> gated behind <c>--confirm</c>: it routes through
    /// <c>SetTitleData</c>/<c>SetTitleInternalData</c> (not a <c>Delete*</c>/<c>Shutdown*</c>
    /// path), so the catastrophic hard-gate in <see cref="PlayFabApiClient.CallApiAsync"/> does
    /// not apply. Removing explicitly named keys is a targeted operation, no more destructive than
    /// <c>set</c> overwriting an existing value, so it deliberately relies on the read-only
    /// convention rather than requiring confirmation.
    /// </remarks>
    internal static async Task<int> RunDeleteAsync(
        PlayFabApiClient client,
        bool internalTier,
        string keysCsv,
        OutputOptions output,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        var keys = ParseCsv(keysCsv);
        if (keys is null || keys.Count == 0)
        {
            stderr.WriteLine("--keys must contain at least one key.");
            return 1;
        }

        var data = keys.ToDictionary(k => k, _ => (string?)null, StringComparer.Ordinal);
        return await RunMutationAsync(client, internalTier, data, includeNullValues: true, output, stdout, stderr, cancellationToken: cancellationToken);
    }

    internal static bool TryParseDataObject(
        string? json,
        out Dictionary<string, string>? result,
        out string? error)
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

            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                var dataKey = property.Name.Trim();
                if (string.IsNullOrWhiteSpace(dataKey))
                {
                    error = "Invalid --data: keys must not be blank.";
                    return false;
                }

                var element = property.Value;
                switch (element.ValueKind)
                {
                    case JsonValueKind.String:
                        dict[dataKey] = element.GetString()!;
                        break;
                    case JsonValueKind.Null or JsonValueKind.Undefined:
                        error = $"Invalid --data: the value for key '{dataKey}' is null. Title data values are strings — " +
                                "provide a value, or use 'title-data delete' to remove the key.";
                        return false;
                    default:
                        dict[dataKey] = element.GetRawText();
                        break;
                }
            }

            result = dict;
            return true;
        }
    }

    internal static bool TryExtractTitleDataValue(string responseJson, string key, out string? value)
    {
        value = null;

        using var doc = JsonDocument.Parse(responseJson);
        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("Data", out var dataObj) ||
            dataObj.ValueKind != JsonValueKind.Object ||
            !dataObj.TryGetProperty(key, out var valueElement))
        {
            return false;
        }

        value = valueElement.ValueKind == JsonValueKind.String
            ? valueElement.GetString()
            : valueElement.GetRawText();
        return true;
    }

    private static async Task<int> RunMutationAsync(
        PlayFabApiClient client,
        bool internalTier,
        IReadOnlyDictionary<string, string?> data,
        bool includeNullValues,
        OutputOptions output,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        var applied = new List<string>();
        var compact = output.IsCompact;

        foreach (var pair in data)
        {
            var (apiPath, request) = BuildSetCall(internalTier, pair.Key, pair.Value);
            var jsonBody = JsonSerializer.Serialize(
                request,
                includeNullValues ? RequestOptionsIncludingNulls : RequestOptions);

            try
            {
                await client.CallApiAsync(apiPath, jsonBody, cancellationToken: cancellationToken);
                applied.Add(pair.Key);
            }
            catch (PlayFabApiException ex) when (ex.Outcome == PlayFabCallOutcome.Succeeded)
            {
                // The write took effect; only its response, which this command doesn't use, was unusable.
                applied.Add(pair.Key);
            }
            catch (Exception ex)
            {
                // Keep the summary on every failure: the earlier keys were applied.
                stderr.WriteLine(FormatMutationSummary(applied, pair.Key, ex, compact, output.DataOnly));
                return 1;
            }
        }

        stdout.WriteLine(FormatMutationSummary(applied, failedKey: null, exception: null, compact, output.DataOnly));
        return 0;
    }

    private static List<string>? ParseCsv(string? csv)
    {
        if (csv is null)
        {
            return null;
        }

        return csv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    private static string FormatMutationSummary(
        IReadOnlyList<string> applied,
        string? failedKey,
        Exception? exception,
        bool compact,
        bool dataOnly)
    {
        object? failed = null;
        object? unknown = null;
        object? notSent = null;
        if (failedKey is not null && exception is not null)
        {
            // Anything but a PlayFabApiException was thrown before the request was sent.
            var apiException = exception as PlayFabApiException;
            switch (apiException?.Outcome)
            {
                case PlayFabCallOutcome.Rejected:
                    failed = new { key = failedKey, error = ParseErrorBody(apiException) };
                    break;
                case PlayFabCallOutcome.NoResponse or PlayFabCallOutcome.Inconclusive:
                    unknown = new
                    {
                        key = failedKey,
                        error = ParseErrorBody(apiException),
                        outcomeUnknown = apiException.OutcomeWarning,
                    };
                    break;
                default:
                    notSent = new { key = failedKey, error = apiException?.FailureMessage ?? exception.Message };
                    break;
            }
        }

        var json = JsonSerializer.Serialize(new { applied, failed, unknown, notSent }, SummaryOptions);
        return Program.FormatOutput(json, compact, dataOnly);
    }

    private static object ParseErrorBody(PlayFabApiException exception)
    {
        try
        {
            using var doc = JsonDocument.Parse(exception.RawBody);
            return doc.RootElement.Clone();
        }
        catch (Exception parseException) when (parseException is JsonException or ArgumentException)
        {
            return exception.FailureMessage;
        }
    }
}
