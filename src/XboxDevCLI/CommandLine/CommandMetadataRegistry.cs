// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Runtime.CompilerServices;
using XboxDevCLI.Safety;

namespace XboxDevCLI.CommandLine;

/// <summary>
/// Side-table associating <see cref="CommandMetadata"/> and <see cref="OptionAudience"/> with the
/// symbols in the command tree.
/// </summary>
/// <remarks>
/// A side table rather than a <see cref="Command"/> subclass: every command factory constructs
/// plain <see cref="Command"/> instances, and the metadata is presentation concern that the
/// factories should be able to opt into without changing how they build commands. Using
/// <see cref="ConditionalWeakTable{TKey,TValue}"/> keeps the registry from pinning command
/// instances alive, which matters for tests that build many disposable root commands.
/// </remarks>
internal static class CommandMetadataRegistry
{
    private static readonly ConditionalWeakTable<Command, CommandMetadata> Metadata = new();
    private static readonly ConditionalWeakTable<Option, object> Audiences = new();

    // Shared wording so an explicitly classified command and one classified from its operation
    // name describe the same effect in the same words. A command that mutates through a storage
    // URL is no less a mutation than one that calls an Update* operation, and a reader comparing
    // two help pages should not have to work out whether a difference in phrasing means anything.
    private const string MutatingDescription =
        "Mutates production title state. Review the request before running it.";

    private const string DestructiveDescription =
        "Destructive and irreversible. Requires --confirm (--yes, -y), " +
        "or an interactive confirmation at a terminal.";

    private const string DependsOnApiPathDescription =
        "Depends on the API path supplied. Operations other than Get, List, and Search mutate " +
        "production title state, and Delete and Shutdown operations additionally require " +
        "--confirm (--yes, -y).";

    /// <summary>Attaches metadata to <paramref name="command"/> and returns it for chaining.</summary>
    internal static Command WithMetadata(this Command command, CommandMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(metadata);

        Metadata.AddOrUpdate(command, metadata);
        return command;
    }

    /// <summary>Marks the audience an option is meaningful for, and returns it for chaining.</summary>
    internal static TOption WithAudience<TOption>(this TOption option, OptionAudience audience)
        where TOption : Option
    {
        ArgumentNullException.ThrowIfNull(option);

        Audiences.AddOrUpdate(option, audience);
        return option;
    }

    /// <summary>Gets the metadata for <paramref name="command"/>, or an empty instance.</summary>
    internal static CommandMetadata GetMetadata(Command command)
    {
        ArgumentNullException.ThrowIfNull(command);

        return Metadata.TryGetValue(command, out var metadata) ? metadata : new CommandMetadata();
    }

    /// <summary>Gets the audience for <paramref name="option"/>, defaulting to universal.</summary>
    internal static OptionAudience GetAudience(Option option)
    {
        ArgumentNullException.ThrowIfNull(option);

        return Audiences.TryGetValue(option, out var audience) && audience is OptionAudience value
            ? value
            : OptionAudience.Universal;
    }

