using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using CUE4Parse.FileProvider.Objects;
using FModel.Settings;
using FModel.ViewModels;
using Serilog;

namespace FModel.Framework;

/// <summary>
/// Optional separate log of slow / inefficient exports.
/// Score is higher when a small file takes a long time (ms per MB), while large files
/// are still listed for comparison by absolute duration.
/// </summary>
public static class ExportPerfLog
{
    private static readonly ConcurrentBag<Sample> Samples = [];
    private static readonly object FlushGate = new();

    private readonly record struct Sample(
        string Path,
        long SizeBytes,
        long ElapsedMs,
        long WsBefore,
        long WsAfter,
        long PrivateBefore,
        long PrivateAfter,
        long GcBefore,
        long GcAfter);

    /// <summary>
    /// Inefficiency: milliseconds per megabyte (floor 0.001 MB so tiny files score high when slow).
    /// </summary>
    public static double Score(long elapsedMs, long sizeBytes)
    {
        var mb = Math.Max(sizeBytes / (1024.0 * 1024.0), 0.001);
        return elapsedMs / mb;
    }

    public static bool IsEnabled => UserSettings.Default?.EnableExportPerfLog == true;

    public static void Record(GameFile entry, long elapsedMs, long wsBefore, long wsAfter,
        long privateBefore, long privateAfter, long gcBefore, long gcAfter)
    {
        if (!IsEnabled || entry == null || elapsedMs < UserSettings.Default.ExportPerfLogMinMs)
            return;

        Samples.Add(new Sample(
            entry.Path ?? entry.Name,
            entry.Size,
            elapsedMs,
            wsBefore, wsAfter,
            privateBefore, privateAfter,
            gcBefore, gcAfter));
    }

    public static void Record(GameFileViewModel entry, long elapsedMs, long wsBefore, long wsAfter,
        long privateBefore, long privateAfter, long gcBefore, long gcAfter)
    {
        if (entry?.Asset != null)
            Record(entry.Asset, elapsedMs, wsBefore, wsAfter, privateBefore, privateAfter, gcBefore, gcAfter);
    }

    /// <summary>Flush collected samples for a finished parallel batch into the perf log file.</summary>
    public static void FlushBatch(string batchLabel, int itemCount, int dop)
    {
        if (!IsEnabled)
        {
            Samples.Clear();
            return;
        }

        Sample[] snapshot;
        lock (FlushGate)
        {
            if (Samples.IsEmpty)
                return;
            snapshot = Samples.ToArray();
            Samples.Clear();
        }

        try
        {
            WriteReport(batchLabel, itemCount, dop, snapshot);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[ExportPerf] failed to write performance log");
        }
    }

    private static void WriteReport(string batchLabel, int itemCount, int dop, Sample[] samples)
    {
        var logsDir = Path.Combine(UserSettings.Default.OutputDirectory, "Logs");
        Directory.CreateDirectory(logsDir);
        var path = Path.Combine(logsDir, $"FModel-ExportPerf-{DateTime.Now:yyyy-MM-dd}.log");

        var minMs = UserSettings.Default.ExportPerfLogMinMs;
        var byScore = samples
            .OrderByDescending(s => Score(s.ElapsedMs, s.SizeBytes))
            .Take(80)
            .ToArray();

        // Large files for comparison (even if score is low): ≥10MB, slowest first.
        var largeCutoff = 10L * 1024 * 1024;
        var large = samples
            .Where(s => s.SizeBytes >= largeCutoff)
            .OrderByDescending(s => s.ElapsedMs)
            .Take(40)
            .ToArray();

        var sb = new StringBuilder(16 * 1024);
        sb.AppendLine("================================================================================");
        sb.AppendLine($"Export performance batch @ {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Label: {batchLabel} | items={itemCount} | DOP={dop} | samples≥{minMs}ms={samples.Length}");
        sb.AppendLine("Score = elapsed_ms / max(size_MB, 0.001)  (higher = small file that was slow)");
        sb.AppendLine("WS/Private = process Working Set / Private Bytes around the export; Δ = after−before.");
        sb.AppendLine();

        sb.AppendLine("--- Highest inefficiency score (small & slow first) ---");
        sb.AppendLine(FormatHeader());
        foreach (var s in byScore)
            sb.AppendLine(FormatRow(s));

        sb.AppendLine();
        sb.AppendLine("--- Large files ≥10MB (by absolute duration, for comparison) ---");
        if (large.Length == 0)
        {
            sb.AppendLine("(none in this batch)");
        }
        else
        {
            sb.AppendLine(FormatHeader());
            foreach (var s in large)
                sb.AppendLine(FormatRow(s));
        }

        sb.AppendLine();

        File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
        Log.Information("[ExportPerf] wrote {Count} samples to {Path}", samples.Length, path);
    }

    private static string FormatHeader()
        => string.Format(CultureInfo.InvariantCulture,
            "{0,10} {1,10} {2,10} {3,10} {4,10} {5,10} {6}",
            "Score", "ms", "SizeMB", "ΔWS_MB", "ΔPrivMB", "ΔGC_MB", "Path");

    private static string FormatRow(Sample s)
    {
        var score = Score(s.ElapsedMs, s.SizeBytes);
        var sizeMb = s.SizeBytes / (1024.0 * 1024.0);
        var dWs = (s.WsAfter - s.WsBefore) / (1024.0 * 1024.0);
        var dPriv = (s.PrivateAfter - s.PrivateBefore) / (1024.0 * 1024.0);
        var dGc = (s.GcAfter - s.GcBefore) / (1024.0 * 1024.0);
        return string.Format(CultureInfo.InvariantCulture,
            "{0,10:F1} {1,10} {2,10:F3} {3,10:F1} {4,10:F1} {5,10:F1} {6}",
            score, s.ElapsedMs, sizeMb, dWs, dPriv, dGc, s.Path);
    }

    public static void ReadProcessMemory(out long workingSet, out long privateBytes)
    {
        using var proc = Process.GetCurrentProcess();
        proc.Refresh();
        workingSet = proc.WorkingSet64;
        privateBytes = proc.PrivateMemorySize64;
    }
}
