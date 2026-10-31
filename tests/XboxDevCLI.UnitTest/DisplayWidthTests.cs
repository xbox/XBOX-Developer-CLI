// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using XboxDevCLI.Output;
using Xunit;

namespace XboxDevCLI.UnitTest;

/// <summary>
/// Ground truth for <see cref="DisplayWidth"/>, stated as literal cell counts read off UAX #11
/// and UAX #29 rather than derived from the implementation.
/// </summary>
public class DisplayWidthTests
{
    [Theory]
    // Nothing to draw.
    [InlineData("", 0)]
    // Narrow scripts, where a cell and a code unit happen to agree.
    [InlineData("build", 5)]
    [InlineData("nod/arena-server:1.4", 20)]
    // East_Asian_Width=Wide. This is the case the issue was filed for: six code units, twelve
    // cells, and a column sized by Length is half as wide as the value it holds.
    [InlineData("日本語ビルド", 12)]
    [InlineData("中文服务器", 10)]
    [InlineData("サーバー", 8)]
    // East_Asian_Width=Fullwidth.
    [InlineData("ｆｕｌｌ", 8)]
    [InlineData("\u3000", 2)]
    // Ambiguous stays narrow: its width depends on a locale a CLI cannot know, and every modern
    // terminal draws it in one cell. Widening Greek, Cyrillic, or box drawing would misalign far
    // more output than it fixed.
    [InlineData("αβγ", 3)]
    [InlineData("Привет", 6)]
    [InlineData("│─┼", 3)]
    // Korean, composed and decomposed. The conjoining jamo compose onto their leading consonant
    // rather than advancing the cursor themselves.
    [InlineData("한글", 4)]
    [InlineData("\u1100\u1161\u11A8", 2)]
    // Combining marks add no width, however many are stacked on one base.
    [InlineData("e\u0301", 1)]
    [InlineData("Jose\u0301", 4)]
    [InlineData("o\u0301\u0308\u0323", 1)]
    // A variation selector is itself zero width. One asking for emoji presentation makes a narrow
    // symbol double-width; one asking for text presentation advances nothing and leaves the base
    // at the width it already had, which is what a terminal's cell buffer does.
    [InlineData("\u2714", 1)]
    [InlineData("\u2714\uFE0E", 1)]
    [InlineData("\u2714\uFE0F", 2)]
    [InlineData("\u231A", 2)]
    [InlineData("\u231A\uFE0E", 2)]
    // Emoji. Each of these is one grapheme cluster that a terminal ligates into one glyph, so it
    // is counted once, not once per scalar: a surrogate pair, a ZWJ family of four people joined
    // by three joiners, a skin-tone modifier, a keycap, a flag built from two regional indicators
    // that are Ambiguous on their own, and a subdivision flag built from tag characters.
    [InlineData("\U0001F600", 2)]
    [InlineData("\U0001F468\u200D\U0001F469\u200D\U0001F467\u200D\U0001F466", 2)]
    [InlineData("\U0001F44D\U0001F3FD", 2)]
    [InlineData("1\uFE0F\u20E3", 2)]
    [InlineData("\U0001F1EF\U0001F1F5", 2)]
    [InlineData("\U0001F3F4\U000E0067\U000E0062\U000E0073\U000E0063\U000E0074\U000E007F", 2)]
    // Format characters draw nothing on their own.
    [InlineData("\u200D", 0)]
    [InlineData("\u200B", 0)]
    // Except the soft hyphen, which shares that category but is simply drawn: a terminal has no
    // line breaking to do, so it has nothing to do with the character except print a hyphen.
    [InlineData("\u00AD", 1)]
    [InlineData("a\u00ADb", 3)]
    // A spacing mark advances the cursor, unlike a combining accent. A Devanagari or Kannada
    // consonant and its vowel sign are one grapheme cluster and two cells, so a cluster cannot be
    // assumed to be as wide as its base.
    [InlineData("\u0915\u093E", 2)]
    [InlineData("\u0C95\u0CBE", 2)]
    [InlineData("\u0915\u093E\u0928\u0940", 4)]
    // A skin-tone modifier is what makes a narrow pointing hand an emoji, so the ligature is two
    // cells rather than the three its parts would sum to.
    [InlineData("\u261D\U0001F3FB", 2)]
    // Mixed scripts, the shape a real build name takes.
    [InlineData("build-日本-01", 13)]
    [InlineData("a日b語c", 7)]
    public void MeasureCountsTerminalCells(string value, int expected) =>
        Assert.Equal(expected, DisplayWidth.Measure(value));

