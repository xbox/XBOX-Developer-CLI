// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace XboxDevCLI.Output;

/// <summary>
/// Removes control characters, Unicode line and paragraph separators, and bidi formatting controls
/// from server-supplied text before it is written to the terminal, so a response cannot inject
/// escape sequences, ring the bell, forge additional output lines, or visually reorder text.
/// </summary>
internal static class TerminalText
{
    /// <summary>Removes every unsafe character, including line breaks, for one-line text.</summary>
    public static string SingleLine(string? value) =>
        string.IsNullOrEmpty(value)
            ? string.Empty
            : string.Concat(value.Where(character => !TableFormatter.IsUnsafeCellCharacter(character)));

    /// <summary>
    /// Removes unsafe characters other than line feeds and tabs, for multi-line text such as a
    /// response body. Carriage returns are removed so a line cannot be overwritten.
    /// </summary>
    public static string MultiLine(string? value) =>
        string.IsNullOrEmpty(value)
            ? string.Empty
            : string.Concat(value.Where(character =>
                character is '\n' or '\t' || !TableFormatter.IsUnsafeCellCharacter(character)));
}
