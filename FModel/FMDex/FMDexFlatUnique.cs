using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FModel.FMDex;

/// <summary>
/// Primary on-disk layout (v5+): drop folder structure when package basenames are unique,
/// and batch every same-tag file together. Colliding basenames keep a path <c>tree</c>.
/// </summary>
public static class FMDexFlatUnique
{
    public const string LayoutName = "flatUnique";

    public sealed class Analysis
    {
        public int Files { get; init; }
        public int UniqueBasenames { get; init; }
        public int CollisionBasenames { get; init; }
        public int CollisionFiles { get; init; }
        public bool AllUnique => CollisionBasenames == 0;
    }

    public static bool IsFlatUniqueDocument(JObject jo)
    {
        if (jo == null) return false;
        var layout = jo.Value<string>("layout");
        if (string.Equals(layout, LayoutName, StringComparison.OrdinalIgnoreCase))
            return true;

        // v5+ without explicit layout still uses root "*" batches
        return jo.Value<int?>("version") >= 5 && jo[FMDexDocument.FileGroupsKey] is JArray;
    }

    /// <summary>
    /// Basename collision analysis. Uses <see cref="StringComparer.OrdinalIgnoreCase"/> so
    /// <c>Foo.uasset</c> / <c>foo.uasset</c> are one name (required: flatUnique <c>*</c> keys
    /// also load into an ignore-case map). A case-sensitive recount can read ~tens higher.
    /// </summary>
    public static Analysis Analyze(IEnumerable<string> packagePaths)
    {
        var groups = packagePaths
            .GroupBy(PackageFileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var collisions = groups.Where(g => g.Count() > 1).ToList();
        return new Analysis
        {
            Files = groups.Sum(g => g.Count()),
            UniqueBasenames = groups.Count(g => g.Count() == 1),
            CollisionBasenames = collisions.Count,
            CollisionFiles = collisions.Sum(g => g.Count())
        };
    }

    /// <summary>
    /// Serialize flat-unique JSON. Unique basenames go in <c>*</c> (filename only, same-tag batched).
    /// Colliding basenames keep a nested <c>tree</c> keyed by full package path.
    /// </summary>
    public static string Serialize(FMDexDocument meta, Dictionary<string, List<string>> flat)
    {
        ArgumentNullException.ThrowIfNull(meta);
        ArgumentNullException.ThrowIfNull(flat);

        // Prefer full paths when a basename-only key and a pathed key both exist (post-load upsert).
        flat = CollapseBasenameDuplicates(flat);

        var analysis = Analyze(flat.Keys);
        var byBase = flat
            .GroupBy(kv => PackageFileName(kv.Key), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var uniqueEntries = byBase
            .Where(g => g.Count() == 1)
            .Select(g => g.First())
            .ToList();
        var collisionFlat = byBase
            .Where(g => g.Count() > 1)
            .SelectMany(g => g)
            // Ordinal keys: preserve distinct path casing inside the collision tree
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        if (uniqueEntries.Count + collisionFlat.Count != flat.Count)
            throw new InvalidOperationException(
                $"flatUnique split lost entries: unique={uniqueEntries.Count}, " +
                $"collision={collisionFlat.Count}, flat={flat.Count}");

        var uniqueBatches = uniqueEntries
            .GroupBy(kv => TagKey(kv.Value), StringComparer.Ordinal)
            .Select(g => (
                Tags: g.First().Value?.ToList() ?? [],
                Names: g.Select(x => PackageFileName(x.Key))
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList()))
            .Where(b => b.Names.Count > 0)
            .ToList();

        var sb = new StringBuilder(Math.Max(4096, flat.Count * 24));
        using var sw = new StringWriter(sb);
        using (var jw = new JsonTextWriter(sw) { Formatting = Formatting.None })
        {
            var serializer = JsonSerializer.CreateDefault();
            jw.WriteStartObject();

            jw.WritePropertyName("version");
            jw.WriteValue(meta.Version > 0 ? meta.Version : FMDexDocument.CurrentVersion);
            jw.WritePropertyName("layout");
            jw.WriteValue(LayoutName);
            jw.WritePropertyName("game");
            jw.WriteValue(meta.Game ?? "");
            jw.WritePropertyName("build");
            jw.WriteValue(meta.Build ?? "");
            jw.WritePropertyName("files");
            jw.WriteValue(flat.Count);
            jw.WritePropertyName("folders");
            jw.WriteValue(meta.Folders);
            jw.WritePropertyName("uniqueNames");
            jw.WriteValue(analysis.AllUnique);
            jw.WritePropertyName("collisionBasenames");
            jw.WriteValue(analysis.CollisionBasenames);
            jw.WritePropertyName("collisionFiles");
            jw.WriteValue(analysis.CollisionFiles);
            jw.WritePropertyName("lastModifiedUtc");
            jw.WriteValue(meta.LastModifiedUtc);

            // Tags are full UE class names (no "L" legend).

            if (uniqueBatches.Count > 0)
            {
                jw.WritePropertyName(FMDexDocument.FileGroupsKey);
                FMDexTreeConverter.WriteTagBatches(jw, uniqueBatches, serializer);
            }

            if (collisionFlat.Count > 0)
            {
                jw.WritePropertyName("tree");
                var collisionTree = FMDexTree.Build(collisionFlat);
                new FMDexTreeConverter().WriteJson(jw, collisionTree, serializer);
            }

            jw.WriteEndObject();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Load flat-unique JSON into a path/basename → tags map.
    /// Unique packages are keyed by basename; collisions keep full package paths from <c>tree</c>.
    /// </summary>
    public static Dictionary<string, List<string>> Deserialize(JObject jo)
    {
        var flat = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (jo == null) return flat;

        if (jo[FMDexDocument.FileGroupsKey] is JArray batches)
        {
            foreach (var (names, tags) in FMDexTreeConverter.ReadTagBatches(batches))
            {
                foreach (var name in names)
                {
                    var key = NormalizePath(name);
                    if (string.IsNullOrEmpty(key) || tags is not { Count: > 0 })
                        continue;
                    flat[key] = tags.ToList();
                }
            }
        }

        if (jo["tree"] is JObject treeObj)
        {
            var tree = FMDexTreeConverter.ReadTreeObject(treeObj);
            foreach (var (path, tags) in FMDexTree.Flatten(tree))
            {
                var key = NormalizePath(path);
                if (string.IsNullOrEmpty(key) || tags is not { Count: > 0 })
                    continue;
                flat[key] = tags.ToList();
            }
        }

        return flat;
    }

    public static string PackageFileName(string packagePath)
    {
        if (string.IsNullOrEmpty(packagePath))
            return packagePath;
        var i = packagePath.LastIndexOfAny(['/', '\\']);
        return i < 0 ? packagePath : packagePath[(i + 1)..];
    }

    /// <summary>
    /// If both <c>Foo.uasset</c> and <c>Dir/Foo.uasset</c> exist, keep the pathed key and merge tags.
    /// </summary>
    private static Dictionary<string, List<string>> CollapseBasenameDuplicates(
        Dictionary<string, List<string>> flat)
    {
        var byBase = flat.GroupBy(kv => PackageFileName(kv.Key), StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, List<string>>(flat.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var group in byBase)
        {
            var entries = group.ToList();
            if (entries.Count == 1)
            {
                result[entries[0].Key] = entries[0].Value;
                continue;
            }

            var pathed = entries.Where(e => e.Key.IndexOfAny(['/', '\\']) >= 0).ToList();
            if (pathed.Count == 0)
            {
                // Multiple basename-only keys that differ only by case — keep one, merge tags
                var tags = MergeTags(entries.Select(e => e.Value));
                result[entries[0].Key] = tags;
                continue;
            }

            foreach (var e in pathed)
            {
                if (result.TryGetValue(e.Key, out var existing))
                    result[e.Key] = MergeTags([existing, e.Value]);
                else
                    result[e.Key] = e.Value?.ToList() ?? [];
            }

            // Drop basename-only duplicates of the same name
            var basenameTags = entries
                .Where(e => e.Key.IndexOfAny(['/', '\\']) < 0)
                .Select(e => e.Value)
                .ToList();
            if (basenameTags.Count == 0)
                continue;

            var merged = MergeTags(basenameTags);
            foreach (var e in pathed)
                result[e.Key] = MergeTags([result[e.Key], merged]);
        }

        return result;
    }

    private static List<string> MergeTags(IEnumerable<List<string>> tagSets)
    {
        return tagSets
            .Where(t => t != null)
            .SelectMany(t => t)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizePath(string path)
        => string.IsNullOrEmpty(path) ? path : path.Replace('\\', '/').TrimStart('/');

    private static string TagKey(List<string> tags)
        => tags is not { Count: > 0 }
            ? string.Empty
            : string.Join('\0', tags);
}
