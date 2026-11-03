// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;

namespace XboxDevCLI.E2ETest;

/// <summary>
/// Reads the CLI's JSON without throwing, so a missing or mistyped field fails through
/// <see cref="CliResult.Check"/>, whose message shows the output.
/// </summary>
internal static class JsonReading
{
    /// <summary>The items of an array, or of the array property <paramref name="name"/>; empty if there's none.</summary>
    public static JsonElement[] Items(this JsonElement element, string? name = null)
    {
        JsonElement array = name is null ? element : element.Field(name);
        return array.ValueKind == JsonValueKind.Array ? [.. array.EnumerateArray()] : [];
    }

    /// <summary>A property, or an undefined element if there's none.</summary>
    public static JsonElement Field(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value)
            ? value
            : default;

    /// <summary>A string, or a number as written; null otherwise.</summary>
    public static string? Text(this JsonElement element, string name)
    {
        JsonElement value = element.Field(name);
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    public static long? Number(this JsonElement element, string name) =>
        element.Field(name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt64(out long number)
            ? number
            : null;

    public static bool? Flag(this JsonElement element, string name) =>
        element.Field(name).ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };

    /// <summary>Whether a string or number equals <paramref name="expected"/>, ignoring case.</summary>
    public static bool Is(this JsonElement element, string name, string expected) =>
        string.Equals(element.Text(name), expected, StringComparison.OrdinalIgnoreCase);

    public static bool IsGuid(this JsonElement element, string name, string expected) =>
        Guid.TryParse(element.Text(name), out Guid actual) && actual == Guid.Parse(expected);

    /// <summary>Whether an array of strings has <paramref name="expected"/>, ignoring case.</summary>
    public static bool HasString(this JsonElement[] items, string expected) =>
        items.Any(item => item.ValueKind == JsonValueKind.String &&
            string.Equals(item.GetString(), expected, StringComparison.OrdinalIgnoreCase));
}
