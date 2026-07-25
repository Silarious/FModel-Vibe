using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Nanite;
using CUE4Parse.UE4.Lua.unluac;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Animations;
using CUE4Parse_Conversion.Meshes;
using CUE4Parse_Conversion.Textures;
using CUE4Parse_Conversion.UEFormat.Enums;
using FModel.Extensions.Themes;
using FModel.Framework;
using FModel.ViewModels;
using FModel.ViewModels.ApiEndpoints.Models;
using FModel.Views.Snooper;
using Newtonsoft.Json;
using Serilog;

namespace FModel.Settings
{
    public sealed class UserSettings : ViewModel
    {
        public static UserSettings Default { get; set; }
        public static readonly ESkipAlreadyExported[] SkipAlreadyExportedModes = Enum.GetValues<ESkipAlreadyExported>();
        public static readonly string AppDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FModel_Vibe");
#if DEBUG
        public static readonly string FilePath = Path.Combine(AppDataFolder, "AppSettings_Debug.json");
#else
        public static readonly string FilePath = Path.Combine(AppDataFolder, "AppSettings.json");
#endif

        private static bool _bSave = true;
        private static bool _autoSaveEnabled;
        private static bool _suspendAutoSave;
        private static int _suspendAutoSaveDepth;
        private static DispatcherTimer _autoSaveTimer;
        private static UserSettings _autoSaveHooked;

        static UserSettings()
        {
            Default = new UserSettings();
        }

        /// <summary>
        /// Hook <see cref="PropertyChanged"/> so live edits debounce-save AppSettings + the active profile.
        /// Call once after AppSettings.json is loaded (replaces the static ctor instance).
        /// </summary>
        public static void EnableAutoSave()
        {
            if (_autoSaveHooked != null)
                _autoSaveHooked.PropertyChanged -= OnDefaultPropertyChanged;

            _autoSaveHooked = Default;
            if (_autoSaveHooked != null)
                _autoSaveHooked.PropertyChanged += OnDefaultPropertyChanged;

            _autoSaveEnabled = true;
        }

        /// <summary>Suppress disk writes during profile PopulateObject / bulk rebinds.</summary>
        public static void SuspendAutoSave()
        {
            _suspendAutoSaveDepth++;
            _suspendAutoSave = true;
        }

        public static void ResumeAutoSave()
        {
            if (_suspendAutoSaveDepth > 0)
                _suspendAutoSaveDepth--;
            if (_suspendAutoSaveDepth == 0)
                _suspendAutoSave = false;
        }

        public static IDisposable BeginAutoSaveSuspend() => new AutoSaveSuspendScope();

        private sealed class AutoSaveSuspendScope : IDisposable
        {
            public AutoSaveSuspendScope() => SuspendAutoSave();
            public void Dispose() => ResumeAutoSave();
        }

        private static void OnDefaultPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (!_autoSaveEnabled || _suspendAutoSave || !_bSave)
                return;
            RequestAutoSave();
        }

        /// <summary>Debounced persist (≈400ms) of AppSettings.json + active profile snapshot.</summary>
        public static void RequestAutoSave()
        {
            if (!_autoSaveEnabled || _suspendAutoSave || !_bSave || Default == null)
                return;

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted)
                return;

            void ArmTimer()
            {
                if (_autoSaveTimer == null)
                {
                    _autoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                    _autoSaveTimer.Tick += (_, _) =>
                    {
                        _autoSaveTimer.Stop();
                        PersistLiveSettings();
                    };
                }

                _autoSaveTimer.Stop();
                _autoSaveTimer.Start();
            }

