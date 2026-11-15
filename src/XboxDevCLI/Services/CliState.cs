// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace XboxDevCLI.Services;

internal static class CliState
{
    // On Unix, the default option returns an empty string when the folder doesn't exist yet,
    // which would put state, including the token cache, in the current directory.
    internal static string GetDefaultDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolderOption.DoNotVerify),
            "XboxDevCLI");
}
