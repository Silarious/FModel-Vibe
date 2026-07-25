using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Objects.UObject;
using FModel.Settings;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;

namespace FModel.FMDex;

/// <summary>
/// Loads/saves active <c>*_FMDex.json.br</c> (Brotli) and answers path→class-tag lookups for Search.
/// On disk (v5+): flatUnique JSON — same-tag basenames batched; colliding names keep a path tree.
/// Plain <c>*_FMDex.json</c> and legacy <c>*_FDex.*</c> still load. In memory: flat path/basename → tags map.
/// </summary>
public sealed class FMDexService
{
    public static FMDexService Instance { get; } = new();

    private readonly object _gate = new();
    private FMDexDocument _doc = new();
    /// <summary>Normalized package path → class tags.</summary>
    private Dictionary<string, List<string>> _flat = new(StringComparer.OrdinalIgnoreCase);
    private string _loadedPath;
    private bool _dirty;
    /// <summary>True after the first load attempt (file or empty). Prevents re-scanning the FMDex folder on every IsIndexed call.</summary>
    private bool _ready;

    public string LoadedPath
    {
        get { lock (_gate) return _loadedPath; }
    }

    public int EntryCount
    {
        get { lock (_gate) return _flat.Count; }
    }

    /// <summary>
    /// Folder for <c>*_FMDex.json.br</c> — always under the FModel install directory
    /// (<c>{install}/FMDex/{Profile}</c>), never the previous profile's Raw/Output paths.
    /// </summary>
    public string DirectoryPath
    {
        get
        {
            var dir = ResolveExportFolder();
            Directory.CreateDirectory(dir);
            if (!string.Equals(UserSettings.Default.FMDexDirectory, dir, StringComparison.OrdinalIgnoreCase))
                UserSettings.Default.FMDexDirectory = dir;
            return dir;
        }
    }

