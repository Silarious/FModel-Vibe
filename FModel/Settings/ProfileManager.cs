using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CUE4Parse.UE4.Versions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FModel.Settings;

/// <summary>
/// Profiles are full snapshots of <see cref="UserSettings"/> saved under a name.
/// Per-game AES keys, UE version, and mapping endpoints live on
/// <see cref="DirectorySettings"/> inside <see cref="UserSettings.PerDirectory"/>.
/// <see cref="UserSettings.CurrentDir"/> is [JsonIgnore], so save/load must flush and rebind it.
/// </summary>
public static class ProfileManager
{
    public const string DefaultProfileName = "Default";

    public static readonly string ProfilesDirectory = Path.Combine(UserSettings.AppDataFolder, "Profiles");

    private static string GetProfilePath(string name) => Path.Combine(ProfilesDirectory, $"{SanitizeName(name)}.json");

    private static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Where(c => !invalid.Contains(c)).ToArray()).Trim();
    }

    public static IEnumerable<string> GetProfileNames()
    {
        Directory.CreateDirectory(ProfilesDirectory);
        return Directory.EnumerateFiles(ProfilesDirectory, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
    }

    public static bool Exists(string name) => File.Exists(GetProfilePath(name));

    /// <summary>
    /// Read mount settings for an ephemeral provider (diff tool) without touching live settings.
    /// </summary>
    public static bool TryReadMountConfig(string name, out ViewModels.ProfileDiff.ProfileMountConfig config)
    {
        config = null;
        var path = GetProfilePath(name);
        if (!File.Exists(path)) return false;

        try
        {
            var json = JObject.Parse(File.ReadAllText(path));
            var gameDir = json.Value<string>("GameDirectory") ?? "";
            var ueVersion = EGame.GAME_UE4_LATEST;
            if (json.TryGetValue("UeVersion", out var ueToken) && ueToken.Type == JTokenType.Integer)
                ueVersion = (EGame)(int)ueToken;

            string aesMain = "";
            var dynamicKeys = new List<ViewModels.ApiEndpoints.Models.DynamicKey>();
            string mappingPath = "";
            var mappingOverwrite = false;
            var texturePlatform = CUE4Parse.UE4.Assets.Exports.Texture.ETexturePlatform.DesktopMobile;

            if (!string.IsNullOrWhiteSpace(gameDir) &&
                json["PerDirectory"] is JObject perDir &&
                perDir[gameDir] is JObject dir)
            {
                if (dir.TryGetValue("UeVersion", out var dirUe) && dirUe.Type == JTokenType.Integer)
                    ueVersion = (EGame)(int)dirUe;

                if (dir.TryGetValue("TexturePlatform", out var plat) && plat.Type == JTokenType.Integer)
                    texturePlatform = (CUE4Parse.UE4.Assets.Exports.Texture.ETexturePlatform)(int)plat;

                aesMain = dir["AesKeys"]?["MainKey"]?.Value<string>()
                          ?? dir["AesKeys"]?["mainKey"]?.Value<string>()
                          ?? "";

                if (dir["AesKeys"]?["DynamicKeys"] is JArray dynArr ||
                    dir["AesKeys"]?["dynamicKeys"] is JArray)
                {
                    var arr = (dir["AesKeys"]?["DynamicKeys"] as JArray)
                              ?? (dir["AesKeys"]?["dynamicKeys"] as JArray);
                    if (arr != null)
                    {
                        foreach (var item in arr)
                        {
                            dynamicKeys.Add(new ViewModels.ApiEndpoints.Models.DynamicKey
                            {
                                Name = item.Value<string>("Name") ?? item.Value<string>("name") ?? "",
                                Guid = item.Value<string>("Guid") ?? item.Value<string>("guid") ?? "",
                                Key = item.Value<string>("Key") ?? item.Value<string>("key") ?? ""
                            });
                        }
                    }
                }

                if (dir["Endpoints"] is JArray endpoints && endpoints.Count > 1)
                {
                    mappingPath = endpoints[1]?.Value<string>("FilePath")
                                  ?? endpoints[1]?.Value<string>("filePath")
                                  ?? "";
                    mappingOverwrite = endpoints[1]?.Value<bool?>("Overwrite")
                                       ?? endpoints[1]?.Value<bool?>("overwrite")
                                       ?? !string.IsNullOrWhiteSpace(mappingPath);
                }
            }

            // Top-level UeVersion still wins when present.
            if (json.TryGetValue("UeVersion", out ueToken) && ueToken.Type == JTokenType.Integer)
                ueVersion = (EGame)(int)ueToken;

            config = new ViewModels.ProfileDiff.ProfileMountConfig
            {
                Name = name,
                GameDirectory = gameDir,
                UeVersion = ueVersion,
                TexturePlatform = texturePlatform,
                AesMainKey = aesMain,
                DynamicKeys = dynamicKeys,
                MappingFilePath = mappingPath,
                MappingOverwrite = mappingOverwrite
            };
            return !string.IsNullOrWhiteSpace(gameDir);
        }
        catch
        {
            config = null;
            return false;
        }
    }

    /// <summary>
    /// Read profile fields for UI preview without touching live <see cref="UserSettings.Default"/>.
    /// </summary>
    public static bool TryReadPreview(string name, out ProfilePreview preview)
    {
        preview = new ProfilePreview { Name = name ?? "" };
        var path = GetProfilePath(name);
        if (!File.Exists(path)) return false;

        try
        {
            var json = JObject.Parse(File.ReadAllText(path));
            preview.GameDirectory = json.Value<string>("GameDirectory") ?? "";
            preview.RawDataDirectory = json.Value<string>("RawDataDirectory") ?? "";
            preview.PropertiesDirectory = json.Value<string>("PropertiesDirectory") ?? "";
            preview.TextureDirectory = json.Value<string>("TextureDirectory") ?? "";
            preview.AudioDirectory = json.Value<string>("AudioDirectory") ?? "";
            preview.ModelDirectory = json.Value<string>("ModelDirectory") ?? "";
            preview.CodeDirectory = json.Value<string>("CodeDirectory") ?? "";

            if (json.TryGetValue("UeVersion", out var ueToken) && ueToken.Type == JTokenType.Integer)
                preview.UeVersion = (EGame)(int)ueToken;

            var gameDir = preview.GameDirectory;
            if (!string.IsNullOrWhiteSpace(gameDir) && json["PerDirectory"] is JObject perDir)
            {
                JObject dir = null;
                if (perDir[gameDir] is JObject exact)
                    dir = exact;
                else
                {
                    var normalized = NormalizeDirKey(gameDir);
                    foreach (var prop in perDir.Properties())
                    {
                        if (string.Equals(prop.Name, gameDir, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(NormalizeDirKey(prop.Name), normalized, StringComparison.OrdinalIgnoreCase))
                        {
                            dir = prop.Value as JObject;
                            break;
                        }
                    }
                }

                if (dir != null)
                {
                    if (dir.TryGetValue("UeVersion", out var dirUe) && dirUe.Type == JTokenType.Integer)
                        preview.UeVersion = (EGame)(int)dirUe;

                    preview.AesMainKey = dir["AesKeys"]?["MainKey"]?.Value<string>()
                                         ?? dir["AesKeys"]?["mainKey"]?.Value<string>()
                                         ?? "";

                    if (dir["Endpoints"] is JArray endpoints && endpoints.Count > 1)
                        preview.MappingFilePath = endpoints[1]?["FilePath"]?.Value<string>() ?? "";
                }
            }

            // Top-level UeVersion still wins when present (historical profile format).
            if (json.TryGetValue("UeVersion", out ueToken) && ueToken.Type == JTokenType.Integer)
                preview.UeVersion = (EGame)(int)ueToken;

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Write preview fields into a profile file without changing live <see cref="UserSettings.Default"/>.
    /// New profiles start from an isolated skeleton (not a clone of the currently loaded game).
    /// </summary>
    public static void SavePreviewAs(string name, ProfilePreview preview)
    {
        if (string.IsNullOrWhiteSpace(name) || preview == null) return;

        Directory.CreateDirectory(ProfilesDirectory);
        var path = GetProfilePath(name);
        JObject json;
        if (File.Exists(path))
            json = JObject.Parse(File.ReadAllText(path));
        else
            json = CreateIsolatedProfileSkeleton();

        var gameDir = NormalizeDirKey(preview.GameDirectory ?? "");
        json["GameDirectory"] = gameDir;
        json["RawDataDirectory"] = preview.RawDataDirectory ?? "";
        json["PropertiesDirectory"] = preview.PropertiesDirectory ?? "";
        json["TextureDirectory"] = preview.TextureDirectory ?? "";
        json["AudioDirectory"] = preview.AudioDirectory ?? "";
        json["ModelDirectory"] = preview.ModelDirectory ?? "";
        json["CodeDirectory"] = preview.CodeDirectory ?? "";
        json["UeVersion"] = (int)preview.UeVersion;

        if (json["PerDirectory"] is not JObject perDir)
        {
            perDir = new JObject();
            json["PerDirectory"] = perDir;
        }

        // Drop duplicate keys for the same path, then write under normalized key.
        foreach (var prop in perDir.Properties()
                     .Where(p => string.Equals(NormalizeDirKey(p.Name), gameDir, StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            if (!string.Equals(prop.Name, gameDir, StringComparison.Ordinal))
                prop.Remove();
        }

        var dir = perDir[gameDir] as JObject ?? new JObject();
        dir["GameDirectory"] = gameDir;
        dir["GameName"] = name;
        dir["UeVersion"] = (int)preview.UeVersion;

        var aes = dir["AesKeys"] as JObject ?? new JObject();
        aes["MainKey"] = preview.AesMainKey ?? "";
        dir["AesKeys"] = aes;

        // Keep game-correct API endpoints when seeding empty; don't leave another game's URLs.
        var defaults = EndpointSettings.Default(name);
        var endpoints = dir["Endpoints"] as JArray;
        if (endpoints == null || endpoints.Count < 2)
        {
            endpoints =
            [
                new JObject
                {
                    ["Url"] = defaults[0].Url ?? "",
                    ["Path"] = defaults[0].Path ?? "",
                    ["Overwrite"] = false,
                    ["FilePath"] = ""
                },
                new JObject
                {
                    ["Url"] = defaults[1].Url ?? "",
                    ["Path"] = defaults[1].Path ?? "",
                    ["Overwrite"] = !string.IsNullOrWhiteSpace(preview.MappingFilePath),
                    ["FilePath"] = preview.MappingFilePath ?? ""
                }
            ];
        }
        else
        {
            var mapEp = endpoints[1] as JObject ?? new JObject();
            mapEp["FilePath"] = preview.MappingFilePath ?? "";
            mapEp["Overwrite"] = !string.IsNullOrWhiteSpace(preview.MappingFilePath);
            endpoints[1] = mapEp;
        }

        dir["Endpoints"] = endpoints;
        perDir[gameDir] = dir;

        File.WriteAllText(path, json.ToString(Formatting.Indented));
    }

    /// <summary>
    /// Minimal profile JSON that does <b>not</b> clone live <see cref="UserSettings.Default"/>
    /// (avoids copying another game's PerDirectory / AES / endpoints).
    /// </summary>
    private static JObject CreateIsolatedProfileSkeleton()
    {
        var output = UserSettings.Default.OutputDirectory ?? "";
        var exports = string.IsNullOrWhiteSpace(output) ? "" : Path.Combine(output, "Exports");
        return new JObject
        {
            ["OutputDirectory"] = output,
            ["RawDataDirectory"] = exports,
            ["PropertiesDirectory"] = exports,
            ["TextureDirectory"] = exports,
            ["AudioDirectory"] = exports,
            ["ModelDirectory"] = exports,
            ["CodeDirectory"] = exports,
            ["GameDirectory"] = "",
            ["CurrentProfileName"] = "",
            ["PerDirectory"] = new JObject()
        };
    }

    /// <summary>
    /// Write a self-contained profile that only contains <paramref name="dir"/> (no other games).
    /// LIVE defaults always get a dedicated <c>{Output}/Exports/{Profile}</c> folder — never another profile's paths.
    /// </summary>
    public static void WriteIsolatedProfile(string name, DirectorySettings dir)
    {
        if (string.IsNullOrWhiteSpace(name) || dir == null) return;

        Directory.CreateDirectory(ProfilesDirectory);
        var path = GetProfilePath(name);

        var json = CreateIsolatedProfileSkeleton();
        var exportDir = DefaultExportDirectory(name);
        var isLiveDefault = string.Equals(name, "Fortnite", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(name, "VALORANT", StringComparison.OrdinalIgnoreCase);

        if (!isLiveDefault && File.Exists(path) && TryReadPreview(name, out var existing) &&
            !IsExportPathPolluted(name, existing.RawDataDirectory))
        {
            // Keep user export paths for non-LIVE profiles when they look clean.
            if (!string.IsNullOrWhiteSpace(existing.RawDataDirectory))
                json["RawDataDirectory"] = existing.RawDataDirectory;
            if (!string.IsNullOrWhiteSpace(existing.PropertiesDirectory))
                json["PropertiesDirectory"] = existing.PropertiesDirectory;
            if (!string.IsNullOrWhiteSpace(existing.TextureDirectory))
                json["TextureDirectory"] = existing.TextureDirectory;
            if (!string.IsNullOrWhiteSpace(existing.AudioDirectory))
                json["AudioDirectory"] = existing.AudioDirectory;
            if (!string.IsNullOrWhiteSpace(existing.ModelDirectory))
                json["ModelDirectory"] = existing.ModelDirectory;
            if (!string.IsNullOrWhiteSpace(existing.CodeDirectory))
                json["CodeDirectory"] = existing.CodeDirectory;
        }
        else
        {
            json["RawDataDirectory"] = exportDir;
            json["PropertiesDirectory"] = exportDir;
            json["TextureDirectory"] = exportDir;
            json["AudioDirectory"] = exportDir;
            json["ModelDirectory"] = exportDir;
            json["CodeDirectory"] = exportDir;
        }

        var gameDir = CanonicalizeGameDirectory(dir.GameDirectory ?? "");
        json["GameDirectory"] = gameDir;
        json["UeVersion"] = (int)dir.UeVersion;
        json["CurrentProfileName"] = name;

        // Ensure API endpoints are valid for mappings/AES pull after restore.
        foreach (var ep in dir.Endpoints ?? [])
            ep.EnsureConfiguredValidity();

        var dirObj = JObject.FromObject(dir);
        dirObj["GameDirectory"] = gameDir;
        dirObj["GameName"] = dir.GameName ?? name;
        json["PerDirectory"] = new JObject { [gameDir] = dirObj };

        File.WriteAllText(path, json.ToString(Formatting.Indented));
        try { Directory.CreateDirectory(exportDir); }
        catch { /* ignore */ }
    }

    /// <summary>Per-profile export root: <c>{OutputDirectory}/Exports/{ProfileName}</c>.</summary>
    public static string DefaultExportDirectory(string profileName)
    {
        var output = UserSettings.Default.OutputDirectory;
        if (string.IsNullOrWhiteSpace(output))
            output = Path.Combine(AppContext.BaseDirectory, "Output");
        var safe = string.Join("_", (profileName ?? "Game").Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(output, "Exports", safe);
    }

    private static bool IsExportPathPolluted(string profileName, string exportPath)
    {
        if (string.IsNullOrWhiteSpace(exportPath)) return true;
        var expected = DefaultExportDirectory(profileName);
        if (string.Equals(
                exportPath.TrimEnd('\\', '/'),
                expected.TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase))
            return false;

        // Another profile's name in the path (and not ours) ⇒ inherited wrongly.
        foreach (var other in GetProfileNames())
        {
            if (string.Equals(other, profileName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (exportPath.Contains(other, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool IsProfilePolluted(string name, DirectorySettings expected)
    {
        var path = GetProfilePath(name);
        if (!File.Exists(path) || expected == null) return true;

        try
        {
            var json = JObject.Parse(File.ReadAllText(path));
            var gameDir = json.Value<string>("GameDirectory") ?? "";
            var expectedKey = NormalizeDirKey(expected.GameDirectory);

            // LIVE profiles must store the opaque trigger token, never an absolute expansion.
            if (TryCanonicalizeLiveTrigger(expected.GameDirectory, out var liveExpected) &&
                !string.Equals(gameDir.Trim(), liveExpected, StringComparison.OrdinalIgnoreCase))
                return true;

            if (!string.Equals(NormalizeDirKey(gameDir), expectedKey, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(gameDir, expected.GameDirectory, StringComparison.OrdinalIgnoreCase))
                return true;

            if (json["PerDirectory"] is not JObject perDir)
                return true;

            if (perDir.Count != 1)
                return true;

            var only = perDir.Properties().First();
            if (!string.Equals(NormalizeDirKey(only.Name), expectedKey, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(only.Name, expected.GameDirectory, StringComparison.OrdinalIgnoreCase))
                return true;

            if (IsExportPathPolluted(name, json.Value<string>("RawDataDirectory")))
                return true;

            // LIVE / known games must keep their AES/mapping API endpoints, not another game's.
            if (only.Value is JObject dirObj &&
                dirObj["Endpoints"] is JArray endpoints &&
                endpoints.Count > 0)
            {
                var defaults = EndpointSettings.Default(name);
                var expectedUrl = defaults[0].Url ?? "";
                if (!string.IsNullOrEmpty(expectedUrl))
                {
                    var actualUrl = endpoints[0]?.Value<string>("Url")
                                    ?? endpoints[0]?.Value<string>("url")
                                    ?? "";
                    if (!string.Equals(actualUrl, expectedUrl, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            return false;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Flush live <see cref="UserSettings.CurrentDir"/> into <see cref="UserSettings.PerDirectory"/>,
    /// then serialize the full settings snapshot (AES, mappings, UE version, export paths, …).
    /// </summary>
    public static void SaveCurrentAs(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;

        Directory.CreateDirectory(ProfilesDirectory);
        SyncCurrentDirToGameDirectory();

        // Save only the active game's PerDirectory entry so profiles stay isolated.
        var json = JObject.FromObject(UserSettings.Default);
        json["UeVersion"] = (int)(UserSettings.Default.CurrentDir?.UeVersion ?? EGame.GAME_UE4_LATEST);

        var activeDir = UserSettings.Default.GameDirectory;
        if (!string.IsNullOrWhiteSpace(activeDir) && json["PerDirectory"] is JObject perDir)
        {
            var key = NormalizeDirKey(activeDir);
            JToken keep = null;
            foreach (var prop in perDir.Properties().ToList())
            {
                if (string.Equals(NormalizeDirKey(prop.Name), key, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(prop.Name, activeDir, StringComparison.OrdinalIgnoreCase))
                {
                    keep = prop.Value;
                }
            }

            var isolated = new JObject();
            if (keep != null)
                isolated[key] = keep;
            else if (UserSettings.Default.CurrentDir != null)
                isolated[key] = JObject.FromObject(UserSettings.Default.CurrentDir);
            json["PerDirectory"] = isolated;
            json["GameDirectory"] = key;
        }

        File.WriteAllText(GetProfilePath(name), json.ToString(Formatting.Indented));
        UserSettings.Default.CurrentProfileName = name;
    }

    /// <summary>
    /// Build <see cref="DirectorySettings"/> from a saved profile for the Profile Selector UI
    /// without mutating live <see cref="UserSettings.Default"/>.
    /// </summary>
    public static bool TryBuildDirectoryFromProfile(string name, out DirectorySettings dir)
    {
        dir = null;
        if (string.IsNullOrWhiteSpace(name) || !Exists(name)) return false;

        try
        {
            var json = JObject.Parse(File.ReadAllText(GetProfilePath(name)));
            var gameDir = CanonicalizeGameDirectory(json.Value<string>("GameDirectory") ?? "");
            EGame ue = EGame.GAME_UE4_LATEST;
            if (json.TryGetValue("UeVersion", out var ueToken) && ueToken.Type == JTokenType.Integer)
                ue = (EGame)(int)ueToken;

            DirectorySettings built = null;
            if (json["PerDirectory"] is JObject perDir)
            {
                JObject dirObj = null;
                if (!string.IsNullOrWhiteSpace(gameDir) && perDir[gameDir] is JObject exact)
                    dirObj = exact;
                else
                {
                    var normalized = NormalizeDirKey(gameDir);
                    foreach (var prop in perDir.Properties())
                    {
                        if (string.Equals(prop.Name, gameDir, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(NormalizeDirKey(prop.Name), normalized, StringComparison.OrdinalIgnoreCase) ||
                            (TryCanonicalizeLiveTrigger(prop.Name, out var liveKey) &&
                             string.Equals(liveKey, gameDir, StringComparison.OrdinalIgnoreCase)))
                        {
                            dirObj = prop.Value as JObject;
                            break;
                        }
                    }

                    dirObj ??= perDir.Properties().FirstOrDefault()?.Value as JObject;
                }

                if (dirObj != null)
                    built = dirObj.ToObject<DirectorySettings>();
            }

            built ??= DirectorySettings.Fresh(name, gameDir, ue);
            built.GameName = name;
            built.GameDirectory = string.IsNullOrWhiteSpace(gameDir) ? (built.GameDirectory ?? "") : gameDir;
            if (json.TryGetValue("UeVersion", out ueToken) && ueToken.Type == JTokenType.Integer)
                built.UeVersion = (EGame)(int)ueToken;

            // LIVE defaults stay protected; everything else is removable from the selector.
            var isLiveDefault = string.Equals(name, "Fortnite", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(name, "VALORANT", StringComparison.OrdinalIgnoreCase);
            built.IsManual = !isLiveDefault;

            foreach (var ep in built.Endpoints ?? [])
                ep.EnsureConfiguredValidity();

            dir = built;
            return true;
        }
        catch
        {
            dir = null;
            return false;
        }
    }

    /// <summary>
    /// Seed default <c>Fortnite</c> / <c>VALORANT</c> profiles that point at the LIVE stream triggers
    /// (not local installs). Repairs profiles that inherited another game's settings.
    /// Also seeds <c>VALORANT (Installed)</c> when a local Riot install is found — LIVE streaming
    /// depends on valorant-api.com/fmodel which is currently returning 404.
    /// </summary>
    public static void EnsureLiveDefaults()
    {
        EnsureFromDirectory(
            DirectorySettings.Fresh("Fortnite", FModel.Constants._FN_LIVE_TRIGGER, EGame.GAME_UE5_8),
            "Fortnite");
        EnsureFromDirectory(
            DirectorySettings.Fresh("VALORANT", FModel.Constants._VAL_LIVE_TRIGGER, EGame.GAME_Valorant),
            "VALORANT");

        TryEnsureLocalValorantInstalledProfile();

        // If a LIVE profile is currently active, sync repaired export folders into live settings.
        ApplyProfileExportDirsToLiveIfActive("Fortnite");
        ApplyProfileExportDirsToLiveIfActive("VALORANT");
    }

    private static void TryEnsureLocalValorantInstalledProfile()
    {
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                var launcher = Path.Combine(drive.Name, "ProgramData", "Riot Games", "RiotClientInstalls.json");
                if (!File.Exists(launcher)) continue;

                var json = JObject.Parse(File.ReadAllText(launcher));
                if (json["associated_client"] is not JObject clients) continue;

                foreach (var prop in clients.Properties())
                {
                    var key = (prop.Name ?? "").Replace('/', '\\');
                    if (!key.Contains("VALORANT", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var gameDir = Path.Combine(key.TrimEnd('\\'), "ShooterGame", "Content", "Paks");
                    if (!Directory.Exists(gameDir)) continue;

                    EnsureFromDirectory(
                        DirectorySettings.Fresh("VALORANT (Installed)", gameDir, EGame.GAME_Valorant, manual: true),
                        "VALORANT (Installed)");
                    return;
                }
            }
        }
        catch
        {
            // optional convenience profile — ignore detect failures
        }
    }

    private static void ApplyProfileExportDirsToLiveIfActive(string name)
    {
        if (!string.Equals(UserSettings.Default.CurrentProfileName, name, StringComparison.OrdinalIgnoreCase))
            return;
        if (!TryReadPreview(name, out var preview))
            return;

        UserSettings.Default.RawDataDirectory = preview.RawDataDirectory ?? "";
        UserSettings.Default.PropertiesDirectory = preview.PropertiesDirectory ?? "";
        UserSettings.Default.TextureDirectory = preview.TextureDirectory ?? "";
        UserSettings.Default.AudioDirectory = preview.AudioDirectory ?? "";
        UserSettings.Default.ModelDirectory = preview.ModelDirectory ?? "";
        UserSettings.Default.CodeDirectory = preview.CodeDirectory ?? "";
    }

    /// <summary>
    /// Create a named profile snapshot from a detected / manual directory if one does not exist yet.
    /// LIVE entries normalize to <c>Fortnite</c> / <c>VALORANT</c> profile names.
    /// Polluted profiles (cloned from another game) are rewritten in isolation.
    /// </summary>
    public static string EnsureFromDirectory(DirectorySettings dir, string profileName = null)
    {
        if (dir == null || string.IsNullOrWhiteSpace(dir.GameDirectory))
            return null;

        var name = NormalizeProfileName(profileName ?? dir.GameName);
        if (string.IsNullOrWhiteSpace(name))
            return null;

        // Prefer a fresh copy for well-known LIVE defaults so endpoints/AES never come from another game.
        // Always pin Archive to the opaque LIVE trigger — never trust a UI/path edit that would create
        // DefaultFileProvider("…\fortnite-live.manifest") and load zero paks.
        DirectorySettings clean = dir;
        if (string.Equals(name, "Fortnite", StringComparison.OrdinalIgnoreCase))
        {
            clean = DirectorySettings.Fresh(
                "Fortnite",
                FModel.Constants._FN_LIVE_TRIGGER,
                EGame.GAME_UE5_8,
                manual: false,
                aes: dir.AesKeys?.MainKey ?? "");
        }
        else if (string.Equals(name, "VALORANT", StringComparison.OrdinalIgnoreCase))
        {
            clean = DirectorySettings.Fresh(
                "VALORANT",
                FModel.Constants._VAL_LIVE_TRIGGER,
                EGame.GAME_Valorant,
                manual: false,
                aes: dir.AesKeys?.MainKey ?? "");
        }

        // Existing healthy profile: do NOT push empty Fresh AES into live PerDirectory
        // (EnsureLiveDefaults used to wipe keys every launch → mounts hang / never finish).
        if (Exists(name) && !IsProfilePolluted(name, clean))
            return name;

        SetPerDirectory(clean.GameDirectory, clean);
        WriteIsolatedProfile(name, clean);
        return name;
    }

    /// <summary>Stable profile name for a directory entry (LIVE → Fortnite / VALORANT).</summary>
    public static string NormalizeProfileName(string gameName)
    {
        if (string.IsNullOrWhiteSpace(gameName)) return gameName;

        var n = gameName.Trim();
        if (n.Equals("Fortnite [LIVE]", StringComparison.OrdinalIgnoreCase) ||
            n.Equals("Fortnite", StringComparison.OrdinalIgnoreCase))
            return "Fortnite";

        if (n.Equals("VALORANT [LIVE]", StringComparison.OrdinalIgnoreCase) ||
            n.Equals("Valorant [LIVE]", StringComparison.OrdinalIgnoreCase) ||
            n.Equals("VALORANT", StringComparison.OrdinalIgnoreCase) ||
            n.Equals("Valorant", StringComparison.OrdinalIgnoreCase))
            return "VALORANT";

        // Installed variants keep the suffix so they don't collide with LIVE defaults.
        return n;
    }

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

    /// <summary>
    /// Load a profile onto the live <see cref="UserSettings.Default"/> and rebind
    /// <see cref="UserSettings.CurrentDir"/> from <see cref="UserSettings.PerDirectory"/>
    /// so AES / UE version / mapping endpoints match the profile.
    /// Caller should restart FModel so the provider picks up the restored settings.
    /// </summary>
    public static bool Load(string name)
    {
        var path = GetProfilePath(name);
        if (!File.Exists(path)) return false;

        // Flush outgoing profile under its own pak-folder key only.
        // Do NOT migrate to the top-level GameDirectory here — that path may already have been
        // edited in the UI for a different profile and would corrupt the switch.
        FlushCurrentDirAtOwnKey();

        var text = File.ReadAllText(path);
        var json = JObject.Parse(text);
        JsonConvert.PopulateObject(text, UserSettings.Default);

        RebindCurrentDirFromPerDirectory(json);

        // Profile JSON often leaves IsValid=false even when Url/Path are set — required for InitMappings.
        foreach (var ep in UserSettings.Default.CurrentDir?.Endpoints ?? [])
            ep.EnsureConfiguredValidity();

        UserSettings.Default.CurrentProfileName = name;
        // Keep AppSettings.json in sync so restart / exit don't clobber the loaded profile.
        UserSettings.Save();
        return true;
    }

    public static void Delete(string name)
    {
        var path = GetProfilePath(name);
        if (File.Exists(path)) File.Delete(path);
    }

    public static bool Rename(string oldName, string newName)
    {
        var oldPath = GetProfilePath(oldName);
        var newPath = GetProfilePath(newName);
        if (!File.Exists(oldPath) || File.Exists(newPath) || string.IsNullOrWhiteSpace(newName)) return false;

        File.Move(oldPath, newPath);
        if (UserSettings.Default.CurrentProfileName == oldName)
            UserSettings.Default.CurrentProfileName = newName;
        return true;
    }

    /// <summary>
    /// Normalize a pak-folder path for use as a <see cref="UserSettings.PerDirectory"/> key.
    /// LIVE stream triggers (<c>fortnite-live.manifest</c> / <c>valorant-live.manifest</c>)
    /// are kept as opaque tokens — never resolved with <see cref="Path.GetFullPath"/>.
    /// </summary>
    public static string NormalizeDirKey(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path ?? "";
        var trimmed = path.Trim().TrimEnd('\\', '/');

        if (TryCanonicalizeLiveTrigger(trimmed, out var live))
            return live;

        try
        {
            return Path.GetFullPath(trimmed);
        }
        catch
        {
            return trimmed;
        }
    }

    /// <summary>
    /// True when <paramref name="path"/> is (or ends with) a LIVE stream trigger token.
    /// </summary>
    public static bool IsLiveTrigger(string path)
        => TryCanonicalizeLiveTrigger(path, out _);

    /// <summary>
    /// Map a LIVE trigger — or a wrongly expanded absolute path ending in one — back to the
    /// canonical token used by <see cref="ViewModels.CUE4ParseViewModel"/> provider selection.
    /// </summary>
    public static bool TryCanonicalizeLiveTrigger(string path, out string trigger)
    {
        trigger = null;
        if (string.IsNullOrWhiteSpace(path)) return false;

        var trimmed = path.Trim().TrimEnd('\\', '/');
        if (trimmed.Equals(FModel.Constants._FN_LIVE_TRIGGER, StringComparison.OrdinalIgnoreCase))
        {
            trigger = FModel.Constants._FN_LIVE_TRIGGER;
            return true;
        }

        if (trimmed.Equals(FModel.Constants._VAL_LIVE_TRIGGER, StringComparison.OrdinalIgnoreCase))
        {
            trigger = FModel.Constants._VAL_LIVE_TRIGGER;
            return true;
        }

        // Polluted profiles stored Path.GetFullPath("valorant-live.manifest") →
        // C:\...\publish\valorant-live.manifest
        var fileName = Path.GetFileName(trimmed);
        if (fileName.Equals(FModel.Constants._FN_LIVE_TRIGGER, StringComparison.OrdinalIgnoreCase))
        {
            trigger = FModel.Constants._FN_LIVE_TRIGGER;
            return true;
        }

        if (fileName.Equals(FModel.Constants._VAL_LIVE_TRIGGER, StringComparison.OrdinalIgnoreCase))
        {
            trigger = FModel.Constants._VAL_LIVE_TRIGGER;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Normalize game directory for provider use: LIVE triggers stay tokens; real paths get FullPath.
    /// </summary>
    public static string CanonicalizeGameDirectory(string path)
    {
        if (TryCanonicalizeLiveTrigger(path, out var live))
            return live;
        return NormalizeDirKey(path);
    }

    /// <summary>
    /// Case-insensitive / normalized lookup in <see cref="UserSettings.PerDirectory"/>.
    /// </summary>
    public static bool TryGetPerDirectory(string gameDirectory, out DirectorySettings dir)
    {
        dir = null;
        var map = UserSettings.Default.PerDirectory;
        if (map == null || string.IsNullOrWhiteSpace(gameDirectory))
            return false;

        if (map.TryGetValue(gameDirectory, out dir) && dir != null)
            return true;

        var normalized = NormalizeDirKey(gameDirectory);
        if (!string.Equals(normalized, gameDirectory, StringComparison.Ordinal) &&
            map.TryGetValue(normalized, out dir) && dir != null)
            return true;

        foreach (var (key, value) in map)
        {
            if (value == null) continue;
            if (string.Equals(key, gameDirectory, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(NormalizeDirKey(key), normalized, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(NormalizeDirKey(value.GameDirectory), normalized, StringComparison.OrdinalIgnoreCase))
            {
                dir = value;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Store <paramref name="dir"/> under a normalized key and drop duplicate keys that refer to the same path.
    /// </summary>
    public static void SetPerDirectory(string gameDirectory, DirectorySettings dir)
    {
        if (dir == null || string.IsNullOrWhiteSpace(gameDirectory)) return;

        var map = UserSettings.Default.PerDirectory;
        var key = NormalizeDirKey(gameDirectory);
        dir.GameDirectory = key;

        foreach (var existing in map.Keys.Where(k =>
                     string.Equals(NormalizeDirKey(k), key, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            if (!string.Equals(existing, key, StringComparison.Ordinal))
                map.Remove(existing);
        }

        map[key] = dir;
    }

    /// <summary>
    /// Flush CurrentDir under its own <see cref="DirectorySettings.GameDirectory"/> key (no migration).
    /// </summary>
    public static void FlushCurrentDirAtOwnKey()
    {
        var current = UserSettings.Default.CurrentDir;
        if (current == null || string.IsNullOrWhiteSpace(current.GameDirectory))
            return;

        SetPerDirectory(current.GameDirectory, current);
    }

    /// <summary>
    /// AES / UE version / mappings live on <see cref="DirectorySettings"/> keyed by pak folder.
    /// When the top-level <see cref="UserSettings.GameDirectory"/> changes, migrate
    /// <see cref="UserSettings.CurrentDir"/> onto that key so a restart still finds them.
    /// </summary>
    public static void SyncCurrentDirToGameDirectory()
    {
        var current = UserSettings.Default.CurrentDir;
        if (current == null) return;

        var targetDir = UserSettings.Default.GameDirectory;
        if (string.IsNullOrWhiteSpace(targetDir))
            targetDir = current.GameDirectory;
        if (string.IsNullOrWhiteSpace(targetDir))
            return;

        targetDir = NormalizeDirKey(targetDir);
        UserSettings.Default.GameDirectory = targetDir;

        var oldDir = current.GameDirectory;
        if (!string.IsNullOrWhiteSpace(oldDir) &&
            !string.Equals(NormalizeDirKey(oldDir), targetDir, StringComparison.OrdinalIgnoreCase))
        {
            // Remove any keys that pointed at the previous pak folder.
            var oldNorm = NormalizeDirKey(oldDir);
            foreach (var existing in UserSettings.Default.PerDirectory.Keys
                         .Where(k => string.Equals(NormalizeDirKey(k), oldNorm, StringComparison.OrdinalIgnoreCase))
                         .ToList())
            {
                UserSettings.Default.PerDirectory.Remove(existing);
            }
        }

        SetPerDirectory(targetDir, current);
    }

    private static void RebindCurrentDirFromPerDirectory(JObject profileJson)
    {
        var gameDir = UserSettings.Default.GameDirectory;
        if (string.IsNullOrWhiteSpace(gameDir))
            return;

        gameDir = CanonicalizeGameDirectory(gameDir);
        UserSettings.Default.GameDirectory = gameDir;

        if (!TryGetPerDirectory(gameDir, out var dir) || dir == null)
        {
            // Profile may only have top-level GameDirectory; keep existing CurrentDir if any.
            dir = UserSettings.Default.CurrentDir;
            if (dir == null)
                return;
        }

        dir.GameDirectory = gameDir;
        SetPerDirectory(gameDir, dir);

        // Explicit top-level UeVersion wins (same as historical profile format).
        if (profileJson.TryGetValue("UeVersion", out var ueToken) && ueToken.Type == JTokenType.Integer)
            dir.UeVersion = (EGame)(int)ueToken;

        UserSettings.Default.CurrentDir = dir;
    }
}
