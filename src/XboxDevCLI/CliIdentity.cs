// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Reflection;

namespace XboxDevCLI;

internal static class CliIdentity
{
    internal const string DisplayName = "XBOX CLI";
    internal const string Tagline = "work seamlessly with your developer account.";

    /// <summary>
    /// The installed command name, sourced from <c>CliCommandName</c> in
    /// <c>Directory.Build.props</c> through this assembly's configured name.
    /// </summary>
    internal static string CommandName { get; } =
        typeof(CliIdentity).Assembly.GetName().Name
        ?? throw new InvalidOperationException("CLI assembly name is unavailable.");

    /// <summary>
    /// The release version, such as <c>0.1.0.9</c> or <c>0.1.0.9-preview1</c>: the informational
    /// version without its <c>+commit</c> build metadata.
    /// </summary>
    internal static string Version { get; } =
        typeof(CliIdentity).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0]
        ?? "unknown";

    /// <summary>
    /// Sent on every XboxPartner and PlayFab API request so the services can tell CLI versions apart.
    /// </summary>
    internal static string UserAgent { get; } = $"XboxDevCLI/{Version}";
}