    /// <summary>
    /// Describes the safety of a command from the PlayFab API paths it invokes, or from an
    /// explicit classification where the operation names would mislead.
    /// </summary>
    /// <remarks>
    /// Classification is derived from the underlying operation names, never from the friendly CLI
    /// name: renaming <c>ops</c> to <c>operation execute</c> must not change what the CLI tells
    /// the user about the call's effect. Read-only means every path is a
    /// <c>Get</c>/<c>List</c>/<c>Search</c> operation.
    /// <para>
    /// <see cref="CommandMetadata.Safety"/> overrides that derivation, because the operation-name
    /// rule is a heuristic over names and has exceptions in both directions:
    /// <c>group member is-member</c> reads through <c>/Group/IsMember</c>, which the rule does not
    /// recognise as a read, and <c>mps asset upload</c> writes over the storage URL that
    /// <c>GetAssetUploadUrl</c> returns. The override is bounded rather than general — it is
    /// applied <em>after</em> the destructive tier, so it can never talk a command out of the
    /// <c>--confirm</c> warning its path will still trigger, and the combinations it may be used
    /// in are enforced by <c>CommandSafetyMetadataTests</c>.
    /// </para>
    /// The destructive tier reuses <see cref="DestructiveOperations"/> so this description and the
    /// runtime gate cannot drift. Returns <c>null</c> when a command records neither a path nor a
    /// classification, so Help omits the section rather than guessing.
    /// </remarks>
    internal static string? DescribeSafety(CommandMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var paths = metadata.ApiPaths ?? [];
        string description;

        // The destructive tier is decided before any explicit classification, because it is the
        // one Help shares with a runtime gate. An explicit value exists to correct an operation
        // name that misleads — /Group/IsMember reads, GetAssetUploadUrl precedes a write — and may
        // move a command between read-only and mutating for that reason. It must never be able to
        // talk a command out of the --confirm warning its path will still trigger.
        if (paths.Any(DestructiveOperations.IsDestructive))
        {
            description = DestructiveDescription;
        }
        else if (metadata.Safety is CommandSafety explicitSafety)
        {
            description = explicitSafety switch
            {
                CommandSafety.ReadOnly => "Read-only. Does not modify remote state.",
                CommandSafety.LocalState => "Changes local CLI state only.",
                CommandSafety.Mutates => MutatingDescription,
                CommandSafety.DependsOnApiPath => DependsOnApiPathDescription,
                CommandSafety.RemoteMutation => "Mutates production state. Review the request before running it.",
                CommandSafety.MachineState =>
                    "Changes this PC, such as its XBOX sandbox or XBOX apps. Does not modify remote state.",
                _ => throw new ArgumentOutOfRangeException(nameof(explicitSafety)),
            };
        }
        else if (paths.Count == 0)
        {
            return null;
        }
        else
        {
            description = paths.All(DestructiveOperations.IsReadOnly)
                ? "Read-only. Does not modify the title."
                : MutatingDescription;
        }

        return string.IsNullOrWhiteSpace(metadata.SafetyDetails)
            ? description
            : $"{description} {metadata.SafetyDetails}";
    }

    /// <summary>
    /// Returns whether <paramref name="command"/> calls PlayFab: it records the PlayFab paths it
    /// calls, or takes one at run time. <c>CommandSafetyMetadataTests</c> makes every command that
    /// does work record its paths, or list why it can't, as <c>context get</c> does.
    /// </summary>
    internal static bool CallsPlayFab(Command command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var metadata = GetMetadata(command);
        return metadata.ApiPaths is { Count: > 0 } || metadata.Safety == CommandSafety.DependsOnApiPath;
    }

    /// <summary>
    /// Returns whether running <paramref name="command"/> can change remote state, judged the way
    /// <see cref="DescribeSafety"/> describes it. <paramref name="apiPath"/> is the PlayFab path
    /// for a command that takes it at run time. A command that declares neither a path nor a
    /// classification isn't counted as a change.
    /// </summary>
    internal static bool IsMutation(Command command, string? apiPath = null)
    {
        ArgumentNullException.ThrowIfNull(command);

        var metadata = GetMetadata(command);
        IReadOnlyList<string> paths = apiPath is null ? metadata.ApiPaths ?? [] : [apiPath];
        if (paths.Any(DestructiveOperations.IsDestructive))
        {
            return true;
        }

        return metadata.Safety switch
        {
            // MachineState changes this PC, never remote state, so it is not counted here.
            CommandSafety.ReadOnly or CommandSafety.LocalState or CommandSafety.MachineState => false,
            CommandSafety.Mutates or CommandSafety.RemoteMutation => true,
            _ => paths.Any(path => !DestructiveOperations.IsReadOnly(path)),
        };
    }
}
