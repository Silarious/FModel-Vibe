using System;
using System.Collections.Generic;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;

namespace FModel.ViewModels.ProfileDiff;

public readonly record struct PackageMeta(long Size, bool IsEncrypted);

public readonly record struct ModifiedPackage(string Path, long OldSize, long NewSize, bool EncryptChanged)
{
    public long SizeDelta => NewSize - OldSize;
}

/// <summary>
/// Package inventory snapshot used for profile-to-profile diffs (path + size + encrypt flag).
/// Matches Backup Manager / Load All (New|Modified) semantics.
/// </summary>
public sealed class PackageInventory
{
    public IReadOnlyDictionary<string, PackageMeta> Entries { get; }

    public PackageInventory(IReadOnlyDictionary<string, PackageMeta> entries)
    {
        Entries = entries;
    }

    public static PackageInventory FromProvider(IFileProvider provider)
    {
        var map = new Dictionary<string, PackageMeta>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, file) in provider.Files)
        {
            if (file.IsUePackagePayload) continue;
            map[path] = new PackageMeta(file.Size, file.IsEncrypted);
        }

        return new PackageInventory(map);
    }

    public static ProfileDiffResult Diff(PackageInventory oldInventory, PackageInventory newInventory)
    {
        var added = new List<string>();
        var modified = new List<ModifiedPackage>();
        var removed = new List<string>();

        foreach (var (path, neo) in newInventory.Entries)
        {
            if (!oldInventory.Entries.TryGetValue(path, out var old))
            {
                added.Add(path);
                continue;
            }

            if (old.Size != neo.Size || old.IsEncrypted != neo.IsEncrypted)
            {
                modified.Add(new ModifiedPackage(
                    path,
                    old.Size,
                    neo.Size,
                    old.IsEncrypted != neo.IsEncrypted));
            }
        }

        foreach (var path in oldInventory.Entries.Keys)
        {
            if (!newInventory.Entries.ContainsKey(path))
                removed.Add(path);
        }

        added.Sort(StringComparer.OrdinalIgnoreCase);
        modified.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path));
        removed.Sort(StringComparer.OrdinalIgnoreCase);

        return new ProfileDiffResult(added, modified, removed);
    }
}

public sealed class ProfileDiffResult
{
    public IReadOnlyList<string> Added { get; }
    public IReadOnlyList<ModifiedPackage> Modified { get; }
    public IReadOnlyList<string> Removed { get; }

    public IEnumerable<string> ModifiedPaths
    {
        get
        {
            foreach (var m in Modified)
                yield return m.Path;
        }
    }

    public int DeltaCount => Added.Count + Modified.Count;

    public ProfileDiffResult(
        IReadOnlyList<string> added,
        IReadOnlyList<ModifiedPackage> modified,
        IReadOnlyList<string> removed)
    {
        Added = added;
        Modified = modified;
        Removed = removed;
    }
}
