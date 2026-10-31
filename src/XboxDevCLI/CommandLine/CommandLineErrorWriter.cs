// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;

namespace XboxDevCLI.CommandLine;

internal static class CommandLineErrorWriter
{
    internal static bool TryWriteUnknownCommand(
        ParseResult parseResult,
        RootCommand rootCommand,
        string cliName,
        TextWriter errorWriter,
        bool requireParseError = true)
    {
        ArgumentNullException.ThrowIfNull(parseResult);
        ArgumentNullException.ThrowIfNull(rootCommand);
        ArgumentException.ThrowIfNullOrWhiteSpace(cliName);
        ArgumentNullException.ThrowIfNull(errorWriter);

        if (requireParseError &&
            (parseResult.Errors.Count == 0 ||
             parseResult.CommandResult.Command.Subcommands.Count == 0))
        {
            return false;
        }

        var unknownCommand = FindUnknownCommand(parseResult.UnmatchedTokens);
        if (unknownCommand is null)
        {
            return false;
        }

        WriteUnknownCommand(
            rootCommand,
            parseResult.CommandResult.Command,
            unknownCommand,
            cliName,
            errorWriter);
        return true;
    }

    /// <summary>
    /// Writes the contextual "unknown command" error for <paramref name="unknownCommand"/> under
    /// <paramref name="command"/>, including a usage line derived from that command's real depth.
    /// </summary>
    internal static void WriteUnknownCommand(
        RootCommand rootCommand,
        Command command,
        string unknownCommand,
        string cliName,
        TextWriter errorWriter)
    {
        ArgumentNullException.ThrowIfNull(rootCommand);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(unknownCommand);
        ArgumentException.ThrowIfNullOrWhiteSpace(cliName);
        ArgumentNullException.ThrowIfNull(errorWriter);

        var segments = CommandPath.Resolve(rootCommand, command);
        var commandPath = segments.Count == 0
            ? cliName
            : $"{cliName} {string.Join(' ', segments)}";

        errorWriter.WriteLine(
            $"unknown command {Quote(unknownCommand)} for {Quote(commandPath)}");
        errorWriter.WriteLine();
        errorWriter.WriteLine($"Usage:  {CommandHelpWriter.BuildUsage(command, commandPath)}");

        var visibleSubcommands = command.Subcommands
            .Where(subcommand => !subcommand.Hidden)
            .OrderBy(subcommand => subcommand.Name, StringComparer.Ordinal)
            .ToArray();
        if (visibleSubcommands.Length > 0)
        {
            errorWriter.WriteLine();
            errorWriter.WriteLine("Available commands:");

            foreach (var subcommand in visibleSubcommands)
            {
                errorWriter.WriteLine($"  {subcommand.Name}");
            }
        }
    }

    private static string? FindUnknownCommand(IEnumerable<string> unmatchedTokens)
    {
        var previousWasOption = false;
        foreach (var token in unmatchedTokens)
        {
            if (!string.IsNullOrWhiteSpace(token) &&
                (token.StartsWith("-", StringComparison.Ordinal) ||
                 token.StartsWith("/", StringComparison.Ordinal)))
            {
                previousWasOption = true;
                continue;
            }

            if (previousWasOption)
            {
                previousWasOption = false;
                continue;
            }

            if (!string.IsNullOrWhiteSpace(token))
            {
                return token;
            }
        }

        return null;
    }

    private static string Quote(string value) =>
        $"\"{value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal)}\"";
}
