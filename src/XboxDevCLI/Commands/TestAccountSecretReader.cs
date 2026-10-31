// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;

namespace XboxDevCLI.Commands;

/// <summary>
/// Seams for the parts of <c>test-account</c> commands that touch the terminal or the clock, so
/// tests can drive prompts, stdin, and waiting without a console.
/// </summary>
internal sealed record TestAccountCommandDependencies(
    Func<TimeSpan, CancellationToken, Task> DelayAsync,
    Func<DateTimeOffset> GetUtcNow,
    Func<bool> IsInteractive,
    Func<string, string?> ReadMaskedSecret,
    Func<string?> ReadConfirmation,
    Func<TextReader> GetStandardInput)
{
    internal static TestAccountCommandDependencies Default { get; } = new(
        Task.Delay,
        () => DateTimeOffset.UtcNow,
        () => XboxPartnerCommandSupport.IsInteractiveTerminal(),
        TestAccountSecretReader.ReadMaskedFromConsole,
        Console.ReadLine,
        // Console.In decodes with the console code page, which is not UTF-8 on Windows; a
        // non-ASCII secret piped in would be sent garbled.
        () => new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false)));
}

/// <summary>
/// Reads the create secrets from a file, from stdin, or from a masked prompt. Never from an
/// option value or an environment variable, which would leave them in shell history, process
/// listings, or logs.
/// </summary>
internal static class TestAccountSecretReader
{
    public const string StandardInput = "-";

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> when <see cref="Read"/> is bound to fail
    /// because the file is missing or no terminal can prompt. Reads nothing.
    /// </summary>
    public static void EnsureSource(
        string? file,
        string fileOptionName,
        string label,
        TestAccountCommandDependencies dependencies)
    {
        if (file == StandardInput)
        {
            return;
        }

        if (file is not null)
        {
            if (!File.Exists(file))
            {
                throw new InvalidOperationException($"Could not read {fileOptionName} '{file}': the file does not exist.");
            }

            return;
        }

        if (!dependencies.IsInteractive())
        {
            throw NoTerminal(fileOptionName, label);
        }
    }

    /// <summary>
    /// Returns the secret, or throws <see cref="InvalidOperationException"/> with guidance.
    /// <paramref name="prompt"/> replaces <paramref name="label"/> in the terminal prompt.
    /// </summary>
    public static string Read(
        string? file,
        string fileOptionName,
        string label,
        TestAccountCommandDependencies dependencies,
        string? prompt = null)
    {
        string? value;
        if (file == StandardInput)
        {
            value = TrimOneNewline(dependencies.GetStandardInput().ReadToEnd());
        }
        else if (file is not null)
        {
            try
            {
                value = TrimOneNewline(File.ReadAllText(file));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException)
            {
                throw new InvalidOperationException(
                    $"Could not read {fileOptionName} '{file}': {exception.Message}", exception);
            }
        }
        else if (dependencies.IsInteractive())
        {
            value = dependencies.ReadMaskedSecret($"{prompt ?? label}: ");
            if (!string.IsNullOrEmpty(value) &&
                !string.Equals(value, dependencies.ReadMaskedSecret($"Confirm {label.ToLowerInvariant()}: "), StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"The {label.ToLowerInvariant()} entries did not match.");
            }
        }
        else
        {
            throw NoTerminal(fileOptionName, label);
        }

        if (string.IsNullOrEmpty(value))
        {
            throw new InvalidOperationException($"The {label.ToLowerInvariant()} must not be empty.");
        }

        return value;
    }

    private static InvalidOperationException NoTerminal(string fileOptionName, string label) =>
        new($"No terminal is available to prompt for the {label.ToLowerInvariant()}. Pass {fileOptionName} " +
            $"with a file path, or {fileOptionName} - to read it from stdin.");

    internal static string TrimOneNewline(string value) =>
        value.EndsWith("\r\n", StringComparison.Ordinal) ? value[..^2]
        : value.EndsWith('\n') ? value[..^1]
        : value;

    /// <summary>Reads a line from the console without echoing it. The prompt goes to stderr.</summary>
    internal static string? ReadMaskedFromConsole(string prompt)
    {
        Console.Error.Write(prompt);
        Console.Error.Flush();
        var builder = new StringBuilder();
        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.Error.WriteLine();
                return builder.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (builder.Length > 0)
                {
                    builder.Length--;
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                builder.Append(key.KeyChar);
            }
        }
    }
}
