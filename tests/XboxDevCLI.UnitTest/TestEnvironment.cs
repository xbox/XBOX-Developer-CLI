// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using XboxDevCLI.Services;

namespace XboxDevCLI.UnitTest;

/// <summary>
/// Keeps every test off the developer's own CLI state: saved context comes from a directory of the
/// run's own, nobody is signed in, no service-principal mode is selected, and no XblPCSandbox.exe path is configured, unless a test says otherwise and calls <see cref="Reset"/>
/// when it's done.
/// </summary>
internal static class TestEnvironment
{
    /// <summary>The state directory saved context is read from by default. Nothing creates it.</summary>
    public static string StateDirectory { get; } =
        Path.Combine(Path.GetTempPath(), $"xbox-test-state-{Guid.NewGuid():N}");

#pragma warning disable CA2255 // Test-wide defaults, set once before any test runs.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Initialize()
    {
        // A developer who points the CLI at a real XblPCSandbox.exe must not point the tests at it.
        Environment.SetEnvironmentVariable("XBOX_XBLPCSANDBOX__PATH", null);
        Reset();
    }

    /// <summary>Restores the defaults: the run's own state directory, nobody signed in, and user sign-in.</summary>
    public static void Reset()
    {
        ContextStore.DefaultDirectory = () => StateDirectory;
        ContextResolver.ReadIdentityAsync = _ => Task.FromResult<SignedInIdentity?>(null);
        UseUserSignIn();
    }

    /// <summary>
    /// Selects a service-principal mode through the authentication settings reader rather than the
    /// process environment, until the returned scope is disposed.
    /// </summary>
    public static IDisposable UseAuthenticationMode(string mode)
    {
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [AutomationAuthenticationSettings.ModeVariable] = mode,
        };
        AutomationAuthenticationSettings.ReadEnvironment = () => settings;
        return new Scope(UseUserSignIn);
    }

    // A developer who exports XBOX_AUTHENTICATION__MODE must not put user-mode tests into automation.
    private static void UseUserSignIn() =>
        AutomationAuthenticationSettings.ReadEnvironment = () => new Dictionary<string, string>();

    /// <summary>
    /// <paramref name="stderr"/> without the changed-endpoint warnings, which a command that calls a
    /// test gateway writes before anything else.
    /// </summary>
    public static string WithoutEndpointWarnings(string stderr)
    {
        var lines = new List<string>();
        var inWarning = false;
        foreach (var line in stderr.Split(Environment.NewLine))
        {
            inWarning = line.StartsWith("Warning: the ", StringComparison.Ordinal)
                && line.Contains(" endpoint is overridden ", StringComparison.Ordinal)
                || (inWarning && line.StartsWith("         ", StringComparison.Ordinal));
            if (!inWarning)
            {
                lines.Add(line);
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private sealed class Scope(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
