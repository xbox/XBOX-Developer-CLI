// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;

namespace XboxDevCLI.CommandLine;

/// <summary>
/// Resolves a registered command to its canonical path through the command tree.
/// </summary>
internal static class CommandPathResolver
{
    internal static IReadOnlyList<string> Resolve(RootCommand rootCommand, Command command)
    {
        ArgumentNullException.ThrowIfNull(rootCommand);
        ArgumentNullException.ThrowIfNull(command);

        if (ReferenceEquals(rootCommand, command))
        {
            return [];
        }

        var segments = new List<string>();
        return TryFind(rootCommand, command, segments)
            ? segments
            : throw new InvalidOperationException(
                $"Command '{command.Name}' is not part of the root command tree.");
    }

    internal static string ResolveTelemetryIdentity(RootCommand rootCommand, Command command)
    {
        var segments = Resolve(rootCommand, command);
        return segments.Count == 0 ? "root" : string.Join("-", segments);
    }

    private static bool TryFind(Command current, Command target, List<string> segments)
    {
        foreach (var subcommand in current.Subcommands)
        {
            segments.Add(subcommand.Name);

            if (ReferenceEquals(subcommand, target) || TryFind(subcommand, target, segments))
            {
                return true;
            }

            segments.RemoveAt(segments.Count - 1);
        }

        return false;
    }
}
