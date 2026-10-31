// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Diagnostics.CodeAnalysis;

namespace XboxDevCLI.CommandLine;

/// <summary>
/// Resolves user-supplied command paths and provides Help-specific path queries.
/// </summary>
/// <remarks>
/// Canonical root-to-command traversal remains centralized in <see cref="CommandPathResolver"/>,
/// which is also used by telemetry. This type owns only the inverse lookup needed by
/// <c>help &lt;path&gt;</c> and Help-specific scope checks.
/// </remarks>
internal static class CommandPath
{
    /// <summary>The name of the PlayFab subtree, used to scope PlayFab-only options in Help.</summary>
    internal const string PlayFabCommandName = "playfab";

    /// <summary>
    /// Returns the path segments from the root to <paramref name="command"/>, excluding the root
    /// itself. Returns an empty list when <paramref name="command"/> is the root.
    /// </summary>
    internal static IReadOnlyList<string> Resolve(RootCommand rootCommand, Command command) =>
        CommandPathResolver.Resolve(rootCommand, command);

    /// <summary>
    /// Resolves a user-supplied path such as <c>["playfab", "file", "list"]</c> to the command it
    /// names. Hidden commands resolve so that <c>help</c> stays usable for them.
    /// </summary>
    /// <param name="rootCommand">The root of the tree to search.</param>
    /// <param name="segments">The path segments to resolve.</param>
    /// <param name="command">The resolved command, when the whole path matched.</param>
    /// <param name="unresolvedSegment">The first segment that did not match, when resolution failed.</param>
    /// <param name="resolvedParent">
    /// The deepest command that did match, so callers can report an accurate "unknown command X for
    /// Y" error rather than blaming the root.
    /// </param>
    internal static bool TryResolve(
        RootCommand rootCommand,
        IReadOnlyList<string> segments,
        [NotNullWhen(true)] out Command? command,
        out string? unresolvedSegment,
        out Command resolvedParent)
    {
        ArgumentNullException.ThrowIfNull(rootCommand);
        ArgumentNullException.ThrowIfNull(segments);

        Command current = rootCommand;
        foreach (var segment in segments)
        {
            var match = current.Subcommands.FirstOrDefault(subcommand =>
                string.Equals(subcommand.Name, segment, StringComparison.Ordinal) ||
                subcommand.Aliases.Contains(segment, StringComparer.Ordinal));

            if (match is null)
            {
                command = null;
                unresolvedSegment = segment;
                resolvedParent = current;
                return false;
            }

            current = match;
        }

        command = current;
        unresolvedSegment = null;
        resolvedParent = current;
        return true;
    }

    /// <summary>
    /// Returns whether <paramref name="command"/> is the <c>playfab</c> command or lives beneath it.
    /// </summary>
    internal static bool IsUnderPlayFab(RootCommand rootCommand, Command command)
    {
        ArgumentNullException.ThrowIfNull(rootCommand);
        ArgumentNullException.ThrowIfNull(command);

        if (ReferenceEquals(rootCommand, command))
        {
            return false;
        }

        var segments = Resolve(rootCommand, command);
        return segments.Count > 0 &&
            string.Equals(segments[0], PlayFabCommandName, StringComparison.Ordinal);
    }

}
