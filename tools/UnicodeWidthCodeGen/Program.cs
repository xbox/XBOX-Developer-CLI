// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace UnicodeWidthCodeGen;

/// <summary>
/// Emits the East Asian Width lookup table that <c>XboxDevCLI.Output.DisplayWidth</c> uses to
/// size table columns in terminal cells.
/// </summary>
/// <remarks>
/// The data is transcribed rather than hand-written for the same reason the PlayFab models are:
/// a table of 100-plus Unicode ranges typed by hand is unreviewable and silently wrong at one
/// boundary. .NET exposes no East Asian Width API, so the alternative was a NuGet dependency in
/// a single-file CLI. Generating from the normative UCD file keeps the data auditable against
/// its source, regenerable when Unicode revises it, and free of a runtime dependency.
/// </remarks>
internal static class Program
{
    /// <summary>
    /// The Unicode release to transcribe. Pinned rather than tracking <c>latest</c> so a
    /// regeneration is reproducible and a version bump is a reviewable diff.
    /// </summary>
    private const string DefaultUnicodeVersion = "18.0.0";

    private const string DefaultOutputPath =
        "src/XboxDevCLI/Output/Generated/EastAsianWidthTable.cs";

    /// <summary>
    /// Blocks whose <em>unassigned</em> code points default to Wide, per the notes in
    /// EastAsianWidth.txt. Unassigned code points are not listed in the file, so the defaults
    /// have to be applied here or a column sized against a code point Unicode has not yet
    /// assigned would be measured narrow.
    /// </summary>
    private static readonly (int Start, int End)[] WideByDefault =
    [
        (0x3400, 0x4DBF),   // CJK Unified Ideographs Extension A
        (0x4E00, 0x9FFF),   // CJK Unified Ideographs
        (0xF900, 0xFAFF),   // CJK Compatibility Ideographs
        (0x20000, 0x2FFFD), // Plane 2, undesignated code points included
        (0x30000, 0x3FFFD), // Plane 3, undesignated code points included
    ];

