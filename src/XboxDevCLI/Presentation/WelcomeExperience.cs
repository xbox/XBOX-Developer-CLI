// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Security;
using System.Text;

namespace XboxDevCLI.Presentation;

internal static class WelcomeExperience
{
    internal const string SentinelFileName = ".welcome-shown";

    private const string LogoResourceName = "XboxDevCLI.Resources.xbox-logo.txt";
    private const string AnsiReset = "\u001b[0m";
    private const string AnsiBold = "\u001b[1m";
    private const string AnsiBrand = "\u001b[1;38;2;139;240;0m";
    private static readonly Lazy<string> Logo = new(LoadLogo);

    internal static string PlainLogo => Logo.Value;

    internal static string RenderLogo(bool useColor)
    {
        if (!useColor)
        {
            return PlainLogo;
        }

        var rendered = new StringBuilder(PlainLogo.Length * 2);
        string? activeColor = null;

        foreach (var character in PlainLogo)
        {
            if (character is '\r' or '\n')
            {
                ResetColor(rendered, ref activeColor);
                rendered.Append(character);
                continue;
            }

            var color = GetLogoColor(character);
            if (color != null && color != activeColor)
            {
                rendered.Append(color);
                activeColor = color;
            }

            rendered.Append(character);
        }

        ResetColor(rendered, ref activeColor);
        return rendered.ToString();
    }

    internal static void WriteHeader(TextWriter writer, bool useColor)
    {
        WriteLogo(writer, useColor);
        writer.WriteLine(
            $"{ColorizeBrand("XBOX", useColor)} CLI: {CliIdentity.Tagline}");
    }

    internal static void WriteInfo(TextWriter writer, string version, bool useColor)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        WriteLogo(writer, useColor);
        WriteVersion(writer, version, useColor);
        writer.WriteLine(ProjectLinks.Home);
        writer.WriteLine();
        writer.WriteLine("Run this command to get started:");
        writer.WriteLine($"  $ {CliIdentity.CommandName} auth login");
    }

    internal static void WriteFirstRun(TextWriter writer, bool useColor)
    {
        WriteHeader(writer, useColor);
        writer.WriteLine();
        writer.WriteLine("Run this command to get started:");
        writer.WriteLine($"  $ {CliIdentity.CommandName} auth login");
        writer.WriteLine();
        writer.WriteLine("Run this command to explore all commands:");
        writer.WriteLine($"  $ {CliIdentity.CommandName} --help");
        writer.WriteLine();
    }

    internal static void WriteFocusedFirstRun(TextWriter writer, bool useColor)
    {
        WriteHeader(writer, useColor);
        writer.WriteLine();
        writer.WriteLine(Colorize("GET STARTED", AnsiBold, useColor));
        writer.WriteLine($"  $ {CliIdentity.CommandName} auth login");
        writer.WriteLine($"  $ {CliIdentity.CommandName} --help");
        writer.WriteLine();
    }

    internal static void WriteVersion(TextWriter writer, string version, bool useColor)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        writer.WriteLine($"{ColorizeBrand("XBOX", useColor)} CLI version {version}");
    }

    private static void WriteLogo(TextWriter writer, bool useColor)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteLine();
        writer.WriteLine(RenderLogo(useColor));
        writer.WriteLine();
    }

    internal static bool WriteIfFirstRun(
        string stateDirectory,
        string version,
        TextWriter writer,
        bool useColor)
    {
        return WriteOnce(
            stateDirectory,
            version,
            writer,
            () => WriteFirstRun(writer, useColor));
    }

    internal static bool WriteFocusedIfFirstRun(
        string stateDirectory,
        string version,
        TextWriter writer,
        bool useColor)
    {
        return WriteOnce(
            stateDirectory,
            version,
            writer,
            () => WriteFocusedFirstRun(writer, useColor));
    }

    internal static bool WriteLogoIfFirstRun(
        string stateDirectory,
        string version,
        TextWriter writer,
        bool useColor)
    {
        return WriteOnce(
            stateDirectory,
            version,
            writer,
            () => WriteLogo(writer, useColor));
    }

    private static bool WriteOnce(
        string stateDirectory,
        string version,
        TextWriter writer,
        Action writeContent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(writeContent);

        if (WasShown(stateDirectory, version))
        {
            return false;
        }

        writeContent();
        MarkShown(stateDirectory, version);
        return true;
    }

    internal static void MarkShown(string stateDirectory, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        try
        {
            Directory.CreateDirectory(stateDirectory);

            var sentinelPath = Path.Combine(stateDirectory, SentinelFileName);
            File.WriteAllText(
                sentinelPath,
                version,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or UnauthorizedAccessException
                or SecurityException
                or NotSupportedException)
        {
            // The welcome is nonessential; unavailable state storage may make it repeat.
        }
    }

    internal static bool SupportsColor(bool isRedirected) =>
        SupportsColor(
            isRedirected,
            Environment.GetEnvironmentVariable("NO_COLOR"),
            Environment.GetEnvironmentVariable("TERM"),
            Environment.GetEnvironmentVariable("CLICOLOR"));

    internal static bool SupportsColor(
        bool isRedirected,
        string? noColor,
        string? term,
        string? cliColor)
    {
        return !isRedirected
            && noColor == null
            && !string.Equals(term, "dumb", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(cliColor, "0", StringComparison.Ordinal);
    }

    internal static string ColorizeBrand(string text, bool useColor) =>
        Colorize(text, AnsiBrand, useColor);

    private static string LoadLogo()
    {
        using var stream = typeof(WelcomeExperience).Assembly.GetManifestResourceStream(LogoResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded XBOX logo resource '{LogoResourceName}' was not found.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        return reader.ReadToEnd()
            .Trim('\r', '\n')
            .ReplaceLineEndings(Environment.NewLine);
    }

    private static string? GetLogoColor(char character) =>
        character switch
        {
            '@' => "\u001b[1;38;2;239;255;197m",
            '%' => "\u001b[1;38;2;199;255;0m",
            '#' => "\u001b[1;38;2;139;240;0m",
            '*' => "\u001b[22;38;2;82;176;0m",
            '+' => "\u001b[22;38;2;16;124;16m",
            '=' => "\u001b[22;38;2;11;90;11m",
            '-' => "\u001b[22;38;2;6;58;6m",
            _ => null,
        };

    private static bool WasShown(string stateDirectory, string version)
    {
        try
        {
            var sentinelPath = Path.Combine(stateDirectory, SentinelFileName);
            return File.Exists(sentinelPath)
                && string.Equals(
                    File.ReadAllText(sentinelPath).Trim(),
                    version,
                    StringComparison.Ordinal);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or SecurityException)
        {
            return false;
        }
    }

    private static string Colorize(string text, string color, bool useColor) =>
        useColor ? $"{color}{text}{AnsiReset}" : text;

    private static void ResetColor(StringBuilder rendered, ref string? activeColor)
    {
        if (activeColor == null)
        {
            return;
        }

        rendered.Append(AnsiReset);
        activeColor = null;
    }
}
