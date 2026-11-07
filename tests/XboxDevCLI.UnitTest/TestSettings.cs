// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using Microsoft.Extensions.Configuration;
using XboxDevCLI.Services;

namespace XboxDevCLI.UnitTest;

/// <summary>The CLI's built-in settings, and ways to make them fail, for tests.</summary>
internal static class TestSettings
{
    /// <summary>The message <see cref="UseUnreadable"/> fails loading with.</summary>
    internal const string UnreadableMessage = "The built-in settings can't be read (test).";

    /// <summary>The built-in settings alone, as the CLI reads them before variables and options.</summary>
    internal static IConfiguration BuiltIn() =>
        new ConfigurationBuilder().AddJsonStream(BuiltInSettings.Open()).Build();

    /// <summary>The built-in value of <paramref name="key"/>.</summary>
    internal static string? BuiltIn(string key) => BuiltIn()[key];

    /// <summary>
    /// Makes loading the built-in settings fail with an <see cref="InvalidDataException"/> for
    /// the calling async flow, until disposed.
    /// </summary>
    internal static IDisposable UseUnreadable() =>
        BuiltInSettings.Replace(() => throw new InvalidDataException(UnreadableMessage));

    /// <summary>Replaces the built-in settings with <paramref name="json"/>, until disposed.</summary>
    internal static IDisposable Use(string json) =>
        BuiltInSettings.Replace(() => new MemoryStream(Encoding.UTF8.GetBytes(json)));
}
