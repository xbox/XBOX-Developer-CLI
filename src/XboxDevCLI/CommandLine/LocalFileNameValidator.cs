// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace XboxDevCLI.CommandLine;

internal static class LocalFileNameValidator
{
    private const string WindowsInvalidCharacters = "<>:\"/\\|?*";

    public static bool IsInvalid(string? fileName, bool enforceWindowsRules)
    {
        return string.IsNullOrWhiteSpace(fileName) ||
            fileName is "." or ".." ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            (enforceWindowsRules &&
                (fileName.Any(character =>
                    character < ' ' || WindowsInvalidCharacters.Contains(character)) ||
                 HasInvalidWindowsSyntax(fileName)));
    }

    public static bool HasInvalidWindowsSyntax(string fileName)
    {
        if (fileName.EndsWith(' ') || fileName.EndsWith('.'))
        {
            return true;
        }

        // Windows reserves these DOS device names even when they have an extension,
        // use different casing, or would otherwise look like a normal file name.
        string baseName = fileName.Split('.')[0];
        return baseName.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            IsNumberedWindowsDeviceName(baseName, "COM") ||
            IsNumberedWindowsDeviceName(baseName, "LPT");
    }

    private static bool IsNumberedWindowsDeviceName(string baseName, string prefix)
    {
        return baseName.Length == prefix.Length + 1 &&
            baseName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            baseName[^1] is >= '1' and <= '9';
    }
}
