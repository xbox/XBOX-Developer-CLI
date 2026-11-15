// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace XboxDevCLI.Presentation;

/// <summary>
/// Writes an interactive prompt to stderr so it stands out from the summary above it: an
/// empty line, then the question in the brand color when the terminal supports color.
/// </summary>
internal static class ConfirmationPrompt
{
    internal static void Write(TextWriter stderr, string prompt) =>
        Write(
            stderr,
            prompt,
            ReferenceEquals(stderr, Console.Error) && WelcomeExperience.SupportsColor(Console.IsErrorRedirected));

    internal static void Write(TextWriter stderr, string prompt, bool useColor)
    {
        string question = prompt.TrimEnd();
        stderr.WriteLine();
        stderr.Write(WelcomeExperience.ColorizeBrand(question, useColor));
        stderr.Write(prompt[question.Length..]);
        stderr.Flush();
    }
}
