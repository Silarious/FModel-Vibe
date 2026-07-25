using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;

namespace FModel.Framework;

public class FStatus : ViewModel
{
    private bool _isReady;
    public bool IsReady
    {
        get => _isReady;
        private set => SetProperty(ref _isReady, value);
    }

    private EStatusKind _kind;
    public EStatusKind Kind
    {
        get => _kind;
        private set
        {
            SetProperty(ref _kind, value);
            IsReady = Kind != EStatusKind.Loading && Kind != EStatusKind.Stopping;
        }
    }

    private string _label;
    public string Label
    {
        get => _label;
        private set => SetProperty(ref _label, value);
    }

    private int _progressDone;
    public int ProgressDone
    {
        get => _progressDone;
        private set
        {
            if (!SetProperty(ref _progressDone, value)) return;
            RaisePropertyChanged(nameof(ProgressText));
            RaisePropertyChanged(nameof(HasProgress));
        }
    }

    private int _progressTotal;
    public int ProgressTotal
    {
        get => _progressTotal;
        private set
        {
            if (!SetProperty(ref _progressTotal, value)) return;
            RaisePropertyChanged(nameof(ProgressText));
            RaisePropertyChanged(nameof(HasProgress));
        }
    }

    public bool HasProgress => ProgressTotal > 0;

    /// <summary>Compact elapsed for mid-progress: <c>m:ss</c> or <c>h:mm:ss</c>.</summary>
    public static string FormatElapsedCompact(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;
        if (elapsed.TotalHours >= 1)
            return $"{(int)elapsed.TotalHours}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
        return $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:D2}";
    }

    /// <summary>
    /// Readable export completion phrase, e.g.
    /// <c>exported in 2 minutes and 5 seconds</c>,
    /// <c>exported in 12 seconds and 340 milliseconds</c>,
    /// <c>exported in 420 milliseconds</c>.
    /// </summary>
    public static string FormatElapsedSentence(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;

        if (elapsed.TotalHours >= 1)
        {
            var hours = (int)elapsed.TotalHours;
            var minutes = elapsed.Minutes;
            var seconds = elapsed.Seconds;
            return $"exported in {Plural(hours, "hour")}, {Plural(minutes, "minute")} and {Plural(seconds, "second")}";
        }

        if (elapsed.TotalMinutes >= 1)
        {
            var totalMinutes = (int)elapsed.TotalMinutes;
            var seconds = elapsed.Seconds;
            return $"exported in {Plural(totalMinutes, "minute")} and {Plural(seconds, "second")}";
        }

        if (elapsed.TotalSeconds >= 1)
        {
            var seconds = (int)elapsed.TotalSeconds;
            var milliseconds = elapsed.Milliseconds;
            return $"exported in {Plural(seconds, "second")} and {Plural(milliseconds, "millisecond")}";
        }

        var ms = (int)Math.Round(elapsed.TotalMilliseconds);
        if (ms < 0) ms = 0;
        return $"exported in {Plural(ms, "millisecond")}";
    }

    private static string Plural(int count, string unit)
        => count == 1 ? $"1 {unit}" : $"{count} {unit}s";

    /// <summary>"12/500  1:23" for the status bar next to Loading…</summary>
    public string ProgressText
    {
        get
        {
            if (!HasProgress) return string.Empty;
            var elapsed = CurrentElapsed;
            return elapsed > TimeSpan.Zero
                ? $"{ProgressDone}/{ProgressTotal}  {FormatElapsedCompact(elapsed)}"
                : $"{ProgressDone}/{ProgressTotal}";
        }
    }

    /// <summary>Last completed progress lap (one BeginProgress→CompleteProgress), for export logs.</summary>
    public string LastLapElapsedText { get; private set; } = string.Empty;

    private readonly Stopwatch _elapsed = new();
    private long _lapStartTicks;
    private TimeSpan _lastSessionElapsed;
    private long _lastProgressUiTicks;
    private int _progressPending;

    public FStatus()
    {
        SetStatus(EStatusKind.Loading);
    }

    public void SetStatus(EStatusKind kind, string label = "")
    {
        Kind = kind;
        if (kind is EStatusKind.Loading)
        {
            _lastSessionElapsed = TimeSpan.Zero;
            LastLapElapsedText = string.Empty;
        }
        else
        {
            ClearProgress();
        }

        UpdateStatusLabel(label);
    }

    public void UpdateStatusLabel(string label, string prefix = null)
    {
        if (Kind == EStatusKind.Loading)
        {
            Label = $"{prefix ?? Kind.ToString()} {label}".Trim();
            return;
        }

        var kindText = Kind.ToString();
        var showElapsed = _lastSessionElapsed > TimeSpan.Zero &&
                          Kind is EStatusKind.Completed or EStatusKind.Stopped or EStatusKind.Failed;
        Label = showElapsed
            ? $"{kindText}  {FormatElapsedSentence(_lastSessionElapsed)}"
            : kindText;
    }

    public void BeginProgress(int total)
    {
        Interlocked.Exchange(ref _progressPending, 0);
        Interlocked.Exchange(ref _lastProgressUiTicks, 0);
        if (!_elapsed.IsRunning)
        {
            _elapsed.Reset();
            _elapsed.Start();
            _lastSessionElapsed = TimeSpan.Zero;
        }

        _lapStartTicks = _elapsed.ElapsedTicks;
        LastLapElapsedText = string.Empty;
        ProgressDone = 0;
        ProgressTotal = Math.Max(0, total);
    }

    public void ClearProgress()
    {
        if (_elapsed.IsRunning || _elapsed.ElapsedTicks > 0)
        {
            _elapsed.Stop();
            _lastSessionElapsed = _elapsed.Elapsed;
            _elapsed.Reset();
        }

        Interlocked.Exchange(ref _progressPending, 0);
        ProgressDone = 0;
        ProgressTotal = 0;
    }

    /// <summary>Thread-safe increment; UI updates are throttled (~8/sec) unless <paramref name="force"/>.</summary>
    public void IncrementProgress(bool force = false)
    {
        var done = Interlocked.Increment(ref _progressPending);
        var total = ProgressTotal;
        var now = Environment.TickCount64;
        if (!force && total > 0 && now - Volatile.Read(ref _lastProgressUiTicks) < 125 && done < total)
            return;

        Interlocked.Exchange(ref _lastProgressUiTicks, now);
        PublishDone(done, total);
    }

    public void CompleteProgress()
    {
        var total = ProgressTotal;
        if (total <= 0) return;

        var lap = _elapsed.Elapsed - TimeSpan.FromTicks(_lapStartTicks);
        if (lap < TimeSpan.Zero) lap = TimeSpan.Zero;
        LastLapElapsedText = FormatElapsedSentence(lap);

        Interlocked.Exchange(ref _progressPending, total);
        Interlocked.Exchange(ref _lastProgressUiTicks, 0);
        PublishDone(total, total);
    }

    private TimeSpan CurrentElapsed =>
        _elapsed.IsRunning || _elapsed.ElapsedTicks > 0 ? _elapsed.Elapsed : _lastSessionElapsed;

    private void PublishDone(int done, int total)
    {
        var clamped = total > 0 ? Math.Min(done, total) : done;

        void Apply()
        {
            ProgressDone = clamped;
            // Elapsed is part of ProgressText; force refresh even if done count is unchanged.
            RaisePropertyChanged(nameof(ProgressText));
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
            Apply();
        else
            dispatcher.BeginInvoke(Apply);
    }
}
