// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XboxDevCLI.Commands;

/// <summary>
/// Flattens a playtest into FIELD and VALUE rows so <c>get</c> and <c>diff</c> can print a table,
/// and builds the diff's change rows. The gateway sends the two versions side by side and leaves
/// the comparison to the caller.
/// </summary>
internal static class PlaytestFields
{
    // Art URIs are regenerated on every request, so they always differ.
    private static readonly HashSet<string> Volatile = new(StringComparer.OrdinalIgnoreCase)
    {
        "gameArtUri",
    };

    internal static IReadOnlyList<KeyValuePair<string, string>> Flatten(JsonElement element)
    {
        var rows = new List<KeyValuePair<string, string>>();
        Add(rows, string.Empty, element);
        return rows;
    }

    private static void Add(
        List<KeyValuePair<string, string>> rows, string path, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (!Volatile.Contains(property.Name))
                    {
                        Add(rows, path.Length == 0 ? property.Name : $"{path}.{property.Name}", property.Value);
                    }
                }

                break;
            case JsonValueKind.Array:
                var keys = PlatformKeys(element);
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    var key = keys?[index] ?? index.ToString(CultureInfo.InvariantCulture);
                    index++;
                    Add(rows, $"{path}[{key}]", item);
                }

                break;
            case JsonValueKind.Null:
                break;
            default:
                rows.Add(new(path, element.ValueKind == JsonValueKind.String
                    ? element.GetString() ?? string.Empty
                    : element.GetRawText()));
                break;
        }
    }

    // A playtest has at most one package for each platform, so keying rows by platform lines up the
    // same package on both sides of a diff even when another package is added or removed. Falls
    // back to positions when an item has no platform or two items share one.
    private static string[]? PlatformKeys(JsonElement array)
    {
        var keys = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("platform", out var platform)
                || platform.ValueKind != JsonValueKind.String
                || string.IsNullOrEmpty(platform.GetString()))
            {
                return null;
            }

            keys.Add(platform.GetString()!);
        }

        return keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() == keys.Count ? keys.ToArray() : null;
    }

    /// <summary>The response as <c>{"fields":[{"field","value"}]}</c>, for the table view.</summary>
    internal static string ToGetFields(string responseJson)
    {
        using var document = JsonDocument.Parse(responseJson);
        var fields = new JsonArray();
        foreach (var (field, value) in Flatten(document.RootElement))
        {
            fields.Add(new JsonObject { ["field"] = field, ["value"] = value });
        }

        return new JsonObject { ["fields"] = fields }.ToJsonString();
    }

    /// <summary>
    /// The diff as <c>{"fields":[{"field","current","compared"}]}</c>, only for fields whose values
    /// differ. A field missing on one side shows an empty cell.
    /// </summary>
    internal static string ToDiffFields(string responseJson)
    {
        using var document = JsonDocument.Parse(responseJson);
        var root = document.RootElement;
        var fields = new JsonArray();
        if (root.TryGetProperty("compared", out var compared) && compared.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("current", out var current))
        {
            var left = Flatten(current).ToDictionary(row => row.Key, row => row.Value, StringComparer.Ordinal);
            var right = Flatten(compared).ToDictionary(row => row.Key, row => row.Value, StringComparer.Ordinal);
            foreach (var key in left.Keys.Union(right.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                left.TryGetValue(key, out var currentValue);
                right.TryGetValue(key, out var comparedValue);
                if (!string.Equals(currentValue, comparedValue, StringComparison.Ordinal))
                {
                    fields.Add(new JsonObject
                    {
                        ["field"] = key,
                        ["current"] = currentValue ?? string.Empty,
                        ["compared"] = comparedValue ?? string.Empty,
                    });
                }
            }
        }

        return new JsonObject { ["fields"] = fields }.ToJsonString();
    }

    /// <summary>
    /// Fails unless a playtest response is a JSON object, so a malformed response is never
    /// reported as a playtest.
    /// </summary>
    internal static void RequirePlaytest(string responseJson)
    {
        using var document = JsonDocument.Parse(responseJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The playtest response must be a JSON object.");
        }
    }

    /// <summary>The validation message code of the rule that the game needs age ratings.</summary>
    internal const string MissingAgeRatingsCode = "PlaytestMissingAgeRatings";

    /// <summary>What a playtest read shows: its details, error code, and top level etag, any of which may be missing.</summary>
    internal sealed record PlaytestSummary(
        string? Name, string? Status, string? StartDate, string? EndDate, string? ErrorCode, string? Etag);

    /// <summary>Reads a playtest's summary.</summary>
    internal static PlaytestSummary ReadSummary(string responseJson)
    {
        RequirePlaytest(responseJson);
        using var document = JsonDocument.Parse(responseJson);
        var root = document.RootElement;
        var details = root.GetProperty("details");
        var errorCode = details.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
            ? ReadOptional(error, "code")
            : null;
        return new PlaytestSummary(
            ReadOptional(details, "name"),
            ReadOptional(details, "status"),
            ReadOptional(details, "startDate"),
            ReadOptional(details, "endDate"),
            errorCode,
            ReadOptional(root, "etag"));
    }

    /// <summary>
    /// Fails unless a publish response is a JSON object, so a malformed response is never reported
    /// as a queued publish.
    /// </summary>
    internal static void RequirePublishResponse(string responseJson)
    {
        using var document = JsonDocument.Parse(responseJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The publish response must be a JSON object.");
        }
    }

    /// <summary>
    /// Fails unless a delete response is a JSON object with a <c>status</c> string, so a malformed
    /// response is never reported as a finished or queued delete.
    /// </summary>
    internal static void RequireDeleteResponse(string responseJson)
    {
        using var document = JsonDocument.Parse(responseJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || string.IsNullOrEmpty(ReadOptional(document.RootElement, "status")))
        {
            throw new InvalidDataException("The delete response must be a JSON object with a status.");
        }
    }

    /// <summary>
    /// Reads the validation messages in a body's <paramref name="property"/> array, or none when the
    /// body isn't a JSON object with one. A publish response's <c>warnings</c> and a refusal's
    /// <c>validation</c> are both lists of XBOX services validation messages.
    /// </summary>
    internal static IReadOnlyList<XboxLiveValidationMessage> ReadMessages(string? body, string property)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(property, out var list)
                    ? XboxLiveValidationMessages.Read(list)
                    : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Reads a readiness response: none when it's <c>ready</c>, otherwise the distinct message of each
    /// check that's a <c>warning</c> or <c>checkFailed</c>. Null when the body has no <c>ready</c>.
    /// </summary>
    internal static IReadOnlyList<string>? ReadReadinessWarnings(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("ready", out var ready)
                || ready.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return null;
            }

            var warnings = new List<string>();
            if (ready.ValueKind == JsonValueKind.True
                || !root.TryGetProperty("checks", out var checks)
                || checks.ValueKind != JsonValueKind.Array)
            {
                return warnings;
            }

            foreach (var check in checks.EnumerateArray())
            {
                if (check.ValueKind == JsonValueKind.Object
                    && ReadOptional(check, "status") is "warning" or "checkFailed"
                    && (ReadOptional(check, "message") ?? ReadOptional(check, "code") ?? ReadOptional(check, "name"))
                        is { Length: > 0 } text
                    && !warnings.Contains(text))
                {
                    warnings.Add(text);
                }
            }

            return warnings;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Reads a delete response's <c>status</c>, or null when the body has none.</summary>
    internal static string? ReadDeleteStatus(string responseJson) => ReadResponseString(responseJson, "status");

    /// <summary>Reads a string property of a response body, or null when the body isn't a JSON object with one.</summary>
    internal static string? ReadResponseString(string? body, string property)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? ReadOptional(document.RootElement, property)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadOptional(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Checks a comparison response and reads <c>hasDiffData</c>. The response must be an object
    /// with <c>hasDiffData</c> as <c>true</c> or <c>false</c> and <c>current</c> as an object, and
    /// <c>compared</c> as an object when <c>hasDiffData</c> is true. Anything else is malformed,
    /// and is never read as "nothing to compare".
    /// </summary>
    internal static bool HasDiffData(string responseJson)
    {
        using var document = JsonDocument.Parse(responseJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("hasDiffData", out var value)
            || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException(
                "The playtest comparison response must contain hasDiffData as true or false.");
        }

        if (!root.TryGetProperty("current", out var current) || current.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The playtest comparison response must contain current as an object.");
        }

        var hasDiffData = value.GetBoolean();
        if (hasDiffData
            && (!root.TryGetProperty("compared", out var compared) || compared.ValueKind != JsonValueKind.Object))
        {
            throw new InvalidDataException(
                "The playtest comparison response must contain compared as an object when hasDiffData is true.");
        }

        return hasDiffData;
    }
}
