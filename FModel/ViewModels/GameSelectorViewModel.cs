using FModel.Framework;
using Newtonsoft.Json;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CUE4Parse.UE4.Objects.Core.Serialization;
using CUE4Parse.UE4.Versions;
using CUE4Parse.Utils;
using FModel.Settings;
using FModel.ViewModels.ApiEndpoints.Models;
using Microsoft.Win32;

namespace FModel.ViewModels;

public class GameSelectorViewModel : ViewModel
{
    public class DetectedGame
    {
        public string GameName { get; set; }
        public string GameDirectory { get; set; }
        public EGame OverridedGame { get; set; }
        public bool IsManual { get; set; }

        // the followings are only used when game is manually added
        public AesResponse AesKeys { get; set; }
        public List<FCustomVersion> OverridedCustomVersions { get; set; }
        public Dictionary<string, bool> OverridedOptions { get; set; }
        public Dictionary<string, KeyValuePair<string, string>> OverridedMapStructTypes { get; set; }
        public IList<CustomDirectory> CustomDirectories { get; set; }
    }

    private DirectorySettings _selectedDirectory;
    public DirectorySettings SelectedDirectory
    {
        get => _selectedDirectory;
        set => SetProperty(ref _selectedDirectory, value);
    }

    private readonly ObservableCollection<DirectorySettings> _detectedDirectories;
    public ReadOnlyObservableCollection<DirectorySettings> DetectedDirectories { get; }
    public ReadOnlyObservableCollection<EGame> UeGames { get; }

    public GameSelectorViewModel(string gameDirectory)
    {
        ProfileManager.EnsureLiveDefaults();

        _detectedDirectories = new ObservableCollection<DirectorySettings>();
        ReloadProfiles(gameDirectory);

        DetectedDirectories = new ReadOnlyObservableCollection<DirectorySettings>(_detectedDirectories);
        UeGames = new ReadOnlyObservableCollection<EGame>(new ObservableCollection<EGame>(EnumerateUeGames()));
    }

