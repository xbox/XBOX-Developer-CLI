// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;
using System.Diagnostics.CodeAnalysis;

namespace XboxDevCLI.Services;

internal static class XboxTitleId
{
    public static bool TryNormalize(string value, [NotNullWhen(true)] out string? normalized)
    {
        var digits = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? value[2..]
            : value;
        if (digits.Length is < 1 or > 8 ||
            !uint.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var titleId))
        {
            normalized = null;
            return false;
        }

        normalized = titleId.ToString("X8", CultureInfo.InvariantCulture);
        return true;
    }
}