    /// <summary>
    /// FMDex output root: <c>{AppContext.BaseDirectory}/FMDex/{CurrentProfile}</c>.
    /// Intentionally ignores RawData/Properties/Output so indexes never inherit another profile's datamine folder.
    /// </summary>
    public static string ResolveExportFolder()
    {
        var install = AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var profile = UserSettings.Default.CurrentProfileName;
        if (string.IsNullOrWhiteSpace(profile))
            profile = "Default";
        var safe = string.Join("_", profile.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        if (string.IsNullOrWhiteSpace(safe))
            safe = "Default";
        return Path.Combine(install, "FMDex", safe);
    }

    public void EnsureLoaded()
    {
        if (_ready) return;

        lock (_gate)
        {
            if (_ready) return;

            // Never auto-pick the last Active File / newest *_FMDex here. That sticky
            // PioneerGame (or other) index across archive switches. BindToProvider
            // selects the correct file once ProjectName is known after mount.
            // Do not clear UserSettings — BindToProvider realigns Active File / Game.
            _doc = new FMDexDocument
            {
                Version = FMDexDocument.CurrentVersion,
                Layout = FMDexFlatUnique.LayoutName
            };
            _flat = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            _loadedPath = null;
            _dirty = false;
            _ready = true;
            Log.Information("FMDex: waiting for mounted project before selecting an index ({Dir})", DirectoryPath);
        }
    }

    /// <summary>
    /// Align the active FMDex with the mounted provider project. Call after VFS mount when
    /// <see cref="IFileProvider.ProjectName"/> is known. Switches away from a previous game's
    /// index (e.g. PioneerGame) instead of appending into it.
    /// </summary>
    public void BindToProvider(IFileProvider provider)
    {
        if (provider == null) return;

        var project = provider.ProjectName?.Trim();
        TryReadBuildManifest(provider, out var manifestGame, out var build);
        var game = FirstNonEmpty(manifestGame, project);
        if (string.IsNullOrWhiteSpace(game))
            game = "UnknownGame";

        lock (_gate)
        {
            var sameGame =
                _ready &&
                string.Equals(_doc.Game, game, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(UserSettings.Default.FMDexGame, game, StringComparison.OrdinalIgnoreCase);

            if (sameGame)
            {
                if (!string.IsNullOrWhiteSpace(build) &&
                    !string.Equals(UserSettings.Default.FMDexBuild, build, StringComparison.Ordinal))
                {
                    UserSettings.Default.FMDexBuild = build;
                    SyncSettingsMetadataUnlocked();
                }

                Log.Debug("FMDex already bound to {Game} ({Path})", game, _loadedPath ?? "(empty)");
                return;
            }

            // Persist the previous game's dirty index before swapping.
            if (_dirty)
            {
                try { SaveUnlocked(); }
                catch (Exception ex)
                {
                    Log.Warning(ex, "FMDex could not save previous index before switching to {Game}", game);
                }
            }

            UserSettings.Default.FMDexGame = game;
            UserSettings.Default.FMDexBuild = build?.Trim() ?? string.Empty;

            // Ensure output dir tracks this profile's export folder before picking a file.
            _ = DirectoryPath;

            var match = FindBestIndexFileUnlocked(game, build);
            if (match != null)
            {
                LoadUnlocked(match);
                Log.Information("FMDex bound to {Game} → {Path} ({Count} entries)", game, match, _flat.Count);
            }
            else
            {
                ResetForGameUnlocked(game, build);
                Log.Information("FMDex bound to {Game} with empty index (no matching file under {Dir})",
                    game, DirectoryPath);
            }
        }
    }

    public void Load(string path)
    {
        lock (_gate) LoadUnlocked(path);
    }

    private void LoadUnlocked(string path)
    {
        try
        {
            var json = FMDexIO.ReadAllText(path);
            var jo = JObject.Parse(json);
            // Legacy "L" legend: expand short codes → full names while reading, then drop L on next save.
            var legend = jo["L"]?.ToObject<Dictionary<string, string>>()
                         ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            using (FMDexCompact.UseDecode(legend))
            {
                if (FMDexFlatUnique.IsFlatUniqueDocument(jo))
                {
                    _flat = FMDexFlatUnique.Deserialize(jo);
                    _doc = new FMDexDocument
                    {
                        Version = jo.Value<int?>("version") ?? FMDexDocument.CurrentVersion,
                        Layout = FMDexFlatUnique.LayoutName,
                        Game = jo.Value<string>("game") ?? string.Empty,
                        Build = jo.Value<string>("build") ?? string.Empty,
                        Files = jo.Value<int?>("files") ?? _flat.Count,
                        Folders = jo.Value<int?>("folders") ?? 0,
                        LastModifiedUtc = jo.Value<DateTime?>("lastModifiedUtc") ?? DateTime.UtcNow,
                        Legend = null,
                        Tree = null,
                        Entries = null
                    };
                }
                else
                {
                    _doc = jo.ToObject<FMDexDocument>() ?? new FMDexDocument();
                    _flat = BuildFlatFromDocument(_doc);
                    _doc.Legend = null;
                    _doc.Entries = null; // never rewrite legacy flat map
                }
            }

            if (legend.Count > 0 || !FMDexIO.IsBrotliPath(path))
            {
                // Expand legend / migrate plaintext → .br immediately so the active file is current format.
                _dirty = true;
                _doc.Version = FMDexDocument.CurrentVersion;
                _doc.Layout = FMDexFlatUnique.LayoutName;
                _loadedPath = path;
                UserSettings.Default.FMDexActiveFile = path;
                SeedSettingsFromDocumentUnlocked();
                _ready = true;
                Log.Information("FMDex loaded {Path} ({Count} entries, layout={Layout}, migrating to .br)",
                    path, _flat.Count, _doc.Layout);
                SaveUnlocked();
                return;
            }

            _dirty = false;
            _doc.Version = FMDexDocument.CurrentVersion;
            _doc.Layout = FMDexFlatUnique.LayoutName;
            _loadedPath = path;
            UserSettings.Default.FMDexActiveFile = path;
            SeedSettingsFromDocumentUnlocked();
            _ready = true;
            Log.Information("FMDex loaded {Path} ({Count} entries, layout={Layout})",
                path, _flat.Count, _doc.Layout);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "FMDex failed to load {Path}", path);
            _doc = new FMDexDocument();
            _flat = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            _loadedPath = path;
            _ready = true;
        }
    }

    private static Dictionary<string, List<string>> BuildFlatFromDocument(FMDexDocument doc)
    {
        // Prefer nested tree (v2+)
        if (doc.Tree is { Count: > 0 })
            return FMDexTree.Flatten(doc.Tree);

        // Migrate legacy v1 flat entries
        var flat = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (doc.Entries == null) return flat;

        foreach (var (path, entry) in doc.Entries)
        {
            var key = NormalizePath(path);
            var tags = entry?.Tags;
            if (string.IsNullOrEmpty(key) || tags is not { Count: > 0 })
                continue;
            flat[key] = tags
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return flat;
    }

    public void Save()
    {
        lock (_gate)
        {
            SyncSettingsMetadataUnlocked();
            if (!_dirty && _loadedPath != null) return;
            SaveUnlocked();
        }
    }

    /// <summary>Caller must hold <see cref="_gate"/>.</summary>
    private void SaveUnlocked()
    {
            _doc.Version = FMDexDocument.CurrentVersion;
            _doc.Layout = FMDexFlatUnique.LayoutName;
            _doc.LastModifiedUtc = DateTime.UtcNow;
            _doc.Entries = null;
            _doc.Tree = null;
            _doc.Legend = null; // full class names in "t" arrays — no abbreviation table
            FMDexTree.CountStats(_flat, out var fileCount, out var folderCount);
            _doc.Files = fileCount;
            _doc.Folders = folderCount;

            // Always write into the live profile export folder (not a sticky path from an older archive).
            var previousPath = _loadedPath;
            var path = Path.Combine(DirectoryPath, DefaultFileNameUnlocked());

            var json = FMDexFlatUnique.Serialize(_doc, _flat);

            FMDexIO.WriteAllTextBrotli(path, json);

            // Drop previous path after rename/migrate (plaintext sibling, other folder, or name without build).
            if (!string.IsNullOrWhiteSpace(previousPath) &&
                !string.Equals(previousPath, path, StringComparison.OrdinalIgnoreCase) &&
                File.Exists(previousPath))
            {
                try { File.Delete(previousPath); }
                catch (Exception ex) { Log.Debug(ex, "FMDex could not delete previous {Path}", previousPath); }
            }

            _loadedPath = path;
            UserSettings.Default.FMDexActiveFile = path;
            _dirty = false;
            _ready = true;
            Log.Information("FMDex saved {Path} ({Files} files, layout={Layout}, brotli={Quality}, game={Game}, build={Build})",
                path, _doc.Files, _doc.Layout, FMDexIO.BrotliQuality, _doc.Game, _doc.Build);
    }

    private string DefaultFileNameUnlocked()
    {
        var game = SanitizeFileToken(UserSettings.Default.FMDexGame);
        if (string.IsNullOrEmpty(game))
            game = SanitizeFileToken(_doc.Game);
        if (string.IsNullOrEmpty(game))
            game = "Game";

        var build = SanitizeFileToken(UserSettings.Default.FMDexBuild);
        if (string.IsNullOrEmpty(build))
            build = SanitizeFileToken(_doc.Build);

        var stem = string.IsNullOrEmpty(build) ? game : $"{game}_{build}";
        return stem + FMDexDocument.FileSuffixBrotli;
    }

    private static string SanitizeFileToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Trim().Select(c => invalid.Contains(c) || c is '/' or '\\' ? '_' : c).ToArray();
        return new string(chars);
    }

    /// <summary>
    /// Align FMDex with the mounted project (see <see cref="BindToProvider"/>).
    /// When already bound to the same game, only fills a blank build from the manifest.
    /// </summary>
    public bool EnsureGameBuildFromManifest(IFileProvider provider, bool overwrite = false)
    {
        if (provider == null) return false;

        if (overwrite)
        {
            BindToProvider(provider);
            return !string.IsNullOrWhiteSpace(UserSettings.Default.FMDexGame);
        }

        var project = provider.ProjectName?.Trim();
        TryReadBuildManifest(provider, out var manifestGame, out var build);
        var game = FirstNonEmpty(manifestGame, project);
        if (string.IsNullOrWhiteSpace(game))
            return false;

        var boundGame = UserSettings.Default.FMDexGame?.Trim();
        if (!string.Equals(boundGame, game, StringComparison.OrdinalIgnoreCase) || !_ready)
        {
            BindToProvider(provider);
            return true;
        }

        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(UserSettings.Default.FMDexBuild) &&
                !string.IsNullOrWhiteSpace(build))
            {
                UserSettings.Default.FMDexBuild = build;
                SyncSettingsMetadataUnlocked();
                Log.Information("FMDex build from manifest: {Build}", build);
            }

            return !string.IsNullOrWhiteSpace(UserSettings.Default.FMDexGame);
        }
    }

    /// <summary>
    /// Reads <c>{Project}/BuildInfo/manifest.json</c> (Embark-style):
    /// <c>identifier</c> (preferred) / <c>baseidentifier</c> / <c>cl</c> → build;
    /// provider project name → game.
    /// </summary>
    public static bool TryReadBuildManifest(IFileProvider provider, out string game, out string build)
    {
        game = null;
        build = null;
        if (provider == null) return false;

        var project = provider.ProjectName?.Trim();
        if (string.IsNullOrWhiteSpace(project))
            return false;

        var candidates = new List<string> { $"{project}/BuildInfo/manifest.json" };

        foreach (var path in candidates)
        {
            if (!provider.TrySaveAsset(path, out var data) || data is not { Length: > 0 })
                continue;

            try
            {
                var text = Encoding.UTF8.GetString(data).TrimStart('\uFEFF');
                var jo = JObject.Parse(text);
                build = FirstNonEmpty(
                    jo.Value<string>("identifier"),
                    jo.Value<string>("baseidentifier"),
                    jo.Value<string>("cl"));
                game = project;
                if (!string.IsNullOrWhiteSpace(build))
                    return true;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "FMDex failed parsing BuildInfo manifest at {Path}", path);
            }
        }

        return false;
    }

    private void SeedSettingsFromDocumentUnlocked()
    {
        // Loaded file wins — avoid keeping a stale previous game in Settings (e.g. PioneerGame).
        if (!string.IsNullOrWhiteSpace(_doc.Game))
            UserSettings.Default.FMDexGame = _doc.Game;

        if (!string.IsNullOrWhiteSpace(_doc.Build))
            UserSettings.Default.FMDexBuild = _doc.Build;
    }

    /// <summary>Caller must hold <see cref="_gate"/>.</summary>
    private void ResetForGameUnlocked(string game, string build)
    {
        _doc = new FMDexDocument
        {
            Version = FMDexDocument.CurrentVersion,
            Layout = FMDexFlatUnique.LayoutName,
            Game = game?.Trim() ?? string.Empty,
            Build = build?.Trim() ?? string.Empty,
            LastModifiedUtc = DateTime.UtcNow
        };
        _flat = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        _loadedPath = null;
        _dirty = false;
        _ready = true;
        UserSettings.Default.FMDexActiveFile = string.Empty;
        UserSettings.Default.FMDexGame = _doc.Game;
        UserSettings.Default.FMDexBuild = _doc.Build;
    }

    /// <summary>Caller must hold <see cref="_gate"/>.</summary>
    private string FindBestIndexFileUnlocked(string game, string build)
    {
        if (string.IsNullOrWhiteSpace(game)) return null;

        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(DirectoryPath)
                .Where(FMDexIO.IsFmDexPath)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "FMDex failed enumerating {Dir}", DirectoryPath);
            return null;
        }

        if (files.Count == 0) return null;

        var preferred = UserSettings.Default.FMDexActiveFile;
        if (!string.IsNullOrWhiteSpace(preferred) &&
            File.Exists(preferred) &&
            IndexBelongsToGame(preferred, game))
            return preferred;

        var scored = new List<(string Path, int Score, DateTime Mtime)>();
        foreach (var path in files)
        {
            if (!IndexBelongsToGame(path, game))
                continue;

            var score = 1;
            if (!string.IsNullOrWhiteSpace(build) &&
                TryPeekGameBuild(path, out _, out var fileBuild) &&
                string.Equals(fileBuild, build, StringComparison.OrdinalIgnoreCase))
                score = 3;
            else if (FileNameLooksLikeGame(path, game))
                score = 2;

            DateTime mtime;
            try { mtime = File.GetLastWriteTimeUtc(path); }
            catch { mtime = DateTime.MinValue; }

            scored.Add((path, score, mtime));
        }

        return scored
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Mtime)
            .Select(x => x.Path)
            .FirstOrDefault();
    }

    private static bool IndexBelongsToGame(string path, string game)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(game))
            return false;

        if (TryPeekGameBuild(path, out var fileGame, out _) &&
            string.Equals(fileGame, game, StringComparison.OrdinalIgnoreCase))
            return true;

        return FileNameLooksLikeGame(path, game);
    }

    private static bool FileNameLooksLikeGame(string path, string game)
    {
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(name) || string.IsNullOrWhiteSpace(game))
            return false;

        // PioneerGame_FMDex.json.br / PioneerGame_CL123_FMDex.json.br (also legacy *_FDex.*)
        return name.StartsWith(game + "_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryPeekGameBuild(string path, out string game, out string build)
    {
        game = null;
        build = null;
        try
        {
            var json = FMDexIO.ReadAllText(path);
            var jo = JObject.Parse(json);
            game = jo.Value<string>("game");
            build = jo.Value<string>("build");
            return !string.IsNullOrWhiteSpace(game);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "FMDex could not peek metadata from {Path}", path);
            return false;
        }
    }

    /// <summary>Copy Settings → document; mark dirty when game/build actually change.</summary>
    private void SyncSettingsMetadataUnlocked()
    {
        var game = UserSettings.Default.FMDexGame?.Trim() ?? string.Empty;
        var build = UserSettings.Default.FMDexBuild?.Trim() ?? string.Empty;
        if (!string.Equals(_doc.Game ?? string.Empty, game, StringComparison.Ordinal) ||
            !string.Equals(_doc.Build ?? string.Empty, build, StringComparison.Ordinal))
        {
            _doc.Game = game;
            _doc.Build = build;
            _dirty = true;
        }
        else
        {
            _doc.Game = game;
            _doc.Build = build;
        }
    }

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    public bool TryGetEntry(string packagePath, out FMDexEntry entry)
    {
        EnsureLoaded();
        lock (_gate)
        {
            if (!TryGetTagsUnlocked(packagePath, out var tags))
            {
                entry = null;
                return false;
            }

            entry = new FMDexEntry { Tags = tags };
            return true;
        }
    }

    /// <summary>
    /// Package path lookup, or a synthetic extension tag for non-packages
    /// (<c>.png</c> → <c>Png</c>, <c>.locres</c> → <c>Locres</c>, …) so Search does not show them as Unindexed.
    /// </summary>
    public bool TryGetEntry(GameFile file, out FMDexEntry entry)
    {
        if (file == null)
        {
            entry = null;
            return false;
        }

        if (TryGetEntry(file.Path, out entry))
            return true;

        if (!file.IsUePackage && TryGetExtensionDefaultTags(file, out var tags))
        {
            entry = new FMDexEntry { Tags = tags };
            return true;
        }

        entry = null;
        return false;
    }

    public bool IsIndexed(string packagePath)
    {
        EnsureLoaded();
        lock (_gate)
            return TryGetTagsUnlocked(packagePath, out _);
    }

    /// <summary>True if present in FMDex, or a non-package with an extension-derived default tag.</summary>
    public bool IsIndexed(GameFile file)
    {
        if (file == null) return false;
        if (IsIndexed(file.Path)) return true;
        return !file.IsUePackage && !string.IsNullOrEmpty(file.Extension);
    }

    /// <summary>
    /// Resolve tags by full package path, then basename (flatUnique stores unique names without folders).
    /// </summary>
    private bool TryGetTagsUnlocked(string packagePath, out List<string> tags)
    {
        var key = NormalizePath(packagePath);
        if (_flat.TryGetValue(key, out tags))
            return true;

        var baseName = FMDexFlatUnique.PackageFileName(key);
        if (!string.IsNullOrEmpty(baseName) &&
            !string.Equals(baseName, key, StringComparison.OrdinalIgnoreCase) &&
            _flat.TryGetValue(baseName, out tags))
            return true;

        tags = null;
        return false;
    }

    /// <summary>Synthetic class tag mirrored from the file extension (<c>png</c> → <c>Png</c>).</summary>
    public static string ExtensionClassTag(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return "File";

        var ext = extension.Trim().TrimStart('.');
        if (ext.Length == 0)
            return "File";

        return char.ToUpperInvariant(ext[0]) + ext[1..].ToLowerInvariant();
    }

    private static bool TryGetExtensionDefaultTags(GameFile file, out List<string> tags)
    {
        tags = null;
        if (file == null || string.IsNullOrEmpty(file.Extension))
            return false;

        tags = [ExtensionClassTag(file.Extension)];
        return true;
    }

    public bool HasTag(string packagePath, string tagFilter)
    {
        if (string.IsNullOrWhiteSpace(tagFilter)) return true;
        if (!TryGetEntry(packagePath, out var entry) || entry.Tags is not { Count: > 0 })
            return false;

        return FMDexTagAliases.Matches(entry.Tags, tagFilter);
    }

    /// <summary>Tag filter match for a package file (aliases + special cases like <c>map</c> → <c>.umap</c>).</summary>
    public bool MatchesTagFilter(GameFile file, string tagFilter)
    {
        if (string.IsNullOrWhiteSpace(tagFilter)) return true;
        if (FMDexTagAliases.IsMapFilter(tagFilter) && FMDexTagAliases.IsUMap(file))
            return true;

        if (!TryGetEntry(file, out var entry) || entry.Tags is not { Count: > 0 })
            return false;

        // Allow filtering non-packages by raw extension too (png / .png)
        if (!file.IsUePackage && !string.IsNullOrEmpty(file.Extension))
        {
            var f = tagFilter.Trim().TrimStart('.');
            if (file.Extension.Equals(f, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return FMDexTagAliases.Matches(entry.Tags, tagFilter);
    }

    public void Upsert(string packagePath, IEnumerable<string> tags)
    {
        EnsureLoaded();
        lock (_gate)
        {
            UpsertUnlocked(packagePath, tags);
        }
    }

    /// <summary>Merge many path→tags results under one lock (used by parallel indexing).</summary>
    public void UpsertBatch(IEnumerable<(string Path, List<string> Tags)> entries)
    {
        EnsureLoaded();
        lock (_gate)
        {
            foreach (var (path, tags) in entries)
                UpsertUnlocked(path, tags);
        }
    }

    private void UpsertUnlocked(string packagePath, IEnumerable<string> tags)
    {
        var key = NormalizePath(packagePath);
        var tagList = tags
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (tagList.Count == 0)
            return;

        // Prefer full path keys: absorb a basename-only entry from a prior flatUnique load.
        var baseName = FMDexFlatUnique.PackageFileName(key);
        if (!string.IsNullOrEmpty(baseName) &&
            !string.Equals(baseName, key, StringComparison.OrdinalIgnoreCase) &&
            _flat.TryGetValue(baseName, out var fromBase))
        {
            tagList = fromBase
                .Concat(tagList)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _flat.Remove(baseName);
        }

        if (_flat.TryGetValue(key, out var existing))
        {
            _flat[key] = existing
                .Concat(tagList)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        else
        {
            _flat[key] = tagList;
        }

        _dirty = true;
    }

    /// <summary>
    /// Cheap class-tag index from package export map (no export body deserialize).
    /// For IoStore packages, resolves script/local class names only — never loads <c>ImportedPackages</c>.
    /// </summary>
    public static List<string> CollectClassTags(IPackage pkg)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        switch (pkg)
        {
            case Package p:
                for (var i = 0; i < p.ExportMap.Length; i++)
                {
                    var className = p.ExportMap[i].ClassName;
                    if (!string.IsNullOrWhiteSpace(className))
                        tags.Add(className);
                }
                break;

            case IoPackage io:
                for (var i = 0; i < io.ExportMap.Length; i++)
                {
                    if (io.TryGetExportClassName(i, out var className) &&
                        !string.IsNullOrWhiteSpace(className))
                        tags.Add(className);
                }
                break;

            default:
                for (var i = 0; i < pkg.ExportMapLength; i++)
                {
                    // Fallback may load imports — avoid for IoPackage (handled above).
                    var className = pkg.ResolvePackageIndex(new FPackageIndex(pkg, i + 1))?.Class?.Name.Text;
                    if (!string.IsNullOrWhiteSpace(className))
                        tags.Add(className);
                }
                break;
        }

        return tags.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public void IndexPackage(GameFile entry, IPackage pkg, bool saveImmediately = true)
    {
        var tags = CollectClassTags(pkg);
        if (tags.Count == 0) return;
        Upsert(entry.Path, tags);
        if (saveImmediately)
            Save();
    }

    public static string NormalizePath(string path)
        => path.Replace('\\', '/').TrimStart('/');
}
