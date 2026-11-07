// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace XboxDevCLI.Services;

/// <summary>
/// The settings built into the CLI, the lowest configuration layer. They're an embedded resource,
/// not a file beside the executable, so the CLI runs the same from a symlink, a package manager's
/// shim, or a lone copy of the executable. <c>XBOX_</c> environment variables and command options
/// change them.
/// </summary>
internal static class BuiltInSettings
{
    internal const string ResourceName = "XboxDevCLI.Resources.defaults.json";

    // Per async flow, so a test that replaces the settings doesn't affect tests running beside it.
    private static readonly AsyncLocal<Func<Stream>?> Replacement = new();

    /// <summary>Opens the built-in settings JSON.</summary>
    /// <exception cref="InvalidOperationException">The resource is missing from the build.</exception>
    internal static Stream Open() =>
        Replacement.Value?.Invoke()
        ?? typeof(BuiltInSettings).Assembly.GetManifestResourceStream(ResourceName)
        ?? throw new InvalidOperationException(
            $"The built-in settings resource '{ResourceName}' is missing from this build of the XBOX CLI. Reinstall it.");

    /// <summary>
    /// Replaces the built-in settings with what <paramref name="open"/> returns, for the calling
    /// async flow, until the result is disposed. For tests.
    /// </summary>
    internal static IDisposable Replace(Func<Stream> open)
    {
        var previous = Replacement.Value;
        Replacement.Value = open;
        return new Restore(previous);
    }

    private sealed class Restore(Func<Stream>? previous) : IDisposable
    {
        public void Dispose() => Replacement.Value = previous;
    }
}
