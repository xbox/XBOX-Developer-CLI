// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using XboxDevCLI.Output;

namespace XboxDevCLI.Commands;

/// <summary>
/// Shows how far a list has got while the CLI reads it a page at a time, such as
/// <c>Reading games... 1646 found, 17 pages, 61s</c>. The service reports no total, so it counts up.
/// </summary>
internal sealed class ListProgress : IDisposable
{
    /// <summary>What every paged list command's Help says about progress, in its OUTPUT section.</summary>
    internal const string HelpOutput = "Progress goes to stderr while the CLI reads pages, when stderr is a terminal.";

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly string _activity;
    private readonly ProgressLine _line;
    private int _found;
    private int _pages;

    public ListProgress(string activity, TextWriter stderr, bool enabled)
    {
        _activity = activity;
        _line = new ProgressLine(stderr, enabled, Describe);
    }

    public static ListProgress Start(string activity, TextWriter? stderr = null) =>
        new(activity, stderr ?? Console.Error, ProgressLine.StderrIsTerminal);

    public void PageRead(int items)
    {
        Interlocked.Add(ref _found, items);
        Interlocked.Increment(ref _pages);
    }

    internal string Describe()
    {
        int pages = Volatile.Read(ref _pages);
        int seconds = (int)_clock.Elapsed.TotalSeconds;
        return pages == 0
            ? FormattableString.Invariant($"{_activity}... {seconds}s")
            : FormattableString.Invariant(
                $"{_activity}... {Volatile.Read(ref _found)} found, {pages} {(pages == 1 ? "page" : "pages")}, {seconds}s");
    }

    public void Dispose() => _line.Dispose();
}
