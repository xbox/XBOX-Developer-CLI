// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace XboxDevCLI.CommandLine;

// Rejects symlinks and junctions present in caller-selected output paths during validation.
// This does not defend against another local process concurrently replacing path components;
// callers are expected to write only to directories controlled by the current user.
internal static class SafeOutputDirectory
{
    public static string Create(string destination)
    {
        string directory = Path.GetFullPath(destination);
        ValidateExistingComponents(directory);
        Directory.CreateDirectory(directory);

        // Directory.CreateDirectory may traverse existing components, so check the resulting
        // path again before callers stage output. This is not an atomic no-follow guarantee.
        ValidateExistingComponents(directory);
        return directory;
    }

    public static void ValidateExistingComponents(string directory)
    {
        string fullPath = Path.GetFullPath(directory);
        var components = new Stack<string>();
        string? component = fullPath;

        // Walk from the filesystem root toward the destination so an unsafe parent is
        // rejected before any child path is used for staging or final output.
        while (!string.IsNullOrEmpty(component))
        {
            components.Push(component);
            string? parent = Directory.GetParent(component)?.FullName;
            if (string.Equals(parent, component, StringComparison.Ordinal))
            {
                break;
            }

            component = parent;
        }

        while (components.TryPop(out string? path))
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(path);
            }
            catch (FileNotFoundException)
            {
                // Missing components are safe to create after all existing parents are checked.
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                // Missing components are safe to create after all existing parents are checked.
                continue;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0 && !IsSystemLink(path))
            {
                throw new IOException(
                    $"The output directory path contains a reparse point: '{path}'.");
            }

            if ((attributes & FileAttributes.Directory) == 0)
            {
                throw new IOException(
                    $"The output directory path contains a non-directory component: '{path}'.");
            }
        }
    }

    // macOS links /tmp, /var, and /etc to real directories under /private. They belong to the OS,
    // so they are not a redirect planted in the caller's path.
    internal static bool IsSystemLink(string path) =>
        OperatingSystem.IsMacOS() && Path.TrimEndingDirectorySeparator(path) is "/tmp" or "/var" or "/etc";
}
