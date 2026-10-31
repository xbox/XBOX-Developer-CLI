// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Buffers;
using System.Globalization;
using System.Text;
using XboxDevCLI.Output.Generated;

namespace XboxDevCLI.Output;

/// <summary>
/// Measures how many terminal cells a string occupies, which is what a fixed-width table has to
/// size and pad by.
/// </summary>
/// <remarks>
/// <see cref="string.Length"/> counts UTF-16 code units, and a cell is not a code unit in either
/// direction: a CJK ideograph is one unit and two cells, a combining mark is one unit and none,
/// and an emoji family is eleven units, seven scalars, and two cells. Sizing a column by
/// <see cref="string.Length"/> therefore lines up only for values written in narrow scripts.
/// <para>
/// Measurement runs per grapheme cluster, because a cluster is what the terminal draws as one
/// glyph. .NET implements the UAX #29 cluster boundaries in
/// <see cref="StringInfo.GetNextTextElementLength(ReadOnlySpan{char})"/>, so the awkward cases —
/// a base plus several marks, a variation selector, a ZWJ emoji sequence, a regional indicator
/// pair — arrive here already grouped, and a sequence the terminal ligates into one glyph is
/// counted once instead of once per code point.
/// </para>
/// <para>
/// Two comes from the East Asian Width property, which .NET does not expose, so the Wide and
/// Fullwidth ranges are transcribed from the Unicode Character Database into
/// <see cref="EastAsianWidthTable"/> by <c>tools/UnicodeWidthCodeGen</c>. Zero comes from the
/// general category, which .NET does expose.
/// </para>
/// <para>
/// This is a rendering estimate, not a guarantee. Terminals disagree about emoji and about East
/// Asian Width's Ambiguous class; a font that lacks a glyph can render anything at any size; and
/// .NET's own Unicode tables are a release or two behind the width data, so a mark from a very
/// recent addition is not yet grouped with the base it belongs to. It is correct for the scripts
/// a table is actually sized by, which is the whole of the defect it replaces.
/// </para>
/// </remarks>
internal static class DisplayWidth
{
    /// <summary>The first code point in <see cref="EastAsianWidthTable.WideRanges"/>.</summary>
    /// <remarks>
    /// Latin, Greek, Cyrillic, Hebrew, and Arabic all sort below it, so the common case skips the
    /// binary search entirely.
    /// </remarks>
    private const int FirstWideCodePoint = 0x1100;

    /// <summary>U+FE0F VARIATION SELECTOR-16, which asks for emoji presentation.</summary>
    /// <remarks>
    /// Honoured wherever it appears rather than only on the bases Unicode defines an emoji
    /// sequence for, which is what Windows Terminal does. Carrying the emoji property data to be
    /// stricter would cost another generated table to reject sequences that are malformed anyway.
    /// <para>
    /// Its counterpart U+FE0E, which asks for text presentation, needs no case of its own: it is
    /// a non-spacing mark, so it advances nothing, and the base keeps the width it already had.
    /// </para>
    /// </remarks>
    private const int EmojiPresentationSelector = 0xFE0F;

    /// <summary>U+200D ZERO WIDTH JOINER, which ligates the emoji on either side of it.</summary>
    private const int ZeroWidthJoiner = 0x200D;

    private const int FirstRegionalIndicator = 0x1F1E6;
    private const int LastRegionalIndicator = 0x1F1FF;

    /// <summary>
    /// The Fitzpatrick skin-tone modifiers, which are drawn as part of the emoji they follow.
    /// </summary>
    /// <remarks>
    /// Their general category is a modifier symbol rather than a mark, so they are not zero
    /// width, and the emoji they most often follow — a pointing hand, a raised fist — is narrow
    /// on its own. The modifier is what makes the pair an emoji, and an emoji is two cells.
    /// </remarks>
    private const int FirstEmojiModifier = 0x1F3FB;
    private const int LastEmojiModifier = 0x1F3FF;

    /// <summary>
    /// U+00AD SOFT HYPHEN, which a terminal draws as a hyphen rather than as a line-break hint.
    /// </summary>
    /// <remarks>
    /// Its general category is Format, which is otherwise zero width. A terminal does no
    /// paragraph reflow, so it has nothing to do with the character except draw it — which is why
    /// POSIX <c>wcwidth</c> carves it out of the same category test.
    /// </remarks>
    private const int SoftHyphen = 0x00AD;

    /// <summary>
    /// Conjoining Hangul jamo, which compose onto the preceding syllable rather than advancing
    /// the cursor themselves.
    /// </summary>
    /// <remarks>
    /// Their general category is a letter, not a mark, so the category test below does not catch
    /// them. Inside a well-formed syllable UAX #29 groups them with their leading consonant and
    /// the width of the cluster is decided by that consonant either way; this matters only for a
    /// value that carries a medial or final jamo on its own.
    /// </remarks>
    private const int FirstConjoiningJamo = 0x1160;
    private const int LastConjoiningJamo = 0x11FF;

    /// <summary>
    /// Measures <paramref name="value"/> in terminal cells.
    /// </summary>
    internal static int Measure(string value) => Measure(value.AsSpan());

    /// <inheritdoc cref="Measure(string)"/>
    internal static int Measure(ReadOnlySpan<char> value) => MeasureCapped(value, int.MaxValue);

