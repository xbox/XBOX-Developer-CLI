// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;

namespace XboxDevCLI.CommandLine;

/// <summary>
/// Quotes the arguments of a command the CLI suggests running, so the suggestion can be pasted
/// into the shell it's likely to be run from: PowerShell on Windows, and a POSIX shell such as
/// bash or zsh elsewhere.
/// </summary>
internal static class ShellQuoting
{
    /// <summary>
    /// <paramref name="value"/> as one argument for this platform's shell.
    /// </summary>
    public static string Quote(string value) => Quote(value, powerShell: OperatingSystem.IsWindows());

    /// <summary>
    /// <paramref name="value"/> as one literal shell argument: unchanged when the shell would pass
    /// it on as it is; otherwise single-quoted, so nothing in it is expanded or run, with the
    /// quote characters inside it escaped, so none of them can end the argument early.
    /// </summary>
    internal static string Quote(string value, bool powerShell)
    {
        if (value.Length > 0
            && value.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '/' or '-')
            && !(powerShell && value.StartsWith('-') && value.Contains('.')))
        {
            return value;
        }

        return powerShell
            ? QuoteForPowerShell(value)
            : $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";
    }

    /// <summary>
    /// A command, for this platform's shell, that writes <paramref name="text"/> and a newline to
    /// stdout unchanged, for piping into a suggested command.
    /// </summary>
    public static string PrintLine(string text) => PrintLine(text, powerShell: OperatingSystem.IsWindows());

    /// <summary>
    /// PowerShell's <c>echo</c> writes its argument as it is. A POSIX shell's may not: zsh's and
    /// dash's turn backslash escapes such as <c>\u0022</c> and <c>\\</c> into the characters they
    /// stand for, which corrupts JSON. <c>printf '%s\n'</c> never interprets its argument.
    /// </summary>
    internal static string PrintLine(string text, bool powerShell) =>
        powerShell
            ? $"echo {Quote(text, powerShell: true)}"
            : $"printf '%s\\n' {Quote(text, powerShell: false)}";

    /// <summary>
    /// Single-quotes <paramref name="value"/> for PowerShell, where nothing in single quotes is
    /// expanded: not <c>$</c>, a backtick, or a double quote. A single quote inside is doubled,
    /// and so are the typographic ones (U+2018 to U+201B), which PowerShell also reads as single
    /// quotes. Unquoted, PowerShell would split <c>-a.b</c> into <c>-a</c> and <c>.b</c>, so a
    /// value like that is quoted too.
    /// </summary>
    private static string QuoteForPowerShell(string value)
    {
        var quoted = new StringBuilder("'");
        foreach (var character in value)
        {
            quoted.Append(character);
            if (character is '\'' or '\u2018' or '\u2019' or '\u201A' or '\u201B')
            {
                quoted.Append(character);
            }
        }

        return quoted.Append('\'').ToString();
    }
}
