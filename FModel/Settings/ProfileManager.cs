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
    /// Merges into an existing profile snapshot when present; otherwise clones current settings first.
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
            json = JObject.FromObject(UserSettings.Default);

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
        dir["UeVersion"] = (int)preview.UeVersion;

        var aes = dir["AesKeys"] as JObject ?? new JObject();
        aes["MainKey"] = preview.AesMainKey ?? "";
        dir["AesKeys"] = aes;

        var endpoints = dir["Endpoints"] as JArray;
        if (endpoints == null || endpoints.Count < 2)
        {
            endpoints =
            [
                new JObject { ["Overwrite"] = false, ["FilePath"] = "" },
                new JObject
                {
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
    /// Flush live <see cref="UserSettings.CurrentDir"/> into <see cref="UserSettings.PerDirectory"/>,
    /// then serialize the full settings snapshot (AES, mappings, UE version, export paths, …).
    /// </summary>
    public static void SaveCurrentAs(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;

        Directory.CreateDirectory(ProfilesDirectory);
        SyncCurrentDirToGameDirectory();

        var json = JObject.FromObject(UserSettings.Default);
        json["UeVersion"] = (int)(UserSettings.Default.CurrentDir?.UeVersion ?? EGame.GAME_UE4_LATEST);

        File.WriteAllText(GetProfilePath(name), json.ToString(Formatting.Indented));
        UserSettings.Default.CurrentProfileName = name;
    }

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
    /// </summary>
    public static string NormalizeDirKey(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path ?? "";
        var trimmed = path.Trim().TrimEnd('\\', '/');
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

        gameDir = NormalizeDirKey(gameDir);
        UserSettings.Default.GameDirectory = gameDir;

        if (!TryGetPerDirectory(gameDir, out var dir) || dir == null)
        {
            // Profile may only have top-level GameDirectory; keep existing CurrentDir if any.
            dir = UserSettings.Default.CurrentDir;
            if (dir == null)
                return;
        }

        SetPerDirectory(gameDir, dir);

        // Explicit top-level UeVersion wins (same as historical profile format).
        if (profileJson.TryGetValue("UeVersion", out var ueToken) && ueToken.Type == JTokenType.Integer)
            dir.UeVersion = (EGame)(int)ueToken;

        UserSettings.Default.CurrentDir = dir;
    }
}