            if (dispatcher.CheckAccess())
                ArmTimer();
            else
                dispatcher.BeginInvoke(ArmTimer);
        }

        /// <summary>Write AppSettings.json and the active named profile (if any).</summary>
        public static void PersistLiveSettings()
        {
            if (!_bSave || Default == null || _suspendAutoSave)
                return;

            using (BeginAutoSaveSuspend())
            {
                try
                {
                    Save();

                    var profile = Default.CurrentProfileName;
                    if (!string.IsNullOrWhiteSpace(profile))
                        ProfileManager.SaveCurrentAs(profile);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to auto-save settings / active profile");
                }
            }
        }

        public static void Save()
        {
            if (!_bSave || Default == null) return;
            ProfileManager.SyncCurrentDirToGameDirectory();
            File.WriteAllText(FilePath, JsonConvert.SerializeObject(Default, Formatting.Indented));
        }

        public static void Delete()
        {
            if (File.Exists(FilePath))
            {
                _bSave = false;
                File.Delete(FilePath);
            }
        }

        public static bool IsEndpointValid(EEndpointType type, out EndpointSettings endpoint)
        {
            endpoint = Default.CurrentDir?.Endpoints?[(int) type];
            if (endpoint == null) return false;
            if (endpoint.Overwrite) return true;
            // Profile JSON often restores Url/Path with IsValid left false — treat configured endpoints as usable.
            if (!string.IsNullOrWhiteSpace(endpoint.Url) && !string.IsNullOrWhiteSpace(endpoint.Path))
            {
                if (!endpoint.IsValid)
                    endpoint.IsValid = true;
                return true;
            }

            return endpoint.IsValid;
        }

        [JsonIgnore]
        public ExporterOptions ExportOptions => new()
        {
            LodFormat = Default.LodExportFormat,
            MeshFormat = Default.MeshExportFormat,
            NaniteMeshFormat = Default.NaniteMeshExportFormat,
            AnimFormat = Default.MeshExportFormat switch
            {
                EMeshFormat.UEFormat => EAnimFormat.UEFormat,
                _ => EAnimFormat.ActorX
            },
            MaterialFormat = Default.MaterialExportFormat,
            TextureFormat = Default.TextureExportFormat,
            SocketFormat = Default.SocketExportFormat,
            CompressionFormat = Default.CompressionFormat,
            Platform = Default.CurrentDir.TexturePlatform,
            ExportMorphTargets = Default.SaveMorphTargets,
            ExportMaterials = Default.SaveEmbeddedMaterials,
            ExportHdrTexturesAsHdr = Default.SaveHdrTexturesAsHdr
        };

        private bool _showChangelog = true;
        public bool ShowChangelog
        {
            get => _showChangelog;
            set => SetProperty(ref _showChangelog, value);
        }

        private string _outputDirectory;
        public string OutputDirectory
        {
            get => _outputDirectory;
            set => SetProperty(ref _outputDirectory, value);
        }

        private string _rawDataDirectory;
        public string RawDataDirectory
        {
            get => _rawDataDirectory;
            set => SetProperty(ref _rawDataDirectory, value);
        }

        private string _propertiesDirectory;
        public string PropertiesDirectory
        {
            get => _propertiesDirectory;
            set => SetProperty(ref _propertiesDirectory, value);
        }

        private string _textureDirectory;
        public string TextureDirectory
        {
            get => _textureDirectory;
            set => SetProperty(ref _textureDirectory, value);
        }

        private string _audioDirectory;
        public string AudioDirectory
        {
            get => _audioDirectory;
            set => SetProperty(ref _audioDirectory, value);
        }

        private string _codeDirectory;
        public string CodeDirectory
        {
            get => _codeDirectory;
            set => SetProperty(ref _codeDirectory, value);
        }

        private string _modelDirectory;
        public string ModelDirectory
        {
            get => _modelDirectory;
            set => SetProperty(ref _modelDirectory, value);
        }

        private string _gameDirectory = string.Empty;
        public string GameDirectory
        {
            get => _gameDirectory;
            set => SetProperty(ref _gameDirectory, value);
        }

        private int _lastOpenedSettingTab;
        public int LastOpenedSettingTab
        {
            get => _lastOpenedSettingTab;
            set => SetProperty(ref _lastOpenedSettingTab, value);
        }

        private bool _isLoggerExpanded = true;
        public bool IsLoggerExpanded
        {
            get => _isLoggerExpanded;
            set => SetProperty(ref _isLoggerExpanded, value);
        }

        // Settings → General SeparatorExpander IsExpanded (persisted per profile)
        private bool _settingsAdvancedExpanded;
        public bool SettingsAdvancedExpanded
        {
            get => _settingsAdvancedExpanded;
            set => SetProperty(ref _settingsAdvancedExpanded, value);
        }

        private bool _settingsExportBehaviorExpanded;
        public bool SettingsExportBehaviorExpanded
        {
            get => _settingsExportBehaviorExpanded;
            set => SetProperty(ref _settingsExportBehaviorExpanded, value);
        }

        private bool _settingsMultiThreadingExpanded;
        public bool SettingsMultiThreadingExpanded
        {
            get => _settingsMultiThreadingExpanded;
            set => SetProperty(ref _settingsMultiThreadingExpanded, value);
        }

        private bool _settingsFortniteLiveExpanded;
        public bool SettingsFortniteLiveExpanded
        {
            get => _settingsFortniteLiveExpanded;
            set => SetProperty(ref _settingsFortniteLiveExpanded, value);
        }

        private GridLength _avalonImageSize = new (200);
        public GridLength AvalonImageSize
        {
            get => _avalonImageSize;
            set => SetProperty(ref _avalonImageSize, value);
        }

        private string _audioDeviceId;
        public string AudioDeviceId
        {
            get => _audioDeviceId;
            set => SetProperty(ref _audioDeviceId, value);
        }

        private float _audioPlayerVolume = 50.0F;
        public float AudioPlayerVolume
        {
            get => _audioPlayerVolume;
            set => SetProperty(ref _audioPlayerVolume, value);
        }

        private ELoadingMode _loadingMode = ELoadingMode.All;
        public ELoadingMode LoadingMode
        {
            get => _loadingMode;
            set => SetProperty(ref _loadingMode, value);
        }

        private DateTime _lastUpdateCheck = DateTime.MinValue;
        public DateTime LastUpdateCheck
        {
            get => _lastUpdateCheck;
            set => SetProperty(ref _lastUpdateCheck, value);
        }

        private DateTime _nextUpdateCheck = DateTime.Now;
        public DateTime NextUpdateCheck
        {
            get => _nextUpdateCheck;
            set => SetProperty(ref _nextUpdateCheck, value);
        }

        private bool _keepDirectoryStructure = true;
        public bool KeepDirectoryStructure
        {
            get => _keepDirectoryStructure;
            set => SetProperty(ref _keepDirectoryStructure, value);
        }

        private ESkipAlreadyExported _skipAlreadyExportedMode = ESkipAlreadyExported.ByName;
        public ESkipAlreadyExported SkipAlreadyExportedMode
        {
            get => _skipAlreadyExportedMode;
            set => SetProperty(ref _skipAlreadyExportedMode, value);
        }

        private bool _overwriteProtection;
        /// <summary>
        /// only relevant while SkipAlreadyExportedMode is Disabled (if skipping is on, existing files
        /// are never touched in the first place). Guards against clobbering files that came from a
        /// different game version/build (e.g. output directories set up wrong): if a destination file
        /// already exists and its size differs from the freshly generated content, the write is refused
        /// instead of silently overwriting it.
        /// </summary>
        public bool OverwriteProtection
        {
            get => _overwriteProtection;
            set => SetProperty(ref _overwriteProtection, value);
        }

        private bool _exportSmallestFilesFirst;
        public bool ExportSmallestFilesFirst
        {
            get => _exportSmallestFilesFirst;
            set => SetProperty(ref _exportSmallestFilesFirst, value);
        }

        private bool _groupExportLooseAssets = true;
        /// <summary>
        /// When true: loose images (png/jpg/bmp/svg) export with Textures;
        /// text/config/font types (ini, txt, ttf, …) export with Properties.
        /// When false: fonts stay raw-only (legacy warning on Properties export).
        /// </summary>
        public bool GroupExportLooseAssets
        {
            get => _groupExportLooseAssets;
            set => SetProperty(ref _groupExportLooseAssets, value);
        }

        /// <summary>
        /// Export Queue: collect every loaded archive file into one flat list, sort smallest→largest,
        /// and skip per-folder success console logs. Default on.
        /// </summary>
        private bool _exportQueueFlatArchiveExport = true;
        public bool ExportQueueFlatArchiveExport
        {
            get => _exportQueueFlatArchiveExport;
            set => SetProperty(ref _exportQueueFlatArchiveExport, value);
        }

        /// <summary>
        /// Export Queue: skip <c>.umap</c> packages while a queue run is active.
        /// </summary>
        private bool _exportQueueExcludeUmap;
        public bool ExportQueueExcludeUmap
        {
            get => _exportQueueExcludeUmap;
            set => SetProperty(ref _exportQueueExcludeUmap, value);
        }

        /// <summary>
        /// Export Queue: when true, skip assets under <see cref="ExportQueueIgnoredFolders"/>.
        /// </summary>
        private bool _exportQueueExcludeDirectories;
        public bool ExportQueueExcludeDirectories
        {
            get => _exportQueueExcludeDirectories;
            set => SetProperty(ref _exportQueueExcludeDirectories, value);
        }

        /// <summary>
        /// Pasteable list of folder path prefixes to skip during Export Queue runs
        /// (one per line, or comma/semicolon-separated). Matched against package Directory/Path.
        /// Saved with the active profile via settings autosave.
        /// </summary>
        private string _exportQueueIgnoredFolders = string.Empty;
        public string ExportQueueIgnoredFolders
        {
            get => _exportQueueIgnoredFolders;
            set => SetProperty(ref _exportQueueIgnoredFolders, value ?? string.Empty);
        }

        /// <summary>
        /// Export Queue: when true, skip packages whose FMDex class tags match
        /// <see cref="ExportQueueIgnoredClasses"/>.
        /// </summary>
        private bool _exportQueueExcludeClasses;
        public bool ExportQueueExcludeClasses
        {
            get => _exportQueueExcludeClasses;
            set => SetProperty(ref _exportQueueExcludeClasses, value);
        }

        /// <summary>
        /// Pasteable UE class names / fragments / FMDex aliases (model, tex, anim, …)
        /// to skip during Export Queue runs. Requires FMDex tags; unindexed packages are not skipped.
        /// </summary>
        private string _exportQueueIgnoredClasses = string.Empty;
        public string ExportQueueIgnoredClasses
        {
            get => _exportQueueIgnoredClasses;
            set => SetProperty(ref _exportQueueIgnoredClasses, value ?? string.Empty);
        }

        /// <summary>
        /// Export Queue: when true, run FMDex indexing on the queue's selected folders
        /// before export work starts (so class-exclusion tags exist). Default off.
        /// </summary>
        private bool _exportQueueIndexBeforeExport;
        public bool ExportQueueIndexBeforeExport
        {
            get => _exportQueueIndexBeforeExport;
            set => SetProperty(ref _exportQueueIndexBeforeExport, value);
        }

        private EMetadataExport _metadataExportMode = EMetadataExport.Disabled;
        public EMetadataExport MetadataExportMode
        {
            get => _metadataExportMode;
            set => SetProperty(ref _metadataExportMode, value);
        }

        /// <summary>
        /// Last-used Export Queue global export flags (checkbox combination). Defaults to AllAssets
        /// (Properties + Textures + Models + Animations + Audio), matching the old dropdown default.
        /// </summary>
        private EBulkType _exportQueueBulkType = EBulkType.AllAssets;
        public EBulkType ExportQueueBulkType
        {
            get => _exportQueueBulkType;
            set => SetProperty(ref _exportQueueBulkType, value);
        }

        private bool _autoExportTexturesWithModels;
        public bool AutoExportTexturesWithModels
        {
            get => _autoExportTexturesWithModels;
            set => SetProperty(ref _autoExportTexturesWithModels, value);
        }

        private bool _autoLoadAllFilesOnStartup;
        public bool AutoLoadAllFilesOnStartup
        {
            get => _autoLoadAllFilesOnStartup;
            set => SetProperty(ref _autoLoadAllFilesOnStartup, value);
        }

        private bool _autoLoadFortniteLiveOnStartup;
        /// <summary>
        /// When true, Fortnite LIVE downloads/registers CDN archives during startup.
        /// Off by default so launching with a LIVE profile does not hammer the network.
        /// Use Directory → Load Fortnite LIVE… when you want streams.
        /// </summary>
        public bool AutoLoadFortniteLiveOnStartup
        {
            get => _autoLoadFortniteLiveOnStartup;
            set => SetProperty(ref _autoLoadFortniteLiveOnStartup, value);
        }

        private bool _fortniteLiveIncludeUefn;
        /// <summary>
        /// When true, Fortnite LIVE also registers UEFN/Creative archives (slower startup).
        /// </summary>
        public bool FortniteLiveIncludeUefn
        {
            get => _fortniteLiveIncludeUefn;
            set => SetProperty(ref _fortniteLiveIncludeUefn, value);
        }

        private bool _convertUint64ToFloat;
        public bool ConvertUint64ToFloat
        {
            get => _convertUint64ToFloat;
            set => SetProperty(ref _convertUint64ToFloat, value);
        }

        private string _fmDexDirectory = string.Empty;
        /// <summary>
        /// Display path for FMDex output. Always <c>{install}/FMDex/{Profile}</c>
        /// (see <c>FMDexService.DirectoryPath</c>); not tied to Raw Data / Output directories.
        /// </summary>
        public string FMDexDirectory
        {
            get => _fmDexDirectory;
            set => SetProperty(ref _fmDexDirectory, value);
        }

        private string _fmDexActiveFile = string.Empty;
        /// <summary>Full path of the active FMDex file (<c>*_FMDex.json.br</c> preferred; plain/legacy still loads).</summary>
        public string FMDexActiveFile
        {
            get => _fmDexActiveFile;
            set => SetProperty(ref _fmDexActiveFile, value);
        }

        private bool _autoIndexUnindexedOnLoad = true;
        /// <summary>When opening a package missing from FMDex, append its export class tags automatically.</summary>
        public bool AutoIndexUnindexedOnLoad
        {
            get => _autoIndexUnindexedOnLoad;
            set => SetProperty(ref _autoIndexUnindexedOnLoad, value);
        }

        private string _fmDexGame = string.Empty;
        /// <summary>Game/project name written into active FMDex (e.g. PioneerGame). Manual entry wins over auto-detect.</summary>
        public string FMDexGame
        {
            get => _fmDexGame;
            set => SetProperty(ref _fmDexGame, value ?? string.Empty);
        }

        private string _fmDexBuild = string.Empty;
        /// <summary>Build identifier written into active FMDex (e.g. CL1299607-…-BK4338 from BuildInfo/manifest.json).</summary>
        public string FMDexBuild
        {
            get => _fmDexBuild;
            set => SetProperty(ref _fmDexBuild, value ?? string.Empty);
        }

        private bool _useConcurrentWorkers = true;
        /// <summary>
        /// When true, folder / Export Queue exports use <see cref="ExportMaxThreads"/> workers.
        /// When false, export degree of parallelism is forced to 1 (single worker).
        /// Default true preserves prior multi-worker behavior.
        /// </summary>
        public bool UseConcurrentWorkers
        {
            get => _useConcurrentWorkers;
            set => SetProperty(ref _useConcurrentWorkers, value);
        }

        private int _exportMaxThreads;
        /// <summary>
        /// Max concurrent workers for folder / Export Queue asset exports (UI: Export Max Concurrent Workers).
        /// 0 = auto (usable cores = ProcessorCount − ReservedCpuCores, min 1).
        /// Ignored when <see cref="UseConcurrentWorkers"/> is false.
        /// </summary>
        public int ExportMaxThreads
        {
            get => _exportMaxThreads;
            set => SetProperty(ref _exportMaxThreads, Math.Max(0, value));
        }

        private int _imageExportWorkerBias;
        /// <summary>
        /// Additional workers for image/texture or model export (UI: Add Workers for Image/Models).
        /// Added to base export DOP when the bulk type includes <c>EBulkType.Textures</c>
        /// or <c>EBulkType.Meshes</c> (texture-only, models, AllAssets, or any batch with those set).
        /// 0 = none; max +16. Still capped at usable cores (ProcessorCount − ReservedCpuCores).
        /// Ignored when <see cref="UseConcurrentWorkers"/> is false.
        /// </summary>
        public int ImageExportWorkerBias
        {
            get => _imageExportWorkerBias;
            set => SetProperty(ref _imageExportWorkerBias, Math.Clamp(value, 0, 16));
        }

        private int _largeFileExportWorkers = 2;
        /// <summary>
        /// Max concurrent workers for packages larger than 10MB and at most 100MB.
        /// Default 2. Clamped 1–32. Still capped by base export DOP / usable cores.
        /// Ignored when <see cref="UseConcurrentWorkers"/> is false.
        /// </summary>
        public int LargeFileExportWorkers
        {
            get => _largeFileExportWorkers;
            set => SetProperty(ref _largeFileExportWorkers, Math.Clamp(value, 1, 32));
        }

        private int _hugeFileExportWorkers = 1;
        /// <summary>
        /// Max concurrent workers for packages larger than 100MB.
        /// Default 1. Clamped 1–32. Still capped by base export DOP / usable cores.
        /// Ignored when <see cref="UseConcurrentWorkers"/> is false.
        /// </summary>
        public int HugeFileExportWorkers
        {
            get => _hugeFileExportWorkers;
            set => SetProperty(ref _hugeFileExportWorkers, Math.Clamp(value, 1, 32));
        }

        private int _reservedCpuCores = 2;
        /// <summary>
        /// Logical CPU threads (hyperthreads) excluded from FModel via process affinity (0–4, default 2).
        /// Keeps CPUs free for other apps; distinct from <see cref="ExportMaxThreads"/>.
        /// 0 disables affinity restriction. UI label: Reserved CPU Threads.
        /// </summary>
        public int ReservedCpuCores
        {
            get => _reservedCpuCores;
            set
            {
                var clamped = Math.Clamp(value, 0, 4);
                if (!SetProperty(ref _reservedCpuCores, clamped))
                    return;
                // Defer off the binding/layout pass — sync Apply during Settings template
                // teardown contributed to WM_SIZE re-entrancy crashes.
                ScheduleAffinityApply();
            }
        }

        private static void ScheduleAffinityApply()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted)
            {
                try { CpuAffinity.Apply(force: true); }
                catch (Exception ex) { Log.Debug(ex, "[CpuAffinity] Apply failed (no dispatcher)"); }
                return;
            }

            dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                try { CpuAffinity.Apply(force: true); }
                catch (Exception ex) { Log.Debug(ex, "[CpuAffinity] deferred Apply failed"); }
            });
        }

        private bool _enableMemoryGuard = true;
        /// <summary>
        /// When true, bulk export workers stall acquiring new packages if process working set
        /// (or critically low system available RAM) exceeds soft/hard % of total physical memory.
        /// </summary>
        public bool EnableMemoryGuard
        {
            get => _enableMemoryGuard;
            set => SetProperty(ref _enableMemoryGuard, value);
        }

        private int _memorySoftTargetPercent = 55;
        /// <summary>
        /// Soft target: % of total physical RAM. Above this, new export work is paused until
        /// working set drops below target − 5% (hysteresis). Default 55.
        /// </summary>
        public int MemorySoftTargetPercent
        {
            get => _memorySoftTargetPercent;
            set
            {
                var soft = Math.Clamp(value, 10, 95);
                if (!SetProperty(ref _memorySoftTargetPercent, soft))
                    return;
                // Avoid nested PropertyChanged during the same binding update.
                if (_memoryHardCapPercent < soft + 5)
                {
                    var hard = soft + 5;
                    var dispatcher = Application.Current?.Dispatcher;
                    if (dispatcher != null && dispatcher.CheckAccess())
                        dispatcher.BeginInvoke(DispatcherPriority.Background, () => MemoryHardCapPercent = hard);
                    else
                        MemoryHardCapPercent = hard;
                }
            }
        }

        private int _memoryHardCapPercent = 85;
        /// <summary>
        /// Hard cap: % of total physical RAM. Above this (or when system available RAM is critical),
        /// stop issuing new export work; while recovering allow at most one package until under soft resume.
        /// Default 85. Must stay above soft target.
        /// </summary>
        public int MemoryHardCapPercent
        {
            get => _memoryHardCapPercent;
            set
            {
                var soft = MemorySoftTargetPercent;
                SetProperty(ref _memoryHardCapPercent, Math.Clamp(value, soft + 5, 99));
            }
        }

        private bool _enableExportPerfLog;
        /// <summary>
        /// When true, write <c>Logs/FModel-ExportPerf-*.log</c> listing slow/inefficient exports
        /// (high score = small file that took long) plus large-file comparison rows.
        /// </summary>
        public bool EnableExportPerfLog
        {
            get => _enableExportPerfLog;
            set => SetProperty(ref _enableExportPerfLog, value);
        }

        private int _exportPerfLogMinMs = 250;
        /// <summary>Only record exports that take at least this many milliseconds (default 250).</summary>
        public int ExportPerfLogMinMs
        {
            get => _exportPerfLogMinMs;
            set => SetProperty(ref _exportPerfLogMinMs, Math.Clamp(value, 0, 600_000));
        }

        private int _fmDexMaxThreads;
        /// <summary>
        /// Max parallel workers for FMDex folder/selection indexing.
        /// 0 = auto (about 4× CPU count, min 32) for I/O-bound Theia/pak reads.
        /// </summary>
        public int FMDexMaxThreads
        {
            get => _fmDexMaxThreads;
            set => SetProperty(ref _fmDexMaxThreads, Math.Max(0, value));
        }

        private string _currentProfileName = "Default";
        public string CurrentProfileName
        {
            get => _currentProfileName;
            set => SetProperty(ref _currentProfileName, value);
        }

        private bool _showDecompileOption = false;
        public bool ShowDecompileOption
        {
            get => _showDecompileOption;
            set => SetProperty(ref _showDecompileOption, value);
        }

        private ECompressedAudio _compressedAudioMode = ECompressedAudio.PlayDecompressed;
        public ECompressedAudio CompressedAudioMode
        {
            get => _compressedAudioMode;
            set => SetProperty(ref _compressedAudioMode, value);
        }

        private EAesReload _aesReload = EAesReload.OncePerDay;
        public EAesReload AesReload
        {
            get => _aesReload;
            set => SetProperty(ref _aesReload, value);
        }

        private ELanguage _assetLanguage = ELanguage.English;
        public ELanguage AssetLanguage
        {
            get => _assetLanguage;
            set => SetProperty(ref _assetLanguage, value);
        }

        private EIconStyle _cosmeticStyle = EIconStyle.Default;
        public EIconStyle CosmeticStyle
        {
            get => _cosmeticStyle;
            set => SetProperty(ref _cosmeticStyle, value);
        }

        private bool _cosmeticDisplayAsset;
        public bool CosmeticDisplayAsset
        {
            get => _cosmeticDisplayAsset;
            set => SetProperty(ref _cosmeticDisplayAsset, value);
        }

        private int _imageMergerMargin = 5;
        public int ImageMergerMargin
        {
            get => _imageMergerMargin;
            set => SetProperty(ref _imageMergerMargin, value);
        }

        private bool _readScriptData;
        public bool ReadScriptData
        {
            get => _readScriptData;
            set => SetProperty(ref _readScriptData, value);
        }

        private bool _readShaderMaps;
        public bool ReadShaderMaps
        {
            get => _readShaderMaps;
            set => SetProperty(ref _readShaderMaps, value);
        }

        private bool _convertAudioOnBulkExport;
        public bool ConvertAudioOnBulkExport
        {
            get => _convertAudioOnBulkExport;
            set => SetProperty(ref _convertAudioOnBulkExport, value);
        }

        private bool _decompileLua;
        public bool DecompileLua
        {
            get => _decompileLua;
            set => SetProperty(ref _decompileLua, value);
        }

        [JsonIgnore]
        public EUnluacMode UnluacMode
        {
            get => UnluacFlags.HasFlag(EUnluacFlags.Disassemble) ? EUnluacMode.Disassemble : EUnluacMode.Decompile;
            set
            {
                var withoutMode = UnluacFlags & ~(EUnluacFlags.Decompile | EUnluacFlags.Disassemble);
                var modeFlag = value == EUnluacMode.Disassemble ? EUnluacFlags.Disassemble : EUnluacFlags.Decompile;
                UnluacFlags = withoutMode | modeFlag;
            }
        }

        private EUnluacFlags _unluacFlags = EUnluacFlags.Decompile;
        public EUnluacFlags UnluacFlags
        {
            get => _unluacFlags;
            set
            {
                if (!SetProperty(ref _unluacFlags, value)) return;
                RaisePropertyChanged(nameof(UnluacMode));
            }
        }

        private EJsonHighlightTheme _jsonHighlightTheme;
        public EJsonHighlightTheme JsonHighlightTheme
        {
            get => _jsonHighlightTheme;
            set => SetProperty(ref _jsonHighlightTheme, value);
        }

        private IDictionary<string, DirectorySettings> _perDirectory = new Dictionary<string, DirectorySettings>();
        public IDictionary<string, DirectorySettings> PerDirectory
        {
            get => _perDirectory;
            set => SetProperty(ref _perDirectory, value);
        }

        [JsonIgnore]
        public DirectorySettings CurrentDir { get; set; }

        /// <summary>
        /// TO DELETEEEEEEEEEEEEE
        /// </summary>
        private IDictionary<string, GameSelectorViewModel.DetectedGame> _manualGames = new Dictionary<string, GameSelectorViewModel.DetectedGame>();
        public IDictionary<string, GameSelectorViewModel.DetectedGame> ManualGames
        {
            get => _manualGames;
            set => SetProperty(ref _manualGames, value);
        }

        private AuthResponse _lastAuthResponse = new() {AccessToken = "", ExpiresAt = DateTime.Now};
        public AuthResponse LastAuthResponse
        {
            get => _lastAuthResponse;
            set => SetProperty(ref _lastAuthResponse, value);
        }

        private Hotkey _dirLeftTab = new(Key.A);
        public Hotkey DirLeftTab
        {
            get => _dirLeftTab;
            set => SetProperty(ref _dirLeftTab, value);
        }

        private Hotkey _dirRightTab = new(Key.D);
        public Hotkey DirRightTab
        {
            get => _dirRightTab;
            set => SetProperty(ref _dirRightTab, value);
        }

        private Hotkey _switchAssetExplorer = new(Key.Z);
        public Hotkey SwitchAssetExplorer
        {
            get => _switchAssetExplorer;
            set => SetProperty(ref _switchAssetExplorer, value);
        }

        private Hotkey _assetLeftTab = new(Key.Q);
        public Hotkey AssetLeftTab
        {
            get => _assetLeftTab;
            set => SetProperty(ref _assetLeftTab, value);
        }

        private Hotkey _assetRightTab = new(Key.E);
        public Hotkey AssetRightTab
        {
            get => _assetRightTab;
            set => SetProperty(ref _assetRightTab, value);
        }

        private Hotkey _assetAddTab = new(Key.T, ModifierKeys.Control);
        public Hotkey AssetAddTab
        {
            get => _assetAddTab;
            set => SetProperty(ref _assetAddTab, value);
        }

        private Hotkey _assetRemoveTab = new(Key.W, ModifierKeys.Control);
        public Hotkey AssetRemoveTab
        {
            get => _assetRemoveTab;
            set => SetProperty(ref _assetRemoveTab, value);
        }

        private Hotkey _addAudio = new(Key.N, ModifierKeys.Control);
        public Hotkey AddAudio
        {
            get => _addAudio;
            set => SetProperty(ref _addAudio, value);
        }

        private Hotkey _playPauseAudio = new(Key.K);
        public Hotkey PlayPauseAudio
        {
            get => _playPauseAudio;
            set => SetProperty(ref _playPauseAudio, value);
        }

        private Hotkey _previousAudio = new(Key.J);
        public Hotkey PreviousAudio
        {
            get => _previousAudio;
            set => SetProperty(ref _previousAudio, value);
        }

        private Hotkey _nextAudio = new(Key.L);
        public Hotkey NextAudio
        {
            get => _nextAudio;
            set => SetProperty(ref _nextAudio, value);
        }

        private EMeshFormat _meshExportFormat = EMeshFormat.UEFormat;
        public EMeshFormat MeshExportFormat
        {
            get => _meshExportFormat;
            set => SetProperty(ref _meshExportFormat, value);
        }

        private ENaniteMeshFormat _naniteMeshExportFormat = ENaniteMeshFormat.OnlyNaniteLOD;
        public ENaniteMeshFormat NaniteMeshExportFormat
        {
            get => _naniteMeshExportFormat;
            set => SetProperty(ref _naniteMeshExportFormat, value);
        }

        private EMaterialFormat _materialExportFormat = EMaterialFormat.FirstLayer;
        public EMaterialFormat MaterialExportFormat
        {
            get => _materialExportFormat;
            set => SetProperty(ref _materialExportFormat, value);
        }

        private ETextureFormat _textureExportFormat = ETextureFormat.Png;
        public ETextureFormat TextureExportFormat
        {
            get => _textureExportFormat;
            set => SetProperty(ref _textureExportFormat, value);
        }

        private ESocketFormat _socketExportFormat = ESocketFormat.Bone;
        public ESocketFormat SocketExportFormat
        {
            get => _socketExportFormat;
            set => SetProperty(ref _socketExportFormat, value);
        }

        private EFileCompressionFormat _compressionFormat = EFileCompressionFormat.ZSTD;
        public EFileCompressionFormat CompressionFormat
        {
            get => _compressionFormat;
            set => SetProperty(ref _compressionFormat, value);
        }

        private ELodFormat _lodExportFormat = ELodFormat.FirstLod;
        public ELodFormat LodExportFormat
        {
            get => _lodExportFormat;
            set => SetProperty(ref _lodExportFormat, value);
        }

        private bool _showSkybox = true;
        public bool ShowSkybox
        {
            get => _showSkybox;
            set => SetProperty(ref _showSkybox, value);
        }

        private bool _showGrid = true;
        public bool ShowGrid
        {
            get => _showGrid;
            set => SetProperty(ref _showGrid, value);
        }

        private bool _animateWithRotationOnly;
        public bool AnimateWithRotationOnly
        {
            get => _animateWithRotationOnly;
            set => SetProperty(ref _animateWithRotationOnly, value);
        }

        private Camera.WorldMode _cameraMode = Camera.WorldMode.Arcball;
        public Camera.WorldMode CameraMode
        {
            get => _cameraMode;
            set => SetProperty(ref _cameraMode, value);
        }

        private int _previewMaxTextureSize = 1024;
        public int PreviewMaxTextureSize
        {
            get => _previewMaxTextureSize;
            set => SetProperty(ref _previewMaxTextureSize, value);
        }

        private bool _previewStaticMeshes = true;
        public bool PreviewStaticMeshes
        {
            get => _previewStaticMeshes;
            set => SetProperty(ref _previewStaticMeshes, value);
        }

        private bool _previewSkeletalMeshes = true;
        public bool PreviewSkeletalMeshes
        {
            get => _previewSkeletalMeshes;
            set => SetProperty(ref _previewSkeletalMeshes, value);
        }

        private bool _previewAnimations = true;
        public bool PreviewAnimations
        {
            get => _previewAnimations;
            set => SetProperty(ref _previewAnimations, value);
        }

        private bool _previewMaterials = true;
        public bool PreviewMaterials
        {
            get => _previewMaterials;
            set => SetProperty(ref _previewMaterials, value);
        }

        private bool _previewWorlds = true;
        public bool PreviewWorlds
        {
            get => _previewWorlds;
            set => SetProperty(ref _previewWorlds, value);
        }

        private bool _saveMorphTargets = true;
        public bool SaveMorphTargets
        {
            get => _saveMorphTargets;
            set => SetProperty(ref _saveMorphTargets, value);
        }

        private bool _saveEmbeddedMaterials = true;
        public bool SaveEmbeddedMaterials
        {
            get => _saveEmbeddedMaterials;
            set => SetProperty(ref _saveEmbeddedMaterials, value);
        }

        private bool _saveSkeletonAsMesh;
        public bool SaveSkeletonAsMesh
        {
            get => _saveSkeletonAsMesh;
            set => SetProperty(ref _saveSkeletonAsMesh, value);
        }

        private bool _saveHdrTexturesAsHdr = true;
        public bool SaveHdrTexturesAsHdr
        {
            get => _saveHdrTexturesAsHdr;
            set => SetProperty(ref _saveHdrTexturesAsHdr, value);
        }

        private bool _featurePreviewNewAssetExplorer = true;
        public bool FeaturePreviewNewAssetExplorer
        {
            get => _featurePreviewNewAssetExplorer;
            set => SetProperty(ref _featurePreviewNewAssetExplorer, value);
        }

        private bool _previewTexturesAssetExplorer = true;
        public bool PreviewTexturesAssetExplorer
        {
            get => _previewTexturesAssetExplorer;
            set => SetProperty(ref _previewTexturesAssetExplorer, value);
        }

        private DiffCheckerSettings _diffChecker = new();
        public DiffCheckerSettings DiffChecker
        {
            get => _diffChecker ??= new DiffCheckerSettings();
            set => SetProperty(ref _diffChecker, value ?? new DiffCheckerSettings());
        }
    }

    /// <summary>Persisted Diff Checker window options (AppSettings.json).</summary>
    public sealed class DiffCheckerSettings
    {
        public bool UseManualMount { get; set; }
        public bool ShareCryptoAndEngine { get; set; } = true;
        public string OldProfile { get; set; }
        public string NewProfile { get; set; }
        public string OldPakFolder { get; set; } = "";
        public string NewPakFolder { get; set; } = "";
        public string SharedAesKey { get; set; } = "";
        public string SharedMappingPath { get; set; } = "";
        public EGame SharedUeVersion { get; set; } = EGame.GAME_UE4_LATEST;
        public string OldAesKey { get; set; } = "";
        public string OldMappingPath { get; set; } = "";
        public EGame OldUeVersion { get; set; } = EGame.GAME_UE4_LATEST;
        public string NewAesKey { get; set; } = "";
        public string NewMappingPath { get; set; } = "";
        public EGame NewUeVersion { get; set; } = EGame.GAME_UE4_LATEST;
        public string ExportRoot { get; set; } = "";
        public bool ExportProperties { get; set; } = true;
        public bool ExportRaw { get; set; }
        public bool ExportTextures { get; set; }
        public bool ExportModels { get; set; }
        public bool ExportAudio { get; set; }
        public bool ExportRemoved { get; set; }
        public bool PopulateExplorer { get; set; } = true;
        public bool WriteDiffLogFile { get; set; } = true;
    }
}
