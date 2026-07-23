using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.Versions;
using FModel;
using FModel.Framework;
using FModel.Services;
using FModel.Settings;
using FModel.Views.Resources.Controls;

namespace FModel.ViewModels.ProfileDiff;

public class ProfileDiffViewModel : ViewModel
{
    private ThreadWorkerViewModel _threadWorkerView => ApplicationService.ThreadWorkerView;
    private ApplicationViewModel _applicationView => ApplicationService.ApplicationView;

    public ObservableCollection<string> ProfileNames { get; } = [];
    public ObservableCollection<EGame> UeGames { get; } = [];

    private bool _useManualMount;
    /// <summary>When true, mount from the manual pak/AES/mapping/engine fields instead of profiles.</summary>
    public bool UseManualMount
    {
        get => _useManualMount;
        set
        {
            if (!SetProperty(ref _useManualMount, value)) return;
            RaisePropertyChanged(nameof(UseProfiles));
            RaisePropertyChanged(nameof(ShowPerSideCrypto));
            StatusText = value
                ? "Set Old/New pak folders (and shared or per-side AES/mapping/engine), then Run Diff."
                : "Pick an older and newer profile, then Run Diff.";
        }
    }

    public bool UseProfiles => !UseManualMount;

    private bool _shareCryptoAndEngine = true;
    /// <summary>When true, AES / mapping / UE version are shared; only pak folders differ.</summary>
    public bool ShareCryptoAndEngine
    {
        get => _shareCryptoAndEngine;
        set
        {
            if (!SetProperty(ref _shareCryptoAndEngine, value)) return;
            RaisePropertyChanged(nameof(ShowPerSideCrypto));
        }
    }

    public bool ShowPerSideCrypto => UseManualMount && !ShareCryptoAndEngine;

    private string _oldProfile;
    public string OldProfile
    {
        get => _oldProfile;
        set
        {
            if (!SetProperty(ref _oldProfile, value)) return;
            if (UseManualMount) ApplyProfileToManual(value, isOld: true);
        }
    }

    private string _newProfile;
    public string NewProfile
    {
        get => _newProfile;
        set
        {
            if (!SetProperty(ref _newProfile, value)) return;
            if (UseManualMount) ApplyProfileToManual(value, isOld: false);
        }
    }

    private string _oldPakFolder = "";
    public string OldPakFolder
    {
        get => _oldPakFolder;
        set => SetProperty(ref _oldPakFolder, value ?? "");
    }

    private string _newPakFolder = "";
    public string NewPakFolder
    {
        get => _newPakFolder;
        set => SetProperty(ref _newPakFolder, value ?? "");
    }

    private string _sharedAesKey = "";
    public string SharedAesKey
    {
        get => _sharedAesKey;
        set => SetProperty(ref _sharedAesKey, value ?? "");
    }

    private string _sharedMappingPath = "";
    public string SharedMappingPath
    {
        get => _sharedMappingPath;
        set => SetProperty(ref _sharedMappingPath, value ?? "");
    }

    private EGame _sharedUeVersion = EGame.GAME_UE4_LATEST;
    public EGame SharedUeVersion
    {
        get => _sharedUeVersion;
        set => SetProperty(ref _sharedUeVersion, value);
    }

    private string _oldAesKey = "";
    public string OldAesKey
    {
        get => _oldAesKey;
        set => SetProperty(ref _oldAesKey, value ?? "");
    }

    private string _oldMappingPath = "";
    public string OldMappingPath
    {
        get => _oldMappingPath;
        set => SetProperty(ref _oldMappingPath, value ?? "");
    }

    private EGame _oldUeVersion = EGame.GAME_UE4_LATEST;
    public EGame OldUeVersion
    {
        get => _oldUeVersion;
        set => SetProperty(ref _oldUeVersion, value);
    }

    private string _newAesKey = "";
    public string NewAesKey
    {
        get => _newAesKey;
        set => SetProperty(ref _newAesKey, value ?? "");
    }

    private string _newMappingPath = "";
    public string NewMappingPath
    {
        get => _newMappingPath;
        set => SetProperty(ref _newMappingPath, value ?? "");
    }

