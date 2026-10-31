// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Diagnostics;
using System.Globalization;
using XboxDevCLI.Output;

namespace XboxDevCLI.Commands;

internal sealed class AchievementImageUploadProgress : IDisposable
{
    private readonly TextWriter _stderr;
    private readonly bool _enabled;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Timer? _timer;
    private readonly object _gate = new();
    private readonly string _fileName;
    private readonly long _totalBytes;
    private string _phase = "Preparing image";
    private TimeSpan _phaseStarted;
    private long _uploadedBytes;
    private int _displayedLength;
    private bool _disposed;

    public AchievementImageUploadProgress(TextWriter stderr, bool enabled, string fileName, long totalBytes)
    {
        _stderr = stderr;
        _enabled = enabled;
        _fileName = XboxPartnerTableFormatter.NormalizeCell(fileName);
        _totalBytes = totalBytes;
        if (enabled)
        {
            _timer = new Timer(_ => Render(), null, TimeSpan.FromMilliseconds(300),
                TimeSpan.FromMilliseconds(200));
        }
    }

    public void SetPhase(string phase)
    {
        lock (_gate)
        {
            _phase = phase;
            _phaseStarted = _clock.Elapsed;
        }
    }

    public void ReportBytes(long bytes)
    {
        lock (_gate)
        {
            _uploadedBytes = Math.Clamp(bytes, 0, _totalBytes);
        }
    }

    private void Render()
    {
        lock (_gate)
        {
            if (_disposed || !_enabled || _clock.Elapsed < TimeSpan.FromMilliseconds(300))
            {
                return;
            }

            TimeSpan elapsed = _clock.Elapsed;
            string line;
            if (_phase == "Uploading")
            {
                double uploaded = _uploadedBytes / 1048576d;
                double total = _totalBytes / 1048576d;
                double percent = _totalBytes > 0 ? _uploadedBytes * 100d / _totalBytes : 0;
                double rate = uploaded / Math.Max((elapsed - _phaseStarted).TotalSeconds, 0.001);
                line = FormattableString.Invariant(
                    $"Uploading {_fileName}  {uploaded:F1} / {total:F1} MiB  {percent:F0}%  {rate:F1} MiB/s");
            }
            else
            {
                line = $"{_phase}... {(elapsed - _phaseStarted).TotalSeconds.ToString("F0", CultureInfo.InvariantCulture)}s";
            }

            _stderr.Write('\r');
            _stderr.Write(line);
            int width = DisplayWidth.Measure(line);
            if (width < _displayedLength)
            {
                _stderr.Write(new string(' ', _displayedLength - width));
            }
            _stderr.Flush();
            _displayedLength = width;
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_displayedLength > 0)
            {
                _stderr.Write('\r');
                _stderr.Write(new string(' ', _displayedLength));
                _stderr.Write('\r');
                _stderr.Flush();
                _displayedLength = 0;
            }
        }
    }
}
