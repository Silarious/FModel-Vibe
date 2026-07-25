using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using CUE4Parse.GameTypes.ArcRaiders.Encryption.Theia;
using FModel.Settings;
using Serilog;

namespace FModel.Framework;

/// <summary>
/// Soft/hard RAM caps (% of total physical memory) for bulk export workers.
/// Soft: stall acquiring new work until under resume band (target minus hysteresis).
/// Hard: stop new work; while recovering allow at most one package until under soft resume.
/// Never kills the process — stalling export is the safety valve.
/// </summary>
public static class MemoryGuard
{
    private const int HysteresisPercentPoints = 5;
    private const int PollMs = 250;
    private const int GcMinIntervalMs = 12_000;
    /// <summary>System available memory below this % of total is treated as hard pressure.</summary>
    private const int CriticalAvailablePercent = 5;

    private enum GateState
    {
        Normal,
        SoftThrottle,
        HardCap
    }

    private static readonly object Gate = new();
    private static GateState _state = GateState.Normal;
    private static long _lastGcTick;
    private static int _hardActiveSlots;

    /// <summary>
    /// Block until memory pressure allows taking another export work item, or cancellation.
    /// Call once per package before dequeue. Returns false if cancelled.
    /// When <paramref name="holdsHardSlot"/> is true, call <see cref="ReleaseHardSlot"/> in a finally.
    /// </summary>
    public static bool WaitToAcquireWork(CancellationToken cancellationToken, out bool holdsHardSlot)
    {
        holdsHardSlot = false;
        if (UserSettings.Default?.EnableMemoryGuard != true)
            return !cancellationToken.IsCancellationRequested;

        while (!cancellationToken.IsCancellationRequested)
        {
            var snap = Sample();
            var decision = Evaluate(snap);

            if (decision.Allow)
            {
                holdsHardSlot = decision.HardSlot;
                return true;
            }

            MaybeRelievePressure(snap, decision.Level);
            cancellationToken.WaitHandle.WaitOne(PollMs);
        }

        return false;
    }

    /// <summary>Release a hard-cap concurrency slot acquired via <see cref="WaitToAcquireWork"/>.</summary>
    public static void ReleaseHardSlot()
    {
        lock (Gate)
        {
            if (_hardActiveSlots > 0)
                _hardActiveSlots--;
        }
    }

    private struct SampleSnapshot
    {
        public long WorkingSetBytes;
        public long TotalPhysBytes;
        public long AvailPhysBytes;
        public long SoftBytes;
        public long SoftResumeBytes;
        public long HardBytes;
        public int SoftPercent;
        public int HardPercent;
        public bool AvailCriticallyLow;
        public bool OverHard;
        public bool OverSoft;
        public bool UnderResume;
    }

    private struct Decision
    {
        public bool Allow;
        public bool HardSlot;
        public GateState Level;
    }

    private static SampleSnapshot Sample()
    {
        var softPct = Math.Clamp(UserSettings.Default?.MemorySoftTargetPercent ?? 55, 10, 95);
        var hardPct = Math.Clamp(UserSettings.Default?.MemoryHardCapPercent ?? 85, softPct + 5, 99);

        TryGetPhysMemory(out var total, out var avail);
        if (total <= 0)
        {
            var info = GC.GetGCMemoryInfo();
            total = info.TotalAvailableMemoryBytes > 0
                ? info.TotalAvailableMemoryBytes
                : 16L * 1024 * 1024 * 1024;
            avail = Math.Max(0, total - info.MemoryLoadBytes);
        }

        long ws;
        try
        {
            var proc = Process.GetCurrentProcess();
            proc.Refresh();
            ws = proc.WorkingSet64;
        }
        catch
        {
            ws = GC.GetTotalMemory(false);
        }

        var soft = total * softPct / 100;
        var hard = total * hardPct / 100;
        var resumePct = Math.Max(5, softPct - HysteresisPercentPoints);
        var softResume = total * resumePct / 100;
        var availCriticallyLow = total > 0 && avail * 100 / total < CriticalAvailablePercent;

        return new SampleSnapshot
        {
            WorkingSetBytes = ws,
            TotalPhysBytes = total,
            AvailPhysBytes = avail,
            SoftBytes = soft,
            SoftResumeBytes = softResume,
            HardBytes = hard,
            SoftPercent = softPct,
            HardPercent = hardPct,
            AvailCriticallyLow = availCriticallyLow,
            OverHard = ws >= hard || availCriticallyLow,
            OverSoft = ws >= soft,
            UnderResume = ws < softResume && !availCriticallyLow
        };
    }