    private EGame _newUeVersion = EGame.GAME_UE4_LATEST;
    public EGame NewUeVersion
    {
        get => _newUeVersion;
        set => SetProperty(ref _newUeVersion, value);
    }

    private string _exportRoot = "";
    public string ExportRoot
    {
        get => _exportRoot;
        set => SetProperty(ref _exportRoot, value ?? "");
    }

    private bool _exportProperties = true;
    public bool ExportProperties
    {
        get => _exportProperties;
        set => SetProperty(ref _exportProperties, value);
    }

    private bool _exportRaw;
    public bool ExportRaw
    {
        get => _exportRaw;
        set => SetProperty(ref _exportRaw, value);
    }

    private bool _exportTextures;
    public bool ExportTextures
    {
        get => _exportTextures;
        set => SetProperty(ref _exportTextures, value);
    }

    private bool _exportModels;
    public bool ExportModels
    {
        get => _exportModels;
        set => SetProperty(ref _exportModels, value);
    }

    private bool _exportAudio;
    public bool ExportAudio
    {
        get => _exportAudio;
        set => SetProperty(ref _exportAudio, value);
    }

    private bool _exportRemoved;
    /// <summary>When true, exports packages that exist only on the Old side into ExportRoot/Removed/.</summary>
    public bool ExportRemoved
    {
        get => _exportRemoved;
        set => SetProperty(ref _exportRemoved, value);
    }

    private bool _populateExplorer = true;
    public bool PopulateExplorer
    {
        get => _populateExplorer;
        set => SetProperty(ref _populateExplorer, value);
    }

    private bool _writeDiffLogFile = true;
    /// <summary>When true, writes the added/modified/removed inventory log into the export root.</summary>
    public bool WriteDiffLogFile
    {
        get => _writeDiffLogFile;
        set => SetProperty(ref _writeDiffLogFile, value);
    }

    private string _statusText = "Pick an older and newer profile, then Run Diff.";
    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    private string _progressPhase = "Idle";
    public string ProgressPhase
    {
        get => _progressPhase;
        set => SetProperty(ref _progressPhase, value);
    }

    private double _progressPercent;
    public double ProgressPercent
    {
        get => _progressPercent;
        set => SetProperty(ref _progressPercent, value);
    }

    private int _processedCount;
    public int ProcessedCount
    {
        get => _processedCount;
        set => SetProperty(ref _processedCount, value);
    }

    private int _totalCount;
    public int TotalCount
    {
        get => _totalCount;
        set => SetProperty(ref _totalCount, value);
    }

    private int _failedCount;
    public int FailedCount
    {
        get => _failedCount;
        set => SetProperty(ref _failedCount, value);
    }

    private int _warningCount;
    public int WarningCount
    {
        get => _warningCount;
        set => SetProperty(ref _warningCount, value);
    }

    public string ProgressReportText
    {
        get
        {
            var pct = TotalCount > 0
                ? $"{ProgressPercent:0}%"
                : ProgressPercent > 0
                    ? $"{ProgressPercent:0}%"
                    : "—";
            var files = TotalCount > 0
                ? $"{ProcessedCount}/{TotalCount}"
                : ProcessedCount > 0
                    ? $"{ProcessedCount}"
                    : "—";
            return $"{ProgressPhase}  |  {pct}  |  Files: {files}  |  Failed: {FailedCount}  |  Warnings: {WarningCount}";
        }
    }

    private void ResetProgress(string phase)
    {
        ProgressPhase = phase;
        ProgressPercent = 0;
        ProcessedCount = 0;
        TotalCount = 0;
        FailedCount = 0;
        WarningCount = 0;
        RaisePropertyChanged(nameof(ProgressReportText));
    }

    private DateTime _lastProgressUi = DateTime.MinValue;