    private static async Task<int> Main(string[] args)
    {
        try
        {
            var options = ParseOptions(args);
            var output = options.GetValueOrDefault("--output") ?? DefaultOutputPath;
            var input = options.GetValueOrDefault("--input");
            var requested = options.GetValueOrDefault("--unicode-version");

            if (requested is not null)
            {
                ValidateVersion(requested);
            }

            var source = input ?? BuildUrl(requested ?? DefaultUnicodeVersion);

            var text = input is null
                ? await FetchAsync(source)
                : await File.ReadAllTextAsync(input);

            // A local file carries its own version in its first line. Trusting the pinned default
            // instead would stamp the generated table with a version it was not built from, which
            // is the one thing the header exists to tell a reader.
            var version = requested ?? (input is null ? DefaultUnicodeVersion : ReadVersion(text, source));

            var ranges = Merge(ParseWideAndFullwidth(text, source));
            var emitted = Emit(ranges, version, source);

            var path = Path.GetFullPath(output);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, emitted, new UTF8Encoding(false));

            Console.WriteLine(
                $"Wrote {ranges.Count} Wide/Fullwidth ranges from Unicode {version} to {path}.");
            return 0;
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync(exception.Message);
            return 1;
        }
    }

    /// <summary>
    /// Reads the recognized options, rejecting anything it cannot account for.
    /// </summary>
    /// <remarks>
    /// Strict because the default behaviour is to overwrite a checked-in source file. Skipping an
    /// argument it did not understand would mean <c>--out ./scratch.cs</c>, or an
    /// <c>--output</c> whose value went missing, quietly regenerating
    /// <see cref="DefaultOutputPath"/> instead — a tool reporting success while doing something
    /// other than what it was asked, to a file already under review.
    /// </remarks>
    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        string[] recognized = ["--output", "--input", "--unicode-version"];
        var options = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var index = 0; index < args.Length; index++)
        {
            var name = args[index];

            if (!recognized.Contains(name, StringComparer.Ordinal))
            {
                throw new ArgumentException(
                    $"Unrecognized argument '{name}'. Usage: UnicodeWidthCodeGen " +
                    $"[{string.Join("] [", recognized.Select(option => $"{option} <value>"))}].");
            }

            if (index + 1 >= args.Length || recognized.Contains(args[index + 1], StringComparer.Ordinal))
            {
                throw new ArgumentException($"'{name}' requires a value.");
            }

            if (!options.TryAdd(name, args[index + 1]))
            {
                throw new ArgumentException($"'{name}' was given more than once.");
            }

            index++;
        }

        return options;
    }

    /// <summary>
    /// Rejects a version that cannot name a UCD directory, so a typo fails here rather than as a
    /// 404 against a URL the caller never typed.
    /// </summary>
    private static void ValidateVersion(string version)
    {
        if (!Regex.IsMatch(version, @"^[0-9]+\.[0-9]+\.[0-9]+$"))
        {
            throw new ArgumentException(
                $"'{version}' is not a Unicode version. Expected three dot-separated numbers, " +
                "such as 18.0.0.");
        }
    }

    private static string BuildUrl(string version) =>
        $"https://www.unicode.org/Public/{version}/ucd/EastAsianWidth.txt";

    /// <summary>
    /// Reads the release from the file's own first line, which reads
    /// <c># EastAsianWidth-18.0.0.txt</c>.
    /// </summary>
    private static string ReadVersion(string text, string source)
    {
        var match = Regex.Match(text, @"^#\s*EastAsianWidth-(?<version>[0-9.]+)\.txt", RegexOptions.Multiline);
        if (!match.Success)
        {
            throw new InvalidDataException(
                $"{source} does not name its Unicode version in the header, so the generated " +
                "table could not be stamped with one. Pass --unicode-version.");
        }

        return match.Groups["version"].Value;
    }

    private static async Task<string> FetchAsync(string url)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("UnicodeWidthCodeGen/1.0");
        return await http.GetStringAsync(url);
    }

    /// <summary>
    /// Reads every code point whose East_Asian_Width is W or F, the two values UAX #11 defines as
    /// occupying two cells.
    /// </summary>
    /// <remarks>
    /// Ambiguous (A) is deliberately excluded. Its width depends on the locale the terminal is
    /// running under, and a CLI cannot know that; treating it as narrow is what every modern
    /// terminal defaults to, and the alternative would widen Greek, Cyrillic, and box-drawing
    /// characters on a Western console.
    /// </remarks>
    private static List<(int Start, int End)> ParseWideAndFullwidth(string text, string source)
    {
        var listed = new Dictionary<int, char>();
        var ranges = new List<(int Start, int End)>();

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine;
            var comment = line.IndexOf('#', StringComparison.Ordinal);
            if (comment >= 0)
            {
                line = line[..comment];
            }

            line = line.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var fields = line.Split(';');
            if (fields.Length < 2)
            {
                continue;
            }

            var (start, end) = ParseCodePointRange(fields[0].Trim());
            var width = fields[1].Trim();
            if (width.Length == 0)
            {
                continue;
            }

            // Remembered for every value, not just W and F, so the default-Wide blocks below can
            // be checked against what the file actually lists rather than trusted blindly.
            for (var codePoint = start; codePoint <= end; codePoint++)
            {
                listed[codePoint] = width[0];
            }

            if (width is "W" or "F")
            {
                ranges.Add((start, end));
            }
        }

        if (ranges.Count == 0)
        {
            throw new InvalidDataException(
                $"No Wide or Fullwidth ranges were found in {source}; the format has changed.");
        }

        foreach (var (start, end) in WideByDefault)
        {
            for (var codePoint = start; codePoint <= end; codePoint++)
            {
                if (listed.TryGetValue(codePoint, out var width) && width is not ('W' or 'F'))
                {
                    throw new InvalidDataException(
                        $"U+{codePoint:X4} sits in a block documented as defaulting to Wide but is " +
                        $"listed as {width}. Re-read the notes in {source} before regenerating.");
                }
            }

            ranges.Add((start, end));
        }

        return ranges;
    }

    private static (int Start, int End) ParseCodePointRange(string field)
    {
        var separator = field.IndexOf("..", StringComparison.Ordinal);
        if (separator < 0)
        {
            var single = ParseCodePoint(field);
            return (single, single);
        }

        return (ParseCodePoint(field[..separator]), ParseCodePoint(field[(separator + 2)..]));
    }

    private static int ParseCodePoint(string value) =>
        int.Parse(value.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    /// <summary>
    /// Sorts and coalesces the ranges, including ones that merely abut, so the emitted table is
    /// the shortest form of the same set and a binary search over it stays shallow.
    /// </summary>
    private static List<(int Start, int End)> Merge(List<(int Start, int End)> ranges)
    {
        ranges.Sort((left, right) => left.Start.CompareTo(right.Start));

        var merged = new List<(int Start, int End)>();
        foreach (var range in ranges)
        {
            if (merged.Count > 0 && range.Start <= merged[^1].End + 1)
            {
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, range.End));
                continue;
            }

            merged.Add(range);
        }

        return merged;
    }

    /// <summary>
    /// Emits the ranges as a flat, sorted array of inclusive start/end pairs.
    /// </summary>
    /// <remarks>
    /// Flat rather than an array of tuples so the lookup is a binary search over contiguous
    /// primitives with no per-entry indirection, and so the table reviews as a column of code
    /// points that can be diffed against the UCD file directly.
    /// </remarks>
    private static string Emit(
        List<(int Start, int End)> ranges,
        string version,
        string source)
    {
        var builder = new StringBuilder();
        builder.AppendLine("// Copyright (c) Microsoft Corporation.");
        builder.AppendLine("// Licensed under the MIT License.");
        builder.AppendLine();
        builder.AppendLine("// <auto-generated>");
        builder.AppendLine("// This file was auto-generated by UnicodeWidthCodeGen from the Unicode Character Database.");
        builder.AppendLine(CultureInfo.InvariantCulture, $"// Unicode version: {version}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"// Source: {source}");
        builder.AppendLine("// Do not edit manually. Re-run the code generator to update.");
        builder.AppendLine("// </auto-generated>");
        builder.AppendLine();
        builder.AppendLine("#nullable enable");
        builder.AppendLine();
        builder.AppendLine("namespace XboxDevCLI.Output.Generated;");
        builder.AppendLine();
        builder.AppendLine("/// <summary>");
        builder.AppendLine("/// Code point ranges whose East_Asian_Width is Wide (W) or Fullwidth (F), which UAX #11");
        builder.AppendLine("/// defines as occupying two cells.");
        builder.AppendLine("/// </summary>");
        builder.AppendLine("/// <remarks>");
        builder.AppendLine("/// Ambiguous (A) is excluded: its width depends on the terminal's locale, and every modern");
        builder.AppendLine("/// terminal defaults it to narrow. Blocks whose unassigned code points default to Wide are");
        builder.AppendLine("/// included in full, so a column sized against a code point Unicode has not yet assigned is");
        builder.AppendLine("/// still measured the way a terminal with a current font will render it.");
        builder.AppendLine("/// </remarks>");
        builder.AppendLine("internal static class EastAsianWidthTable");
        builder.AppendLine("{");
        builder.AppendLine("    /// <summary>The Unicode release this table was transcribed from.</summary>");
        builder.AppendLine(CultureInfo.InvariantCulture, $"    internal const string UnicodeVersion = \"{version}\";");
        builder.AppendLine();
        builder.AppendLine("    /// <summary>");
        builder.AppendLine("    /// Sorted, non-overlapping, non-abutting inclusive start/end pairs: entry <c>i</c> spans");
        builder.AppendLine("    /// <c>WideRanges[2 * i]</c> through <c>WideRanges[(2 * i) + 1]</c>.");
        builder.AppendLine("    /// </summary>");
        builder.AppendLine("    internal static readonly int[] WideRanges =");
        builder.AppendLine("    [");

        foreach (var (start, end) in ranges)
        {
            builder.AppendLine(
                CultureInfo.InvariantCulture,
                $"        0x{start:X4}, 0x{end:X4},");
        }

        builder.AppendLine("    ];");
        builder.AppendLine("}");

        return builder.ToString().ReplaceLineEndings("\n");
    }
}