    private static Decision Evaluate(in SampleSnapshot s)
    {
        lock (Gate)
        {
            var prev = _state;

            if (s.OverHard)
                _state = GateState.HardCap;
            else if (_state == GateState.HardCap)
                _state = s.UnderResume ? GateState.Normal : GateState.HardCap;
            else if (s.OverSoft)
                _state = GateState.SoftThrottle;
            else if (_state == GateState.SoftThrottle)
                _state = s.UnderResume ? GateState.Normal : GateState.SoftThrottle;
            else
                _state = GateState.Normal;

            if (_state != prev)
                LogTransition(prev, _state, s);

            return _state switch
            {
                GateState.Normal => new Decision { Allow = true, HardSlot = false, Level = _state },
                GateState.SoftThrottle => new Decision { Allow = false, HardSlot = false, Level = _state },
                GateState.HardCap => DecideHard(s),
                _ => new Decision { Allow = true, HardSlot = false, Level = _state }
            };
        }
    }

    private static Decision DecideHard(in SampleSnapshot s)
    {
        // Over hard (or critically low avail): issue no new work. In-flight packages continue.
        if (s.OverHard)
            return new Decision { Allow = false, HardSlot = false, Level = GateState.HardCap };

        // Recovering (under hard, still above soft resume): claim at most one package under Gate lock.
        if (_hardActiveSlots == 0)
        {
            _hardActiveSlots = 1;
            return new Decision { Allow = true, HardSlot = true, Level = GateState.HardCap };
        }

        return new Decision { Allow = false, HardSlot = false, Level = GateState.HardCap };
    }

    private static void MaybeRelievePressure(in SampleSnapshot s, GateState level)
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastGcTick);
        if (now - last < GcMinIntervalMs)
            return;
        if (Interlocked.CompareExchange(ref _lastGcTick, now, last) != last)
            return;

        try
        {
            TheiaDecryptingFileArchive.TrimAllPlaintextCaches();
        }
        catch
        {
            // optional
        }

        try
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false, compacting: false);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false, compacting: false);
        }
        catch
        {
            // optional
        }

        Log.Debug(
            "[MemGuard] relieved pressure (level={Level}) WS={Ws:F2}GB soft={Soft:F2}GB hard={Hard:F2}GB avail={Avail:F2}GB/{Total:F2}GB",
            level, Gb(s.WorkingSetBytes), Gb(s.SoftBytes), Gb(s.HardBytes), Gb(s.AvailPhysBytes), Gb(s.TotalPhysBytes));
    }

    private static void LogTransition(GateState from, GateState to, in SampleSnapshot s)
    {
        const string fmt =
            "[MemGuard] {To} (was {From}) WS={Ws:F2}GB soft={SoftPct}%/{Soft:F2}GB hard={HardPct}%/{Hard:F2}GB avail={Avail:F2}GB total={Total:F2}GB";

        switch (to)
        {
            case GateState.HardCap:
                Log.Warning(fmt + " — stopping new export work to protect the OS",
                    "HARD cap", from, Gb(s.WorkingSetBytes), s.SoftPercent, Gb(s.SoftBytes),
                    s.HardPercent, Gb(s.HardBytes), Gb(s.AvailPhysBytes), Gb(s.TotalPhysBytes));
                break;
            case GateState.SoftThrottle:
                Log.Information(fmt + " — throttling new export workers",
                    "SOFT throttle", from, Gb(s.WorkingSetBytes), s.SoftPercent, Gb(s.SoftBytes),
                    s.HardPercent, Gb(s.HardBytes), Gb(s.AvailPhysBytes), Gb(s.TotalPhysBytes));
                break;
            default:
                Log.Information(fmt + " — resumed",
                    "resumed", from, Gb(s.WorkingSetBytes), s.SoftPercent, Gb(s.SoftBytes),
                    s.HardPercent, Gb(s.HardBytes), Gb(s.AvailPhysBytes), Gb(s.TotalPhysBytes));
                break;
        }
    }

    private static double Gb(long bytes) => bytes / (1024.0 * 1024.0 * 1024.0);

    private static bool TryGetPhysMemory(out long totalPhys, out long availPhys)
    {
        totalPhys = 0;
        availPhys = 0;
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (!GlobalMemoryStatusEx(ref status))
                return false;
            totalPhys = (long)status.ullTotalPhys;
            availPhys = (long)status.ullAvailPhys;
            return totalPhys > 0;
        }
        catch
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
