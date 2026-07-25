using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using FModel.Settings;
using Serilog;

namespace FModel.Framework;

/// <summary>
/// Restricts the FModel process (and optionally the calling thread) to a subset of
/// logical processors so reserved CPUs stay free for other apps.
/// Distinct from Export Max Concurrent Workers (degree of parallelism).
/// </summary>
public static class CpuAffinity
{
    private static readonly object Gate = new();
    private static bool _ready;
    private static int? _appliedReserved;
    private static nint? _appliedMask;

    /// <summary>
    /// Logical processors FModel may use: <c>ProcessorCount − reserved</c>, always ≥ 1.
    /// </summary>
    public static int GetUsableCoreCount()
    {
        var total = Environment.ProcessorCount;
        var reserved = EffectiveReserved(UserSettings.Default?.ReservedCpuCores ?? 2, total);
        return Math.Max(1, total - reserved);
    }

    /// <summary>
    /// Call once after Serilog is configured and <see cref="UserSettings.Default"/> is loaded.
    /// </summary>
    public static void Initialize()
    {
        lock (Gate)
        {
            _ready = true;
        }

        Apply(force: true);
    }

    /// <summary>
    /// Apply process affinity from <see cref="UserSettings.ReservedCpuCores"/>.
    /// Idempotent: skips work when the mask is unchanged unless <paramref name="force"/>.
    /// Always re-reads the OS mask after a successful set and logs desired vs actual.
    /// </summary>
    public static void Apply(bool force = false)
    {
        lock (Gate)
        {
            if (!_ready && !force)
                return;

            var total = Environment.ProcessorCount;
            var reserved = EffectiveReserved(UserSettings.Default?.ReservedCpuCores ?? 2, total);
            var usable = Math.Max(1, total - reserved);
            var desired = BuildMask(usable, reserved, out var systemMask);

            if (!force && _appliedReserved == reserved && _appliedMask == desired)
                return;

            if (!OperatingSystem.IsWindows())
            {
                Log.Warning(
                    "[CpuAffinity] not applied (non-Windows); reserved={Reserved} usable={Usable} total={Total}",
                    reserved, usable, total);
                _appliedReserved = reserved;
                _appliedMask = desired;
                return;
            }

            if (!TrySetProcessAffinity(desired, out var actual, out var setVia, out var error))
            {
                Log.Warning(
                    "[CpuAffinity] FAILED reserved={Reserved} usable={Usable} desired=0x{Desired:X} via={Via}: {Error}",
                    reserved, usable, (ulong)desired, setVia, error);
                return;
            }

            _appliedReserved = reserved;
            _appliedMask = desired;

            var ok = actual == desired;
            if (ok)
            {
                if (reserved <= 0)
                {
                    Log.Information(
                        "[CpuAffinity] unrestricted usable={Usable}/{Total} actual=0x{Actual:X} via={Via}",
                        usable, total, (ulong)actual, setVia);
                }
                else
                {
                    Log.Information(
                        "[CpuAffinity] reserved={Reserved} usable={Usable}/{Total} desired=0x{Desired:X} actual=0x{Actual:X} via={Via} (verify: Task Manager → Details → FModel_Vibe.exe → Set affinity; CPU {FirstBlocked}–{LastBlocked} unchecked)",
                        reserved, usable, total, (ulong)desired, (ulong)actual, setVia,
                        usable, total - 1);
                }
            }
            else
            {
                Log.Warning(
                    "[CpuAffinity] MISMATCH reserved={Reserved} usable={Usable}/{Total} desired=0x{Desired:X} actual=0x{Actual:X} system=0x{System:X} via={Via}",
                    reserved, usable, total, (ulong)desired, (ulong)actual, (ulong)systemMask, setVia);
            }
        }
    }

