// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using XboxDevCLI.Output;

namespace XboxDevCLI.Commands;

/// <summary>
/// Shows what the CLI is waiting for while a request that returns no partial results is in
/// flight, such as <c>Getting configuration for STAGE.0... 4s</c>. Set <see cref="Activity"/> to
/// describe each step of a command that sends several.
/// </summary>
internal sealed class RequestProgress : IDisposable
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly ProgressLine _line;
    private volatile string _activity;

    public RequestProgress(string activity, TextWriter stderr, bool enabled)
    {
        _activity = TerminalText.SingleLine(activity);
        _line = new ProgressLine(stderr, enabled, Describe);
    }

    // Activities can name user input or service-returned values, such as a schema type, so
    // control characters are removed to keep the line one row that Dispose can erase.
    public string Activity
    {
        get => _activity;
        set => _activity = TerminalText.SingleLine(value);
    }

    public static RequestProgress Start(string activity, TextWriter? stderr = null) =>
        new(activity, stderr ?? Console.Error, ProgressLine.StderrIsTerminal);

    internal string Describe() =>
        FormattableString.Invariant($"{_activity}... {(int)_clock.Elapsed.TotalSeconds}s");

    public void Dispose() => _line.Dispose();
}
