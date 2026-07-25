using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FModel.FMDex;

/// <summary>
/// Shareable package→class-tag index. On disk: <c>*_FMDex.json.br</c> (Brotli-11 minified JSON).
/// Plain <c>*_FMDex.json</c> and legacy <c>*_FDex.*</c> still load; next save rewrites as <c>*_FMDex.json.br</c>.
/// Layout (v5+ <c>flatUnique</c>): same-tag basenames batched in root <c>*</c>;
/// colliding basenames keep a nested path <c>tree</c>. Tags store full UE class names.
/// </summary>
public sealed class FMDexDocument
{
    public const int CurrentVersion = 6;
    /// <summary>Legacy uncompressed suffix (still readable).</summary>
    public const string FileSuffixPlain = "_FMDex.json";
    /// <summary>Current on-disk suffix (Brotli-compressed minified JSON).</summary>
    public const string FileSuffixBrotli = "_FMDex.json.br";
    /// <summary>Legacy plain suffix from early FMDex builds (still readable; migrates on save).</summary>
    public const string LegacyFileSuffixPlain = "_FDex.json";
    /// <summary>Legacy Brotli suffix from early FMDex builds (still readable; migrates on save).</summary>
    public const string LegacyFileSuffixBrotli = "_FDex.json.br";
    /// <summary>Preferred save suffix.</summary>
    public const string FileSuffix = FileSuffixBrotli;

    /// <summary>Reserved key holding batched same-tag file groups (folder tree or flatUnique root).</summary>
    public const string FileGroupsKey = "*";

    [JsonProperty("version")]
    public int Version { get; set; } = CurrentVersion;

    /// <summary>On-disk layout. <c>flatUnique</c> is current; older files omit this and use nested <c>tree</c>.</summary>
    [JsonProperty("layout", NullValueHandling = NullValueHandling.Ignore)]
    public string Layout { get; set; }

    [JsonProperty("game")]
    public string Game { get; set; } = string.Empty;

    [JsonProperty("build")]
    public string Build { get; set; } = string.Empty;

    /// <summary>Number of indexed package files in <see cref="Tree"/>.</summary>
    [JsonProperty("files")]
    public int Files { get; set; }

    /// <summary>Number of unique folder path segments in the index tree.</summary>
    [JsonProperty("folders")]
    public int Folders { get; set; }

    /// <summary>UTC timestamp of the last save that changed this index.</summary>
    [JsonProperty("lastModifiedUtc")]
    public DateTime LastModifiedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Legacy abbreviation table (code → full class). No longer written; still read for migration.</summary>
    [JsonProperty("L", NullValueHandling = NullValueHandling.Ignore)]
    public Dictionary<string, string> Legend { get; set; }