    private void ReportProgress(string phase, int processed, int total, int failures, int warnings, bool force = false)
    {
        var now = DateTime.UtcNow;
        if (!force && processed < total && (now - _lastProgressUi).TotalMilliseconds < 50)
            return;

        _lastProgressUi = now;

        void Apply()
        {
            ProgressPhase = phase;
            ProcessedCount = processed;
            TotalCount = total;
            FailedCount = failures;
            WarningCount = warnings;
            ProgressPercent = total > 0
                ? Math.Clamp(100.0 * processed / total, 0, 100)
                : 0;
            RaisePropertyChanged(nameof(ProgressReportText));
            StatusText = ProgressReportText;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
            Apply();
        else
            dispatcher.Invoke(Apply);
    }

    private void AddWarning(string message, bool logToUi = true)
    {
        WarningCount++;
        RaisePropertyChanged(nameof(ProgressReportText));
        if (!logToUi) return;
        Application.Current.Dispatcher.Invoke(() =>
            FLogger.Append(ELog.Warning, () =>
                FLogger.Text(message, Constants.WHITE, true)));
    }

    public void RefreshProfiles()
    {
        ProfileNames.Clear();
        foreach (var name in ProfileManager.GetProfileNames())
            ProfileNames.Add(name);

        UeGames.Clear();
        foreach (var game in Enum.GetValues<EGame>()
                     .GroupBy(value => (int)value)
                     .Select(group => group.First())
                     .OrderBy(value => ((int)value & 0xFF) == 0))
            UeGames.Add(game);

        LoadFromSettings();

        OldProfile ??= ProfileNames.FirstOrDefault();
        NewProfile ??= UserSettings.Default.CurrentProfileName
                       ?? ProfileNames.FirstOrDefault(n => !string.Equals(n, OldProfile, StringComparison.OrdinalIgnoreCase))
                       ?? ProfileNames.FirstOrDefault();

        PrefillManualFromLive();

        if (string.IsNullOrWhiteSpace(ExportRoot))
            ExportRoot = Path.Combine(UserSettings.Default.OutputDirectory, "Exports");
    }

    public void PersistSettings()
    {
        var s = UserSettings.Default.DiffChecker;
        s.UseManualMount = UseManualMount;
        s.ShareCryptoAndEngine = ShareCryptoAndEngine;
        s.OldProfile = OldProfile;
        s.NewProfile = NewProfile;
        s.OldPakFolder = OldPakFolder;
        s.NewPakFolder = NewPakFolder;
        s.SharedAesKey = SharedAesKey;
        s.SharedMappingPath = SharedMappingPath;
        s.SharedUeVersion = SharedUeVersion;
        s.OldAesKey = OldAesKey;
        s.OldMappingPath = OldMappingPath;
        s.OldUeVersion = OldUeVersion;
        s.NewAesKey = NewAesKey;
        s.NewMappingPath = NewMappingPath;
        s.NewUeVersion = NewUeVersion;
        s.ExportRoot = ExportRoot;
        s.ExportProperties = ExportProperties;
        s.ExportRaw = ExportRaw;
        s.ExportTextures = ExportTextures;
        s.ExportModels = ExportModels;
        s.ExportAudio = ExportAudio;
        s.ExportRemoved = ExportRemoved;
        s.PopulateExplorer = PopulateExplorer;
        s.WriteDiffLogFile = WriteDiffLogFile;
        UserSettings.Save();
    }

    private void LoadFromSettings()
    {
        var s = UserSettings.Default.DiffChecker;
        if (s == null) return;

        UseManualMount = s.UseManualMount;
        ShareCryptoAndEngine = s.ShareCryptoAndEngine;
        if (!string.IsNullOrWhiteSpace(s.OldProfile)) OldProfile = s.OldProfile;
        if (!string.IsNullOrWhiteSpace(s.NewProfile)) NewProfile = s.NewProfile;
        if (!string.IsNullOrWhiteSpace(s.OldPakFolder)) OldPakFolder = s.OldPakFolder;
        if (!string.IsNullOrWhiteSpace(s.NewPakFolder)) NewPakFolder = s.NewPakFolder;
        if (!string.IsNullOrWhiteSpace(s.SharedAesKey)) SharedAesKey = s.SharedAesKey;
        if (!string.IsNullOrWhiteSpace(s.SharedMappingPath)) SharedMappingPath = s.SharedMappingPath;
        SharedUeVersion = s.SharedUeVersion;
        if (!string.IsNullOrWhiteSpace(s.OldAesKey)) OldAesKey = s.OldAesKey;
        if (!string.IsNullOrWhiteSpace(s.OldMappingPath)) OldMappingPath = s.OldMappingPath;
        OldUeVersion = s.OldUeVersion;
        if (!string.IsNullOrWhiteSpace(s.NewAesKey)) NewAesKey = s.NewAesKey;
        if (!string.IsNullOrWhiteSpace(s.NewMappingPath)) NewMappingPath = s.NewMappingPath;
        NewUeVersion = s.NewUeVersion;
        if (!string.IsNullOrWhiteSpace(s.ExportRoot)) ExportRoot = s.ExportRoot;
        ExportProperties = s.ExportProperties;
        ExportRaw = s.ExportRaw;
        ExportTextures = s.ExportTextures;
        ExportModels = s.ExportModels;
        ExportAudio = s.ExportAudio;
        ExportRemoved = s.ExportRemoved;
        PopulateExplorer = s.PopulateExplorer;
        WriteDiffLogFile = s.WriteDiffLogFile;
    }

    private void PrefillManualFromLive()
    {
        var live = UserSettings.Default;
        if (string.IsNullOrWhiteSpace(NewPakFolder))
            NewPakFolder = live.GameDirectory ?? "";
        if (string.IsNullOrWhiteSpace(SharedAesKey))
            SharedAesKey = live.CurrentDir?.AesKeys?.MainKey ?? "";
        if (string.IsNullOrWhiteSpace(SharedMappingPath) &&
            live.CurrentDir?.Endpoints is { Length: > 1 } endpoints)
            SharedMappingPath = endpoints[1].FilePath ?? "";
        if (live.CurrentDir != null && SharedUeVersion == EGame.GAME_UE4_LATEST)
            SharedUeVersion = live.CurrentDir.UeVersion;

        if (string.IsNullOrWhiteSpace(OldAesKey)) OldAesKey = SharedAesKey;
        if (string.IsNullOrWhiteSpace(NewAesKey)) NewAesKey = SharedAesKey;
        if (string.IsNullOrWhiteSpace(OldMappingPath)) OldMappingPath = SharedMappingPath;
        if (string.IsNullOrWhiteSpace(NewMappingPath)) NewMappingPath = SharedMappingPath;
        if (OldUeVersion == EGame.GAME_UE4_LATEST) OldUeVersion = SharedUeVersion;
        if (NewUeVersion == EGame.GAME_UE4_LATEST) NewUeVersion = SharedUeVersion;
    }

    private void ApplyProfileToManual(string profileName, bool isOld)
    {
        if (string.IsNullOrWhiteSpace(profileName) ||
            !ProfileManager.TryReadMountConfig(profileName, out var config))
            return;

        if (isOld)
        {
            OldPakFolder = config.GameDirectory;
            OldAesKey = config.AesMainKey;
            OldMappingPath = config.MappingFilePath;
            OldUeVersion = config.UeVersion;
            if (ShareCryptoAndEngine)
            {
                SharedAesKey = config.AesMainKey;
                SharedMappingPath = config.MappingFilePath;
                SharedUeVersion = config.UeVersion;
            }
        }
        else
        {
            NewPakFolder = config.GameDirectory;
            NewAesKey = config.AesMainKey;
            NewMappingPath = config.MappingFilePath;
            NewUeVersion = config.UeVersion;
            if (ShareCryptoAndEngine)
            {
                SharedAesKey = config.AesMainKey;
                SharedMappingPath = config.MappingFilePath;
                SharedUeVersion = config.UeVersion;
            }
        }
    }

    private bool TryResolveConfigs(out ProfileMountConfig oldConfig, out ProfileMountConfig newConfig, out string oldLabel, out string newLabel)
    {
        oldConfig = null;
        newConfig = null;
        oldLabel = "";
        newLabel = "";

        if (!UseManualMount)
        {
            if (string.IsNullOrWhiteSpace(OldProfile) || string.IsNullOrWhiteSpace(NewProfile))
            {
                StatusText = "Select both an Old and New profile.";
                return false;
            }

            if (string.Equals(OldProfile, NewProfile, StringComparison.OrdinalIgnoreCase))
            {
                StatusText = "Old and New profiles must be different.";
                return false;
            }

            if (!ProfileManager.TryReadMountConfig(OldProfile, out oldConfig) ||
                !ProfileManager.TryReadMountConfig(NewProfile, out newConfig))
            {
                StatusText = "Could not read one of the selected profiles.";
                return false;
            }

            oldLabel = OldProfile;
            newLabel = NewProfile;
            return true;
        }

        if (string.IsNullOrWhiteSpace(OldPakFolder) || string.IsNullOrWhiteSpace(NewPakFolder))
        {
            StatusText = "Set both Old and New pak folders.";
            return false;
        }

        if (string.Equals(OldPakFolder.Trim(), NewPakFolder.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            StatusText = "Old and New pak folders must be different.";
            return false;
        }

        if (!Directory.Exists(OldPakFolder) || !Directory.Exists(NewPakFolder))
        {
            StatusText = "One or both pak folders do not exist.";
            return false;
        }

        var oldAes = ShareCryptoAndEngine ? SharedAesKey : OldAesKey;
        var newAes = ShareCryptoAndEngine ? SharedAesKey : NewAesKey;
        var oldMap = ShareCryptoAndEngine ? SharedMappingPath : OldMappingPath;
        var newMap = ShareCryptoAndEngine ? SharedMappingPath : NewMappingPath;
        var oldUe = ShareCryptoAndEngine ? SharedUeVersion : OldUeVersion;
        var newUe = ShareCryptoAndEngine ? SharedUeVersion : NewUeVersion;

        oldConfig = BuildManualConfig("Manual-Old", OldPakFolder, oldAes, oldMap, oldUe);
        newConfig = BuildManualConfig("Manual-New", NewPakFolder, newAes, newMap, newUe);
        oldLabel = Path.GetFileName(OldPakFolder.TrimEnd('\\', '/')) is { Length: > 0 } o ? o : "Old";
        newLabel = Path.GetFileName(NewPakFolder.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : "New";
        return true;
    }

    private static ProfileMountConfig BuildManualConfig(
        string name, string gameDir, string aes, string mapping, EGame ueVersion)
    {
        var map = mapping?.Trim() ?? "";
        return new ProfileMountConfig
        {
            Name = name,
            GameDirectory = gameDir.Trim(),
            UeVersion = ueVersion,
            AesMainKey = aes?.Trim() ?? "",
            MappingFilePath = map,
            MappingOverwrite = !string.IsNullOrWhiteSpace(map)
        };
    }

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!SetProperty(ref _isRunning, value)) return;
            RaisePropertyChanged(nameof(CanRun));
            RaisePropertyChanged(nameof(CanStop));
            RaisePropertyChanged(nameof(RunButtonText));
        }
    }

