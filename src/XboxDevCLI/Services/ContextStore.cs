// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace XboxDevCLI.Services;

/// <summary>
/// A value to save for the service at <see cref="Endpoint"/> (its base URL), or to remove when
/// <see cref="Value"/> is <c>null</c>.
/// </summary>
internal sealed record ContextChange(string Key, string Endpoint, string? Value);

/// <summary>
/// The context saved by <c>xbox context set</c>, in <c>context.json</c> in the CLI state
/// directory. Values are scoped to the signed-in account and to the host of the endpoint they
/// belong to, so another account or a test endpoint on another host never picks them up. The
/// default profile's values sit at the top of the file and named profiles beside them, so a file
/// saved before profiles existed is the default profile as it is.
/// </summary>
internal sealed class ContextStore
{
    internal const string FileName = "context.json";

    // Version 2 added named profiles. A version-1 file is the same shape without them, and a
    // version-1 CLI refuses this version rather than rewriting the file without its profiles.
    private const int CurrentVersion = 2;
    private const int ReplaceAttempts = 100;
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(50);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _filePath;

    public ContextStore()
        : this(Path.Combine(DefaultDirectory(), FileName))
    {
    }

    /// <summary>Where <see cref="ContextStore()"/> keeps saved context. Tests point it elsewhere.</summary>
    internal static Func<string> DefaultDirectory { get; set; } = CliState.GetDefaultDirectory;

    internal ContextStore(string filePath)
    {
        _filePath = filePath;
    }

    /// <summary>
    /// Reads a value saved in <paramref name="profile"/>, or <c>null</c> when this identity and
    /// endpoint have none. A named profile the identity has nothing saved in doesn't exist, so
    /// asking for one fails rather than quietly finding nothing.
    /// </summary>
    public async Task<string?> GetAsync(
        SignedInIdentity identity,
        string key,
        string endpoint,
        string profile = ContextProfiles.Default,
        CancellationToken cancellationToken = default)
    {
        var state = await ReadStateAsync(cancellationToken);
        var scopes = FindScopes(state, identity, profile)
            ?? throw new InvalidOperationException(ContextProfiles.NotFoundMessage(profile));
        return scopes.TryGetValue(CreateScope(identity, endpoint), out var values)
            ? values.GetValueOrDefault(key)
            : null;
    }

    /// <summary>
    /// Whether anything is saved under any of <paramref name="keys"/> in <paramref name="profile"/>,
    /// for any account, so a caller can skip reading the account when nothing is. A named profile
    /// nobody has saved anything in doesn't exist, and asking for one fails.
    /// </summary>
    public async Task<bool> HasAnyAsync(
        IReadOnlyCollection<string> keys,
        string profile,
        CancellationToken cancellationToken = default)
    {
        var state = await ReadStateAsync(cancellationToken);
        var scopes = profile == ContextProfiles.Default
            ? state.Contexts
            : state.Profiles?.GetValueOrDefault(profile)
              ?? throw new InvalidOperationException(ContextProfiles.NotFoundMessage(profile));
        return scopes.Values.Any(values => keys.Any(values.ContainsKey));
    }

    /// <summary>Whether <paramref name="profile"/> exists for <paramref name="identity"/>.</summary>
    public async Task<bool> HasProfileAsync(
        SignedInIdentity identity,
        string profile,
        CancellationToken cancellationToken = default) =>
        FindScopes(await ReadStateAsync(cancellationToken), identity, profile) is not null;