    /// <summary>
    /// Measures <paramref name="value"/> in terminal cells, stopping once <paramref name="cap"/>
    /// is reached and returning that instead.
    /// </summary>
    /// <remarks>
    /// Callers only ever compare a measurement against a column width, so a value far wider than
    /// its column does not need to be walked to the end to be known too wide. Without this, a
    /// pathologically long cell would be measured in full twice — once to size the column and
    /// once to truncate it — for an answer that was settled in the first few characters.
    /// </remarks>
    internal static int MeasureCapped(ReadOnlySpan<char> value, int cap)
    {
        if (cap <= 0 || value.IsEmpty)
        {
            return 0;
        }

        // Printable ASCII is one cell per code unit, so a prefix of `cap` such units already
        // reaches the cap whatever follows it, and a shorter all-ASCII value is its own width.
        var probe = value.Length <= cap ? value : value[..cap];
        if (!probe.ContainsAnyExceptInRange(' ', '~'))
        {
            return probe.Length;
        }

        var total = 0;
        var remaining = value;
        while (!remaining.IsEmpty && total < cap)
        {
            var length = StringInfo.GetNextTextElementLength(remaining);

            // Accumulated through a long so an uncapped measurement of a value near the maximum
            // string length cannot wrap negative and run the loop past the end of its budget.
            total = (int)Math.Min((long)total + MeasureTextElement(remaining[..length]), cap);
            remaining = remaining[length..];
        }

        return total;
    }

    /// <summary>
    /// Measures one grapheme cluster, which a terminal draws as one glyph.
    /// </summary>
    /// <remarks>
    /// Sums what each scalar advances the cursor by. Marks, joiners and format characters advance
    /// nothing, so a base with any number of accents stacked on it stays one cell — but a spacing
    /// mark does advance, which is why a Devanagari or Kannada consonant carrying a vowel sign is
    /// two cells rather than one, even though the pair is a single cluster.
    /// <para>
    /// Emoji are the exception, because a terminal ligates them: a ZWJ sequence and a skin-tone
    /// modifier draw several emoji as one glyph, so their sum is clamped to the two cells that
    /// glyph occupies. A pair of regional indicators is a flag on the same grounds even though
    /// neither indicator is Wide, and a variation selector asking for emoji presentation makes a
    /// narrow symbol into a double-width one.
    /// </para>
    /// </remarks>
    internal static int MeasureTextElement(ReadOnlySpan<char> cluster)
    {
        var total = 0;
        var regionalIndicators = 0;
        var emojiPresentation = false;
        var ligated = false;

        var remaining = cluster;
        while (!remaining.IsEmpty)
        {
            var status = Rune.DecodeFromUtf16(remaining, out var rune, out var consumed);
            remaining = remaining[consumed..];

            if (status != OperationStatus.Done)
            {
                // An unpaired surrogate. The console substitutes one replacement glyph for it,
                // so it advances the cursor like any other narrow character.
                total++;
                continue;
            }

            if (rune.Value is ZeroWidthJoiner or
                (>= FirstEmojiModifier and <= LastEmojiModifier))
            {
                ligated = true;
            }
            else if (rune.Value is >= FirstRegionalIndicator and <= LastRegionalIndicator)
            {
                regionalIndicators++;
            }
            else if (rune.Value == EmojiPresentationSelector)
            {
                emojiPresentation = true;
            }

            if (IsZeroWidth(rune))
            {
                continue;
            }

            total += IsWide(rune.Value) ? 2 : 1;
        }

        if (regionalIndicators >= 2)
        {
            return 2;
        }

        if (emojiPresentation && total > 0)
        {
            return 2;
        }

        return ligated ? Math.Min(total, 2) : total;
    }

    /// <summary>
    /// Whether the scalar composes onto its neighbour instead of advancing the cursor.
    /// </summary>
    /// <remarks>
    /// Non-spacing and enclosing marks cover combining accents and the variation selectors, both
    /// of which Unicode classifies as marks. Format characters cover the zero-width joiner and
    /// non-joiner, the tag characters behind subdivision flags, and the zero-width space — but
    /// not the soft hyphen, which a terminal simply draws, having no line breaking to do with it.
    /// Control characters cannot reach a rendered cell — <see cref="TableFormatter.NormalizeCell"/>
    /// replaces them with spaces first — but are answered here so that measuring an unsanitized
    /// string still gives the width the terminal would draw.
    /// </remarks>
    private static bool IsZeroWidth(Rune rune) =>
        rune.Value != SoftHyphen
        && (Rune.GetUnicodeCategory(rune) is
                UnicodeCategory.NonSpacingMark
                or UnicodeCategory.EnclosingMark
                or UnicodeCategory.Format
                or UnicodeCategory.Control
            || rune.Value is >= FirstConjoiningJamo and <= LastConjoiningJamo);

    /// <summary>
    /// Whether the code point's East_Asian_Width is Wide or Fullwidth, and so two cells.
    /// </summary>
    private static bool IsWide(int codePoint)
    {
        if (codePoint < FirstWideCodePoint)
        {
            return false;
        }

        var ranges = EastAsianWidthTable.WideRanges;
        var low = 0;
        var high = (ranges.Length / 2) - 1;

        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            if (codePoint < ranges[middle * 2])
            {
                high = middle - 1;
            }
            else if (codePoint > ranges[(middle * 2) + 1])
            {
                low = middle + 1;
            }
            else
            {
                return true;
            }
        }

        return false;
    }
}