    /// <summary>
    /// Pin the calling thread to the current process affinity mask (belt-and-suspenders for
    /// LongRunning export workers). No-op when affinity is disabled or not yet applied.
    /// </summary>
    public static void ApplyToCurrentThread()
    {
        if (!OperatingSystem.IsWindows())
            return;

        nint mask;
        lock (Gate)
        {
            if (_appliedMask is not { } applied || _appliedReserved is not > 0)
                return;
            mask = applied;
        }

        try
        {
            var previous = SetThreadAffinityMask(GetCurrentThread(), mask);
            if (previous == nint.Zero)
            {
                var err = Marshal.GetLastWin32Error();
                if (err != 0)
                    Log.Debug("[CpuAffinity] SetThreadAffinityMask failed win32={Error}", err);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[CpuAffinity] ApplyToCurrentThread failed");
        }
    }

    /// <summary>Clamp to 0–4 and ensure at least one usable core remains.</summary>
    private static int EffectiveReserved(int reserved, int total)
    {
        reserved = Math.Clamp(reserved, 0, 4);
        if (total <= reserved)
            reserved = Math.Max(0, total - 1);
        return reserved;
    }

    /// <summary>
    /// Bits 0..(usable−1) set — reserves the highest-numbered logical processors.
    /// When reserved is 0, returns the system affinity mask (all CPUs this process may use).
    /// Valid for Windows with ≤ 64 logical CPUs (single processor group).
    /// </summary>
    private static nint BuildMask(int usable, int reserved, out nint systemMask)
    {
        systemMask = QuerySystemAffinityMask();

        if (reserved <= 0)
            return systemMask != nint.Zero ? systemMask : FullMaskForProcessorCount(Environment.ProcessorCount);

        usable = Math.Clamp(usable, 1, 64);
        return MaskForUsable(usable);
    }

    private static nint MaskForUsable(int usable)
    {
        if (usable >= 64)
            return unchecked((nint)(long)ulong.MaxValue);
        return (nint)((1UL << usable) - 1UL);
    }

    private static nint FullMaskForProcessorCount(int total)
    {
        total = Math.Clamp(total, 1, 64);
        return MaskForUsable(total);
    }

    private static nint QuerySystemAffinityMask()
    {
        try
        {
            // Pseudo-handle — do not CloseHandle / Dispose.
            if (GetProcessAffinityMask(GetCurrentProcess(), out _, out var system) && system != nint.Zero)
                return system;
        }
        catch
        {
            // fall through
        }

        return FullMaskForProcessorCount(Environment.ProcessorCount);
    }

    private static bool TrySetProcessAffinity(nint desired, out nint actual, out string via, out string error)
    {
        actual = nint.Zero;
        via = "none";
        error = null;

        var handle = GetCurrentProcess(); // pseudo-handle for current process

        try
        {
            if (SetProcessAffinityMask(handle, desired))
            {
                via = "SetProcessAffinityMask";
            }
            else
            {
                var win32 = Marshal.GetLastWin32Error();
                try
                {
                    // Managed fallback (does not dispose the Process instance).
                    Process.GetCurrentProcess().ProcessorAffinity = desired;
                    via = "Process.ProcessorAffinity";
                }
                catch (Exception ex)
                {
                    error = $"SetProcessAffinityMask win32={win32}; managed={ex.Message}";
                    return false;
                }
            }

            if (!GetProcessAffinityMask(handle, out actual, out _))
            {
                try
                {
                    actual = Process.GetCurrentProcess().ProcessorAffinity;
                }
                catch (Exception ex)
                {
                    error = $"GetProcessAffinityMask failed; managed read={ex.Message}";
                    return false;
                }
            }

            // Refresh managed Process cache so Task Manager / diagnostics stay consistent.
            try { Process.GetCurrentProcess().ProcessorAffinity = actual; }
            catch { /* ignore */ }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessAffinityMask(nint hProcess, out nint lpProcessAffinityMask, out nint lpSystemAffinityMask);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessAffinityMask(nint hProcess, nint dwProcessAffinityMask);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint SetThreadAffinityMask(nint hThread, nint dwThreadAffinityMask);
}
