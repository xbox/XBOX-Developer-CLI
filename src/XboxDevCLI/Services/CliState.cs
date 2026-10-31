// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace XboxDevCLI.Services;

internal static class CliState
{
    internal static string GetDefaultDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XboxDevCLI");
}