    public bool CanRun => !IsRunning;
    public bool CanStop => IsRunning;

    public string RunButtonText => IsRunning ? "Diff in Progress" : "Run Diff";

    public void CancelDiff()
    {
        if (!IsRunning) return;
        StatusText = "Stopping Diff Checker…";
        ProgressPhase = "Stopping";
        RaisePropertyChanged(nameof(ProgressReportText));
        _threadWorkerView.Cancel();
    }

    public async Task RunDiffAsync()
    {
        if (IsRunning) return;

        if (!ExportProperties && !ExportRaw && !ExportTextures && !ExportModels && !ExportAudio &&
            !ExportRemoved && !WriteDiffLogFile && !PopulateExplorer)
        {
            StatusText = "Enable at least one export option, Write Diff Log, or Populate Explorer.";
            return;
        }

        if (!TryResolveConfigs(out var oldConfig, out var newConfig, out var oldLabel, out var newLabel))
            return;

        PersistSettings();

        ResetProgress("Starting");
        IsRunning = true;

        if (string.Equals(oldConfig.GameDirectory, newConfig.GameDirectory, StringComparison.OrdinalIgnoreCase))
        {
            AddWarning(
                "Both sides point at the same pak folder — the inventory diff may be empty unless AES/mappings changed what mounted.");
        }

        try
        {
            await _threadWorkerView.Begin(cancellationToken =>
            {
            AbstractVfsFileProvider oldProvider = null;
            AbstractVfsFileProvider newProvider = null;
            string logPath = null;
            try
            {
                ReportProgress($"Mounting old \"{oldLabel}\"", 0, 0, FailedCount, WarningCount, force: true);
                _applicationView.Status.UpdateStatusLabel("Diff Checker", "Mounting old");
                oldProvider = ProfileProviderFactory.Create(oldConfig);
                ProfileProviderFactory.InitializeAndMount(oldProvider, oldConfig);
                var oldInventory = PackageInventory.FromProvider(oldProvider);

                // Keep Old mounted when exporting removed packages; otherwise free it early.
                if (!ExportRemoved)
                {
                    oldProvider.Dispose();
                    oldProvider = null;
                }

                ReportProgress($"Mounting new \"{newLabel}\"", 0, 0, FailedCount, WarningCount, force: true);
                _applicationView.Status.UpdateStatusLabel("Diff Checker", "Mounting new");
                newProvider = ProfileProviderFactory.Create(newConfig);
                ProfileProviderFactory.InitializeAndMount(newProvider, newConfig);
                var newInventory = PackageInventory.FromProvider(newProvider);

                cancellationToken.ThrowIfCancellationRequested();
                var diff = PackageInventory.Diff(oldInventory, newInventory);

                ReportProgress(
                    $"Diff: +{diff.Added.Count} / ~{diff.Modified.Count} / -{diff.Removed.Count}",
                    0, 0, FailedCount, WarningCount, force: true);

                var exportRoot = string.IsNullOrWhiteSpace(ExportRoot)
                    ? UserSettings.Default.OutputDirectory
                    : ExportRoot;
                var propertiesDir = Path.Combine(exportRoot, "Properties");
                var rawDir = Path.Combine(exportRoot, "RawData");
                Directory.CreateDirectory(exportRoot);

                if (WriteDiffLogFile)
                {
                    logPath = ProfileDiffExporter.WriteDiffLog(oldLabel, newLabel, diff, exportRoot);
                    ReportProgress("Wrote diff log", 0, 0, FailedCount, WarningCount, force: true);
                }

                var deltaPaths = diff.Added.Concat(diff.ModifiedPaths).ToList();
                var exported = 0;
                var removedExported = 0;
                var failures = FailedCount;
                var warnings = WarningCount;

                if ((ExportProperties || ExportRaw) && deltaPaths.Count > 0)
                {
                    Directory.CreateDirectory(propertiesDir);
                    Directory.CreateDirectory(rawDir);
                    _applicationView.Status.UpdateStatusLabel($"{deltaPaths.Count} packages", "Extracting delta");
                    exported = ProfileDiffExporter.ExportDelta(
                        newProvider,
                        deltaPaths,
                        propertiesDir,
                        rawDir,
                        ExportProperties,
                        ExportRaw,
                        cancellationToken,
                        (processed, total, f, w) =>
                            ReportProgress("Exporting properties/raw", processed, total, failures + f, warnings + w,
                                force: processed >= total),
                        out var deltaFailures,
                        out var deltaWarnings);
                    failures += deltaFailures;
                    warnings += deltaWarnings;
                    ReportProgress("Exporting properties/raw", deltaPaths.Count, deltaPaths.Count, failures, warnings, force: true);
                }

                var assetExported = 0;
                if ((ExportTextures || ExportModels || ExportAudio) && deltaPaths.Count > 0)
                {
                    _applicationView.Status.UpdateStatusLabel($"{deltaPaths.Count} packages", "Exporting assets");
                    assetExported = ExportDeltaAssets(
                        newProvider,
                        deltaPaths,
                        exportRoot,
                        cancellationToken,
                        ref failures,
                        ref warnings);
                }

                if (ExportRemoved && diff.Removed.Count > 0 && oldProvider != null)
                {
                    var removedRoot = Path.Combine(exportRoot, "Removed");
                    var removedProps = Path.Combine(removedRoot, "Properties");
                    var removedRaw = Path.Combine(removedRoot, "RawData");

                    // If no format toggles are on, still archive removed packs as properties.
                    var anyFormat = ExportProperties || ExportRaw || ExportTextures || ExportModels || ExportAudio;
                    var removedPropsOn = ExportProperties || !anyFormat;
                    var removedRawOn = ExportRaw;

                    if (removedPropsOn || removedRawOn)
                    {
                        if (removedPropsOn) Directory.CreateDirectory(removedProps);
                        if (removedRawOn) Directory.CreateDirectory(removedRaw);
                        StatusText = $"Exporting {diff.Removed.Count} removed packages…";
                        _applicationView.Status.UpdateStatusLabel($"{diff.Removed.Count} packages", "Exporting removed");
                        removedExported = ProfileDiffExporter.ExportDelta(
                            oldProvider,
                            diff.Removed,
                            removedProps,
                            removedRaw,
                            removedPropsOn,
                            removedRawOn,
                            cancellationToken,
                            (processed, total, f, w) =>
                                ReportProgress("Exporting removed", processed, total, failures + f, warnings + w,
                                    force: processed >= total),
                            out var remFailures,
                            out var remWarnings);
                        failures += remFailures;
                        warnings += remWarnings;
                        ReportProgress("Exporting removed", diff.Removed.Count, diff.Removed.Count, failures, warnings, force: true);
                    }

                    if (ExportTextures || ExportModels || ExportAudio)
                    {
                        _applicationView.Status.UpdateStatusLabel($"{diff.Removed.Count} packages", "Exporting removed assets");
                        var removedAssets = ExportDeltaAssets(
                            oldProvider,
                            diff.Removed,
                            removedRoot,
                            cancellationToken,
                            ref failures,
                            ref warnings);
                        removedExported = Math.Max(removedExported, removedAssets);
                    }
                }

                // Old side no longer needed.
                oldProvider?.Dispose();
                oldProvider = null;

                if (PopulateExplorer && deltaPaths.Count > 0)
                {
                    var live = _applicationView.CUE4Parse.Provider;
                    var liveDir = UserSettings.Default.GameDirectory;
                    if (string.Equals(liveDir, newConfig.GameDirectory, StringComparison.OrdinalIgnoreCase))
                    {
                        ReportProgress("Populating explorer", 0, deltaPaths.Count, failures, warnings);
                        var entries = new List<GameFile>();
                        for (var i = 0; i < deltaPaths.Count; i++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (live.Files.TryGetValue(deltaPaths[i], out var file) && !file.IsUePackagePayload)
                                entries.Add(file);
                            if (i == deltaPaths.Count - 1 || (i + 1) % 50 == 0)
                                ReportProgress("Populating explorer", i + 1, deltaPaths.Count, failures, warnings);
                        }

                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            _applicationView.CUE4Parse.AssetsFolder.Folders.Clear();
                            _applicationView.CUE4Parse.SearchVm.SearchResults.Clear();
                            _applicationView.SelectedLeftTabIndex = 1;
                            _applicationView.IsAssetsExplorerVisible = true;
                            _applicationView.Status.UpdateStatusLabel($"{entries.Count:### ### ###} Packages");
                            _applicationView.CUE4Parse.AssetsFolder.BulkPopulate(entries);
                        });
                    }
                    else
                    {
                        warnings++;
                        ReportProgress("Populate Explorer skipped", 0, 0, failures, warnings, force: true);
                        Application.Current.Dispatcher.Invoke(() =>
                            FLogger.Append(ELog.Warning, () =>
                                FLogger.Text(
                                    "Populate Explorer skipped: the New pak folder differs from the currently loaded game. Load that game first, or rely on the exported files.",
                                    Constants.WHITE, true)));
                    }
                }

                ReportProgress("Finished", Math.Max(ProcessedCount, TotalCount), Math.Max(TotalCount, 1), failures, warnings, force: true);
                ProgressPercent = 100;
                RaisePropertyChanged(nameof(ProgressReportText));

                var summary =
                    $"Diff Checker finished: +{diff.Added.Count} added, ~{diff.Modified.Count} modified, -{diff.Removed.Count} removed. " +
                    $"Exported {exported} package(s)" +
                    (assetExported > 0 ? $", {assetExported} asset package(s)" : "") +
                    (removedExported > 0 ? $", {removedExported} removed package(s)" : "") +
                    $". Failed: {failures}, Warnings: {warnings}" +
                    (logPath != null ? $". Log: {logPath}" : "");

                StatusText = summary;
                Application.Current.Dispatcher.Invoke(() =>
                    FLogger.Append(ELog.Information, () =>
                    {
                        FLogger.Text(
                            $"Diff Checker \"{oldLabel}\" → \"{newLabel}\": +{diff.Added.Count} / ~{diff.Modified.Count} / -{diff.Removed.Count}. Failed: {failures}, Warnings: {warnings}. ",
                            Constants.WHITE);
                        if (logPath != null)
                            FLogger.Link("Diff log", logPath, true);
                        else
                            FLogger.Text("", Constants.WHITE, true);
                    }));
            }
            catch (OperationCanceledException)
            {
                ReportProgress("Cancelled", ProcessedCount, TotalCount, FailedCount, WarningCount, force: true);
                StatusText = "Diff Checker cancelled.";
                throw;
            }
            catch (Exception e)
            {
                FailedCount++;
                ReportProgress("Failed", ProcessedCount, TotalCount, FailedCount, WarningCount, force: true);
                StatusText = $"Diff Checker failed: {e.Message}";
                Application.Current.Dispatcher.Invoke(() =>
                    FLogger.Append(ELog.Error, () =>
                        FLogger.Text($"Diff Checker failed: {e.Message}", Constants.WHITE, true)));
            }
            finally
            {
                oldProvider?.Dispose();
                newProvider?.Dispose();
                _applicationView.Status.SetStatus(EStatusKind.Ready);
            }
        });
        }
        finally
        {
            IsRunning = false;
        }
    }

    private int ExportDeltaAssets(
        IFileProvider provider,
        IReadOnlyList<string> paths,
        string exportRoot,
        CancellationToken cancellationToken,
        ref int failures,
        ref int warnings)
    {
        var exported = 0;
        var settings = UserSettings.Default;
        var prevTextures = settings.TextureDirectory;
        var prevModels = settings.ModelDirectory;
        var prevAudio = settings.AudioDirectory;

        var texturesDir = Path.Combine(exportRoot, "Textures");
        var modelsDir = Path.Combine(exportRoot, "Models");
        var audioDir = Path.Combine(exportRoot, "Audio");
        if (ExportTextures) Directory.CreateDirectory(texturesDir);
        if (ExportModels) Directory.CreateDirectory(modelsDir);
        if (ExportAudio) Directory.CreateDirectory(audioDir);

        EBulkType bulk = EBulkType.None;
        if (ExportTextures) bulk |= EBulkType.Textures;
        if (ExportModels) bulk |= EBulkType.Meshes;
        if (ExportAudio) bulk |= EBulkType.Audio;

        var total = paths.Count;
        var localFailures = 0;
        var localWarnings = 0;
        var baseFailures = failures;
        var baseWarnings = warnings;

        try
        {
            if (ExportTextures) settings.TextureDirectory = texturesDir;
            if (ExportModels) settings.ModelDirectory = modelsDir;
            if (ExportAudio) settings.AudioDirectory = audioDir;

            var cue4 = _applicationView.CUE4Parse;
            for (var i = 0; i < paths.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = paths[i];
                if (!provider.Files.TryGetValue(path, out var entry) || !entry.IsUePackage)
                {
                    localWarnings++;
                    ReportProgress("Exporting textures/models/audio", i + 1, total,
                        baseFailures + localFailures, baseWarnings + localWarnings,
                        force: i + 1 >= total);
                    continue;
                }

                try
                {
                    cue4.ExtractAssetsFromProvider(provider, cancellationToken, entry, bulk);
                    exported++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    localFailures++;
                    Serilog.Log.Warning(e, "Diff Checker asset export failed for {Path}", path);
                }

                ReportProgress("Exporting textures/models/audio", i + 1, total,
                    baseFailures + localFailures, baseWarnings + localWarnings,
                    force: i + 1 >= total);
            }
        }
        finally
        {
            settings.TextureDirectory = prevTextures;
            settings.ModelDirectory = prevModels;
            settings.AudioDirectory = prevAudio;
        }

        failures += localFailures;
        warnings += localWarnings;
        return exported;
    }
}
