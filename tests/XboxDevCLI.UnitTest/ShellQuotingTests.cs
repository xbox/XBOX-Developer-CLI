// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using XboxDevCLI.CommandLine;
using Xunit;

namespace XboxDevCLI.UnitTest;

/// <summary>
/// Quoting for the commands the CLI suggests, such as the DeleteTitle refusal and the
/// <c>game create</c> recovery commands, so a suggestion pasted into PowerShell on Windows, or a
/// POSIX shell elsewhere, runs as one command with the arguments it shows and nothing else.
/// </summary>
public class ShellQuotingTests
{
    [Theory]
    [InlineData("ABC12")]
    [InlineData("/Admin/DeleteTitle")]
    [InlineData("https://gateway.example")]
    [InlineData("TESTSB.42")]
    [InlineData("job-1_a:b")]
    [InlineData("--playfab-title-id")]
    [InlineData("-y")]
    public void Quote_LeavesAnArgumentNoShellInterpretsAlone(string value)
    {
        Assert.Equal(value, ShellQuoting.Quote(value, powerShell: false));
        Assert.Equal(value, ShellQuoting.Quote(value, powerShell: true));
        Assert.Equal(value, ShellQuoting.Quote(value));
    }

    /// <summary>
    /// An empty argument, or one with a space, quote, or shell metacharacter, is single-quoted
    /// for a POSIX shell, where an apostrophe has to close the quotes, be quoted itself, and
    /// reopen them.
    /// </summary>
    [Theory]
    [InlineData("", "''")]
    [InlineData("a b", "'a b'")]
    [InlineData("don't", "'don'\"'\"'t'")]
    [InlineData("it's; rm -rf ~", "'it'\"'\"'s; rm -rf ~'")]
    [InlineData("say \"hi\"", "'say \"hi\"'")]
    [InlineData("Admin\\DeleteTitle", "'Admin\\DeleteTitle'")]
    [InlineData("$HOME `id` $(id)", "'$HOME `id` $(id)'")]
    [InlineData("-a.b", "-a.b")]
    public void Quote_SingleQuotesForAPosixShell(string value, string expected)
    {
        Assert.Equal(expected, ShellQuoting.Quote(value, powerShell: false));
    }

    /// <summary>
    /// For PowerShell, the Windows shell, an argument is single-quoted, so a <c>$</c>, a
    /// backtick, or a double quote stays literal. A single quote inside is doubled, including the
    /// typographic ones PowerShell also reads as single quotes, and a value PowerShell would split
    /// at a dot after a leading dash is quoted too.
    /// </summary>
    [Theory]
    [InlineData("", "''")]
    [InlineData("a b", "'a b'")]
    [InlineData("C:\\temp\\$HOME.json", "'C:\\temp\\$HOME.json'")]
    [InlineData("$(Remove-Item x)", "'$(Remove-Item x)'")]
    [InlineData("tick`n `$x", "'tick`n `$x'")]
    [InlineData("say \"hi\"", "'say \"hi\"'")]
    [InlineData("\u201Csmart\u201D", "'\u201Csmart\u201D'")]
    [InlineData("don't", "'don''t'")]
    [InlineData("x' $(id) 'y", "'x'' $(id) ''y'")]
    [InlineData("x\u2019 $(id) \u2018y", "'x\u2019\u2019 $(id) \u2018\u2018y'")]
    [InlineData("\u201Aa\u201B", "'\u201A\u201Aa\u201B\u201B'")]
    [InlineData("Admin\\DeleteTitle", "'Admin\\DeleteTitle'")]
    [InlineData("C:\\my dir\\", "'C:\\my dir\\'")]
    [InlineData("-a.b", "'-a.b'")]
    [InlineData("-x/y.json", "'-x/y.json'")]
    public void Quote_SingleQuotesForPowerShell(string value, string expected)
    {
        Assert.Equal(expected, ShellQuoting.Quote(value, powerShell: true));
    }

    /// <summary>
    /// Text piped into a suggested command, such as a JSON body with escapes in it, is printed
    /// with <c>printf '%s\n'</c> for a POSIX shell, because zsh's and dash's <c>echo</c>
    /// interpret backslash escapes, and with <c>echo</c> for PowerShell, whose <c>echo</c> doesn't.
    /// </summary>
    [Theory]
    [InlineData(false, """printf '%s\n' '{"F":["a\u0022b\\c it'"'"'s"]}'""")]
    [InlineData(true, """echo '{"F":["a\u0022b\\c it''s"]}'""")]
    public void PrintLine_UsesACommandThatWritesTheTextUnchanged(bool powerShell, string expected)
    {
        Assert.Equal(expected, ShellQuoting.PrintLine("""{"F":["a\u0022b\\c it's"]}""", powerShell));
    }
}
