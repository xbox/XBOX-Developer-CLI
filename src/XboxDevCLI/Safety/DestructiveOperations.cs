// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace XboxDevCLI.Safety;

/// <summary>
/// Identifies catastrophic, irreversible PlayFab operations (such as deleting a
/// title, store, build, segment, or master player account, or shutting down a
/// running multiplayer server session) that must not be executed without explicit
/// confirmation from the user.
/// </summary>
/// <remarks>
/// The CLI is designed to be driven by automation, including AI agents. These
/// operations are irreversible and can wipe an entire title, studio, or namespace,
/// or force-terminate an active game server session, so they are gated behind an
/// explicit, hard-coded confirmation step to prevent an agent from performing them
/// without the user's deliberate authorization.
/// </remarks>
internal static class DestructiveOperations
{
    /// <summary>
    /// Operation-name prefixes that denote a catastrophic, irreversible action.
    /// "Delete*" permanently wipes persisted data; "Shutdown*" force-terminates a
    /// running multiplayer server session. Both are irreversible.
    /// </summary>
    private static readonly string[] DestructivePrefixes = { "Delete", "Shutdown" };

    /// <summary>
    /// Destructive operations whose direct target, what they delete, shut down, or replace, is a
    /// context value rather than something named in the request body, with the option that names
    /// it. Their target has to come from the command line: saved context and environment variables
    /// don't count, because another session or a shell profile can change them unseen.
    /// </summary>
    private static readonly Dictionary<string, ContextTarget> ContextTargets =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["DeleteTitle"] = new("--playfab-title-id"),

            // Appending is the default and leaves the policy in place; overwriting replaces the
            // title's whole API access policy, and a wrong one can lock every player out.
            ["UpdatePolicy"] = new("--playfab-title-id", Mode: "--overwrite"),
        };

    /// <summary>
    /// Base URI used only to canonicalize relative API paths during classification. It is never
    /// contacted; the <c>.invalid</c> TLD is reserved and guaranteed not to resolve.
    /// </summary>
    private static readonly Uri NormalizationBase = new("https://placeholder.invalid/", UriKind.Absolute);

    /// <summary>
    /// Returns <c>true</c> when the supplied PlayFab API path corresponds to a
    /// destructive operation.
    /// </summary>
    /// <remarks>
    /// Detection is based on the operation name (the final segment of the path):
    /// any operation whose name begins with a known destructive prefix
    /// (<c>Delete</c>, <c>Shutdown</c>) is treated as destructive because the
    /// effect is irreversible.
    /// </remarks>
    public static bool IsDestructive(string? apiPath)
    {
        if (string.IsNullOrWhiteSpace(apiPath))
        {
            return false;
        }

        var operation = GetOperationName(apiPath);
        foreach (var prefix in DestructivePrefixes)
        {
            if (operation.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns whether an API path is read-only: only <c>Get*</c>, <c>List*</c>, and
    /// <c>Search*</c> operations are safe. The
    /// operation is read the way the request will reach it, so a query or fragment such as
    /// <c>/Admin/SetTitleData?next=/GetTitleData</c> can't make a write look like a read.
    /// </summary>
    public static bool IsReadOnly(string? apiPath)
    {
        if (string.IsNullOrWhiteSpace(apiPath))
        {
            return false;
        }

        var operation = GetOperationName(apiPath);

        return operation.StartsWith("Get", StringComparison.OrdinalIgnoreCase) ||
            operation.StartsWith("List", StringComparison.OrdinalIgnoreCase) ||
            operation.StartsWith("Search", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns the context value <paramref name="apiPath"/> destructively acts on directly, such as
    /// <c>--playfab-title-id</c> for <c>/Admin/DeleteTitle</c>, or <c>null</c>.
    /// </summary>
    public static ContextTarget? GetContextTarget(string? apiPath) =>
        string.IsNullOrWhiteSpace(apiPath)
            ? null
            : ContextTargets.GetValueOrDefault(GetOperationName(apiPath));

    /// <summary>
    /// Extracts the operation name (the final path segment) from an API path, canonicalizing it the
    /// same way the transport layer (<see cref="System.Uri"/>) will when the request is built. The
    /// gate must classify against that canonical form: a destructive op disguised with a backslash
    /// (<c>Admin\DeleteTitle</c>) or a percent-encoded character (<c>Admin/%44eleteTitle</c>) is
    /// normalized to the real path on the wire, so without this it could slip past <c>--confirm</c>.
    /// Resolving the path against a base URI decodes percent-encoding, folds
    /// backslashes to '/', and drops any query/fragment in one step. The read-only check uses it
    /// too, so a query or fragment can't make a write look like a read.
    /// For example, "/Admin/DeleteTitle" yields "DeleteTitle".
    /// </summary>
    internal static string GetOperationName(string apiPath)
    {
        var relative = apiPath.Trim().Replace('\\', '/').TrimStart('/');
        var path = Uri.TryCreate(NormalizationBase, relative, out var resolved)
            ? resolved.AbsolutePath
            : relative;

        path = path.Trim('/');
        var lastSlash = path.LastIndexOf('/');
        return lastSlash >= 0 ? path[(lastSlash + 1)..] : path;
    }
}

/// <summary>
/// The context value a destructive operation acts on directly, as the option that names it.
/// </summary>
/// <param name="Option">The option naming the target, such as <c>--playfab-title-id</c>.</param>
/// <param name="Mode">
/// The flag that makes the operation destructive, for an operation that is destructive only in one
/// mode, such as <c>--overwrite</c> for <c>/Admin/UpdatePolicy</c>; <c>null</c> when it always is.
/// The target is refused only from a command that takes this flag and was given it.
/// </param>
internal sealed record ContextTarget(string Option, string? Mode = null);
