// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using XboxDevCLI.Output;

namespace XboxDevCLI.Commands;

/// <summary>One XBOX Live validation message from XCon or XORC.</summary>
internal sealed record XboxLiveValidationMessage(
    string? Severity,
    string? DocumentName,
    string? Error,
    string? Message)
{
    /// <summary>Whether the message has a text or an error key to show.</summary>
    public bool HasText => !string.IsNullOrWhiteSpace(Message) || !string.IsNullOrWhiteSpace(Error);
}

/// <summary>
/// Reads and writes the XBOX Live validation messages that explain why XCon or XORC refused
/// configuration documents: a commit's <c>ValidationInfo</c> and the gateway's <c>validation</c>.
/// </summary>
internal static class XboxLiveValidationMessages
{
    /// <summary>XCon's warning that the documents match the latest ones, so there's nothing to commit.</summary>
    internal const string FilesNotChangedCommit = "FilesNotChangedCommit";

    /// <summary>
    /// Reads every message object from a JSON array, ignoring anything else. Messages without text
    /// still count when classifying the result; filter on <see cref="XboxLiveValidationMessage.HasText"/>
    /// to show them.
    /// </summary>
    public static IReadOnlyList<XboxLiveValidationMessage> Read(JsonElement messages)
    {
        if (messages.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<XboxLiveValidationMessage>();
        foreach (JsonElement item in messages.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object)
            {
                result.Add(new XboxLiveValidationMessage(
                    GetString(item, "severity"),
                    GetString(item, "documentName"),
                    GetString(item, "error"),
                    GetString(item, "message")));
            }
        }

        return result;
    }

    /// <summary>
    /// Whether a commit result says only that the documents match the latest ones: XCon then
    /// reports nothing committed or committable, with no error.
    /// </summary>
    public static bool IsUnchanged(IReadOnlyList<XboxLiveValidationMessage> messages) =>
        messages.Any(static message => string.Equals(
            message.Error,
            FilesNotChangedCommit,
            StringComparison.Ordinal)) &&
        !messages.Any(IsError);

    /// <summary>
    /// Formats a message as <c>Error: achievements2017.xml: text (ErrorKey)</c>, or with the error key
    /// alone when there's no text. Control characters, line breaks and bidi overrides become spaces, so
    /// gateway text can't move the cursor or forge another line on the terminal.
    /// </summary>
    public static string Format(XboxLiveValidationMessage message)
    {
        string? severity = Clean(message.Severity);
        string? document = Clean(message.DocumentName);
        string? text = Clean(message.Message);
        string? error = Clean(message.Error);
        string body = text is null ? error ?? string.Empty : error is null ? text : $"{text} ({error})";
        return $"{(severity is null ? string.Empty : $"{severity}: ")}" +
            $"{(document is null ? string.Empty : $"{document}: ")}{body}";
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : TableFormatter.NormalizeCell(value).Trim();

    private static bool IsError(XboxLiveValidationMessage message) =>
        string.Equals(message.Severity, "Error", StringComparison.OrdinalIgnoreCase);

    private static string? GetString(JsonElement element, string propertyName)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
            }
        }

        return null;
    }
}
