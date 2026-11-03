// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace XboxDevCLI.Output;

/// <summary>
/// One line of progress on stderr, redrawn in place after a short delay and cut to the
/// terminal's width, because <c>\r</c> can't return to a row the line wrapped from. Disposing it
/// erases the line, so dispose it before writing the command's result or error.
/// </summary>
internal sealed class ProgressLine : IDisposable
{
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan RedrawInterval = TimeSpan.FromMilliseconds(200);

    private readonly TextWriter _stderr;
    private readonly Func<string> _describe;
    private readonly Func<int> _columns;
    private readonly ITimer? _timer;
    private readonly object _gate = new();
    private int _displayedWidth;
    private bool _disposed;

    // Callers enable it when stderr is a terminal; disabled, no timer runs and nothing is drawn.
    // A width of 0 means it's unknown, and the line isn't cut. Tests call Draw directly, or pass a
    // fake time provider to control when the timer fires.
    public ProgressLine(
        TextWriter stderr,
        bool enabled,
        Func<string> describe,
        Func<int>? columns = null,
        TimeProvider? timeProvider = null)
    {
        _stderr = stderr;
        _describe = describe;
        _columns = columns ?? ConsoleColumns;
        if (enabled)
        {
            _timer = (timeProvider ?? TimeProvider.System).CreateTimer(_ => Draw(), null, Delay, RedrawInterval);
        }
    }

    public static bool StderrIsTerminal => Environment.UserInteractive && !Console.IsErrorRedirected;

    // Timer callbacks can overlap. While one is still writing, such as when the terminal has paused
    // output, the next skips its redraw rather than wait, so threads don't pile up behind it.
    internal void Draw()
    {
        if (!Monitor.TryEnter(_gate))
        {
            return;
        }

        try
        {
            if (_disposed)
            {
                return;
            }

            // Some terminals wrap as soon as the last column is written, so it stays empty.
            string line = _describe();
            int columns = _columns();
            if (columns > 1)
            {
                line = TableFormatter.Truncate(line, columns - 1);
            }

            int width = DisplayWidth.Measure(line);
            _stderr.Write('\r');
            _stderr.Write(line);
            if (width < _displayedWidth)
            {
                _stderr.Write(new string(' ', _displayedWidth - width));
            }

            _stderr.Flush();
            _displayedWidth = width;
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();

        // Unlike Draw, this waits for a draw in progress, so it can erase what that draw wrote.
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_displayedWidth > 0)
            {
                _stderr.Write('\r');
                _stderr.Write(new string(' ', _displayedWidth));
                _stderr.Write('\r');
                _stderr.Flush();
            }
        }
    }

    private static int ConsoleColumns()
    {
        try
        {
            return Console.WindowWidth;
        }
        catch (IOException)
        {
            return 0;
        }
    }
}
