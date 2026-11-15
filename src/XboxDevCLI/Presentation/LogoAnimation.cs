// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;

namespace XboxDevCLI.Presentation;

// Plays the first-run logo animation: a band of light sweeps diagonally across the logo,
// lighting it as it passes, and then the registered mark flashes. It takes about a second.
internal static class LogoAnimation
{
    internal static readonly TimeSpan FrameDelay = TimeSpan.FromMilliseconds(40);

    private const string HideCursor = "\u001b[?25l";
    private const string ShowCursor = "\u001b[?25h";
    private const string Reset = "\u001b[0m";
    private const int FrameCount = 16;
    private const double BandHalfWidth = 6;

    private static readonly (int R, int G, int B) Highlight = (239, 255, 197);

    // The animation rewinds the cursor over the logo, so a logo that wraps or scrolls would
    // leave partial frames behind. It needs every row on screen, and one column to spare
    // past the widest line so the terminal doesn't wrap at the edge.
    internal static bool Fits(string logo, string suffix, int width, int height)
    {
        var lines = logo.Split(Environment.NewLine);
        var widest = Math.Max(lines.Max(line => line.Length), lines[^1].Length + suffix.Length);
        return width > widest && height >= lines.Length;
    }

    // Writes the animation, ending on finalLogo plus suffix without a trailing newline.
    internal static void Write(TextWriter writer, string logo, string finalLogo, string suffix, TimeSpan delay)
    {
        var lines = logo.Split(Environment.NewLine);
        ConsoleCancelEventHandler restore = (_, _) =>
        {
            writer.Write(Reset + ShowCursor);
            writer.Flush();
        };
        Console.CancelKeyPress += restore;
        writer.Write(HideCursor);
        try
        {
            for (var frame = 0; frame < FrameCount; frame++)
            {
                writer.Write(RenderFrame(lines, (frame + 1) / (double)(FrameCount + 1)));
                writer.Write($"\u001b[{lines.Length - 1}A\r");
                writer.Flush();
                Thread.Sleep(delay);
            }

            writer.Write(finalLogo + suffix);
            FlashMark(writer, suffix.TrimStart(), delay * 1.5);
        }
        finally
        {
            writer.Write(ShowCursor);
            writer.Flush();
            Console.CancelKeyPress -= restore;
        }
    }

    private static void FlashMark(TextWriter writer, string mark, TimeSpan delay)
    {
        (int R, int G, int B)[] steps = [Highlight, (255, 255, 255), Highlight, (199, 255, 0), (139, 240, 0)];
        foreach (var rgb in steps)
        {
            writer.Write($"\u001b[{mark.Length}D\u001b[1;38;2;{rgb.R};{rgb.G};{rgb.B}m{mark}{Reset}");
            writer.Flush();
            Thread.Sleep(delay);
        }

        writer.Write($"\u001b[{mark.Length}D{mark}");
    }

    // Rows count double, since terminal cells are about twice as tall as they are wide.
    // Cells the band hasn't reached yet are dimmed.
    private static string RenderFrame(string[] lines, double progress)
    {
        var width = lines.Max(line => line.Length);
        var front = (progress * (width + (lines.Length * 2) + (BandHalfWidth * 2))) - BandHalfWidth;
        var builder = new StringBuilder();
        for (var row = 0; row < lines.Length; row++)
        {
            for (var column = 0; column < lines[row].Length; column++)
            {
                var character = lines[row][column];
                if (GetColor(character) is not { } color)
                {
                    builder.Append(Reset).Append(character);
                    continue;
                }

                var position = column + (row * 2);
                var strength = Math.Clamp(1 - (Math.Abs(position - front) / BandHalfWidth), 0, 1);
                var ahead = position > front;
                var rgb = Lerp(color.Rgb, Highlight, strength);
                if (ahead)
                {
                    rgb = Lerp((12, 12, 12), rgb, 0.15 + (0.85 * strength));
                }

                var bold = (color.Bold && !ahead) || strength > 0.5;
                builder.Append(bold ? "\u001b[1;" : "\u001b[22;")
                    .Append($"38;2;{rgb.R};{rgb.G};{rgb.B}m")
                    .Append(character);
            }

            builder.Append(Reset);
            if (row < lines.Length - 1)
            {
                builder.Append(Environment.NewLine);
            }
        }

        return builder.ToString();
    }

    private static (int R, int G, int B) Lerp((int R, int G, int B) from, (int R, int G, int B) to, double amount) =>
        ((int)Math.Round(from.R + ((to.R - from.R) * amount)),
         (int)Math.Round(from.G + ((to.G - from.G) * amount)),
         (int)Math.Round(from.B + ((to.B - from.B) * amount)));

    private static ((int R, int G, int B) Rgb, bool Bold)? GetColor(char character) =>
        character switch
        {
            '@' => ((239, 255, 197), true),
            '%' => ((199, 255, 0), true),
            '#' => ((139, 240, 0), true),
            '*' => ((82, 176, 0), false),
            '+' => ((16, 124, 16), false),
            '=' => ((11, 90, 11), false),
            '-' => ((6, 58, 6), false),
            _ => null,
        };
}