    /// <summary>
    /// The profiles <paramref name="identity"/> has: <see cref="ContextProfiles.Default"/>, which
    /// always exists, then every named profile with something saved in it, by name.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListProfilesAsync(
        SignedInIdentity identity,
        CancellationToken cancellationToken = default)
    {
        var state = await ReadStateAsync(cancellationToken);
        var prefix = IdentityPrefix(identity);
        return
        [
            ContextProfiles.Default,
            .. (state.Profiles ?? [])
                .Where(profile => profile.Value.Keys.Any(scope => scope.StartsWith(prefix, StringComparison.Ordinal)))
                .Select(profile => profile.Key)
                .Order(StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// Saves and removes values in <paramref name="profile"/> in one write, leaving every other
    /// entry in place, including keys this version of the CLI does not know. Saving to a named
    /// profile creates it.
    /// </summary>
    /// <remarks>
    /// Any number of shells, scripts, and agents can run the CLI at once, so a write holds
    /// <see cref="LockAsync"/> from reading the file to replacing it, and two concurrent saves
    /// both take effect.
    /// </remarks>
    public async Task UpdateAsync(
        SignedInIdentity identity,
        IEnumerable<ContextChange> changes,
        string profile = ContextProfiles.Default,
        CancellationToken cancellationToken = default)
    {
        var changeList = changes.ToList();

        // Removing what isn't saved is a no-op, and shouldn't need a writable state directory.
        if (!Apply(await ReadStateAsync(cancellationToken), identity, changeList, profile))
        {
            return;
        }

        using var writeLock = await LockAsync(cancellationToken);
        var state = await ReadStateAsync(cancellationToken);
        if (Apply(state, identity, changeList, profile))
        {
            await WriteStateAsync(state, cancellationToken);
        }
    }

    /// <summary>
    /// Removes every value saved for <paramref name="identity"/> in <paramref name="profile"/>, on
    /// every endpoint; returns whether there were any.
    /// </summary>
    public async Task<bool> RemoveAllAsync(
        SignedInIdentity identity,
        string profile = ContextProfiles.Default,
        CancellationToken cancellationToken = default)
    {
        if (!RemoveAll(await ReadStateAsync(cancellationToken), identity, profile))
        {
            return false;
        }

        using var writeLock = await LockAsync(cancellationToken);
        var state = await ReadStateAsync(cancellationToken);
        if (RemoveAll(state, identity, profile))
        {
            await WriteStateAsync(state, cancellationToken);
        }

        return true;
    }

    /// <summary>
    /// The key values are saved under: tenant and user, then the endpoint's host. This is the
    /// format seller selections have always used, so a seller saved for a test gateway stays there.
    /// </summary>
    internal static string CreateScope(SignedInIdentity identity, string endpoint)
    {
        // A PlayFab template can put the title in the host ("https://{0}.playfabapi.com"). Only
        // the host matters here, so any stand-in for the placeholder works.
        var url = endpoint.Replace("{0}", "title", StringComparison.Ordinal);
        var authority = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Authority : url;
        return IdentityPrefix(identity) + authority.ToLowerInvariant();
    }

    private static string IdentityPrefix(SignedInIdentity identity) =>
        $"{identity.TenantId}:{identity.ObjectId}@";

    /// <summary>
    /// The values saved in <paramref name="profile"/>, keyed by scope, or <c>null</c> when it
    /// doesn't exist: the default profile always does, and a named one only while
    /// <paramref name="identity"/> has something saved in it.
    /// </summary>
    private static Dictionary<string, Dictionary<string, string?>>? FindScopes(
        ContextState state,
        SignedInIdentity identity,
        string profile)
    {
        if (profile == ContextProfiles.Default)
        {
            return state.Contexts;
        }

        var prefix = IdentityPrefix(identity);
        return state.Profiles?.GetValueOrDefault(profile) is { } scopes
               && scopes.Keys.Any(scope => scope.StartsWith(prefix, StringComparison.Ordinal))
            ? scopes
            : null;
    }

    /// <summary>
    /// Applies <paramref name="changes"/> to <paramref name="profile"/>; returns whether anything
    /// changed.
    /// </summary>
    private static bool Apply(
        ContextState state,
        SignedInIdentity identity,
        IReadOnlyList<ContextChange> changes,
        string profile)
    {
        var scopes = profile == ContextProfiles.Default
            ? state.Contexts
            : (state.Profiles ??= new(StringComparer.Ordinal)).GetValueOrDefault(profile)
              ?? (state.Profiles[profile] = new(StringComparer.Ordinal));
        var changed = false;
        foreach (var change in changes)
        {
            var scope = CreateScope(identity, change.Endpoint);
            if (change.Value is null)
            {
                if (scopes.TryGetValue(scope, out var existing)
                    && existing.Remove(change.Key))
                {
                    changed = true;
                    if (existing.Count == 0)
                    {
                        scopes.Remove(scope);
                    }
                }

                continue;
            }

            if (!scopes.TryGetValue(scope, out var values))
            {
                values = new Dictionary<string, string?>(StringComparer.Ordinal);
                scopes[scope] = values;
            }

            changed |= values.GetValueOrDefault(change.Key) != change.Value;
            values[change.Key] = change.Value;
        }

        RemoveEmptyProfiles(state);
        return changed;
    }

    /// <summary>
    /// Removes <paramref name="identity"/>'s scopes from <paramref name="profile"/>; returns
    /// whether there were any.
    /// </summary>
    private static bool RemoveAll(ContextState state, SignedInIdentity identity, string profile)
    {
        if (FindScopes(state, identity, profile) is not { } values)
        {
            return false;
        }

        var prefix = IdentityPrefix(identity);
        var scopes = values.Keys
            .Where(scope => scope.StartsWith(prefix, StringComparison.Ordinal))
            .ToList();
        foreach (var scope in scopes)
        {
            values.Remove(scope);
        }

        RemoveEmptyProfiles(state);
        return scopes.Count > 0;
    }

    /// <summary>A named profile with nothing saved in it no longer exists.</summary>
    private static void RemoveEmptyProfiles(ContextState state)
    {
        if (state.Profiles is null)
        {
            return;
        }

        foreach (var empty in state.Profiles.Where(profile => profile.Value.Count == 0).ToList())
        {
            state.Profiles.Remove(empty.Key);
        }

        if (state.Profiles.Count == 0)
        {
            state.Profiles = null;
        }
    }

    /// <summary>
    /// Takes the write lock: <c>context.json.lock</c>, opened exclusively. The OS releases it when
    /// the handle closes or the process exits, even after a crash, so the leftover file never
    /// blocks a later write.
    /// </summary>
    private async Task<FileStream> LockAsync(CancellationToken cancellationToken)
    {
        var path = _filePath + ".lock";
        var deadline = DateTime.UtcNow + LockTimeout;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            while (true)
            {
                try
                {
                    return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException) when (DateTime.UtcNow < deadline)
                {
                    // Another process is saving; it holds the lock only for one small write.
                    await Task.Delay(LockRetryDelay, cancellationToken);
                }
            }
        }
        catch (UnauthorizedAccessException exception)
        {
            throw NotWritable(exception);
        }
    }

    private async Task<ContextState> ReadStateAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return new ContextState();
        }

        // Copy the bytes and close the file before parsing: on Windows a save can't replace the
        // file while any reader has it open, so readers hold it as briefly as possible. Sharing
        // delete lets this open succeed while a save is replacing the file; the save retries
        // until the handle closes.
        byte[] bytes;
        await using (var stream = new FileStream(
            _filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete))
        {
            bytes = new byte[stream.Length];
            await stream.ReadExactlyAsync(bytes, cancellationToken);
        }

        ContextState? state;
        try
        {
            state = JsonSerializer.Deserialize<ContextState>(bytes, JsonOptions);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            throw Invalid(exception);
        }

        // A null scope ("<scope>": null) is as malformed as null contexts; without this check it
        // fails later as a null dereference that names neither the file nor the fix.
        if (state?.Contexts is null
            || state.Contexts.Values.Any(values => values is null)
            || state.Profiles?.Values.Any(scopes => scopes is null || scopes.Values.Any(values => values is null)) == true)
        {
            throw Invalid(null);
        }

        // A newer CLI may have changed the format. Reading it as this version's would misread
        // it, and writing it back would downgrade it under that CLI.
        return state.Version > CurrentVersion
            ? throw new InvalidDataException(
                $"The saved context file '{_filePath}' was written by a newer version of the XBOX "
                + "CLI. Update the CLI to use it.")
            : state;
    }

    /// <summary>
    /// Serializes the state to a temporary file and renames it into place, so an interrupted
    /// write (Ctrl+C, power loss) cannot leave truncated JSON behind. Each write stages its own
    /// file, so two writers can't publish each other's half-written file, and a failed write
    /// removes it.
    /// </summary>
    private async Task WriteStateAsync(ContextState state, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_filePath)
            ?? throw new InvalidOperationException(
                $"Could not determine the directory for '{_filePath}'.");

        state.Version = CurrentVersion;
        var temporaryPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            try
            {
                Directory.CreateDirectory(directory);

                await using var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None);
                await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken);
            }
            catch (UnauthorizedAccessException exception)
            {
                throw NotWritable(exception);
            }

            // On Windows, replacing the file fails while another process is reading it, which
            // takes only a moment, so retry briefly rather than fail the save.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(temporaryPath, _filePath, overwrite: true);
                    return;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                      && attempt < ReplaceAttempts)
                {
                    await Task.Delay(LockRetryDelay, cancellationToken);
                }
            }
        }
        finally
        {
            // Still there only when the write or the rename failed.
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private InvalidDataException Invalid(Exception? innerException) =>
        new(
            $"The saved context file '{_filePath}' is not valid. Fix or delete it, then run "
            + $"'{CliIdentity.CommandName} context set' again.",
            innerException);

    private UnauthorizedAccessException NotWritable(UnauthorizedAccessException innerException) =>
        new(
            $"Cannot save the context because '{Path.GetDirectoryName(_filePath)}' is not "
            + "writable. Reading saved context still works; only saving requires write access.",
            innerException);

    private sealed class ContextState
    {
        public int Version { get; set; } = CurrentVersion;

        /// <summary>The default profile's values, keyed by scope.</summary>
        public Dictionary<string, Dictionary<string, string?>> Contexts { get; set; } =
            new(StringComparer.Ordinal);

        /// <summary>Named profiles, each keyed by scope like <see cref="Contexts"/>.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, Dictionary<string, Dictionary<string, string?>>>? Profiles { get; set; }
    }
}
