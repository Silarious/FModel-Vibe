using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.Utils;
using FModel.Extensions;
using FModel.Settings;
using Newtonsoft.Json;
using Serilog;

namespace FModel.ViewModels.ProfileDiff;

/// <summary>
/// Exports delta packages from a mounted provider (raw + properties) without using the live CUE4Parse provider.
/// </summary>
public static class ProfileDiffExporter
{
    public static int ExportDelta(
        IFileProvider provider,
        IReadOnlyList<string> paths,
        string propertiesDirectory,
        string rawDataDirectory,
        bool exportProperties,
        bool exportRaw,
        CancellationToken cancellationToken,
        Action<int, int, int, int> onProgress,
        out int failures,
        out int warnings)
    {
        failures = 0;
        warnings = 0;
        var exported = 0;
        var keepStructure = UserSettings.Default.KeepDirectoryStructure;
        var total = paths.Count;
        var processed = 0;

        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            processed++;

            if (!provider.Files.TryGetValue(path, out var entry) || entry.IsUePackagePayload)
            {
                onProgress?.Invoke(processed, total, failures, warnings);
                continue;
            }

            try
            {
                var wrote = false;
                if (exportRaw)
                {
                    var rawResult = TryExportRaw(provider, entry, rawDataDirectory, keepStructure);
                    wrote |= rawResult.Wrote;
                    warnings += rawResult.Warnings;
                }

                if (exportProperties && entry.IsUePackage)
                {
                    var propsResult = TryExportProperties(provider, entry, propertiesDirectory, keepStructure);
                    wrote |= propsResult.Wrote;
                    warnings += propsResult.Warnings;
                }

                if (wrote) exported++;
            }
            catch (Exception e)
            {
                failures++;
                Log.Warning(e, "Diff Checker failed to export {Path}", path);
            }

            onProgress?.Invoke(processed, total, failures, warnings);
        }

        return exported;
    }

    public static string WriteDiffLog(
        string oldProfileName,
        string newProfileName,
        ProfileDiffResult diff,
        string exportRoot)
    {
        var root = string.IsNullOrWhiteSpace(exportRoot)
            ? UserSettings.Default.OutputDirectory
            : exportRoot;
        Directory.CreateDirectory(root);

        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var safeOld = SanitizeFileName(oldProfileName);
        var safeNew = SanitizeFileName(newProfileName);
        var fullPath = Path.Combine(root, $"diff_{safeOld}_to_{safeNew}_{stamp}.txt");

        using var writer = new StreamWriter(fullPath);
        writer.WriteLine("# Diff Checker");
        writer.WriteLine($"# Old: {oldProfileName}");
        writer.WriteLine($"# New: {newProfileName}");
        writer.WriteLine($"# Generated: {DateTime.Now:O}");
        writer.WriteLine($"# Added: {diff.Added.Count} | Modified: {diff.Modified.Count} | Removed: {diff.Removed.Count}");
        writer.WriteLine();

        WriteSection(writer, "ADDED", diff.Added);
        WriteModifiedSection(writer, diff.Modified);
        WriteSection(writer, "REMOVED", diff.Removed);

        return fullPath;
    }

    private static void WriteSection(StreamWriter writer, string title, IReadOnlyList<string> paths)
    {
        writer.WriteLine($"## {title} ({paths.Count})");
        foreach (var path in paths)
            writer.WriteLine(path);
        writer.WriteLine();
    }

    private static void WriteModifiedSection(StreamWriter writer, IReadOnlyList<ModifiedPackage> modified)
    {
        writer.WriteLine($"## MODIFIED ({modified.Count})");
        foreach (var entry in modified)
        {
            var delta = entry.SizeDelta;
            var deltaText = delta >= 0 ? $"+{delta}" : delta.ToString();
            var encryptNote = entry.EncryptChanged ? "  [encrypt flag changed]" : "";
            writer.WriteLine($"{entry.Path}  [{entry.OldSize} -> {entry.NewSize}  ({deltaText})]{encryptNote}");
        }
        writer.WriteLine();
    }

    private readonly struct ExportAttempt
    {
        public bool Wrote { get; init; }
        public int Warnings { get; init; }
    }

    private static ExportAttempt TryExportRaw(
        IFileProvider provider,
        GameFile entry,
        string rawDataDirectory,
        bool keepStructure)
    {
        if (!provider.TrySavePackage(entry, out var assets) || assets == null || assets.Count == 0)
            return new ExportAttempt { Wrote = false, Warnings = 1 };

        var any = false;
        var warnings = 0;
        foreach (var (key, bytes) in assets)
        {
            var target = Path.Combine(
                rawDataDirectory,
                keepStructure ? key : key.SubstringAfterLast('/')).Replace('\\', '/');

            if (Helper.IsAlreadyExported(target))
            {
                warnings++;
                continue;
            }

            if (Helper.IsOverwriteProtected(target, bytes.LongLength, out _))
            {
                warnings++;
                continue;
            }

            Directory.CreateDirectory(target.SubstringBeforeLast('/'));
            File.WriteAllBytes(target, bytes);
            any = true;
        }

        return new ExportAttempt { Wrote = any, Warnings = warnings };
    }

    private static ExportAttempt TryExportProperties(
        IFileProvider provider,
        GameFile entry,
        string propertiesDirectory,
        bool keepStructure)
    {
        var fileName = Path.ChangeExtension(entry.Name, ".json");
        var target = Path.Combine(
            propertiesDirectory,
            keepStructure ? entry.Directory : "",
            fileName).Replace('\\', '/');

        if (Helper.IsAlreadyExported(target))
            return new ExportAttempt { Wrote = false, Warnings = 1 };

        var result = provider.GetLoadPackageResult(entry);
        var json = JsonConvert.SerializeObject(result.GetDisplayData(true), Formatting.Indented);
        var bytes = System.Text.Encoding.UTF8.GetByteCount(json);
        if (Helper.IsOverwriteProtected(target, bytes, out _))
            return new ExportAttempt { Wrote = false, Warnings = 1 };

        Directory.CreateDirectory(target.SubstringBeforeLast('/'));
        File.WriteAllText(target, json);
        return new ExportAttempt { Wrote = true, Warnings = 0 };
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "profile";
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Trim();
    }
}
