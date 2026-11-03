// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Globalization;
using XboxDevCLI.Output;

namespace XboxDevCLI.Commands;

internal sealed class AchievementImageUploadProgress : IDisposable
{
    private readonly TimeProvider _time;
    private readonly long _started;
    private readonly object _gate = new();
    private readonly string _fileName;
    private readonly long _totalBytes;
    private readonly ProgressLine _line;
    private string _phase = "Preparing image";
    private TimeSpan _phaseStarted;
    private long _uploadedBytes;

    public AchievementImageUploadProgress(
        TextWriter stderr,
        bool enabled,
        string fileName,
        long totalBytes,
        TimeProvider? timeProvider = null,
        Func<int>? columns = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _started = _time.GetTimestamp();
        _fileName = XboxPartnerTableFormatter.NormalizeCell(fileName);
        _totalBytes = totalBytes;
        _line = new ProgressLine(stderr, enabled, Describe, columns, _time);
    }

    private TimeSpan Elapsed => _time.GetElapsedTime(_started);

    public void SetPhase(string phase)
    {
        lock (_gate)
        {
            _phase = phase;
            _phaseStarted = Elapsed;
        }
    }

    public void ReportBytes(long bytes)
    {
        lock (_gate)
        {
            _uploadedBytes = Math.Clamp(bytes, 0, _totalBytes);
        }
    }

    private string Describe()
    {
        lock (_gate)
        {
            TimeSpan elapsed = Elapsed;
            if (_phase == "Uploading")
            {
                double uploaded = _uploadedBytes / 1048576d;
                double total = _totalBytes / 1048576d;
                double percent = _totalBytes > 0 ? _uploadedBytes * 100d / _totalBytes : 0;
                double rate = uploaded / Math.Max((elapsed - _phaseStarted).TotalSeconds, 0.001);
                return FormattableString.Invariant(
                    $"Uploading {_fileName}  {uploaded:F1} / {total:F1} MiB  {percent:F0}%  {rate:F1} MiB/s");
            }

            return $"{_phase}... {(elapsed - _phaseStarted).TotalSeconds.ToString("F0", CultureInfo.InvariantCulture)}s";
        }
    }

    public void Dispose() => _line.Dispose();
}