    /// <summary>
    /// Nested folder tree. Files: <c>{"t":["Code"]}</c>; batches (v6+):
    /// <c>["Prefix_",["a","b"],".uasset",["Code"]]</c> (positional p/f/e/t; length 2–4).
    /// Legacy object form <c>{"p":…,"f":…,"e":…,"t":…}</c> still loads.
    /// </summary>
    [JsonProperty("tree")]
    [JsonConverter(typeof(FMDexTreeConverter))]
    public Dictionary<string, FMDexTreeNode> Tree { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Legacy v1 flat map — only used when reading old files.</summary>
    [JsonProperty("entries", NullValueHandling = NullValueHandling.Ignore)]
    public Dictionary<string, FMDexEntry> Entries { get; set; }

    public static string EnsureFileName(string nameOrPath)
    {
        var fileName = System.IO.Path.GetFileName(nameOrPath);
        if (fileName.EndsWith(FileSuffixBrotli, StringComparison.OrdinalIgnoreCase))
            return fileName;

        // Strip .br then .json so PioneerGame_FMDex.json → PioneerGame_FMDex.json.br
        if (fileName.EndsWith(FileSuffixPlain, StringComparison.OrdinalIgnoreCase))
            return fileName + ".br";

        // Legacy *_FDex.* → preferred *_FMDex.json.br
        if (fileName.EndsWith(LegacyFileSuffixBrotli, StringComparison.OrdinalIgnoreCase))
        {
            var legacyStem = fileName[..^LegacyFileSuffixBrotli.Length];
            return legacyStem + FileSuffixBrotli;
        }

        if (fileName.EndsWith(LegacyFileSuffixPlain, StringComparison.OrdinalIgnoreCase))
        {
            var legacyStem = fileName[..^LegacyFileSuffixPlain.Length];
            return legacyStem + FileSuffixBrotli;
        }

        var stem = fileName;
        if (stem.EndsWith(".br", StringComparison.OrdinalIgnoreCase))
            stem = stem[..^3];
        if (stem.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            stem = stem[..^5];
        if (stem.EndsWith("_FMDex", StringComparison.OrdinalIgnoreCase))
            return stem + ".json.br";
        if (stem.EndsWith("_FDex", StringComparison.OrdinalIgnoreCase))
            return stem[..^"_FDex".Length] + FileSuffixBrotli;

        return stem + FileSuffixBrotli;
    }

    /// <summary>Serialize with no whitespace (minified) for smallest on-disk size.</summary>
    public static string Serialize(FMDexDocument doc)
    {
        var sb = new StringBuilder(256 * 1024);
        using var sw = new StringWriter(sb);
        using (var jw = new JsonTextWriter(sw) { Formatting = Formatting.None })
        {
            var serializer = JsonSerializer.CreateDefault();
            serializer.Serialize(jw, doc);
        }

        return sb.ToString();
    }
}

/// <summary>In-memory / legacy API: package class tags.</summary>
public sealed class FMDexEntry
{
    [JsonProperty("tags")]
    public List<string> Tags { get; set; } = [];
}

/// <summary>Folder (Children set) or file (Tags set) node in the FMDex tree.</summary>
public sealed class FMDexTreeNode
{
    public List<string> Tags { get; init; }
    public Dictionary<string, FMDexTreeNode> Children { get; init; }

    public bool IsFile => Tags != null;
    public bool IsFolder => Children != null;

    public static FMDexTreeNode File(IEnumerable<string> tags) => new()
    {
        Tags = tags.ToList()
    };

    public static FMDexTreeNode Folder() => new()
    {
        Children = new Dictionary<string, FMDexTreeNode>(StringComparer.OrdinalIgnoreCase)
    };
}

/// <summary>
/// Optional decode of a legacy <c>L</c> abbreviation table when reading older FMDex files.
/// New saves write full UE class names in <c>t</c> and omit <c>L</c>.
/// </summary>
public static class FMDexCompact
{
    [ThreadStatic] private static Dictionary<string, string> _decode; // code → full

    public static IDisposable UseDecode(IReadOnlyDictionary<string, string> codeToFull)
    {
        _decode = codeToFull != null
            ? new Dictionary<string, string>(codeToFull, StringComparer.OrdinalIgnoreCase)
            : null;
        return new PopDecode();
    }

    public static string DecodeTag(string codeOrFull)
    {
        if (string.IsNullOrEmpty(codeOrFull) || _decode == null)
            return codeOrFull;
        return _decode.TryGetValue(codeOrFull, out var full) ? full : codeOrFull;
    }

    public static List<string> EncodeTags(IEnumerable<string> tags)
        => tags?.Where(t => !string.IsNullOrWhiteSpace(t))
              .Select(t => t.Trim())
              .Distinct(StringComparer.OrdinalIgnoreCase)
              .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
              .ToList()
           ?? [];

    public static List<string> DecodeTags(IEnumerable<string> tags)
        => tags?.Select(DecodeTag).Where(t => !string.IsNullOrWhiteSpace(t)).ToList() ?? [];

    private sealed class PopDecode : IDisposable
    {
        public void Dispose() => _decode = null;
    }
}

/// <summary>
/// Serializes tree as nested JSON. Same-tag files are batched; shared <c>_</c> prefixes
/// become positional <c>[p,f,e,t]</c> (v6+) or legacy <c>{p,f,e,t}</c> on read. Tags are full UE class names.
/// </summary>
public sealed class FMDexTreeConverter : JsonConverter<Dictionary<string, FMDexTreeNode>>
{
    public override void WriteJson(JsonWriter writer, Dictionary<string, FMDexTreeNode> value, JsonSerializer serializer)
    {
        WriteFolder(writer, value ?? new Dictionary<string, FMDexTreeNode>(), serializer);
    }

    private static void WriteFolder(JsonWriter writer, Dictionary<string, FMDexTreeNode> children, JsonSerializer serializer)
    {
        var folders = children
            .Where(kv => kv.Value.IsFolder && kv.Key != FMDexDocument.FileGroupsKey)
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var files = children
            .Where(kv => kv.Value.IsFile)
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (folders.Count == 0 && files.Count >= 2)
        {
            var tagGroups = GroupFilesByTags(files);
            if (tagGroups.Count == 1)
            {
                var (tags, names) = tagGroups[0];
                var clusters = ClusterByNamePrefix(names);
                if (clusters.Count == 1)
                {
                    WriteFileGroup(writer, clusters[0], tags, serializer);
                    return;
                }

                // Multiple prefix clusters, same tags → object with "*" array
                writer.WriteStartObject();
                writer.WritePropertyName(FMDexDocument.FileGroupsKey);
                writer.WriteStartArray();
                foreach (var cluster in clusters)
                    WriteFileGroup(writer, cluster, tags, serializer);
                writer.WriteEndArray();
                writer.WriteEndObject();
                return;
            }
        }

        writer.WriteStartObject();

        foreach (var (name, node) in folders)
        {
            writer.WritePropertyName(name);
            WriteFolder(writer, node.Children, serializer);
        }

        var groups = GroupFilesByTags(files);
        var singles = new List<(string Name, List<string> Tags)>();
        var batches = new List<(List<string> Tags, List<string> Names)>();

        foreach (var (tags, names) in groups)
        {
            if (names.Count == 1)
                singles.Add((names[0], tags));
            else
                batches.Add((tags, names));
        }

        foreach (var (name, tags) in singles.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            writer.WritePropertyName(name);
            writer.WriteStartObject();
            writer.WritePropertyName("t");
            serializer.Serialize(writer, FMDexCompact.EncodeTags(tags));
            writer.WriteEndObject();
        }

        if (batches.Count > 0)
        {
            writer.WritePropertyName(FMDexDocument.FileGroupsKey);
            WriteTagBatches(writer, batches, serializer);
        }

        writer.WriteEndObject();
    }

    /// <summary>Minimum siblings sharing a prefix before we factor it (size-driven).</summary>
    private const int MinPrefixGroup = 3;

    private static void WriteFileGroup(JsonWriter writer, NameCluster cluster, List<string> tags, JsonSerializer serializer)
    {
        // v6+: positional [p?, f, e?, t] — omit absent p/e; discriminate length-3 by types on read.
        var hasP = !string.IsNullOrEmpty(cluster.Prefix);
        var hasE = !string.IsNullOrEmpty(cluster.Extension);
        var encodedTags = FMDexCompact.EncodeTags(tags);

        writer.WriteStartArray();
        if (hasP)
            writer.WriteValue(cluster.Prefix);

        WriteFItems(writer, cluster.Items, serializer);

        if (hasE)
            writer.WriteValue(cluster.Extension);

        serializer.Serialize(writer, encodedTags);
        writer.WriteEndArray();
    }

    private static void WriteFItems(JsonWriter writer, List<FItem> items, JsonSerializer serializer)
    {
        writer.WriteStartArray();
        var i = 0;
        while (i < items.Count)
        {
            var item = items[i];
            if (!item.IsLeaf)
            {
                // Nested prefix group: ["Oil_", ["01","02"]] (positional p,f)
                writer.WriteStartArray();
                writer.WriteValue(item.Prefix);
                WriteFItems(writer, item.Children, serializer);
                writer.WriteEndArray();
                i++;
                continue;
            }

            // Coalesce consecutive pure-digit leaves into "01-13" ranges
            if (TryDigit(item.Leaf, out var startVal) &&
                i + MinPrefixGroup - 1 < items.Count)
            {
                var runEnd = i;
                var expect = startVal;
                for (var j = i + 1; j < items.Count; j++)
                {
                    if (!items[j].IsLeaf || !TryDigit(items[j].Leaf, out var v) || v != expect + 1)
                        break;
                    expect = v;
                    runEnd = j;
                }

                var runLen = runEnd - i + 1;
                if (runLen >= MinPrefixGroup)
                {
                    writer.WriteValue($"{items[i].Leaf}-{items[runEnd].Leaf}");
                    i = runEnd + 1;
                    continue;
                }
            }

            writer.WriteValue(item.Leaf);
            i++;
        }

        writer.WriteEndArray();
    }

    private static bool TryDigit(string s, out long value)
    {
        value = 0;
        if (string.IsNullOrEmpty(s)) return false;
        for (var i = 0; i < s.Length; i++)
        {
            if (!char.IsDigit(s[i]))
                return false;
        }

        return long.TryParse(s, out value);
    }

    /// <summary>Leaf filename fragment or nested <c>{p,f}</c> group.</summary>
    private sealed class FItem
    {
        public string Leaf;
        public string Prefix;
        public List<FItem> Children;
        public bool IsLeaf => Children == null;

        public static FItem FromLeaf(string s) => new() { Leaf = s };

        public static FItem FromGroup(string prefix, List<FItem> children) => new()
        {
            Prefix = prefix,
            Children = children
        };
    }

    private sealed class NameCluster
    {
        public string Prefix;
        public string Extension;
        public List<FItem> Items;
    }

    /// <summary>
    /// Factor common extension, then recursively factor <c>_</c> segments and
    /// letter+digit tails whenever ≥ <see cref="MinPrefixGroup"/> names share them.
    /// </summary>
    private static List<NameCluster> ClusterByNamePrefix(List<string> names)
    {
        var sorted = names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        TryFactorExtension(sorted, out var stems, out var ext);
        var items = CompressFragments(stems);

        // Hoist a single top-level group into the cluster's p when possible
        if (items is [{ Children: not null } only] && !only.IsLeaf)
        {
            return
            [
                new NameCluster
                {
                    Prefix = only.Prefix,
                    Extension = ext,
                    Items = only.Children
                }
            ];
        }

        return
        [
            new NameCluster
            {
                Prefix = null,
                Extension = ext,
                Items = items
            }
        ];
    }

    private static void TryFactorExtension(List<string> names, out List<string> stems, out string extension)
    {
        extension = null;
        stems = names;
        if (names.Count == 0) return;

        string ext = null;
        foreach (var name in names)
        {
            var dot = name.LastIndexOf('.');
            if (dot <= 0) { ext = null; break; }
            var thisExt = name[dot..];
            if (ext == null) ext = thisExt;
            else if (!string.Equals(ext, thisExt, StringComparison.OrdinalIgnoreCase))
            {
                ext = null;
                break;
            }
        }

        if (string.IsNullOrEmpty(ext)) return;
        extension = ext;
        stems = names.Select(n => n[..^ext.Length]).ToList();
    }

    private static List<FItem> CompressFragments(List<string> names)
    {
        if (names == null || names.Count == 0)
            return [];

        var sorted = names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

        // 1) Longest shared prefix cut at '_' or PascalCase boundary (InvestigateArcPart / Onboarding).
        if (sorted.Count >= MinPrefixGroup &&
            TryLongestSharedCompressiblePrefix(sorted, out var sharedPrefix))
        {
            var rests = new List<string>(sorted.Count);
            foreach (var n in sorted)
                rests.Add(n[sharedPrefix.Length..]);

            return CollapseChains([FItem.FromGroup(sharedPrefix, CompressFragments(rests))]);
        }

        // 2) Branch on first '_' or PascalCase segment when that key has ≥ MinPrefixGroup names.
        //    (branchKeys>=2 used to be required, but that skipped cases like Item / Item_* where the
        //    bare name blocks LCP and there is only one compactable branch.)
        var result = new List<FItem>();
        var leftover = new List<string>();
        var bySeg = sorted.GroupBy(FirstSegmentKey, StringComparer.OrdinalIgnoreCase).ToList();

        foreach (var group in bySeg)
        {
            var key = group.Key;
            var list = group.ToList();
            if (key != null && list.Count >= MinPrefixGroup)
            {
                var prefix = BuildSegmentPrefix(key, list[0]);
                var rests = new List<string>(list.Count);
                var ok = true;
                foreach (var n in list)
                {
                    if (n.Length > prefix.Length &&
                        n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        rests.Add(n[prefix.Length..]);
                    else
                    {
                        ok = false;
                        break;
                    }
                }

                if (ok)
                    result.Add(FItem.FromGroup(prefix, CompressFragments(rests)));
                else
                    leftover.AddRange(list);
            }
            else
            {
                leftover.AddRange(list);
            }
        }

        // 3) Shared prefix + trailing digits (Enforcer1..4, _01.._05, Foo_01..) — needs ≥3
        var afterDigits = new List<string>();
        foreach (var group in leftover.GroupBy(TrailingDigitKey, StringComparer.OrdinalIgnoreCase))
        {
            var key = group.Key;
            var list = group.ToList();
            if (key != null && list.Count >= MinPrefixGroup)
            {
                var digits = new List<FItem>(list.Count);
                var ok = true;
                foreach (var n in list.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    if (n.Length <= key.Length ||
                        !n.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                    {
                        ok = false;
                        break;
                    }

                    digits.Add(FItem.FromLeaf(n[key.Length..]));
                }

                // Pure digit rests always win (enables "01-05" ranges, e.g. Oil + _01.._05)
                var allDigits = ok && digits.All(d => TryDigit(d.Leaf, out _));
                if (ok && (allDigits || WorthPrefix(key.Length, list.Count)))
                    result.Add(FItem.FromGroup(key, digits));
                else
                    afterDigits.AddRange(list);
            }
            else
            {
                afterDigits.AddRange(list);
            }
        }

        foreach (var n in afterDigits.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            result.Add(FItem.FromLeaf(n));

        return CollapseChains(result
            .OrderBy(i => i.IsLeaf ? i.Leaf : i.Prefix, StringComparer.OrdinalIgnoreCase)
            .ToList());
    }

    /// <summary>
    /// Collapse <c>{p:"A_",f:[{p:"B_",f:[…]}]}</c> into <c>{p:"A_B_",f:[…]}</c>
    /// whenever a group has only one nested child group (no siblings).
    /// </summary>
    private static List<FItem> CollapseChains(List<FItem> items)
    {
        if (items == null || items.Count == 0)
            return items ?? [];

        return items.Select(CollapseOne).ToList();
    }

    private static FItem CollapseOne(FItem item)
    {
        if (item.IsLeaf)
            return item;

        var children = CollapseChains(item.Children);
        var prefix = item.Prefix ?? string.Empty;
        while (children is [{ Children: not null } only])
        {
            prefix += only.Prefix;
            children = only.Children;
        }

        return FItem.FromGroup(prefix, children);
    }

    /// <summary>
    /// Longest prefix shared by all names, cut at a trailing <c>_</c> or a PascalCase segment boundary
    /// (so <c>InvestigateArcPartFour_…</c> → <c>InvestigateArcPart</c>).
    /// </summary>
    private static bool TryLongestSharedCompressiblePrefix(List<string> names, out string prefix)
    {
        prefix = null;
        if (names.Count < MinPrefixGroup)
            return false;

        var lcp = LongestCommonPrefix(names);
        if (lcp.Length < 2)
            return false;

        var cuts = new SortedSet<int>();
        for (var i = 1; i < lcp.Length; i++)
        {
            if (lcp[i] == '_')
                cuts.Add(i + 1);
            else if (IsPascalBoundary(lcp[i - 1], lcp[i]))
                cuts.Add(i);
        }

        // Full LCP is valid when every name continues with '_' or a new Pascal segment
        if (names.All(n =>
                n.Length > lcp.Length &&
                (n[lcp.Length] == '_' || IsPascalBoundary(lcp[^1], n[lcp.Length]))))
            cuts.Add(lcp.Length);

        foreach (var cut in cuts.Reverse())
        {
            if (cut < 2)
                continue;
            var candidate = lcp[..cut];
            if (!WorthPrefix(candidate.Length, names.Count))
                continue;
            if (names.Any(n =>
                    n.Length <= candidate.Length ||
                    !n.StartsWith(candidate, StringComparison.OrdinalIgnoreCase)))
                continue;

            prefix = candidate;
            return true;
        }

        return false;
    }

    private static string LongestCommonPrefix(List<string> names)
    {
        var first = names[0];
        var len = first.Length;
        for (var i = 1; i < names.Count; i++)
        {
            var s = names[i];
            var m = Math.Min(len, s.Length);
            var k = 0;
            while (k < m && char.ToLowerInvariant(first[k]) == char.ToLowerInvariant(s[k]))
                k++;
            len = k;
            if (len == 0) break;
        }

        return first[..len];
    }

    /// <summary>JSON overhead for a nested <c>{"p":"…","f":[…]}</c> — only compress when we save chars.</summary>
    private static bool WorthPrefix(int prefixLen, int count)
        => prefixLen > 0 && count >= MinPrefixGroup && prefixLen * (count - 1) >= 12;

    /// <summary>
    /// First filename segment: ends at <c>_</c> or at the first PascalCase boundary
    /// (<c>Dialogue</c> from <c>Dialogue_…</c>, <c>Onboarding</c> from <c>OnboardingLoot…</c>,
    /// <c>I</c> from <c>I_01</c>).
    /// </summary>
    private static string FirstSegmentKey(string name)
    {
        if (string.IsNullOrEmpty(name))
            return null;

        for (var i = 1; i < name.Length; i++)
        {
            if (name[i] == '_' || IsPascalBoundary(name[i - 1], name[i]))
                return name[..i]; // allow single-char segments (I_01, A_Foo, …)
        }

        return null;
    }

    /// <summary>Prefix to strip for a segment key, including a following <c>_</c> when present.</summary>
    private static string BuildSegmentPrefix(string key, string sample)
    {
        if (sample.Length > key.Length &&
            sample.StartsWith(key, StringComparison.OrdinalIgnoreCase) &&
            sample[key.Length] == '_')
            return sample[..(key.Length + 1)];

        return sample.StartsWith(key, StringComparison.OrdinalIgnoreCase)
            ? sample[..key.Length]
            : key;
    }

    private static bool IsPascalBoundary(char prev, char next)
        => (char.IsUpper(next) && (char.IsLower(prev) || char.IsDigit(prev))) ||
           (char.IsDigit(next) && char.IsLetter(prev)) ||
           (char.IsLetter(next) && char.IsDigit(prev));

    /// <summary>
    /// Everything before a trailing digit run (<c>Enforcer</c>, <c>_</c>, <c>Foo_</c>), or null.
    /// Enables nesting <c>_01.._05</c> → <c>{p:"_",f:["01-05"]}</c> under a shared outer prefix like Oil.
    /// </summary>
    private static string TrailingDigitKey(string name)
    {
        if (string.IsNullOrEmpty(name) || !char.IsDigit(name[^1]))
            return null;

        var i = name.Length - 1;
        while (i >= 0 && char.IsDigit(name[i]))
            i--;

        if (i < 0 || i == name.Length - 1)
            return null;

        return name[..(i + 1)];
    }

    /// <summary>
    /// Write same-tag file batches with prefix compression (used by tree + flat-unique layouts).
    /// Root <c>*</c> groups are sorted by (extension, tagset) for stable Brotli-friendly output.
    /// </summary>
    public static void WriteTagBatches(
        JsonWriter writer,
        IEnumerable<(List<string> Tags, List<string> Names)> batches,
        JsonSerializer serializer)
    {
        var groups = new List<(string Ext, string TagSort, NameCluster Cluster, List<string> Tags)>();
        foreach (var (tags, names) in batches)
        {
            var encoded = FMDexCompact.EncodeTags(tags);
            var tagSort = TagKey(encoded);
            foreach (var cluster in ClusterByNamePrefix(names))
                groups.Add((cluster.Extension ?? string.Empty, tagSort, cluster, encoded));
        }

        writer.WriteStartArray();
        foreach (var g in groups
                     .OrderBy(x => x.Ext, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(x => x.TagSort, StringComparer.Ordinal))
        {
            WriteFileGroup(writer, g.Cluster, g.Tags, serializer);
        }

        writer.WriteEndArray();
    }

    /// <summary>Expand <c>*</c> batch array into filename/path + tag lists.</summary>
    public static List<(List<string> Names, List<string> Tags)> ReadTagBatches(JArray batches)
    {
        var result = new List<(List<string> Names, List<string> Tags)>();
        if (batches == null) return result;

        foreach (var item in batches)
        {
            if (!TryReadFileGroupToken(item, out var names, out var tags))
                continue;
            result.Add((names, tags));
        }

        return result;
    }

    /// <summary>Parse a nested folder object (same shape as on-disk <c>tree</c>).</summary>
    public static Dictionary<string, FMDexTreeNode> ReadTreeObject(JObject obj)
        => ReadFolder(obj ?? new JObject());

    private static List<(List<string> Tags, List<string> Names)> GroupFilesByTags(
        List<KeyValuePair<string, FMDexTreeNode>> files)
    {
        return files
            .GroupBy(kv => TagKey(kv.Value.Tags), StringComparer.Ordinal)
            .Select(g => (
                Tags: g.First().Value.Tags?.ToList() ?? [],
                Names: g.Select(x => x.Key).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()))
            .ToList();
    }

    private static string TagKey(List<string> tags)
        => tags is not { Count: > 0 }
            ? string.Empty
            : string.Join('\0', FMDexCompact.EncodeTags(tags));

    public override Dictionary<string, FMDexTreeNode> ReadJson(
        JsonReader reader, Type objectType, Dictionary<string, FMDexTreeNode> existingValue,
        bool hasExistingValue, JsonSerializer serializer)
    {
        var token = JToken.Load(reader);
        return token switch
        {
            JObject obj => ReadFolder(obj),
            JArray arr when TryReadFileGroupToken(arr, out var names, out var tags) =>
                names.ToDictionary(n => n, _ => FMDexTreeNode.File(tags), StringComparer.OrdinalIgnoreCase),
            _ => new Dictionary<string, FMDexTreeNode>(StringComparer.OrdinalIgnoreCase)
        };
    }

    private static Dictionary<string, FMDexTreeNode> ReadFolder(JObject obj)
    {
        if (TryReadFileGroup(obj, out var groupNames, out var groupTags))
        {
            var only = new Dictionary<string, FMDexTreeNode>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in groupNames)
                only[name] = FMDexTreeNode.File(groupTags);
            return only;
        }

        var result = new Dictionary<string, FMDexTreeNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in obj.Properties())
        {
            if (prop.Name == FMDexDocument.FileGroupsKey)
            {
                if (prop.Value is JArray arr)
                {
                    foreach (var item in arr)
                    {
                        if (!TryReadFileGroupToken(item, out var names, out var tags))
                            continue;
                        foreach (var name in names)
                            result[name] = FMDexTreeNode.File(tags);
                    }
                }

                continue;
            }

            if (prop.Value is JArray childArr &&
                TryReadFileGroupToken(childArr, out var arrNames, out var arrTags))
            {
                // Folder value collapsed to a single positional file-group array
                if (arrNames.Count == 1 && string.Equals(arrNames[0], prop.Name, StringComparison.OrdinalIgnoreCase))
                    result[prop.Name] = FMDexTreeNode.File(arrTags);
                else
                {
                    foreach (var name in arrNames)
                        result[name] = FMDexTreeNode.File(arrTags);
                }

                continue;
            }

            if (prop.Value is not JObject child)
                continue;

            // Single file leaf: { "t": [ ... ] } (no f/p)
            if (child["t"] != null && child["f"] == null && child["p"] == null &&
                child.Properties().All(p => p.Name is "t"))
            {
                var raw = ReadTagArray(child["t"]);
                result[prop.Name] = FMDexTreeNode.File(FMDexCompact.DecodeTags(raw));
                continue;
            }

            result[prop.Name] = new FMDexTreeNode
            {
                Children = ReadFolder(child)
            };
        }

        return result;
    }

    private static bool TryReadFileGroupToken(JToken token, out List<string> names, out List<string> tags)
    {
        names = null;
        tags = null;
        return token switch
        {
            JObject obj => TryReadFileGroup(obj, out names, out tags),
            JArray arr => TryReadFileGroupArray(arr, out names, out tags),
            _ => false
        };
    }

    private static bool TryReadFileGroup(JObject obj, out List<string> names, out List<string> tags)
    {
        names = null;
        tags = null;
        if (obj["f"] is not JArray filesArr || obj["t"] is null)
            return false;

        var prefix = obj.Value<string>("p") ?? string.Empty;
        var ext = obj.Value<string>("e") ?? string.Empty;
        names = [];
        ExpandFItems(filesArr, prefix, ext, names);

        tags = FMDexCompact.DecodeTags(ReadTagArray(obj["t"]));
        return names.Count > 0;
    }

    /// <summary>
    /// Positional file group: <c>[f,t]</c>, <c>[p,f,t]</c>, <c>[f,e,t]</c>, or <c>[p,f,e,t]</c>.
    /// Length-3 is disambiguated by whether the first element is a string (p) or array (f).
    /// </summary>
    private static bool TryReadFileGroupArray(JArray arr, out List<string> names, out List<string> tags)
    {
        names = null;
        tags = null;
        if (arr == null) return false;

        string prefix = string.Empty;
        string ext = string.Empty;
        JArray filesArr = null;
        JToken tagsTok = null;

        switch (arr.Count)
        {
            case 2:
                // [f, t]
                filesArr = arr[0] as JArray;
                tagsTok = arr[1];
                break;
            case 3 when arr[0] is JValue { Type: JTokenType.String } && arr[1] is JArray:
                // [p, f, t]
                prefix = arr[0].Value<string>() ?? string.Empty;
                filesArr = (JArray)arr[1];
                tagsTok = arr[2];
                break;
            case 3 when arr[0] is JArray && arr[1] is JValue { Type: JTokenType.String }:
                // [f, e, t]
                filesArr = (JArray)arr[0];
                ext = arr[1].Value<string>() ?? string.Empty;
                tagsTok = arr[2];
                break;
            case 4:
                // [p, f, e, t]
                prefix = arr[0].Value<string>() ?? string.Empty;
                filesArr = arr[1] as JArray;
                ext = arr[2].Value<string>() ?? string.Empty;
                tagsTok = arr[3];
                break;
            default:
                return false;
        }

        if (filesArr == null || tagsTok == null)
            return false;

        names = [];
        ExpandFItems(filesArr, prefix, ext, names);
        tags = FMDexCompact.DecodeTags(ReadTagArray(tagsTok));
        return names.Count > 0;
    }

    private static void ExpandFItems(JArray items, string prefix, string ext, List<string> names)
    {
        foreach (var token in items)
        {
            switch (token)
            {
                case JValue { Type: JTokenType.String } v:
                {
                    var leaf = v.Value<string>();
                    if (string.IsNullOrWhiteSpace(leaf))
                        continue;

                    if (TryExpandDigitRange(leaf, out var expanded))
                    {
                        foreach (var part in expanded)
                            AppendName(names, prefix, part, ext);
                    }
                    else
                    {
                        AppendName(names, prefix, leaf, ext);
                    }

                    break;
                }
                case JArray nested when nested.Count == 2 &&
                                        nested[0] is JValue { Type: JTokenType.String } &&
                                        nested[1] is JArray nestedF:
                {
                    var nestedPrefix = prefix + (nested[0].Value<string>() ?? string.Empty);
                    ExpandFItems(nestedF, nestedPrefix, ext, names);
                    break;
                }
                case JObject nested when nested["f"] is JArray nestedF:
                {
                    var nestedPrefix = prefix + (nested.Value<string>("p") ?? string.Empty);
                    ExpandFItems(nestedF, nestedPrefix, ext, names);
                    break;
                }
            }
        }
    }

    private static void AppendName(List<string> names, string prefix, string leaf, string ext)
    {
        names.Add(string.IsNullOrEmpty(prefix) && string.IsNullOrEmpty(ext)
            ? leaf
            : prefix + leaf + ext);
    }

    /// <summary>Expand <c>01-13</c> into zero-padded consecutive integers (inclusive).</summary>
    private static bool TryExpandDigitRange(string leaf, out List<string> parts)
    {
        parts = null;
        var dash = leaf.IndexOf('-');
        if (dash <= 0 || dash != leaf.LastIndexOf('-') || dash >= leaf.Length - 1)
            return false;

        var left = leaf[..dash];
        var right = leaf[(dash + 1)..];
        if (!TryDigit(left, out var start) || !TryDigit(right, out var end) || end < start)
            return false;

        // Avoid treating real hyphenated names with digits on both sides as ranges
        // unless both sides are purely numeric (already checked) and span is sane.
        if (end - start > 100_000)
            return false;

        var width = left.Length;
        parts = new List<string>((int)(end - start + 1));
        for (var n = start; n <= end; n++)
        {
            var s = n.ToString();
            if (s.Length < width)
                s = s.PadLeft(width, '0');
            parts.Add(s);
        }

        return true;
    }

    private static List<string> ReadTagArray(JToken token)
    {
        return token switch
        {
            JArray arr => arr.Values<string>().Where(s => !string.IsNullOrWhiteSpace(s)).ToList(),
            JValue { Type: JTokenType.String } v => [v.Value<string>()],
            _ => []
        };
    }
}

public static class FMDexTree
{
    public static Dictionary<string, List<string>> Flatten(Dictionary<string, FMDexTreeNode> tree)
    {
        var flat = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (tree == null) return flat;
        Walk(tree, "", flat);
        return flat;
    }

    private static void Walk(Dictionary<string, FMDexTreeNode> nodes, string prefix, Dictionary<string, List<string>> flat)
    {
        foreach (var (name, node) in nodes)
        {
            if (name == FMDexDocument.FileGroupsKey)
                continue;

            var path = string.IsNullOrEmpty(prefix) ? name : prefix + "/" + name;
            if (node.IsFile)
                flat[path] = node.Tags?.ToList() ?? [];
            else if (node.IsFolder)
                Walk(node.Children, path, flat);
        }
    }

    public static Dictionary<string, FMDexTreeNode> Build(Dictionary<string, List<string>> flat)
    {
        var root = new Dictionary<string, FMDexTreeNode>(StringComparer.OrdinalIgnoreCase);
        if (flat == null) return root;

        foreach (var (path, tags) in flat)
        {
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;

            var current = root;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                var segment = parts[i];
                if (!current.TryGetValue(segment, out var node) || !node.IsFolder)
                {
                    node = FMDexTreeNode.Folder();
                    current[segment] = node;
                }

                current = node.Children;
            }

            current[parts[^1]] = FMDexTreeNode.File(tags ?? []);
        }

        return root;
    }

    /// <summary>Count indexed files and unique folder paths from a flat path→tags map.</summary>
    public static void CountStats(Dictionary<string, List<string>> flat, out int files, out int folders)
    {
        files = flat?.Count ?? 0;
        if (flat is not { Count: > 0 })
        {
            folders = 0;
            return;
        }

        var folderSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in flat.Keys)
        {
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var acc = "";
            for (var i = 0; i < parts.Length - 1; i++)
            {
                acc = i == 0 ? parts[i] : acc + "/" + parts[i];
                folderSet.Add(acc);
            }
        }

        folders = folderSet.Count;
    }
}
