// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;
using Xunit;

namespace XboxDevCLI.UnitTest;

/// <summary>
/// A terminal-cell width oracle for the table fixtures, written independently of the code it is
/// used to check.
/// </summary>
/// <remarks>
/// Alignment can only be asserted against some notion of how wide a rendered row is, and using
/// <c>DisplayWidth</c> for that would make the assertion circular: reverting the renderer to
/// <see cref="string.Length"/> would change the yardstick along with the thing being measured, and
/// every alignment test would keep passing.
/// <para>
/// So the widths here are a hand-transcribed table of the specific clusters the fixtures use, read
/// off UAX #11 rather than off the implementation, and an unrecognised cluster is an error rather
/// than a guess. A new fixture has to state what it expects instead of quietly borrowing the
/// answer from the code under test.
/// </para>
/// </remarks>
internal static class TerminalCells
{
    /// <summary>
    /// Grapheme clusters the table fixtures use, with the cells a terminal advances for each.
    /// </summary>
    private static readonly Dictionary<string, int> Known = new(StringComparer.Ordinal)
    {
        // Japanese. Kanji and katakana are East_Asian_Width=Wide.
        ["日"] = 2,
        ["本"] = 2,
        ["語"] = 2,
        ["ビ"] = 2,
        ["ル"] = 2,
        ["ド"] = 2,
        ["サ"] = 2,
        ["ー"] = 2,
        ["バ"] = 2,
        ["ス"] = 2,
        ["タ"] = 2,
        ["ジ"] = 2,
        ["オ"] = 2,

        // Chinese. Also Wide.
        ["中"] = 2,
        ["文"] = 2,
        ["服"] = 2,
        ["务"] = 2,
        ["器"] = 2,

        // Korean, precomposed and decomposed. A leading consonant plus its conjoining vowel and
        // final consonant is one cluster, drawn in the same two cells as the composed syllable.
        ["한"] = 2,
        ["글"] = 2,
        ["\u1100\u1161\u11A8"] = 2,

        // Fullwidth Latin.
        ["ｆ"] = 2,
        ["ｕ"] = 2,
        ["ｌ"] = 2,

        // A base plus a combining acute accent: one glyph in one cell, two code units. Stacking
        // more marks on the same base does not make it any wider.
        ["e\u0301"] = 1,
        ["e\u0301\u0301\u0301"] = 1,
        ["a\u0301"] = 1,
        ["o\u0301\u0308"] = 1,

        // Devanagari. A consonant and its spacing vowel sign are one cluster, but the vowel sign
        // advances the cursor the way a letter does rather than composing like an accent.
        ["\u0915\u093E"] = 2,
        ["\u0928\u0940"] = 2,

        // Emoji. A surrogate pair, a ZWJ family sequence, a skin-tone modifier sequence, and a
        // regional indicator pair are each one cluster in two cells, however many scalars deep.
        ["\U0001F600"] = 2,
        ["\U0001F3AE"] = 2,
        ["\U0001F468\u200D\U0001F469\u200D\U0001F467\u200D\U0001F466"] = 2,
        ["\U0001F44D\U0001F3FD"] = 2,
        ["\U0001F1EF\U0001F1F5"] = 2,
    };

    /// <summary>
    /// Measures <paramref name="value"/> in terminal cells.
    /// </summary>
    /// <remarks>
    /// Fails the test when the value contains a cluster this oracle has no hand-checked width
    /// for, rather than guessing at one.
    /// </remarks>
    internal static int Measure(string value)
    {
        var total = 0;
        var remaining = value.AsSpan();

        while (!remaining.IsEmpty)
        {
            var length = StringInfo.GetNextTextElementLength(remaining);
            total += MeasureCluster(remaining[..length].ToString());
            remaining = remaining[length..];
        }

        return total;
    }

    private static int MeasureCluster(string cluster)
    {
        if (Known.TryGetValue(cluster, out var width))
        {
            return width;
        }

        // Printable ASCII, which is every character the renderer itself contributes: the spaces
        // it pads with, the dashes of the separator row, and the ellipsis.
        if (cluster.Length == 1 && cluster[0] is >= ' ' and <= '~')
        {
            return 1;
        }

        Assert.Fail(
            $"The width oracle has no hand-checked width for the cluster " +
            $"{string.Join(" ", cluster.Select(unit => $"U+{(int)unit:X4}"))}. Add it with a width " +
            "read from UAX #11 rather than from the renderer.");
        return 0;
    }
}