    /// <summary>
    /// The width the truncation budget reserves for its ellipsis, asserted here so the constant
    /// cannot drift from the string it stands for.
    /// </summary>
    [Fact]
    public void EllipsisIsThreeCells() => Assert.Equal(3, DisplayWidth.Measure("..."));

    /// <summary>
    /// An unpaired surrogate is not a character, but it does reach the console — which draws one
    /// replacement glyph for it. Measuring it as nothing would let it push a column out of line.
    /// </summary>
    /// <remarks>
    /// Written as literals in the body rather than as <c>InlineData</c>: an attribute argument is
    /// stored UTF-8 encoded, and an unpaired surrogate cannot be encoded that way, so the case
    /// arrives at the test already replaced.
    /// </remarks>
    [Fact]
    public void MeasureCountsAnUnpairedSurrogateAsOneCell()
    {
        Assert.Equal(1, DisplayWidth.Measure("\uD83D"));
        Assert.Equal(1, DisplayWidth.Measure("\uDE00"));
        Assert.Equal(3, DisplayWidth.Measure("a\uD83Db"));
        Assert.Equal(3, DisplayWidth.Measure("\uDE00\uD83D\uDE00"));
    }

    /// <summary>
    /// Capped measurement has to agree with full measurement wherever the cap does not bind,
    /// because the renderer uses the capped form to size a column and the plain form to pad it.
    /// A cap that rounded differently would pad a cell to the wrong width.
    /// </summary>
    [Theory]
    [InlineData("日本語ビルド")]
    [InlineData("Jose\u0301 Nod")]
    [InlineData("\U0001F468\u200D\U0001F469\u200D\U0001F467\u200D\U0001F466 family")]
    [InlineData("build-日本-01")]
    public void MeasureCappedAgreesWithMeasureUpToTheCap(string value)
    {
        var full = DisplayWidth.Measure(value);

        for (var cap = 0; cap <= full + 2; cap++)
        {
            Assert.Equal(Math.Min(full, cap), DisplayWidth.MeasureCapped(value, cap));
        }
    }

    /// <summary>
    /// The cap exists so a pathologically long value settles the "too wide for its column"
    /// question in its first few characters instead of being walked to the end. A value far
    /// beyond any column width must still report exactly the cap.
    /// </summary>
    [Fact]
    public void MeasureCappedStopsAtTheCap()
    {
        var value = string.Concat(Enumerable.Repeat("日本語", 40_000));

        Assert.Equal(40, DisplayWidth.MeasureCapped(value, 40));
    }

    /// <summary>
    /// The oracle the alignment tests measure with has to agree with the renderer on the
    /// fixtures, or one of the two is wrong. It is written from the Unicode data independently,
    /// so this is the point where the two derivations are compared.
    /// </summary>
    [Theory]
    [InlineData("日本語ビルド")]
    [InlineData("中文服务器")]
    [InlineData("Jose\u0301")]
    [InlineData("\U0001F468\u200D\U0001F469\u200D\U0001F467\u200D\U0001F466")]
    [InlineData("\U0001F44D\U0001F3FD")]
    [InlineData("\U0001F1EF\U0001F1F5")]
    [InlineData("\u1100\u1161\u11A8")]
    [InlineData("nod-日本-server")]
    public void MeasureAgreesWithTheIndependentOracle(string value) =>
        Assert.Equal(TerminalCells.Measure(value), DisplayWidth.Measure(value));
}
