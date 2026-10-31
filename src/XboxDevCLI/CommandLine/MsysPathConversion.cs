// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace XboxDevCLI.CommandLine;

/// <summary>
/// Recognizes an argument that Git Bash, or another MSYS shell on Windows, converted from a
/// POSIX path before starting the CLI. MSYS turns <c>/Catalog/GetCatalogConfig</c> into
/// <c>C:/Program Files/Git/Catalog/GetCatalogConfig</c>: its install directory, then the rest
/// of the path.
/// </summary>
/// <remarks>
/// This only explains a refusal. The conversion can't always be reversed (MSYS collapses
/// repeated slashes and maps some directories, such as <c>/tmp</c>, elsewhere), so the result
/// is never used as the path to call.
/// </remarks>
internal static class MsysPathConversion
{
    /// <summary>
    /// The MSYS runtime, present in every MSYS installation, including Git for Windows.
    /// </summary>
    private const string RuntimeRelativePath = "/usr/bin/msys-2.0.dll";

    /// <summary>
    /// Whether <paramref name="argument"/> is an MSYS install directory followed by a path, and
    /// if so, the POSIX path it was most likely converted from.
    /// </summary>
    /// <param name="argument">The argument as the CLI received it.</param>
    /// <param name="isMsysRoot">Whether a directory, written with forward slashes, is an MSYS install directory.</param>
    /// <param name="originalPath">The rest of the argument after the install directory, with a leading <c>/</c>.</param>
    public static bool TryGetOriginalPath(string argument, Func<string, bool> isMsysRoot, out string originalPath)
    {
        originalPath = string.Empty;

        // MSYS writes a drive letter, a colon, and forward slashes; anything else wasn't converted.
        if (argument.Length < 4 || !char.IsAsciiLetter(argument[0]) || argument[1] != ':' || argument[2] != '/')
        {
            return false;
        }

        // The shortest prefix that is an install directory is the root the conversion added.
        for (var index = 2; index >= 0 && index < argument.Length - 1; index = argument.IndexOf('/', index + 1))
        {
            if (isMsysRoot(argument[..index]))
            {
                originalPath = argument[index..];
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="directory"/> holds the MSYS runtime. Always <c>false</c> off
    /// Windows, where nothing converts arguments this way.
    /// </summary>
    public static bool IsMsysRoot(string directory) =>
        OperatingSystem.IsWindows() && File.Exists(directory + RuntimeRelativePath);
}
