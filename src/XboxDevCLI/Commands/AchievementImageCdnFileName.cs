// Copyright (C) Microsoft Corporation. All rights reserved.

using XboxDevCLI.CommandLine;

namespace XboxDevCLI.Commands;

internal static class AchievementImageCdnFileName
{
    internal static string? FromUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ? FromUri(uri) : null;

    internal static string? FromUri(Uri uri)
    {
        string segment = uri.Segments.Length == 0 ? string.Empty : uri.Segments[^1];
        string filename = Uri.UnescapeDataString(segment);
        return LocalFileNameValidator.IsInvalid(filename, enforceWindowsRules: true)
            ? null
            : filename;
    }
}
