// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using XboxDevCLI.Presentation;
using Xunit;

namespace XboxDevCLI.UnitTest;

public class ConfirmationPromptTests
{
    private const string Prompt = "Publish to RETAIL? [y/N]: ";

    [Fact]
    public void Write_WithColor_SeparatesThePromptAndColorsOnlyTheQuestion()
    {
        using var stderr = new StringWriter();
        stderr.Write("RETAIL is live." + Environment.NewLine);

        ConfirmationPrompt.Write(stderr, Prompt, useColor: true);

        Assert.Equal(
            "RETAIL is live." + Environment.NewLine + Environment.NewLine +
            "\u001b[1;38;2;139;240;0mPublish to RETAIL? [y/N]:\u001b[0m ",
            stderr.ToString());
    }

    [Theory]
    [InlineData(true, null, null, null)]
    [InlineData(false, "1", null, null)]
    [InlineData(false, null, "dumb", null)]
    [InlineData(false, null, null, "0")]
    public void Write_WithoutColorSupport_WritesPlainText(bool redirected, string? noColor, string? term, string? cliColor)
    {
        using var stderr = new StringWriter();

        ConfirmationPrompt.Write(stderr, Prompt, WelcomeExperience.SupportsColor(redirected, noColor, term, cliColor));

        Assert.Equal(Environment.NewLine + Prompt, stderr.ToString());
    }

    [Fact]
    public void Write_ToAWriterOtherThanTheConsole_WritesPlainText()
    {
        using var stderr = new StringWriter();

        ConfirmationPrompt.Write(stderr, Prompt);

        Assert.Equal(Environment.NewLine + Prompt, stderr.ToString());
    }
}