    /// <summary>Populate the selector from saved profiles only (not Epic/Steam auto-detect names).</summary>
    public void ReloadProfiles(string preferredGameDirectory = null)
    {
        _detectedDirectories.Clear();
        foreach (var name in ProfileManager.GetProfileNames())
        {
            if (ProfileManager.TryBuildDirectoryFromProfile(name, out var dir) && dir != null)
                _detectedDirectories.Add(dir);
        }

        var preferredProfile = UserSettings.Default.CurrentProfileName;
        if (!string.IsNullOrWhiteSpace(preferredProfile) &&
            _detectedDirectories.FirstOrDefault(d =>
                string.Equals(d.GameName, preferredProfile, StringComparison.OrdinalIgnoreCase)) is { } byProfile)
        {
            SelectedDirectory = byProfile;
        }
        else if (!string.IsNullOrEmpty(preferredGameDirectory) &&
                 _detectedDirectories.FirstOrDefault(x =>
                     string.Equals(x.GameDirectory, preferredGameDirectory, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(
                         ProfileManager.CanonicalizeGameDirectory(x.GameDirectory ?? ""),
                         ProfileManager.CanonicalizeGameDirectory(preferredGameDirectory),
                         StringComparison.OrdinalIgnoreCase)) is { } byDir)
        {
            SelectedDirectory = byDir;
        }
        else
        {
            SelectedDirectory =
                _detectedDirectories.FirstOrDefault(d =>
                    string.Equals(d.GameName, "Fortnite", StringComparison.OrdinalIgnoreCase))
                ?? _detectedDirectories.FirstOrDefault(d =>
                    string.Equals(d.GameName, "VALORANT", StringComparison.OrdinalIgnoreCase))
                ?? _detectedDirectories.FirstOrDefault();
        }
    }

    public void AddUndetectedDir(string gameDirectory) => CreateProfile(gameDirectory.SubstringAfterLast('\\'), gameDirectory);
    public void AddUndetectedDir(string gameName, string gameDirectory) => CreateProfile(gameName, gameDirectory);

    /// <summary>Add a manual game directory and create a matching named profile.</summary>
    public void CreateProfile(string gameName, string gameDirectory)
    {
        // Always canonicalize to …\Content\Paks when found; UE version may still fall back.
        TryResolveGameDirectory(gameDirectory, out var ueVersion, out var newGameDirectory);
        if (!string.IsNullOrEmpty(newGameDirectory))
            gameDirectory = newGameDirectory;

        var setting = DirectorySettings.Fresh(gameName, gameDirectory, ueVersion, manual: true);
        var profileName = ProfileManager.EnsureFromDirectory(setting);
        if (!string.IsNullOrWhiteSpace(profileName))
            setting.GameName = profileName;

        setting.IsManual = true;
        UserSettings.Default.PerDirectory[ProfileManager.CanonicalizeGameDirectory(gameDirectory)] = setting;

        // Refresh list so we don't duplicate if Ensure rewrote an existing name.
        ReloadProfiles(setting.GameDirectory);
        SelectedDirectory = DetectedDirectories.FirstOrDefault(d =>
            string.Equals(d.GameName, setting.GameName, StringComparison.OrdinalIgnoreCase)) ?? setting;
    }

    public void DeleteSelectedGame()
    {
        var name = ProfileManager.NormalizeProfileName(SelectedDirectory?.GameName);
        if (SelectedDirectory != null)
            UserSettings.Default.PerDirectory.Remove(SelectedDirectory.GameDirectory);

        if (!string.IsNullOrWhiteSpace(name) &&
            !string.Equals(name, "Fortnite", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(name, "VALORANT", StringComparison.OrdinalIgnoreCase))
        {
            if (ProfileManager.Exists(name))
                ProfileManager.Delete(name);
        }

        ReloadProfiles();
        SelectedDirectory = DetectedDirectories.LastOrDefault();
    }

    /// <summary>
    /// Canonicalize to a Paks folder when possible and sniff UE version from shipping/bootstrap exes.
    /// Always sets <paramref name="resolvedDirectory"/> (Paks path if found, else the input).
    /// Returns true when a UE version was detected from an exe / Arc Raiders path heuristic.
    /// </summary>
    public static bool TryResolveGameDirectory(
        string gameDirectory,
        out EGame ueVersion,
        [NotNull] out string resolvedDirectory)
    {
        var targetGameDir = gameDirectory ?? "";
        if (!string.IsNullOrEmpty(targetGameDir) &&
            !targetGameDir.EndsWith("Paks", StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(targetGameDir))
        {
            var dirs = Directory.GetDirectories(targetGameDir, "Paks", SearchOption.AllDirectories);
            var paksDir = dirs.Length == 1
                ? dirs[0]
                : dirs.FirstOrDefault(x => !x.EndsWith("Engine\\Programs\\CrashReportClient\\Content\\Paks"));
            if (!string.IsNullOrEmpty(paksDir))
            {
                Log.Warning("Selected directory \"{GameDirectory}\" does not end with \"Paks\". Looking in \"{PaksDir}\" instead.", targetGameDir, paksDir);
                targetGameDir = paksDir;
            }
        }

        // Arc Raiders before exe sniffing — shipping/bootstrap exes often report UE4 and mis-tag Tencent/CN.
        var projectDirEarly = Path.Combine(targetGameDir, "..", "..");
        if (LooksLikeArcRaiders(targetGameDir, projectDirEarly) || LooksLikeArcRaiders(gameDirectory ?? "", projectDirEarly))
        {
            resolvedDirectory = targetGameDir;
            ueVersion = EGame.GAME_ArcRaiders;
            Log.Information("Detected Arc Raiders from PioneerGame / path at \"{Dir}\"", targetGameDir);
            return true;
        }

        if (!string.IsNullOrEmpty(gameDirectory) &&
            !gameDirectory.EndsWith("Paks", StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(gameDirectory))
        {
            if (Directory.GetFiles(gameDirectory, "*.exe") is { Length: 1 } exe && TryGetUeVersionFromExe(exe[0], out ueVersion))
            {
                // we checked the exe in the original directory, the BootstrapPackagedGame one
                // but we still want c4p to use the paks folder as the game directory (if any), not the original one
                resolvedDirectory = targetGameDir;
                Log.Information("Detected UE version {UeVersion} from \"{Exe}\"", ueVersion, exe[0]);
                return true;
            }
        }

        // past this point, we assume targetGameDir is the correct Paks folder
        resolvedDirectory = targetGameDir;
        var projectDir = Path.Combine(targetGameDir, "..", "..");

        var projectBinariesDir = Path.Combine(projectDir, "Binaries", "Win64");
        if (Directory.Exists(projectBinariesDir))
        {
            if (Directory.GetFiles(projectBinariesDir, "*-Win64-Shipping.exe") is { Length: > 0 } shipping)
            {
                foreach (var exe in shipping)
                {
                    if (TryGetUeVersionFromExe(exe, out ueVersion))
                    {
                        Log.Information("Detected UE version {UeVersion} from \"{Exe}\"", ueVersion, exe);
                        return true;
                    }
                }
            }
            else if (Directory.GetFiles(projectBinariesDir, "*.exe") is { Length: < 3 } exes)
            {
                foreach (var exe in exes)
                {
                    if (TryGetUeVersionFromExe(exe, out ueVersion))
                    {
                        Log.Information("Detected UE version {UeVersion} from \"{Exe}\"", ueVersion, exe);
                        return true;
                    }
                }
            }
        }

        var crashReportClientExe = Path.Combine(projectDir, "..", "Engine", "Binaries", "Win64", "CrashReportClient.exe");
        if (File.Exists(crashReportClientExe) && TryGetUeVersionFromExe(crashReportClientExe, out ueVersion))
        {
            Log.Information("Detected UE version {UeVersion} from \"{Exe}\"", ueVersion, crashReportClientExe);
            return true;
        }

        ueVersion = EGame.GAME_UE4_LATEST;
        Log.Warning("Failed to detect UE version for \"{GameDirectory}\".", gameDirectory);
        return false;
    }

    private static bool LooksLikeArcRaiders(string paksDir, string projectDir)
    {
        var projectName = Path.GetFileName(Path.GetFullPath(projectDir));
        if (projectName.Equals("PioneerGame", StringComparison.OrdinalIgnoreCase))
            return true;

        if (paksDir.Contains("PioneerGame", StringComparison.OrdinalIgnoreCase) ||
            paksDir.Contains("ArcRaiders", StringComparison.OrdinalIgnoreCase))
            return File.Exists(Path.Combine(paksDir, "global.utoc"));

        return false;
    }

    private static bool TryGetUeVersionFromExe(string exePath, out EGame ueVersion)
    {
        ueVersion = EGame.GAME_UE4_LATEST;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exePath);
            ueVersion = info.FileMajorPart switch
            {
                4 => (EGame) Math.Min((uint)(GameUtils.GameUe4Base + (info.FileMinorPart << 16)), (uint) EGame.GAME_UE4_LATEST),
                5 => (EGame) Math.Min((uint)(GameUtils.GameUe5Base + (info.FileMinorPart << 16)), (uint) EGame.GAME_UE5_LATEST),
                _ => throw new InvalidOperationException($"Unsupported UE major version {info.FileMajorPart} detected from {exePath}")
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    private IEnumerable<EGame> EnumerateUeGames()
        => Enum.GetValues<EGame>()
            .GroupBy(value => (int)value)
            .Select(group => group.First())
            .OrderBy(value => ((int)value & 0xFF) == 0);
    private IEnumerable<DirectorySettings> EnumerateDetectedGames()
    {
        // Fortnite LIVE is the default Fortnite profile (streamed manifests).
        yield return DirectorySettings.Fresh("Fortnite", Constants._FN_LIVE_TRIGGER, EGame.GAME_UE5_8);
        var fortniteInstalled = GetUnrealEngineGame("Fortnite", "Fortnite (Installed)", "\\FortniteGame\\Content\\Paks", EGame.GAME_UE5_8);
        if (fortniteInstalled != null)
        {
            yield return fortniteInstalled;
        }

        yield return GetUnrealEngineGame("Pewee", "Rogue Company", "\\RogueCompany\\Content\\Paks", EGame.GAME_RogueCompany);
        yield return GetUnrealEngineGame("Rosemallow", "The Outer Worlds", "\\Indiana\\Content\\Paks", EGame.GAME_UE4_21);
        yield return GetUnrealEngineGame("Catnip", "Borderlands 3", "\\OakGame\\Content\\Paks", EGame.GAME_Borderlands3);
        yield return GetUnrealEngineGame("AzaleaAlpha", "The Cycle", "\\Prospect\\Content\\Paks", EGame.GAME_UE4_27);
        yield return GetUnrealEngineGame("shoebill", "Star Wars Jedi Fallen Order", "\\SwGame\\Content\\Paks", EGame.GAME_StarWarsJediFallenOrder);
        yield return GetUnrealEngineGame("Snoek", "State Of Decay 2", "\\StateOfDecay2\\Content\\Paks", EGame.GAME_StateOfDecay2);
        yield return GetUnrealEngineGame("711c5e95dc094ca58e5f16bd48e751d6", "MultiVersus", "\\MultiVersus\\Content\\Paks", EGame.GAME_UE4_26);
        yield return GetUnrealEngineGame("9361c8c6d2f34b42b5f2f61093eedf48", "PLAYERUNKNOWN'S BATTLEGROUNDS", "\\TslGame\\Content\\Paks", EGame.GAME_PlayerUnknownsBattlegrounds);

        // VALORANT LIVE is the default Valorant profile.
        yield return DirectorySettings.Fresh("VALORANT", Constants._VAL_LIVE_TRIGGER, EGame.GAME_Valorant);
        var valorantInstalled = GetRiotGame("VALORANT", "ShooterGame\\Content\\Paks", EGame.GAME_Valorant);
        if (valorantInstalled != null)
        {
            valorantInstalled.GameName = "VALORANT (Installed)";
            yield return valorantInstalled;
        }

        yield return GetSteamGame(381210, "\\DeadByDaylight\\Content\\Paks", EGame.GAME_DeadByDaylight, aesKey: "0x22b1639b548124925cf7b9cbaa09f9ac295fcf0324586d6b37ee1d42670b39b3"); // Dead By Daylight
        yield return GetSteamGame(578080, "\\TslGame\\Content\\Paks", EGame.GAME_PlayerUnknownsBattlegrounds); // PUBG
        yield return GetSteamGame(1172380, "\\SwGame\\Content\\Paks", EGame.GAME_StarWarsJediFallenOrder); // STAR WARS Jedi: Fallen Order™
        yield return GetSteamGame(677620, "\\PortalWars\\Content\\Paks", EGame.GAME_Splitgate); // Splitgate
        yield return GetSteamGame(1172620, "\\Athena\\Content\\Paks", EGame.GAME_SeaOfThieves); // Sea of Thieves
        yield return GetSteamGame(1665460, "\\pak", EGame.GAME_UE4_26); // eFootball 2023
        yield return GetRockstarGamesGame("GTA III - Definitive Edition", "\\Gameface\\Content\\Paks", EGame.GAME_GTATheTrilogyDefinitiveEdition);
        yield return GetRockstarGamesGame("GTA San Andreas - Definitive Edition", "\\Gameface\\Content\\Paks", EGame.GAME_GTATheTrilogyDefinitiveEdition);
        yield return GetRockstarGamesGame("GTA Vice City - Definitive Edition", "\\Gameface\\Content\\Paks", EGame.GAME_GTATheTrilogyDefinitiveEdition);
        yield return GetLevelInfiniteGame("tof_launcher", "\\Hotta\\Content\\Paks", EGame.GAME_TowerOfFantasy);
    }

    private LauncherInstalled _launcherInstalled;
    /// <param name="epicAppName">AppName key in Epic LauncherInstalled.dat.</param>
    /// <param name="profileName">Friendly profile / selector label (not the Epic codename).</param>
    private DirectorySettings GetUnrealEngineGame(string epicAppName, string profileName, string pakDirectory, EGame ueVersion)
    {
        _launcherInstalled ??= GetDriveLauncherInstalls<LauncherInstalled>("ProgramData\\Epic\\UnrealEngineLauncher\\LauncherInstalled.dat");
        if (_launcherInstalled?.InstallationList != null)
        {
            foreach (var installationList in _launcherInstalled.InstallationList)
            {
                var gameDir = $"{installationList.InstallLocation}{pakDirectory}";
                if (installationList.AppName.Equals(epicAppName, StringComparison.OrdinalIgnoreCase) && Directory.Exists(gameDir))
                {
                    Log.Debug("Found {GameName} in LauncherInstalled.dat", profileName);
                    return DirectorySettings.Default(profileName, gameDir, ue: ueVersion);
                }
            }
        }

        return null;
    }

    private RiotClientInstalls _riotClientInstalls;
    private DirectorySettings GetRiotGame(string gameName, string pakDirectory, EGame ueVersion)
    {
        _riotClientInstalls ??= GetDriveLauncherInstalls<RiotClientInstalls>("ProgramData\\Riot Games\\RiotClientInstalls.json");
        if (_riotClientInstalls is { AssociatedClient: { } })
        {
            foreach (var (key, _) in _riotClientInstalls.AssociatedClient)
            {
                var gameDir = $"{key.Replace('/', '\\')}{pakDirectory}";
                if (key.Contains(gameName, StringComparison.OrdinalIgnoreCase) && Directory.Exists(gameDir))
                {
                    Log.Debug("Found {GameName} in RiotClientInstalls.json", gameName);
                    return DirectorySettings.Default(gameName, gameDir, ue: ueVersion);
                }
            }
        }

        return null;
    }

    private DirectorySettings GetSteamGame(int id, string pakDirectory, EGame ueVersion, string aesKey = "")
    {
        var steamInfo = SteamDetection.GetSteamGameById(id);
        if (steamInfo is not null)
        {
            Log.Debug("Found {GameName} in steam manifests", steamInfo.Name);
            return DirectorySettings.Default(steamInfo.Name, $"{steamInfo.GameRoot}{pakDirectory}", ue: ueVersion, aes: aesKey);
        }

        return null;
    }

    private DirectorySettings GetRockstarGamesGame(string key, string pakDirectory, EGame ueVersion)
    {
        var installLocation = string.Empty;
        try
        {
            installLocation = App.GetRegistryValue(@$"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{key}", "InstallLocation", RegistryHive.LocalMachine);
        }
        catch
        {
            // ignored
        }

        var gameDir = $"{installLocation}{pakDirectory}";
        if (Directory.Exists(gameDir))
        {
            Log.Debug("Found {GameName} in the registry", key);
            return DirectorySettings.Default(key, gameDir, ue: ueVersion);
        }

        return null;
    }

    private DirectorySettings GetLevelInfiniteGame(string key, string pakDirectory, EGame ueVersion)
    {
        var installLocation = string.Empty;
        var displayName = string.Empty;

        try
        {
            installLocation = App.GetRegistryValue($@"Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\{key}", "GameInstallPath", RegistryHive.CurrentUser);
            displayName = App.GetRegistryValue($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{key}", "DisplayName", RegistryHive.CurrentUser);
        }
        catch
        {
            // ignored
        }

        var gameDir = $"{installLocation}{pakDirectory}";
        if (Directory.Exists(gameDir))
        {
            Log.Debug("Found {GameName} in the registry", key);
            return DirectorySettings.Default(displayName, gameDir, ue: ueVersion);
        }

        return null;
    }

    private T GetDriveLauncherInstalls<T>(string jsonFile)
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            var launcher = $"{drive.Name}{jsonFile}";
            if (!File.Exists(launcher)) continue;

            Log.Debug("\"{Launcher}\" found in drive \"{DriveName}\"", launcher, drive.Name);
            return JsonConvert.DeserializeObject<T>(File.ReadAllText(launcher));
        }

        return default;
    }

#pragma warning disable 649
    private class LauncherInstalled
    {
        public Installation[] InstallationList;
    }

    private class Installation
    {
        public string InstallLocation;
        public string AppName;
        public string AppVersion;
    }

    private class RiotClientInstalls
    {
        [JsonProperty("associated_client", NullValueHandling = NullValueHandling.Ignore)]
        public Dictionary<string, string> AssociatedClient;

        [JsonProperty("patchlines", NullValueHandling = NullValueHandling.Ignore)]
        public Dictionary<string, string> Patchlines;

        [JsonProperty("rc_default", NullValueHandling = NullValueHandling.Ignore)]
        public string RcDefault;

        [JsonProperty("rc_live", NullValueHandling = NullValueHandling.Ignore)]
        public string RcLive;
    }

    private class LauncherSettings
    {
        [JsonProperty("channel", NullValueHandling = NullValueHandling.Ignore)]
        public string Channel;

        [JsonProperty("customChannels", NullValueHandling = NullValueHandling.Ignore)]
        public object[] CustomChannels;

        [JsonProperty("deviceId", NullValueHandling = NullValueHandling.Ignore)]
        public string DeviceId;

        [JsonProperty("formatVersion", NullValueHandling = NullValueHandling.Ignore)]
        public int FormatVersion;

        [JsonProperty("locale", NullValueHandling = NullValueHandling.Ignore)]
        public string Locale;

        [JsonProperty("productLibraryDir", NullValueHandling = NullValueHandling.Ignore)]
        public string ProductLibraryDir;
    }
#pragma warning restore 649

    // https://stackoverflow.com/questions/54767662/finding-game-launcher-executables-in-directory-c-sharp/67679123#67679123
    public static class SteamDetection
    {
        private static readonly List<AppInfo> _steamApps;

        static SteamDetection()
        {
            _steamApps = GetSteamApps(GetSteamLibs());
        }

        public static AppInfo GetSteamGameById(int id) => _steamApps.FirstOrDefault(app => app.Id == id.ToString());

        private static List<AppInfo> GetSteamApps(IEnumerable<string> steamLibs)
        {
            var apps = new List<AppInfo>();
            foreach (var files in steamLibs
                         .Select(lib => Path.Combine(lib, "SteamApps"))
                         .Select(appMetaDataPath => Directory.Exists(appMetaDataPath) ? Directory.GetFiles(appMetaDataPath, "*.acf") : null)
                         .Where(files => files != null))
            {
                apps.AddRange(files.Select(GetAppInfo).Where(appInfo => appInfo != null));
            }

            return apps;
        }

        private static AppInfo GetAppInfo(string appMetaFile)
        {
            var fileDataLines = File.ReadAllLines(appMetaFile);
            var dic = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in fileDataLines)
            {
                var match = Regex.Match(line, @"\s*""(?<key>\w+)""\s+""(?<val>.*)""");
                if (!match.Success) continue;
                var key = match.Groups["key"].Value;
                var val = match.Groups["val"].Value;
                dic[key] = val;
            }

            if (!dic.TryGetValue("appid", out var appId) ||
                !dic.TryGetValue("name", out var name) ||
                !dic.TryGetValue("installDir", out var installDir)) return null;

            var path = Path.GetDirectoryName(appMetaFile) ?? "";
            var libGameRoot = Path.Combine(path, "common", installDir);

            return Directory.Exists(libGameRoot) ? new AppInfo { Id = appId, Name = name, GameRoot = libGameRoot } : null;
        }

        private static List<string> GetSteamLibs()
        {
            var steamPath = GetSteamPath();
            if (steamPath == null || !Directory.Exists(steamPath)) return new List<string>();
            var libraries = new List<string> { steamPath };

            var listFile = Path.Combine(steamPath, @"steamapps\libraryfolders.vdf");
            if (!File.Exists(listFile)) return new List<string>();
            var lines = File.ReadAllLines(listFile);
            foreach (var line in lines)
            {
                var match = Regex.Match(line, @"""(?<path>\w:\\\\.*)""");
                if (!match.Success) continue;
                var path = match.Groups["path"].Value.Replace(@"\\", @"\");
                if (Directory.Exists(path) && !libraries.Contains(path))
                {
                    libraries.Add(path);
                }
            }

            return libraries;
        }

        private static string GetSteamPath() => (string) Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", ""); // Win64, we don't support Win32

        public class AppInfo
        {
            public string Id { get; internal set; }
            public string Name { get; internal set; }
            public string GameRoot { get; internal set; }

            public override string ToString()
            {
                return $"{Name} ({Id})";
            }
        }
    }
}
