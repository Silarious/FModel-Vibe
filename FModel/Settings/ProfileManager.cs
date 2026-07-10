using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CUE4Parse.UE4.Versions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FModel.Settings;

/// <summary>
/// Profiles are full snapshots of <see cref="UserSettings"/> (every toggle, every per-file-type
/// export folder, endpoints, hotkeys, ...) saved under a name so the user can quickly switch between
/// different setups (e.g. "Fortnite Cosmetics Only", "Valorant Audio", ...).
/// Each profile is stored as its own json file so switching/deleting one never risks corrupting
/// the currently active settings file.
///
/// UeVersion and the pak archive directory deserve a note: the archive directory (GameDirectory) is
/// a plain top-level UserSettings property so it's captured automatically. UeVersion however lives on
/// UserSettings.CurrentDir, which is [JsonIgnore] (it's keyed per-game, not global), so it has to be
/// captured/restored explicitly alongside the rest of the snapshot.
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
    /// serializes the current UserSettings.Default state (every per-file-type export directory
    /// included, since none of them are [JsonIgnore]) plus the current UE Version, and saves/overwrites
    /// it under the given name. This always creates/overwrites its own independent file, so saving
    /// under a brand new name never touches any other existing profile.
    /// </summary>
    public static void SaveCurrentAs(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;

        Directory.CreateDirectory(ProfilesDirectory);

        var json = JObject.FromObject(UserSettings.Default);
        json["UeVersion"] = (int) (UserSettings.Default.CurrentDir?.UeVersion ?? EGame.GAME_UE4_LATEST);

        File.WriteAllText(GetProfilePath(name), json.ToString(Formatting.Indented));
        UserSettings.Default.CurrentProfileName = name;
    }

    /// <summary>
    /// loads a saved profile onto the current, live UserSettings.Default instance (in place, via
    /// PopulateObject) so every existing binding in the UI keeps working without a restart
    /// </summary>
    public static bool Load(string name)
    {
        var path = GetProfilePath(name);
        if (!File.Exists(path)) return false;

        var text = File.ReadAllText(path);
        JsonConvert.PopulateObject(text, UserSettings.Default);

        if (UserSettings.Default.CurrentDir != null)
        {
            var json = JObject.Parse(text);
            if (json.TryGetValue("UeVersion", out var ueToken) && ueToken.Type == JTokenType.Integer)
                UserSettings.Default.CurrentDir.UeVersion = (EGame) (int) ueToken;
        }

        UserSettings.Default.CurrentProfileName = name;
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
}
