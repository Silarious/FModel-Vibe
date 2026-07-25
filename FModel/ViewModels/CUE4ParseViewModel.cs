using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using AdonisUI.Controls;
using CUE4Parse;
using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.GameTypes.Aion2.Objects;
using CUE4Parse.GameTypes.AoC.Objects;
using CUE4Parse.GameTypes.ArcRaiders.Encryption.Theia;
using CUE4Parse.GameTypes.AshEchoes.FileProvider;
using CUE4Parse.GameTypes.Borderlands3.Assets.Exports;
using CUE4Parse.GameTypes.Borderlands4.Assets.Exports;
using CUE4Parse.GameTypes.Borderlands4.Wwise;
using CUE4Parse.GameTypes.DFHO.Assets.Objects;
using CUE4Parse.GameTypes.HonorOfKings.FileProvider;
using CUE4Parse.GameTypes.KRD.Assets.Exports;
using CUE4Parse.GameTypes.LegoBatman.Assets;
using CUE4Parse.GameTypes.RocoKingdomWorld.Assets.Objects;
using CUE4Parse.GameTypes.SMG.UE4.Assets.Exports.Wwise;
using CUE4Parse.GameTypes.SquareEnix.UE4.Assets.Exports;
using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.AssetRegistry;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.CriWare;
using CUE4Parse.UE4.Assets.Exports.Fmod;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.Sound;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Exports.Verse;
using CUE4Parse.UE4.Assets.Exports.Wwise;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.BinaryConfig;
using CUE4Parse.UE4.CriWare;
using CUE4Parse.UE4.CriWare.Readers;
using CUE4Parse.UE4.FMod;
using CUE4Parse.UE4.GameFeatures;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.IO.Objects;
using CUE4Parse.UE4.Localization;
using CUE4Parse.UE4.Lua.unluac;
using CUE4Parse.UE4.Objects.Core.Serialization;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Objects.UObject.Editor;
using CUE4Parse.UE4.Oodle.Objects;
using CUE4Parse.UE4.Pak.Objects;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Shaders;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.Wwise;
using CUE4Parse.Utils;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Sounds;
using CUE4Parse.MappingsProvider.Jmap;
using CUE4Parse.MappingsProvider.Usmap;
using EpicManifestParser;
using EpicManifestParser.UE;
using FModel.Creator;
using FModel.Extensions;
using FModel.FMDex;
using FModel.Framework;
using FModel.Services;
using FModel.Settings;
using FModel.Views;
using FModel.Views.Resources.Controls;
using FModel.Views.Snooper;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using Serilog;
using SkiaSharp;
using Svg.Skia;
using UE4Config.Parsing;
using Application = System.Windows.Application;
using FGuid = CUE4Parse.UE4.Objects.Core.Misc.FGuid;

namespace FModel.ViewModels;

public class CUE4ParseViewModel : ViewModel
{
    private ThreadWorkerViewModel _threadWorkerView => ApplicationService.ThreadWorkerView;
    private ApiEndpointViewModel _apiEndpointView => ApplicationService.ApiEndpointView;
    private readonly Regex _fnLiveRegex = new(@"^FortniteGame[/\\]Content[/\\]Paks[/\\]",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly HttpClient _chunkClient = CreateChunkClient();

    /// <summary>Last Fortnite LIVE build manifest (for "Save LIVE Archives to Disk").</summary>
    private FBuildPatchAppManifest _fortniteLiveManifest;

    private static HttpClient CreateChunkClient()
    {
        var client = ManifestParseOptions.CreateDefaultClient();
        // CreateDefaultClient often uses InfiniteTimeSpan — a bad CDN mirror then hangs forever.
        if (client.Timeout == Timeout.InfiniteTimeSpan || client.Timeout > TimeSpan.FromMinutes(3))
            client.Timeout = TimeSpan.FromMinutes(2);
        return client;
    }

    public bool HasFortniteLiveManifest => _fortniteLiveManifest != null;

    private bool _modelIsOverwritingMaterial;
    public bool ModelIsOverwritingMaterial
    {
        get => _modelIsOverwritingMaterial;
        set => SetProperty(ref _modelIsOverwritingMaterial, value);
    }

    private bool _modelIsWaitingAnimation;
    public bool ModelIsWaitingAnimation
    {
        get => _modelIsWaitingAnimation;
        set => SetProperty(ref _modelIsWaitingAnimation, value);
    }

    public bool IsSnooperOpen => _snooper is { Exists: true, IsVisible: true };
    private Snooper _snooper;
    public Snooper SnooperViewer
    {
        get
        {
            if (_snooper != null) return _snooper;

            return Application.Current.Dispatcher.Invoke(delegate
            {
                var scale = ImGuiController.GetDpiScale();
                var htz = Snooper.GetMaxRefreshFrequency();
                return _snooper = new Snooper(
                    new GameWindowSettings { UpdateFrequency = htz },
                    new NativeWindowSettings
                    {
                        ClientSize = new OpenTK.Mathematics.Vector2i(
                            Convert.ToInt32(SystemParameters.MaximizedPrimaryScreenWidth * .75 * scale),
                            Convert.ToInt32(SystemParameters.MaximizedPrimaryScreenHeight * .85 * scale)),
                        NumberOfSamples = Constants.SAMPLES_COUNT,
                        WindowBorder = WindowBorder.Resizable,
                        Flags = ContextFlags.ForwardCompatible,
                        Profile = ContextProfile.Core,
                        Vsync = VSyncMode.Adaptive,
                        APIVersion = new Version(4, 6),
                        StartVisible = false,
                        StartFocused = false,
                        Title = "3D Viewer"
                    });
            });
        }
    }

    public AbstractVfsFileProvider Provider { get; }
    public GameDirectoryViewModel GameDirectory { get; }
    public AssetsFolderViewModel AssetsFolder { get; }
    public SearchViewModel SearchVm { get; }
    public SearchViewModel RefVm { get; }
    public TabControlViewModel TabControl { get; }
    public ConfigIni IoStoreOnDemand { get; }
    private Lazy<WwiseProvider> _wwiseProviderLazy;
    public WwiseProvider WwiseProvider => _wwiseProviderLazy.Value;
    private Lazy<FModProvider> _fmodProviderLazy;
    public FModProvider FmodProvider => _fmodProviderLazy?.Value;
    private Lazy<CriWareProvider> _criWareProviderLazy;
    public CriWareProvider CriWareProvider => _criWareProviderLazy?.Value;
    public ConcurrentBag<string> UnknownExtensions = [];

    public int ExportedCount;
    public int FailedExportCount;
    public int SkippedExportCount;
    public int ProtectedExportCount;

    public CUE4ParseViewModel()
    {
        var currentDir = UserSettings.Default.CurrentDir
            ?? throw new InvalidOperationException("CurrentDir is not set");
        // LIVE triggers must stay opaque tokens (never Path.GetFullPath → …\publish\valorant-live.manifest).
        var gameDirectory = ProfileManager.CanonicalizeGameDirectory(currentDir.GameDirectory ?? "");
        if (!string.Equals(currentDir.GameDirectory, gameDirectory, StringComparison.Ordinal))
            currentDir.GameDirectory = gameDirectory;
        if (!string.Equals(UserSettings.Default.GameDirectory, gameDirectory, StringComparison.Ordinal))
            UserSettings.Default.GameDirectory = gameDirectory;

        var versioning = currentDir.Versioning ?? new VersioningSettings();
        var versionContainer = new VersionContainer(
            game: currentDir.UeVersion, platform: currentDir.TexturePlatform,
            customVersions: new FCustomVersionContainer(versioning.CustomVersions),
            optionOverrides: versioning.Options,
            mapStructTypesOverrides: versioning.MapStructTypes);
        var pathComparer = StringComparer.OrdinalIgnoreCase;

        switch (gameDirectory)
        {
            case Constants._FN_LIVE_TRIGGER:
            {
                Provider = new StreamedFileProvider("FortniteLive", versionContainer, pathComparer);
                break;
            }
            case Constants._VAL_LIVE_TRIGGER:
            {
                Provider = new StreamedFileProvider("ValorantLive", versionContainer, pathComparer);
                break;
            }
            default:
            {
                var project = gameDirectory.SubstringBeforeLast(gameDirectory.Contains("eFootball") ? "\\pak" : "\\Content").SubstringAfterLast("\\");
                Provider = project switch
                {
                    "StateOfDecay2" => new DefaultFileProvider(new DirectoryInfo(gameDirectory),
                    [
                        new(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\\StateOfDecay2\\Saved\\Paks"),
                        new(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\\StateOfDecay2\\Saved\\DisabledPaks")
                    ], SearchOption.AllDirectories, versionContainer, pathComparer),
                    "eFootball" => new DefaultFileProvider(new DirectoryInfo(gameDirectory),
                    [
                        new(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) + "\\KONAMI\\eFootball\\ST\\Download")
                    ], SearchOption.AllDirectories, versionContainer, pathComparer),
                    "DeadByDaylight" => new DefaultFileProvider(new DirectoryInfo(gameDirectory),
                    [
                        new(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\\DeadByDaylight\\Saved\\PersistentDownloadDir\\DynamicContent")
                    ], SearchOption.AllDirectories, versionContainer, pathComparer),
                    _ when versionContainer.Game is EGame.GAME_AshEchoes => new AEDefaultFileProvider(gameDirectory, SearchOption.AllDirectories, versionContainer, pathComparer),
                    _ when versionContainer.Game is EGame.GAME_BlackStigma => new DefaultFileProvider(gameDirectory, SearchOption.AllDirectories, versionContainer, StringComparer.Ordinal),
                    _ when versionContainer.Game is EGame.GAME_HonorofKingsWorld => new HoKWDefaultFileProvider(gameDirectory, SearchOption.AllDirectories, versionContainer, pathComparer),
                    // Arc Raiders UE enum OR any folder with Theia metadat0 siblings (e.g. Marvel Tokon / MTFS).
                    _ when versionContainer.Game is EGame.GAME_ArcRaiders || TheiaPakDecryptor.DirectoryHasTheiaMeta(gameDirectory)
                        => CreateTheiaAwareProvider(gameDirectory, versionContainer, pathComparer),
                    _ => CreateDefaultProviderMaybeWarn(gameDirectory, versionContainer, pathComparer)
                };

                break;
            }
        }

        Provider.ReadScriptData = UserSettings.Default.ReadScriptData;
        Provider.ReadShaderMaps = UserSettings.Default.ReadShaderMaps;
        Provider.ReadNaniteData = true;

        GameDirectory = new GameDirectoryViewModel();
        AssetsFolder = new AssetsFolderViewModel();
        SearchVm = new SearchViewModel();
        RefVm = new SearchViewModel();
        TabControl = new TabControlViewModel();
        IoStoreOnDemand = new ConfigIni(nameof(IoStoreOnDemand));
    }

    public async Task Initialize()
    {
        await _threadWorkerView.Begin(cancellationToken =>
        {
            Provider.OnDemandOptions = new IoStoreOnDemandOptions
            {
                ChunkHostUri = new Uri("https://egdownload.fastly-edge.com/", UriKind.Absolute),
                ChunkCacheDirectory = Directory.CreateDirectory(Path.Combine(UserSettings.Default.OutputDirectory, ".data")),
                DownloaderClient = _chunkClient
            };

            switch (Provider)
            {
                case StreamedFileProvider p:
                    // Prefer LiveGame; also accept Canonical GameDirectory so a mismatched LiveGame
                    // string can never leave Fortnite/VALORANT LIVE with zero registered paks.
                    var liveKind = p.LiveGame;
                    if (string.IsNullOrWhiteSpace(liveKind))
                    {
                        var gd = ProfileManager.CanonicalizeGameDirectory(UserSettings.Default.GameDirectory ?? "");
                        liveKind = gd switch
                        {
                            Constants._FN_LIVE_TRIGGER => "FortniteLive",
                            Constants._VAL_LIVE_TRIGGER => "ValorantLive",
                            _ => liveKind
                        };
                    }

                    switch (liveKind)
                    {
                        case "FortniteLive":
                        {
                            if (!UserSettings.Default.AutoLoadFortniteLiveOnStartup)
                            {
                                Log.Information(
                                    "Fortnite LIVE: skipping auto-load (Settings → Auto Load Fortnite LIVE on Startup is off). Use Directory → Load Fortnite LIVE…");
                                FLogger.Append(ELog.Information, () =>
                                    FLogger.Text(
                                        "Fortnite LIVE profile ready — use Directory → Load Fortnite LIVE… to download/register archives (or enable auto-load in Settings).",
                                        Constants.WHITE, true));
                                break;
                            }

                            RegisterFortniteLiveFromCdn(p, cancellationToken);
                            break;
                        }
                        case "ValorantLive":
                        {
                            var manifest = _apiEndpointView.ValorantApi.GetManifest(cancellationToken);
                            if (manifest == null)
                            {
                                throw new Exception(
                                    "Could not load latest Valorant LIVE manifest (API returned an error / 404). " +
                                    "The public valorant-api.com/fmodel endpoint appears offline — switch to a local VALORANT install profile, " +
                                    "or create one via Profile Selector → Make New Profile pointing at ShooterGame\\Content\\Paks.");
                            }

                            Parallel.ForEach(manifest.Paks, pak =>
                            {
                                p.RegisterVfs(pak.GetFullName(), [pak.GetStream(manifest)]);
                            });

                            FLogger.Append(ELog.Information, () =>
                                FLogger.Text($"Valorant '{manifest.Header.GameVersion}' has been loaded successfully", Constants.WHITE, true));
                            break;
                        }
                        default:
                            Log.Warning("StreamedFileProvider LiveGame '{LiveGame}' is not FortniteLive/ValorantLive — no LIVE archives registered", p.LiveGame);
                            break;
                    }

                    break;
                case DefaultFileProvider:
                {
                    var ioStoreOnDemandPath = Path.Combine(UserSettings.Default.GameDirectory, "..\\..\\..\\Cloud\\IoStoreOnDemand.ini");
                    if (File.Exists(ioStoreOnDemandPath))
                    {
                        using var s = new StreamReader(ioStoreOnDemandPath);
                        IoStoreOnDemand.Read(s);
                    }
                    break;
                }
            }

            Provider.Initialize();
            GameDirectory.AddLooseFiles(Provider.LooseFileCount);
            _wwiseProviderLazy = new Lazy<WwiseProvider>(() => new WwiseProvider(Provider, UserSettings.Default.GameDirectory));
            _fmodProviderLazy = new Lazy<FModProvider>(() => new FModProvider(Provider, UserSettings.Default.GameDirectory));
            _criWareProviderLazy = new Lazy<CriWareProvider>(() => new CriWareProvider(Provider, UserSettings.Default.GameDirectory));
            Log.Information($"{Provider.Versions.Game} ({Provider.Versions.Platform}) | Archives: x{Provider.UnloadedVfs.Count} | AES: x{Provider.RequiredKeys.Count} | Loose Files: x{Provider.Files.Count}");
        });
    }

    /// <summary>
    /// Download Fortnite LIVE build manifest from Epic CDN and register pak/utoc archives.
    /// Used on startup when auto-load is enabled, or via Directory → Load Fortnite LIVE….
    /// </summary>
    public async Task LoadFortniteLiveArchivesAsync()
    {
        if (Provider is not StreamedFileProvider p)
            throw new InvalidOperationException("Not a streamed (LIVE) provider.");

        var liveKind = p.LiveGame;
        if (string.IsNullOrWhiteSpace(liveKind) ||
            !liveKind.Equals("FortniteLive", StringComparison.OrdinalIgnoreCase))
        {
            var gd = ProfileManager.CanonicalizeGameDirectory(UserSettings.Default.GameDirectory ?? "");
            if (gd != Constants._FN_LIVE_TRIGGER)
                throw new InvalidOperationException("Switch to the Fortnite LIVE profile first.");
            liveKind = "FortniteLive";
        }

        if (_fortniteLiveManifest != null)
            throw new InvalidOperationException("Fortnite LIVE archives are already loaded in this session.");

        await _threadWorkerView.Begin(cancellationToken =>
        {
            RegisterFortniteLiveFromCdn(p, cancellationToken);
        });
    }

    /// <summary>
    /// Fetch Epic launcher + build manifests and register Fortnite LIVE VFS archives (and optional UEFN).
    /// Must run on the thread-worker (blocking HTTP).
    /// </summary>
    private void RegisterFortniteLiveFromCdn(StreamedFileProvider p, CancellationToken cancellationToken)
    {
        Log.Information("Fortnite LIVE: requesting launcher manifest info…");
        var manifestInfo = _apiEndpointView.EpicApi.GetManifest(cancellationToken);
        if (manifestInfo is null)
        {
            throw new FileLoadException("Could not load latest Fortnite manifest, you may have to switch to your local installation.");
        }

        var cacheDir = Directory.CreateDirectory(Path.Combine(UserSettings.Default.OutputDirectory, ".data")).FullName;
        var manifestOptions = new ManifestParseOptions
        {
            ChunkCacheDirectory = cacheDir,
            ManifestCacheDirectory = cacheDir,
            ChunkBaseUrl = "https://egdownload.fastly-edge.com/Builds/Fortnite/CloudDir/",
            Decompressor = Compression.Decompressor,
            Client = _chunkClient,
            // Match upstream FModel — false is the tested path for LIVE chunk reuse.
            CacheChunksAsIs = false
        };

        var startTs = Stopwatch.GetTimestamp();
        FBuildPatchAppManifest manifest;

        // Epic returns several CDN mirrors. Akamai often 403s; cloudfront is newer.
        // Prefer Fastly (known-good with f_token). Never block forever on a bad mirror.
        try
        {
            ApplicationService.ApplicationView?.Status.UpdateStatusLabel(
                "Downloading LIVE build manifest…", "Loading");
            Log.Information("Fortnite LIVE: downloading build manifest (preferring egdownload.fastly-edge.com)…");

            using var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            downloadCts.CancelAfter(TimeSpan.FromMinutes(2));

            (manifest, _) = manifestInfo.DownloadAndParseAsync(manifestOptions,
                cancellationToken: downloadCts.Token,
                elementDownloadPredicate: static x =>
                    x.Uri.Host.Equals("egdownload.fastly-edge.com", StringComparison.OrdinalIgnoreCase)
            ).ConfigureAwait(false).GetAwaiter().GetResult();

            _fortniteLiveManifest = manifest;
            Log.Information("Fortnite LIVE: build manifest parsed OK");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                "Timed out downloading the Fortnite LIVE build manifest from Epic CDN (egdownload.fastly-edge.com). " +
                "Check network/firewall, or switch to a local Fortnite install.");
        }
        catch (HttpRequestException ex)
        {
            Log.Error("Failed to download manifest ({ManifestUri})", ex.Data["ManifestUri"]?.ToString() ?? "");
            throw;
        }

        if (manifest.TryFindFile("Cloud/IoStoreOnDemand.ini", out var ioStoreOnDemandFile))
        {
            IoStoreOnDemand.Read(new StreamReader(ioStoreOnDemandFile.GetStream()));
        }

        ApplicationService.ApplicationView?.Status.UpdateStatusLabel(
            "Registering LIVE archives…", "Loading");
        Log.Information("Fortnite LIVE: registering VFS archives…");

        // Match upstream FModel: pak/utoc in parallel; .uondemandtoc must be
        // materialized via EpicManifestParser's parallel chunk path first.
        // Sync RegisterVfs on those TOCs downloads chunks one-by-one and can
        // stall several minutes on the last few small files.
        RegisterFortniteLiveArchives(p, manifest, cancellationToken);

        ApplicationService.ApplicationView?.Status.UpdateStatusLabel(
            $"{p.UnloadedVfs.Count} archives (Fortnite)", "Registered");
        Log.Information("Fortnite LIVE: registered {Count} archives", p.UnloadedVfs.Count);

        // UEFN / Creative is optional (off by default) — it roughly doubles LIVE register time.
        if (UserSettings.Default.FortniteLiveIncludeUefn)
        {
            try
            {
                var manifests = _apiEndpointView.DillyApi.GetManifests(cancellationToken);
                var studio = manifests?.FirstOrDefault(x => x.AppName == "Fortnite_Studio");
                if (studio != null && !string.IsNullOrWhiteSpace(studio.DownloadUrl))
                {
                    ApplicationService.ApplicationView?.Status.UpdateStatusLabel(
                        "UEFN manifest…", "Registered");

                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
                    var manifestBytes = client.GetByteArrayAsync(studio.DownloadUrl, cancellationToken)
                        .ConfigureAwait(false).GetAwaiter().GetResult();

                    var uefnManifest = FBuildPatchAppManifest.Deserialize(manifestBytes, manifestOptions);
                    RegisterFortniteLiveArchives(p, uefnManifest, cancellationToken);
                }
                else
                {
                    Log.Warning("Fortnite_Studio manifest missing from Dilly — skipping UEFN LIVE archives");
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "UEFN LIVE registration failed; continuing with main Fortnite archives");
                FLogger.Append(ELog.Warning, () =>
                    FLogger.Text("UEFN LIVE archives skipped (download/register failed)", Constants.WHITE, true));
            }
        }
        else
        {
            Log.Information("Fortnite LIVE: skipping UEFN (enable Settings → Fortnite LIVE Include UEFN to load Creative archives)");
        }

        var elapsedTime = Stopwatch.GetElapsedTime(startTs);
        FLogger.Append(ELog.Information, () =>
            FLogger.Text($"Fortnite [LIVE] has been loaded successfully in {elapsedTime.TotalMilliseconds:F1}ms", Constants.WHITE, true));
    }

    /// <summary>
    /// Register Fortnite LIVE pak/utoc archives, and materialize <c>.uondemandtoc</c> files via
    /// EpicManifestParser's parallel chunk download before handing them to CUE4Parse.
    /// Sync-streaming those TOCs downloads BuildPatch chunks one-by-one and can stall minutes
    /// on the last few (often small) files — matching the slow path vs upstream FModel.
    /// </summary>
    private void RegisterFortniteLiveArchives(StreamedFileProvider provider, FBuildPatchAppManifest manifest,
        CancellationToken cancellationToken)
    {
        var archiveFiles = manifest.Files.Where(x =>
            _fnLiveRegex.IsMatch(x.FileName) &&
            (x.FileName.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) ||
             x.FileName.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase) ||
             x.FileName.EndsWith(".uondemandtoc", StringComparison.OrdinalIgnoreCase))).ToList();
        var parallelOptions = new ParallelOptions { CancellationToken = cancellationToken };

        Parallel.ForEach(
            archiveFiles.Where(x => !x.FileName.EndsWith(".uondemandtoc", StringComparison.OrdinalIgnoreCase)),
            parallelOptions,
            fileManifest =>
            {
                provider.RegisterVfs(fileManifest.FileName, [fileManifest.GetStream()],
                    it => new FRandomAccessStreamArchive(it, manifest.FindFile(it)!.GetStream(), provider.Versions));
            });

        var onDemand = archiveFiles
            .Where(x => x.FileName.EndsWith(".uondemandtoc", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (onDemand.Count == 0)
            return;

        Log.Information("Fortnite LIVE: materializing {Count} on-demand TOC(s) via parallel chunk download…", onDemand.Count);
        ApplicationService.ApplicationView?.Status.UpdateStatusLabel(
            $"{onDemand.Count} on-demand TOC(s)…", "Registered");

        foreach (var fileManifest in onDemand)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();
            using var stream = fileManifest.GetStream();
            // concurrency: 8 — EpicManifestParser parallel chunk fetch (not sync one-chunk-at-a-time).
            var data = stream.SaveBytesAsync(8, cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();
            using var archive = new FByteArchive(fileManifest.FileName, data, provider.Versions);
            provider.RegisterVfs(new IoChunkToc(archive));
            Log.Information("Fortnite LIVE: on-demand TOC {Name} ({Size:N0} bytes) in {Ms:F0}ms",
                Path.GetFileName(fileManifest.FileName), data.Length, sw.Elapsed.TotalMilliseconds);
        }
    }

    /// <summary>
    /// Download (or copy from chunk cache) Fortnite LIVE pak/utoc/ucas files into a local folder,
    /// then create/update a <c>Fortnite (Cached)</c> profile so the next launch uses DefaultFileProvider.
    /// </summary>
    /// <returns>Local Paks directory path, or null if cancelled/unavailable.</returns>
    public async Task<string> CacheFortniteLiveArchivesToDiskAsync(string rootDirectory, IProgress<(int done, int total, string name)> progress = null, CancellationToken cancellationToken = default)
    {
        if (_fortniteLiveManifest == null)
            throw new InvalidOperationException("Load Fortnite LIVE first in this session, then save archives to disk.");

        var files = _fortniteLiveManifest.Files
            .Where(x => _fnLiveRegex.IsMatch(x.FileName))
            .OrderBy(x => x.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (files.Count == 0)
            throw new InvalidOperationException("No FortniteGame/Content/Paks files found in the LIVE manifest.");

        Directory.CreateDirectory(rootDirectory);
        var total = files.Count;
        var done = 0;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = file.FileName.Replace('/', Path.DirectorySeparatorChar);
            var destPath = Path.Combine(rootDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

            progress?.Report((done, total, file.FileName));

            if (File.Exists(destPath) && new FileInfo(destPath).Length == (long)file.FileSize)
            {
                done++;
                continue;
            }

            var stream = file.GetStream();
            await stream.SaveFileAsync(destPath, cancellationToken: cancellationToken).ConfigureAwait(false);
            done++;
            progress?.Report((done, total, file.FileName));
        }

        var paksDir = Path.Combine(rootDirectory, "FortniteGame", "Content", "Paks");
        if (!Directory.Exists(paksDir))
            throw new DirectoryNotFoundException($"Expected paks folder was not created: {paksDir}");

        var cached = DirectorySettings.Fresh("Fortnite (Cached)", paksDir, EGame.GAME_UE5_8, manual: true);
        // Carry over AES from the LIVE session so the cached profile mounts immediately.
        if (UserSettings.Default.CurrentDir?.AesKeys != null)
            cached.AesKeys = UserSettings.Default.CurrentDir.AesKeys;

        ProfileManager.EnsureFromDirectory(cached, "Fortnite (Cached)");
        Log.Information("Cached {Count} LIVE archives under {Dir}; profile Fortnite (Cached) → {Paks}", total, rootDirectory, paksDir);
        return paksDir;
    }

    public long EstimateFortniteLiveCacheBytes()
    {
        if (_fortniteLiveManifest == null) return 0;
        return _fortniteLiveManifest.Files
            .Where(x => _fnLiveRegex.IsMatch(x.FileName))
            .Sum(x => (long)x.FileSize);
    }

    /// <summary>
    /// load virtual files system from GameDirectory
    /// </summary>
    /// <returns></returns>
    public void LoadVfs(IEnumerable<KeyValuePair<FGuid, FAesKey>> aesKeys)
    {
        Provider.SubmitKeys(aesKeys);
        Provider.PostMount();

        var aesMax = Provider.RequiredKeys.Count + Provider.Keys.Count;
        var archiveMax = Provider.UnloadedVfs.Count + Provider.MountedVfs.Count;
        Log.Information($"Project: {Provider.ProjectName} | Mounted: {Provider.MountedVfs.Count}/{archiveMax} | AES: {Provider.Keys.Count}/{aesMax} | Files: x{Provider.Files.Count}");

        // Switch FMDex off any previous game's index (persisted Active File / Settings).
        FMDexService.Instance.BindToProvider(Provider);

        if (UserSettings.Default.AutoLoadAllFilesOnStartup)
            AutoLoadAllFiles();
    }

    /// <summary>
    /// populates the assets explorer with every recognized file, equivalent to pressing "Load" with the
    /// "All" loading mode selected; used right after pak/archive files have been recognized when the
    /// "Auto Load All Files on Startup" setting is enabled
    /// </summary>
    private void AutoLoadAllFiles()
    {
        if (ShouldBlockAutoLoadForMissingAes())
        {
            Application.Current.Dispatcher.Invoke(() =>
                FLogger.Append(ELog.Warning, () =>
                    FLogger.Text(
                        "Auto Load All Files was blocked: archives are encrypted and no AES key is set. Add a key in Profiles or AES Manager, then load manually.",
                        Constants.WHITE, true)));
            return;
        }

        if (Provider.Files.Count == 0) return;

        Application.Current.Dispatcher.Invoke(() =>
        {
            AssetsFolder.Folders.Clear();
            SearchVm.SearchResults.Clear();
            ApplicationService.ApplicationView.SelectedLeftTabIndex = 1; // folders tab
            ApplicationService.ApplicationView.IsAssetsExplorerVisible = true;
        });

        var entries = new List<GameFile>();
        foreach (var asset in Provider.Files.Values)
        {
            if (asset.IsUePackagePayload) continue;
            entries.Add(asset);
        }

        ApplicationService.ApplicationView.Status.UpdateStatusLabel($"{entries.Count:### ### ###} Packages");
        AssetsFolder.BulkPopulate(entries);
    }

    /// <summary>
    /// Auto-load is useless until encrypted archives can be mounted; ignore the toggle until a key exists.
    /// </summary>
    private bool ShouldBlockAutoLoadForMissingAes()
    {
        var encryptedPending = Provider.RequiredKeys.Count > 0
                               || Provider.UnloadedVfs.Any(r => r.IsEncrypted);
        if (!encryptedPending) return false;

        var aes = UserSettings.Default.CurrentDir?.AesKeys;
        if (aes == null) return true;

        var mainKey = Helper.FixKey(aes.MainKey);
        return mainKey.Length != 66 && !aes.HasDynamicKeys;
    }

    public void ClearProvider()
    {
        if (Provider == null) return;

        AssetsFolder.Folders.Clear();
        SearchVm.SearchResults.Clear();
        Helper.CloseWindow<AdonisWindow>("Search For Packages");
        Provider.UnloadNonStreamedVfs();
        GC.Collect();
    }

    public async Task RefreshAes()
    {
        // game directory dependent, we don't have the provider game name yet since we don't have aes keys
        // except when this comes from the AES Manager
        if (!UserSettings.IsEndpointValid(EEndpointType.Aes, out var endpoint))
            return;

        await _threadWorkerView.Begin(cancellationToken =>
        {
            // deprecated values
            if (endpoint.Url == "https://fortnitecentral.genxgames.gg/api/v1/aes") endpoint.Url = "https://uedb.dev/svc/api/v1/fortnite/aes";

            var aes = _apiEndpointView.DynamicApi.GetAesKeys(cancellationToken, endpoint.Url, endpoint.Path);
            if (aes is not { IsValid: true }) return;

            UserSettings.Default.CurrentDir.AesKeys = aes;
        });
    }

    public async Task InitInformation()
    {
        await _threadWorkerView.Begin(cancellationToken =>
        {
            var info = _apiEndpointView.FModelApi.GetNews(cancellationToken, Provider.ProjectName);
            if (info == null) return;

            FLogger.Append(ELog.None, () =>
            {
                for (var i = 0; i < info.Messages.Length; i++)
                {
                    FLogger.Text(info.Messages[i], info.Colors[i], bool.Parse(info.NewLines[i]));
                }
            });
        });
    }

    private ITypeMappingsProvider SelectMappingsProvider(string path)
    {
        if (path.EndsWith(".jmap.gz", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jmap", StringComparison.OrdinalIgnoreCase))
        {
            return new JmapTypeMappingsProvider(path);
        }

        return new FileUsmapTypeMappingsProvider(path);
    }

    public Task InitMappings(bool force = false)
    {
        if (!UserSettings.IsEndpointValid(EEndpointType.Mapping, out var endpoint))
        {
            Provider.MappingsContainer = null;
            return Task.CompletedTask;
        }

        return Task.Run(() =>
        {
            var l = ELog.Information;
            if (endpoint.Overwrite && File.Exists(endpoint.FilePath))
            {
                Provider.MappingsContainer = SelectMappingsProvider(endpoint.FilePath);
            }
            else if (endpoint.IsValid)
            {
                // deprecated values
                if (endpoint.Path == "$.[?(@.meta.compressionMethod=='Oodle')].['url','fileName']") endpoint.Path = "$.[0].['url','fileName']";
                if (endpoint.Url == "https://fortnitecentral.genxgames.gg/api/v1/mappings")
                {
                    endpoint.Url = "https://uedb.dev/svc/api/v1/fortnite/mappings";
                    endpoint.Path = "$.mappings.ZStandard";
                }

                var mappingsFolder = Path.Combine(UserSettings.Default.OutputDirectory, ".data");
                var mappings = _apiEndpointView.DynamicApi.GetMappings(CancellationToken.None, endpoint.Url, endpoint.Path);
                if (mappings is { Length: > 0 })
                {
                    foreach (var mapping in mappings)
                    {
                        if (!mapping.IsValid) continue;

                        var mappingPath = Path.Combine(mappingsFolder, mapping.FileName);
                        if (force || !File.Exists(mappingPath) || new FileInfo(mappingPath).Length == 0)
                        {
                            _apiEndpointView.DownloadFile(mapping.Url, mappingPath);
                        }

                        Provider.MappingsContainer = SelectMappingsProvider(mappingPath);
                        break;
                    }
                }

                if (Provider.MappingsContainer == null)
                {
                    var latestUsmaps = new DirectoryInfo(mappingsFolder).GetFiles("*_oo.usmap");
                    if (latestUsmaps.Length <= 0) return;

                    var latestUsmapInfo = latestUsmaps.OrderBy(f => f.LastWriteTime).Last();
                    Provider.MappingsContainer = new FileUsmapTypeMappingsProvider(latestUsmapInfo.FullName);
                    l = ELog.Warning;
                }
            }

            if (Provider.MappingsContainer is FileUsmapTypeMappingsProvider m)
            {
                Log.Information($"Mappings pulled from '{m.FileName}'");
                FLogger.Append(l, () => FLogger.Text($"Mappings pulled from '{m.FileName}'", Constants.WHITE, true));
            }
        });
    }

    public Task VerifyConsoleVariables()
    {
        if (Provider.Versions["StripAdditiveRefPose"])
        {
            FLogger.Append(ELog.Warning, () =>
                FLogger.Text("Additive animations have their reference pose stripped, which will lead to inaccurate preview and export", Constants.WHITE, true));
        }

        if (Provider.Versions.Game is EGame.GAME_UE4_LATEST or EGame.GAME_UE5_LATEST && !Provider.ProjectName.Equals("FortniteGame", StringComparison.OrdinalIgnoreCase)) // ignore fortnite globally
        {
            FLogger.Append(ELog.Warning, () =>
                FLogger.Text($"Experimental UE version selected, likely unsuitable for '{Provider.GameDisplayName ?? Provider.ProjectName}'", Constants.WHITE, true));
        }

        return Task.CompletedTask;
    }

    public Task VerifyOnDemandArchives()
    {
        // only local fortnite
        if (Provider is not DefaultFileProvider || !Provider.ProjectName.Equals("FortniteGame", StringComparison.OrdinalIgnoreCase))
            return Task.CompletedTask;

        // scuffed but working
        var persistentDownloadDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FortniteGame/Saved/PersistentDownloadDir");
        var iasFileInfo = new FileInfo(Path.Combine(persistentDownloadDir, "ias", "ias.cache.0"));
        if (!iasFileInfo.Exists || iasFileInfo.Length == 0)
            return Task.CompletedTask;

        return Task.Run(async () =>
        {
            var inst = new List<InstructionToken>();
            IoStoreOnDemand.FindPropertyInstructions("Endpoint", "TocPath", inst);
            if (inst.Count <= 0) return;

            var ioStoreOnDemandPath = Path.Combine(UserSettings.Default.GameDirectory, "..\\..\\..\\Cloud", inst[0].Value.SubstringAfterLast("/").SubstringBefore("\""));
            if (!File.Exists(ioStoreOnDemandPath)) return;

            await Provider.RegisterVfsAsync(new IoChunkToc(ioStoreOnDemandPath, Provider.Versions));
            var onDemandCount = await Provider.MountAsync();
            FLogger.Append(ELog.Information, () =>
                FLogger.Text($"{onDemandCount} on-demand archive{(onDemandCount > 1 ? "s" : "")} streamed via epicgames.com", Constants.WHITE, true));
        });
    }

    public int LocalizedResourcesCount { get; set; }
    public bool LocalResourcesDone { get; set; }
    public bool HotfixedResourcesDone { get; set; }

    public async Task LoadLocalizedResources()
    {
        var snapshot = LocalizedResourcesCount;
        await Task.WhenAll(LoadGameLocalizedResources(), LoadHotfixedLocalizedResources()).ConfigureAwait(false);

        LocalizedResourcesCount = Provider.Internationalization.Count;
        if (snapshot != LocalizedResourcesCount)
        {
            FLogger.Append(ELog.Information, () =>
                FLogger.Text($"{LocalizedResourcesCount} localized resources loaded for '{UserSettings.Default.AssetLanguage.GetDescription()}'", Constants.WHITE, true));
            Utils.Typefaces = new Typefaces(this);
        }
    }

    private Task LoadGameLocalizedResources()
    {
        if (LocalResourcesDone) return Task.CompletedTask;
        return Task.Run(() =>
        {
            LocalResourcesDone = Provider.TryChangeCulture(Provider.GetLanguageCode(UserSettings.Default.AssetLanguage));
        });
    }

    private Task LoadHotfixedLocalizedResources()
    {
        if (!Provider.ProjectName.Equals("fortnitegame", StringComparison.OrdinalIgnoreCase) || HotfixedResourcesDone) return Task.CompletedTask;
        return Task.Run(() =>
        {
            var hotfixes = ApplicationService.ApiEndpointView.DillyApi.GetHotfixes(CancellationToken.None, Provider.GetLanguageCode(UserSettings.Default.AssetLanguage));
            if (hotfixes == null) return;

            Provider.Internationalization.Override(hotfixes);
            HotfixedResourcesDone = true;
        });
    }

    private int _virtualPathCount { get; set; }
    public Task LoadVirtualPaths()
    {
        if (_virtualPathCount > 0) return Task.CompletedTask;
        return Task.Run(() =>
        {
            _virtualPathCount = Provider.LoadVirtualPaths(UserSettings.Default.CurrentDir.UeVersion.GetVersion());
            if (_virtualPathCount > 0)
            {
                FLogger.Append(ELog.Information, () =>
                    FLogger.Text($"{_virtualPathCount} virtual paths loaded", Constants.WHITE, true));
            }
            else
            {
                FLogger.Append(ELog.Warning, () =>
                    FLogger.Text("Could not load virtual paths, plugin manifest may not exist", Constants.WHITE, true));
            }
        });
    }

    public void ExtractSelected(CancellationToken cancellationToken, IEnumerable<GameFile> assetItems)
    {
        foreach (var entry in assetItems)
        {
            Thread.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            Extract(cancellationToken, entry, TabControl.HasNoTabs);
        }
    }

    /// <summary>
    /// Provider with on-read Theia decrypt for <c>metadat0</c> sibling .meta files.
    /// Used for Arc Raiders and MTFS / Marvel Tokon (same layout, separate CONST8 profiles).
    /// </summary>
    private static AbstractVfsFileProvider CreateTheiaAwareProvider(
        string gameDirectory,
        VersionContainer versionContainer,
        StringComparer pathComparer)
    {
        // Profile (Arc vs Mtfs CONST8) is resolved inside the provider and logged there.
        Log.Information(
            "Theia-aware provider for {Dir} (UE={Game}) — on-read decrypt when .meta is present",
            gameDirectory, versionContainer.Game);
        return new ArcRaidersFileProvider(
            gameDirectory,
            SearchOption.AllDirectories,
            versionContainer,
            pathComparer);
    }

    [Obsolete("Use CreateTheiaAwareProvider")]
    private static AbstractVfsFileProvider CreateArcRaidersProvider(
        string gameDirectory,
        VersionContainer versionContainer,
        StringComparer pathComparer)
        => CreateTheiaAwareProvider(gameDirectory, versionContainer, pathComparer);

    private static AbstractVfsFileProvider CreateDefaultProviderMaybeWarn(
        string gameDirectory,
        VersionContainer versionContainer,
        StringComparer pathComparer)
    {
        // Belt-and-suspenders: even if the switch missed it, never open Theia packs as plaintext.
        if (TheiaPakDecryptor.DirectoryHasTheiaMeta(gameDirectory))
            return CreateTheiaAwareProvider(gameDirectory, versionContainer, pathComparer);

        WarnIfArcRaidersPathMismatch(gameDirectory, versionContainer.Game);
        return new DefaultFileProvider(gameDirectory, SearchOption.AllDirectories, versionContainer, pathComparer);
    }

    private static void WarnIfArcRaidersPathMismatch(string gameDirectory, EGame game)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory)) return;
        var looksPioneer =
            gameDirectory.Contains("PioneerGame", StringComparison.OrdinalIgnoreCase) ||
            gameDirectory.Contains("Arc Raiders", StringComparison.OrdinalIgnoreCase) ||
            gameDirectory.Contains("ArcRaiders", StringComparison.OrdinalIgnoreCase);
        if (!looksPioneer) return;

        var hasGlobal = File.Exists(Path.Combine(gameDirectory, "global.utoc")) ||
                        Directory.Exists(gameDirectory) &&
                        Directory.EnumerateFiles(gameDirectory, "global.utoc", SearchOption.AllDirectories).Any();
        if (!hasGlobal) return;

        Log.Warning(
            "Path looks like Arc Raiders ({Dir}) but UE version is {Game}. " +
            "Set UE version to Arc Raiders (Chinese/Tencent builds also need this — they have no Theia .meta).",
            gameDirectory, game);
    }

    /// <summary>
    /// order the given assets list according to the "export smallest files first" user setting
    /// (no-op, in original order, when the toggle is disabled)
    /// </summary>
    private static IEnumerable<GameFileViewModel> OrderForExport(IEnumerable<GameFileViewModel> assets)
        => UserSettings.Default.ExportSmallestFilesFirst ? assets.OrderBy(a => a.Asset.Size) : assets;

    private static IEnumerable<GameFile> OrderForExport(IEnumerable<GameFile> assets)
        => UserSettings.Default.ExportSmallestFilesFirst ? assets.OrderBy(a => a.Size) : assets;

    /// <param name="applyImageWorkerBias">
    /// When true and <see cref="UserSettings.ImageExportWorkerBias"/> &gt; 0, add that many
    /// workers to base DOP (texture/image or model/mesh batches). Final DOP is capped at usable cores.
    /// </param>
    private static int ResolveExportMaxThreads(bool applyImageWorkerBias = false)
    {
        if (!UserSettings.Default.UseConcurrentWorkers)
        {
            Log.Debug(
                "[ExportParallel] ResolveExportMaxThreads: UseConcurrentWorkers=false → effectiveDOP=1");
            return 1;
        }

        var configured = UserSettings.Default.ExportMaxThreads;
        var usable = CpuAffinity.GetUsableCoreCount();
        var n = configured;
        if (n <= 0)
            // Auto: use affinity-usable cores (ProcessorCount − ReservedCpuCores).
            n = usable;
        n = Math.Clamp(n, 1, 512);

        var bias = 0;
        if (applyImageWorkerBias)
        {
            bias = UserSettings.Default.ImageExportWorkerBias;
            if (bias > 0)
                n = Math.Min(n + bias, usable);
        }

        if (bias > 0)
        {
            Log.Debug(
                "[ExportParallel] ResolveExportMaxThreads: setting={Setting} (0=auto) imageBias=+{Bias} → effectiveDOP={Dop} usableCores={Usable} cores={Cores}",
                configured, bias, n, usable, Environment.ProcessorCount);
        }
        else
        {
            Log.Debug(
                "[ExportParallel] ResolveExportMaxThreads: setting={Setting} (0=auto) → effectiveDOP={Dop} usableCores={Usable} cores={Cores}",
                configured, n, usable, Environment.ProcessorCount);
        }
        return n;
    }

    /// <summary>Packages above this (and ≤ <see cref="HugeExportFileBytes"/>) use <see cref="UserSettings.LargeFileExportWorkers"/>.</summary>
    private const long LargeExportFileBytes = 10L * 1024 * 1024;

    /// <summary>Packages above this use <see cref="UserSettings.HugeFileExportWorkers"/>.</summary>
    private const long HugeExportFileBytes = 100L * 1024 * 1024;

    /// <summary>
    /// Bulk export: full DOP for files ≤ 10MB, then reduced DOP for &gt;10MB and &gt;100MB
    /// (see <see cref="UserSettings.LargeFileExportWorkers"/> / <see cref="UserSettings.HugeFileExportWorkers"/>).
    /// Prefer dedicated <see cref="TaskCreationOptions.LongRunning"/> threads (same pattern as FMDex)
    /// over <see cref="Parallel.ForEach"/> — ThreadPool hill-climbing often stalls around ~6–8 workers
    /// for mixed Theia I/O + decrypt + package load work.
    /// </summary>
    private static void RunExportParallel(GameFile[] items, CancellationToken cancellationToken, Action<GameFile> body,
        bool applyImageWorkerBias = false)
        => RunExportParallelPartitioned(items, cancellationToken, body, static e => e.Size, applyImageWorkerBias);

    private static void RunExportParallel(GameFileViewModel[] items, CancellationToken cancellationToken, Action<GameFileViewModel> body,
        bool applyImageWorkerBias = false)
        => RunExportParallelPartitioned(items, cancellationToken, body, static e => e.Asset.Size, applyImageWorkerBias);

    private static void RunExportParallelPartitioned<T>(
        T[] items, CancellationToken cancellationToken, Action<T> body, Func<T, long> getSize,
        bool applyImageWorkerBias = false)
    {
        if (items.Length == 0) return;

        // Sort before partitioning so "export smallest first" is honored inside each DOP batch
        // (callers also OrderForExport; this keeps the parallel path self-contained).
        if (UserSettings.Default.ExportSmallestFilesFirst)
        {
            Array.Sort(items, (a, b) => getSize(a).CompareTo(getSize(b)));
            Log.Information("[ExportParallel] ExportSmallestFilesFirst: queue ordered by GameFile.Size ascending");
        }

        var small = new List<T>(items.Length);
        var large = new List<T>();
        var huge = new List<T>();
        foreach (var item in items)
        {
            var size = getSize(item);
            if (size > HugeExportFileBytes)
                huge.Add(item);
            else if (size > LargeExportFileBytes)
                large.Add(item);
            else
                small.Add(item);
        }

        // Re-apply + verify OS mask before export workers start.
        CpuAffinity.Apply(force: true);

        var concurrent = UserSettings.Default.UseConcurrentWorkers;
        var configured = UserSettings.Default.ExportMaxThreads;
        var imageBias = applyImageWorkerBias && concurrent
            ? UserSettings.Default.ImageExportWorkerBias
            : 0;
        var fullDop = ResolveExportMaxThreads(applyImageWorkerBias);
        var largeDop = concurrent
            ? Math.Min(UserSettings.Default.LargeFileExportWorkers, fullDop)
            : 1;
        var hugeDop = concurrent
            ? Math.Min(UserSettings.Default.HugeFileExportWorkers, fullDop)
            : 1;
        var settingLabel = !concurrent
            ? "single"
            : configured <= 0 ? $"auto→{fullDop}" : configured.ToString();

        Log.Information(
            "[ExportParallel] concurrent={Concurrent} setting={Setting}{BiasPart} resolvedDOP={FullDop} usableCores={Usable} cores={Cores} items={Total} " +
            "(≤{SmallMaxMb}MB={SmallCount} DOP={SmallDop}; >{LargeMinMb}MB={LargeCount} DOP={LargeDop}; >{HugeMinMb}MB={HugeCount} DOP={HugeDop})",
            concurrent, settingLabel,
            imageBias > 0 ? $" imageBias=+{imageBias}" : "",
            fullDop, CpuAffinity.GetUsableCoreCount(), Environment.ProcessorCount, items.Length,
            LargeExportFileBytes / (1024 * 1024), small.Count, small.Count > 0 ? Math.Min(fullDop, small.Count) : 0,
            LargeExportFileBytes / (1024 * 1024), large.Count, large.Count > 0 ? Math.Min(largeDop, large.Count) : 0,
            HugeExportFileBytes / (1024 * 1024), huge.Count, huge.Count > 0 ? Math.Min(hugeDop, huge.Count) : 0);

        if (small.Count > 0)
            RunExportParallelCore(small.ToArray(), cancellationToken, body, Math.Min(fullDop, small.Count), imageBias);
        if (large.Count > 0)
            RunExportParallelCore(large.ToArray(), cancellationToken, body, Math.Min(largeDop, large.Count), imageBias);
        if (huge.Count > 0)
            RunExportParallelCore(huge.ToArray(), cancellationToken, body, Math.Min(hugeDop, huge.Count), imageBias);
    }

    /// <summary>
    /// Bulk export worker pool. Bumps ThreadPool min threads up to <paramref name="dop"/> only
    /// (never above Export Max Concurrent Workers). Logs effective DOP / peak concurrency as <c>[ExportParallel]</c>.
    /// </summary>
    private static void RunExportParallelCore<T>(T[] items, CancellationToken cancellationToken, Action<T> body, int dop,
        int imageBias = 0)
    {
        if (items.Length == 0 || dop <= 0) return;

        // Cap ThreadPool min at export DOP only. Raising to ProcessorCount*2 previously let
        // nested ThreadPool / Parallel work ignore Export Max Concurrent Workers in Task Manager.
        ThreadPool.GetMinThreads(out var prevMinWorkers, out var prevMinIo);
        ThreadPool.GetMaxThreads(out var maxWorkers, out var maxIo);
        var desiredMin = Math.Min(Math.Max(prevMinWorkers, dop), maxWorkers);
        ThreadPool.SetMinThreads(desiredMin, Math.Max(prevMinIo, Math.Min(desiredMin, maxIo)));
        ThreadPool.GetAvailableThreads(out var availWorkers, out var availIo);

        if (imageBias > 0)
        {
            Log.Information(
                "[ExportParallel] start items={Count} effectiveDOP={Dop} imageBias=+{Bias} cores={Cores} ThreadPool min={PrevMin}/{PrevIo}→{DesiredMin} (capped≤DOP) max={MaxW}/{MaxIo} available={AvailW}/{AvailIo} mode=LongRunning",
                items.Length, dop, imageBias, Environment.ProcessorCount,
                prevMinWorkers, prevMinIo, desiredMin, maxWorkers, maxIo, availWorkers, availIo);
        }
        else
        {
            Log.Information(
                "[ExportParallel] start items={Count} effectiveDOP={Dop} cores={Cores} ThreadPool min={PrevMin}/{PrevIo}→{DesiredMin} (capped≤DOP) max={MaxW}/{MaxIo} available={AvailW}/{AvailIo} mode=LongRunning",
                items.Length, dop, Environment.ProcessorCount,
                prevMinWorkers, prevMinIo, desiredMin, maxWorkers, maxIo, availWorkers, availIo);
        }

        var queue = new ConcurrentQueue<T>(items);
        var active = 0;
        var peak = 0;

        if (UserSettings.Default.EnableMemoryGuard)
        {
            Log.Information(
                "[MemGuard] enabled soft={Soft}% hard={Hard}% of total RAM (hysteresis resume at soft−5%)",
                UserSettings.Default.MemorySoftTargetPercent,
                UserSettings.Default.MemoryHardCapPercent);
        }

        var workers = new Task[dop];
        for (var w = 0; w < dop; w++)
        {
            workers[w] = Task.Factory.StartNew(() =>
            {
                // Belt-and-suspenders: pin LongRunning export threads to the process affinity mask.
                CpuAffinity.ApplyToCurrentThread();

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Stall before taking the next package when process WS / system avail exceed caps.
                    if (!MemoryGuard.WaitToAcquireWork(cancellationToken, out var hardSlot))
                        cancellationToken.ThrowIfCancellationRequested();

                    if (!queue.TryDequeue(out var item))
                    {
                        if (hardSlot)
                            MemoryGuard.ReleaseHardSlot();
                        break;
                    }

                    var now = Interlocked.Increment(ref active);
                    int snap;
                    while (now > (snap = Volatile.Read(ref peak)) &&
                           Interlocked.CompareExchange(ref peak, now, snap) != snap)
                    {
                    }

                    try
                    {
                        if (ExportPerfLog.IsEnabled)
                        {
                            ExportPerfLog.ReadProcessMemory(out var wsBefore, out var privBefore);
                            var gcBefore = GC.GetTotalMemory(false);
                            var sw = Stopwatch.StartNew();
                            try
                            {
                                body(item);
                            }
                            finally
                            {
                                sw.Stop();
                                ExportPerfLog.ReadProcessMemory(out var wsAfter, out var privAfter);
                                var gcAfter = GC.GetTotalMemory(false);
                                switch (item)
                                {
                                    case GameFile gf:
                                        ExportPerfLog.Record(gf, sw.ElapsedMilliseconds, wsBefore, wsAfter,
                                            privBefore, privAfter, gcBefore, gcAfter);
                                        break;
                                    case GameFileViewModel gvm:
                                        ExportPerfLog.Record(gvm, sw.ElapsedMilliseconds, wsBefore, wsAfter,
                                            privBefore, privAfter, gcBefore, gcAfter);
                                        break;
                                }
                            }
                        }
                        else
                        {
                            body(item);
                        }
                    }
                    finally
                    {
                        Interlocked.Decrement(ref active);
                        if (hardSlot)
                            MemoryGuard.ReleaseHardSlot();
                    }
                }
            }, cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        try
        {
            Task.WaitAll(workers, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // cancellation: workers stop on next dequeue / ThrowIfCancellationRequested
        }
        finally
        {
            ThreadPool.SetMinThreads(prevMinWorkers, prevMinIo);
            Log.Information(
                "[ExportParallel] done. PeakConcurrentWorkers={Peak} requestedDOP={Dop}",
                Volatile.Read(ref peak), dop);
            ExportPerfLog.FlushBatch($"parallel DOP={dop}", items.Length, dop);
        }
    }

    private void BulkFolder(CancellationToken cancellationToken, TreeItem folder, Action<GameFile> action,
        EBulkType bulk = EBulkType.None)
    {
        var assets = new List<GameFile>();
        CollectFolderAssets(folder, assets);
        if (ShouldExcludeUmapForQueue())
            assets.RemoveAll(static a => a.Extension.Equals("umap", StringComparison.OrdinalIgnoreCase));
        if (ShouldExcludeDirectoriesForQueue())
            assets.RemoveAll(IsUnderIgnoredQueueFolder);
        if (ShouldExcludeClassesForQueue())
            assets.RemoveAll(MatchesIgnoredQueueClass);
        var ordered = OrderForExport(assets).ToArray();
        var status = ApplicationService.ApplicationView.Status;
        status.BeginProgress(ordered.Length);
        var applyImageBias = HasFlag(bulk, EBulkType.Textures) || HasFlag(bulk, EBulkType.Meshes);
        try
        {
            RunExportParallel(ordered, cancellationToken, entry =>
            {
                try { action(entry); }
                catch { /* ignore per-asset failures in bulk */ }
                finally { status.IncrementProgress(); }
            }, applyImageBias);
        }
        finally
        {
            status.CompleteProgress();
        }
    }

    private static bool IsQueueRunActive()
        => ApplicationService.ApplicationView?.ExportQueue?.IsRunning == true;

    private static bool ShouldExcludeUmapForQueue()
        => UserSettings.Default.ExportQueueExcludeUmap && IsQueueRunActive();

    private static bool ShouldExcludeDirectoriesForQueue()
        => UserSettings.Default.ExportQueueExcludeDirectories && IsQueueRunActive();

    private static bool ShouldExcludeClassesForQueue()
        => UserSettings.Default.ExportQueueExcludeClasses && IsQueueRunActive();

    // Parsed ignore lists keyed by the raw settings string so we do not re-split
    // on every asset during BulkFolder / flat-archive / BulkExportEntries filters.
    private static string _ignoredFoldersCacheKey;
    private static string[] _ignoredFoldersCache = [];
    private static string _ignoredClassesCacheKey;
    private static string[] _ignoredClassesCache = [];

    /// <summary>Parse pasteable ignore list (newlines / commas / semicolons).</summary>
    private static string[] ParseIgnoredQueueFolders(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        return text
            .Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeQueueFolderPrefix)
            .Where(static s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] ParseIgnoredQueueClasses(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        return text
            .Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static s => s.Trim())
            .Where(static s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] GetIgnoredQueueFolderPrefixes()
    {
        var text = UserSettings.Default.ExportQueueIgnoredFolders ?? string.Empty;
        if (!string.Equals(_ignoredFoldersCacheKey, text, StringComparison.Ordinal))
        {
            _ignoredFoldersCacheKey = text;
            _ignoredFoldersCache = ParseIgnoredQueueFolders(text);
        }
        return _ignoredFoldersCache;
    }

    private static string[] GetIgnoredQueueClassPatterns()
    {
        var text = UserSettings.Default.ExportQueueIgnoredClasses ?? string.Empty;
        if (!string.Equals(_ignoredClassesCacheKey, text, StringComparison.Ordinal))
        {
            _ignoredClassesCacheKey = text;
            _ignoredClassesCache = ParseIgnoredQueueClasses(text);
        }
        return _ignoredClassesCache;
    }

    private static string NormalizeQueueFolderPrefix(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;
        var n = path.Replace('\\', '/').Trim().Trim('/');
        return n;
    }

    private static bool IsUnderIgnoredQueueFolder(GameFile asset)
    {
        var prefixes = GetIgnoredQueueFolderPrefixes();
        if (prefixes.Length == 0)
            return false;

        var directory = NormalizeQueueFolderPrefix(asset.Directory ?? string.Empty);
        var path = NormalizeQueueFolderPrefix(asset.Path ?? string.Empty);
        foreach (var prefix in prefixes)
        {
            if (directory.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                directory.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase) ||
                path.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// True when FMDex tags for this asset match any ignored class pattern / alias.
    /// Unindexed packages return false (not skipped).
    /// </summary>
    private static bool MatchesIgnoredQueueClass(GameFile asset)
    {
        var patterns = GetIgnoredQueueClassPatterns();
        if (patterns.Length == 0)
            return false;

        if (!FMDexService.Instance.TryGetEntry(asset, out var entry) ||
            entry?.Tags == null || entry.Tags.Count == 0)
            return false;

        foreach (var pattern in patterns)
        {
            if (FMDexTagAliases.IsMapFilter(pattern) && FMDexTagAliases.IsUMap(asset))
                return true;
            if (FMDexTagAliases.Matches(entry.Tags, pattern))
                return true;
        }

        return false;
    }

    /// <summary>True when this asset should be skipped for the active Export Queue run.</summary>
    private static bool ShouldSkipAssetForQueue(GameFile asset)
    {
        if (!IsQueueRunActive())
            return false;
        if (UserSettings.Default.ExportQueueExcludeUmap &&
            asset.Extension.Equals("umap", StringComparison.OrdinalIgnoreCase))
            return true;
        if (UserSettings.Default.ExportQueueExcludeDirectories && IsUnderIgnoredQueueFolder(asset))
            return true;
        if (UserSettings.Default.ExportQueueExcludeClasses && MatchesIgnoredQueueClass(asset))
            return true;
        return false;
    }

    /// <summary>Export/extract only assets that live directly on <paramref name="folder"/> (no child folders).</summary>
    public void ExportAssetsAtFolderLevel(CancellationToken cancellationToken, TreeItem folder, EBulkType bulktype)
    {
        var assets = OrderForExport(folder.AssetsList.Assets).ToArray();
        BulkExportEntries(cancellationToken, assets.Select(static a => a.Asset).ToArray(), bulktype);
    }

    /// <summary>
    /// Parallel export for an explicit list of packages (multi-select / directory groups).
    /// </summary>
    public void BulkExportEntries(CancellationToken cancellationToken, GameFile[] entries, EBulkType bulktype)
    {
        if (entries == null || entries.Length == 0) return;

        var flags = bulktype & ~EBulkType.Auto;
        IEnumerable<GameFile> source = entries;
        if (IsQueueRunActive())
            source = entries.Where(static a => !ShouldSkipAssetForQueue(a));
        var ordered = OrderForExport(source).ToArray();
        if (ordered.Length == 0) return;
        var status = ApplicationService.ApplicationView.Status;
        status.BeginProgress(ordered.Length);
        var applyImageBias = HasFlag(flags, EBulkType.Textures) || HasFlag(flags, EBulkType.Meshes);
        try
        {
            var extractFlags = flags & ~(EBulkType.Raw | EBulkType.Metadata);
            switch (flags)
            {
                case EBulkType.Raw:
                    RunExportParallel(ordered, cancellationToken, entry =>
                    {
                        try { ExportData(entry, false); }
                        finally { status.IncrementProgress(); }
                    });
                    break;
                case EBulkType.Metadata:
                    RunExportParallel(ordered, cancellationToken, entry =>
                    {
                        try
                        {
                            if (entry.IsUePackage)
                                ExportMetadata(entry, false);
                        }
                        finally { status.IncrementProgress(); }
                    });
                    break;
                default:
                    RunExportParallel(ordered, cancellationToken, entry =>
                    {
                        try
                        {
                            if (HasFlag(flags, EBulkType.Raw))
                                ExportData(entry, false);
                            if (HasFlag(flags, EBulkType.Metadata) && entry.IsUePackage)
                                ExportMetadata(entry, false);
                            if (extractFlags != EBulkType.None)
                                Extract(cancellationToken, entry, TabControl.HasNoTabs, extractFlags | (bulktype & EBulkType.Auto));
                        }
                        catch
                        {
                            // ignore
                        }
                        finally { status.IncrementProgress(); }
                    }, applyImageBias);
                    break;
            }
        }
        finally
        {
            status.CompleteProgress();
        }
    }

    public void ExportFolder(CancellationToken cancellationToken, TreeItem folder)
    {
        var assets = new List<GameFile>();
        CollectFolderAssets(folder, assets);
        var ordered = OrderForExport(assets).ToArray();
        var status = ApplicationService.ApplicationView.Status;
        status.BeginProgress(ordered.Length);
        try
        {
            RunExportParallel(ordered, cancellationToken, entry =>
            {
                try { ExportData(entry, false); }
                finally { status.IncrementProgress(); }
            });
        }
        finally
        {
            status.CompleteProgress();
        }
    }

    public void ExportMetadataFolder(CancellationToken cancellationToken, TreeItem folder)
    {
        var assets = new List<GameFile>();
        CollectFolderAssets(folder, assets);
        var ordered = OrderForExport(assets).ToArray();
        var status = ApplicationService.ApplicationView.Status;
        status.BeginProgress(ordered.Length);
        try
        {
            RunExportParallel(ordered, cancellationToken, entry =>
            {
                try
                {
                    if (entry.IsUePackage)
                        ExportMetadata(entry, false);
                }
                finally { status.IncrementProgress(); }
            });
        }
        finally
        {
            status.CompleteProgress();
        }
    }

    /// <summary>
    /// Runs a single bulk folder export (used by the Export Queue). Mirrors the dirType/folderAction/log
    /// logic in RightClickMenuCommand but is self-contained so queued steps can run back-to-back without
    /// going through the context menu's parameter parsing.
    /// </summary>
    public void RunFolderExport(CancellationToken cancellationToken, TreeItem folder, EBulkType bulktype)
    {
        var flags = bulktype & ~EBulkType.Auto;
        // Raw/Metadata use dedicated exporters; when combined with extractable flags, run both.
        var extractFlags = flags & ~(EBulkType.Raw | EBulkType.Metadata);
        var (dirType, filetype) = flags switch
        {
            EBulkType.Raw => (UserSettings.Default.RawDataDirectory, "files"),
            EBulkType.Properties => (UserSettings.Default.PropertiesDirectory, "json files"),
            EBulkType.Metadata => (UserSettings.Default.PropertiesDirectory, "metadata files"),
            EBulkType.Textures => (UserSettings.Default.TextureDirectory, "textures"),
            EBulkType.Meshes => (UserSettings.Default.ModelDirectory, "models"),
            EBulkType.Animations => (UserSettings.Default.ModelDirectory, "animations"),
            EBulkType.Audio => (UserSettings.Default.AudioDirectory, "audio files"),
            EBulkType.Code => (UserSettings.Default.CodeDirectory, "code files"),
            EBulkType.AllAssets => (UserSettings.Default.OutputDirectory, "json/textures/models/animations/audio"),
            _ when (flags & EBulkType.AllAssets) != 0
                => (UserSettings.Default.OutputDirectory, "combined exports"),
            // Combined checkbox flags (e.g. Properties|Textures) must not fall through to null.
            _ when extractFlags != EBulkType.None
                => (UserSettings.Default.OutputDirectory, "combined exports"),
            _ when (flags & EBulkType.Code) != 0
                => (UserSettings.Default.CodeDirectory, "code files"),
            _ when (flags & EBulkType.Raw) != 0
                => (UserSettings.Default.RawDataDirectory, "files"),
            _ when (flags & EBulkType.Metadata) != 0
                => (UserSettings.Default.PropertiesDirectory, "metadata files"),
            _ => (null, null),
        };
        if (string.IsNullOrEmpty(dirType)) return;
        Action<TreeItem> folderAction = flags switch
        {
            EBulkType.Raw => f => ExportFolder(cancellationToken, f),
            EBulkType.Metadata => f => ExportMetadataFolder(cancellationToken, f),
            _ when extractFlags != EBulkType.None && HasFlag(flags, EBulkType.Raw) && HasFlag(flags, EBulkType.Metadata) => f =>
            {
                ExportFolder(cancellationToken, f);
                ExportMetadataFolder(cancellationToken, f);
                ExtractFolder(cancellationToken, f, extractFlags | EBulkType.Auto);
            },
            _ when extractFlags != EBulkType.None && HasFlag(flags, EBulkType.Raw) => f =>
            {
                ExportFolder(cancellationToken, f);
                ExtractFolder(cancellationToken, f, extractFlags | EBulkType.Auto);
            },
            _ when extractFlags != EBulkType.None && HasFlag(flags, EBulkType.Metadata) => f =>
            {
                ExportMetadataFolder(cancellationToken, f);
                ExtractFolder(cancellationToken, f, extractFlags | EBulkType.Auto);
            },
            _ when HasFlag(flags, EBulkType.Raw) && HasFlag(flags, EBulkType.Metadata) => f =>
            {
                ExportFolder(cancellationToken, f);
                ExportMetadataFolder(cancellationToken, f);
            },
            _ => f => ExtractFolder(cancellationToken, f, bulktype | EBulkType.Auto),
        };

        var children = folder.Folders.ToArray();
        if (children.Length == 0)
        {
            Interlocked.Exchange(ref ExportedCount, 0);
            Interlocked.Exchange(ref FailedExportCount, 0);
            Interlocked.Exchange(ref SkippedExportCount, 0);
            folderAction(folder);
            LogQueueFolderExport(folder, dirType, filetype);
            return;
        }

        Interlocked.Exchange(ref ExportedCount, 0);
        Interlocked.Exchange(ref FailedExportCount, 0);
        Interlocked.Exchange(ref SkippedExportCount, 0);
        ExportAssetsAtFolderLevel(cancellationToken, folder, bulktype | EBulkType.Auto);
        if (ExportedCount > 0 || FailedExportCount > 0)
            LogQueueFolderExport(folder, dirType, filetype);

        foreach (var child in children)
        {
            Interlocked.Exchange(ref ExportedCount, 0);
            Interlocked.Exchange(ref FailedExportCount, 0);
            Interlocked.Exchange(ref SkippedExportCount, 0);
            folderAction(child);
            LogQueueFolderExport(child, dirType, filetype);
        }
    }

    private void LogQueueFolderExport(TreeItem folder, string dirType, string filetype)
    {
        var path = Path.Combine(
            dirType,
            UserSettings.Default.KeepDirectoryStructure ? folder.PathAtThisPoint : folder.Header).Replace('\\', '/');
        var elapsed = ApplicationService.ApplicationView.Status.LastLapElapsedText;
        var elapsedSuffix = string.IsNullOrEmpty(elapsed) ? string.Empty : $", {elapsed}";
        if (ExportedCount > 0)
        {
            FLogger.Append(ELog.Information, () =>
            {
                FLogger.Text($"[Queue] Successfully exported {ExportedCount} {filetype} from ", Constants.WHITE);
                FLogger.Link(folder.Header, Path.Exists(path) ? path : dirType, string.IsNullOrEmpty(elapsedSuffix));
                if (!string.IsNullOrEmpty(elapsedSuffix))
                    FLogger.Text(elapsedSuffix, Constants.WHITE, true);
            });
        }
        else if (FailedExportCount == 0)
        {
            FLogger.Append(ELog.Warning, () => FLogger.Text($"[Queue] Failed to find any {filetype} in {folder.Header}{elapsedSuffix}", Constants.WHITE, true));
        }
        else
        {
            FLogger.Append(ELog.Error, () => FLogger.Text($"[Queue] Failed to export {FailedExportCount} {filetype} from {folder.Header}{elapsedSuffix}", Constants.WHITE, true));
        }
    }

    /// <summary>
    /// Export Queue flat mode: every file from <see cref="Provider.Files"/> in one list,
    /// sorted smallest→largest, no per-folder success console logs.
    /// </summary>
    public void RunFlatArchiveExport(CancellationToken cancellationToken, EBulkType bulktype)
    {
        var flags = bulktype & ~EBulkType.Auto;
        var extractFlags = flags & ~(EBulkType.Raw | EBulkType.Metadata);
        var (dirType, filetype) = flags switch
        {
            EBulkType.Raw => (UserSettings.Default.RawDataDirectory, "files"),
            EBulkType.Properties => (UserSettings.Default.PropertiesDirectory, "json files"),
            EBulkType.Metadata => (UserSettings.Default.PropertiesDirectory, "metadata files"),
            EBulkType.Textures => (UserSettings.Default.TextureDirectory, "textures"),
            EBulkType.Meshes => (UserSettings.Default.ModelDirectory, "models"),
            EBulkType.Animations => (UserSettings.Default.ModelDirectory, "animations"),
            EBulkType.Audio => (UserSettings.Default.AudioDirectory, "audio files"),
            EBulkType.Code => (UserSettings.Default.CodeDirectory, "code files"),
            EBulkType.AllAssets => (UserSettings.Default.OutputDirectory, "json/textures/models/animations/audio"),
            _ when (flags & EBulkType.AllAssets) != 0
                => (UserSettings.Default.OutputDirectory, "combined exports"),
            _ when extractFlags != EBulkType.None
                => (UserSettings.Default.OutputDirectory, "combined exports"),
            _ when (flags & EBulkType.Code) != 0
                => (UserSettings.Default.CodeDirectory, "code files"),
            _ when (flags & EBulkType.Raw) != 0
                => (UserSettings.Default.RawDataDirectory, "files"),
            _ when (flags & EBulkType.Metadata) != 0
                => (UserSettings.Default.PropertiesDirectory, "metadata files"),
            _ => (null, null),
        };
        if (string.IsNullOrEmpty(dirType)) return;

        var assets = new List<GameFile>(Provider.Files.Count);
        // Provider.Files.Values walks every mounted VFS bag without dedupe — the same path can
        // appear once per overlapping pak/patch. Keep the first (highest readOrder) only.
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var duplicatePaths = 0;
        foreach (var asset in Provider.Files.Values)
        {
            if (asset.IsUePackagePayload) continue;
            if (ShouldSkipAssetForQueue(asset)) continue;
            if (!seenPaths.Add(asset.Path))
            {
                duplicatePaths++;
                continue;
            }
            assets.Add(asset);
        }

        // Flat mode always sorts smallest→largest regardless of the global setting.
        assets.Sort(static (a, b) => a.Size.CompareTo(b.Size));
        var ordered = assets.ToArray();
        Log.Information(
            "[Queue] FlatArchiveExport: {Count} files from Provider.Files, sorted by Size ascending, bulk={Bulk} (skipped {Dupes} duplicate paths)",
            ordered.Length, flags, duplicatePaths);

        Interlocked.Exchange(ref ExportedCount, 0);
        Interlocked.Exchange(ref FailedExportCount, 0);
        Interlocked.Exchange(ref SkippedExportCount, 0);

        var status = ApplicationService.ApplicationView.Status;
        status.BeginProgress(ordered.Length);
        var applyImageBias = HasFlag(flags, EBulkType.Textures) || HasFlag(flags, EBulkType.Meshes);
        try
        {
            RunExportParallel(ordered, cancellationToken, entry =>
            {
                try
                {
                    if (HasFlag(flags, EBulkType.Raw))
                        ExportData(entry, false);
                    if (HasFlag(flags, EBulkType.Metadata) && entry.IsUePackage)
                        ExportMetadata(entry, false);
                    if (extractFlags != EBulkType.None)
                        Extract(cancellationToken, entry, false, extractFlags | EBulkType.Auto);
                }
                catch
                {
                    // ignore per-asset failures
                }
                finally
                {
                    status.IncrementProgress();
                }
            }, applyImageBias);
        }
        finally
        {
            status.CompleteProgress();
        }

        // No per-folder success FLogger spam — status bar shows completion + elapsed.
        var elapsed = ApplicationService.ApplicationView.Status.LastLapElapsedText;
        Log.Information(
            "[Queue] FlatArchiveExport finished: exported={Exported} failed={Failed} skipped={Skipped} type={Type}{Elapsed}",
            ExportedCount, FailedExportCount, SkippedExportCount, filetype,
            string.IsNullOrEmpty(elapsed) ? "" : $", {elapsed}");

        if (FailedExportCount > 0)
        {
            FLogger.Append(ELog.Error, () =>
                FLogger.Text($"[Queue] Flat export finished with {FailedExportCount} failures ({filetype})", Constants.WHITE, true));
        }
    }

    /// <summary>Write a loose archive file (e.g. .ttf) into the Properties output tree.</summary>
    private void SaveLooseBytesToProperties(GameFile entry, bool updateUi)
    {
        var path = Path.Combine(
            UserSettings.Default.PropertiesDirectory,
            UserSettings.Default.KeepDirectoryStructure ? entry.Directory : "",
            entry.Name).Replace('\\', '/');

        if (IsAlreadyExported(path, entry.Name, updateUi)) return;

        byte[] data;
        try
        {
            data = Provider.SaveAsset(entry);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref FailedExportCount);
            Log.Error(ex, "Failed to read loose asset {Path}", entry.Path);
            return;
        }

        if (IsOverwriteProtected(path, entry.Name, data.LongLength, updateUi)) return;

        Directory.CreateDirectory(path.SubstringBeforeLast('/'));
        File.WriteAllBytes(path, data);
        Interlocked.Increment(ref ExportedCount);
        Log.Information("{FileName} successfully saved (loose→Properties)", entry.Name);
        if (updateUi)
        {
            FLogger.Append(ELog.Information, () =>
            {
                FLogger.Text("Successfully saved ", Constants.WHITE);
                FLogger.Link(entry.Name, path, true);
            });
        }
    }

    public void ExtractFolder(CancellationToken cancellationToken, TreeItem folder, EBulkType bulk)
        => BulkFolder(cancellationToken, folder, asset => Extract(cancellationToken, asset, TabControl.HasNoTabs, bulk), bulk);

    public void ExtractFolder(CancellationToken cancellationToken, TreeItem folder)
        => BulkFolder(cancellationToken, folder, asset => Extract(cancellationToken, asset, TabControl.HasNoTabs, EBulkType.Auto));

    public void SaveFolder(CancellationToken cancellationToken, TreeItem folder)
        => BulkFolder(cancellationToken, folder, asset => Extract(cancellationToken, asset, TabControl.HasNoTabs, EBulkType.Properties | EBulkType.Auto),
            EBulkType.Properties | EBulkType.Auto);

    public void TextureFolder(CancellationToken cancellationToken, TreeItem folder)
        => BulkFolder(cancellationToken, folder, asset => Extract(cancellationToken, asset, TabControl.HasNoTabs, EBulkType.Textures | EBulkType.Auto),
            EBulkType.Textures | EBulkType.Auto);

    public void ModelFolder(CancellationToken cancellationToken, TreeItem folder)
    {
        var bulk = EBulkType.Meshes | EBulkType.Auto |
                   (UserSettings.Default.AutoExportTexturesWithModels ? EBulkType.Textures : EBulkType.None);
        BulkFolder(cancellationToken, folder, asset => Extract(cancellationToken, asset, TabControl.HasNoTabs, bulk), bulk);
    }

    public void AnimationFolder(CancellationToken cancellationToken, TreeItem folder)
        => BulkFolder(cancellationToken, folder, asset => Extract(cancellationToken, asset, TabControl.HasNoTabs, EBulkType.Animations | EBulkType.Auto),
            EBulkType.Animations | EBulkType.Auto);

    public void AudioFolder(CancellationToken cancellationToken, TreeItem folder)
        => BulkFolder(cancellationToken, folder, asset => Extract(cancellationToken, asset, TabControl.HasNoTabs, EBulkType.Audio | EBulkType.Auto),
            EBulkType.Audio | EBulkType.Auto);

    public void CodeFolder(CancellationToken cancellationToken, TreeItem folder)
        => BulkFolder(cancellationToken, folder, asset => Extract(cancellationToken, asset, TabControl.HasNoTabs, EBulkType.Code | EBulkType.Auto),
            EBulkType.Code | EBulkType.Auto);

    /// <summary>Walk a folder tree and append UE class tags to the active FMDex (one save at the end).</summary>
    public void IndexFolderForFMDex(CancellationToken cancellationToken, TreeItem folder)
    {
        FMDexService.Instance.BindToProvider(Provider);

        var assets = new List<GameFile>();
        CollectFolderAssets(folder, assets);
        RunFMDexIndex(cancellationToken, assets, folder.PathAtThisPoint);
    }

    /// <summary>Index every mounted package into FMDex (one save at the end). Does not require Assets Explorer.</summary>
    public void IndexArchiveForFMDex(CancellationToken cancellationToken)
    {
        FMDexService.Instance.BindToProvider(Provider);

        var assets = new List<GameFile>(Provider.Files.Count);
        foreach (var asset in Provider.Files.Values)
        {
            if (asset.IsUePackagePayload) continue;
            assets.Add(asset);
        }

        RunFMDexIndex(cancellationToken, assets, Provider.ProjectName ?? "archive");
    }

    /// <summary>Index selected packages into FMDex (one save at the end).</summary>
    public void IndexAssetsForFMDex(CancellationToken cancellationToken, IEnumerable<GameFile> assets)
    {
        FMDexService.Instance.BindToProvider(Provider);
        RunFMDexIndex(cancellationToken, assets.ToList(), "selection");
    }

    private static void CollectFolderAssets(TreeItem folder, List<GameFile> into)
    {
        // Per-folder size order only when smallest-first is on; otherwise keep AssetsList order.
        // BulkFolder still applies OrderForExport for a global size sort when the setting is enabled.
        IEnumerable<GameFileViewModel> assets = folder.AssetsList.Assets;
        if (UserSettings.Default.ExportSmallestFilesFirst)
            assets = assets.OrderBy(a => a.Asset.Size);
        foreach (var entry in assets)
            into.Add(entry.Asset);
        foreach (var f in folder.Folders)
            CollectFolderAssets(f, into);
    }

    private static int ResolveFMDexMaxThreads()
    {
        var n = UserSettings.Default.FMDexMaxThreads;
        if (n <= 0)
        {
            // Header reads are mostly I/O + Theia decrypt wait — oversubscribe CPUs so
            // workers stay busy while others block on disk/decrypt.
            n = Math.Max(Environment.ProcessorCount * 4, 32);
        }

        return Math.Clamp(n, 1, 512);
    }

    private void RunFMDexIndex(CancellationToken cancellationToken, List<GameFile> assets, string label)
    {
        var indexed = 0;
        var nonPackage = 0;
        var alreadyIndexed = 0;
        var emptyTags = 0;
        var failed = 0;
        var processed = 0;
        var threads = ResolveFMDexMaxThreads();
        var timer = Stopwatch.StartNew();

        FMDexService.Instance.EnsureLoaded();

        // Skip packages already present in the active FMDex (same session / same index file).
        // Smallest first so early progress is fast and workers stay busy on light packages.
        var queue = new ConcurrentQueue<GameFile>();
        foreach (var a in assets
                     .Where(a =>
                     {
                         if (!a.IsUePackage)
                         {
                             nonPackage++;
                             return false;
                         }

                         if (FMDexService.Instance.IsIndexed(a.Path))
                         {
                             alreadyIndexed++;
                             return false;
                         }

                         return true;
                     })
                     .OrderBy(a => a.Size))
        {
            queue.Enqueue(a);
        }

        var packageTotal = queue.Count;
        if (packageTotal == 0)
        {
            timer.Stop();
            ApplicationService.ApplicationView.Status.UpdateStatusLabel(
                alreadyIndexed > 0 ? $"{alreadyIndexed} already indexed" : "no packages to index",
                "FMDex");
            FLogger.Append(ELog.Information, () =>
                FLogger.Text(
                    alreadyIndexed > 0
                        ? $"FMDex: '{label}' — {alreadyIndexed} already indexed, nothing new ({FormatElapsed(timer.Elapsed)})"
                        : $"FMDex: '{label}' has no .uasset/.umap packages ({nonPackage} other file(s)).",
                    Constants.WHITE, true));
            return;
        }

        ApplicationService.ApplicationView.Status.UpdateStatusLabel(
            $"indexing… 0/{packageTotal} ({threads} workers, {alreadyIndexed} skipped)", "FMDex");

        // Batch results to avoid lock contention on every Upsert
        var pending = new ConcurrentDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var lastStatusTicks = Environment.TickCount64;

        void PushStatus(bool force)
        {
            var now = Environment.TickCount64;
            if (!force && now - Volatile.Read(ref lastStatusTicks) < 200)
                return;
            Interlocked.Exchange(ref lastStatusTicks, now);

            var done = Volatile.Read(ref processed);
            var idx = Volatile.Read(ref indexed);
            var fail = Volatile.Read(ref failed);
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                ApplicationService.ApplicationView.Status.UpdateStatusLabel(
                    $"indexing… {idx} indexed / {fail} failed ({done}/{packageTotal}) [{threads} thr]",
                    "FMDex");
                return;
            }

            dispatcher.BeginInvoke(() =>
                ApplicationService.ApplicationView.Status.UpdateStatusLabel(
                    $"indexing… {idx} indexed / {fail} failed ({done}/{packageTotal}) [{threads} thr]",
                    "FMDex"));
        }

        var workers = new Task[threads];
        for (var w = 0; w < threads; w++)
        {
            workers[w] = Task.Factory.StartNew(() =>
            {
                CpuAffinity.ApplyToCurrentThread();

                while (queue.TryDequeue(out var entry))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var result = TryIndexEntryForFMDex(entry, pending);
                    switch (result)
                    {
                        case 1: Interlocked.Increment(ref indexed); break;
                        case 2: Interlocked.Increment(ref emptyTags); break;
                        default: Interlocked.Increment(ref failed); break;
                    }

                    Interlocked.Increment(ref processed);
                    PushStatus(force: false);
                }
            }, cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        try
        {
            Task.WaitAll(workers, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // flush whatever completed
        }

        PushStatus(force: true);

        FMDexService.Instance.UpsertBatch(
            pending.Select(kv => (kv.Key, kv.Value)));
        pending.Clear();
        FMDexService.Instance.Save();

        // Header reads allocate large byte[] (LOH). Force compact so RAM drops after a full-game index.
        ReleaseFMDexIndexMemory();

        Application.Current.Dispatcher.Invoke(() => SearchVm.RefreshFilter());

        timer.Stop();
        var skipNote = alreadyIndexed > 0 ? $", {alreadyIndexed} already indexed" : "";
        var detail =
            $"FMDex indexed '{label}': {indexed} package(s)" +
            (emptyTags > 0 ? $", {emptyTags} empty-export" : "") +
            (failed > 0 ? $", {failed} failed" : "") +
            skipNote +
            (nonPackage > 0 ? $", {nonPackage} non-package skipped" : "") +
            $" [{threads} workers] in {FormatElapsed(timer.Elapsed)} → ";
        var outPath = FMDexService.Instance.LoadedPath;
        var outDir = FMDexService.Instance.DirectoryPath;

        FLogger.Append(ELog.Information, () =>
        {
            FLogger.Text(detail, Constants.WHITE);
            if (!string.IsNullOrWhiteSpace(outPath) && File.Exists(outPath))
                FLogger.Link(Path.GetFileName(outPath), outPath);
            else
                FLogger.Text("(unsaved)", Constants.WHITE);

            FLogger.Text("  ", Constants.WHITE);
            if (!string.IsNullOrWhiteSpace(outDir) && Directory.Exists(outDir))
                FLogger.Link(outDir, outDir, true);
            else
                FLogger.Text("", Constants.WHITE, true);
        });

        ApplicationService.ApplicationView.Status.UpdateStatusLabel(
            $"{indexed} indexed" + (alreadyIndexed > 0 ? $", {alreadyIndexed} skipped" : ""),
            "FMDex");
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed.TotalHours >= 1)
            return elapsed.ToString(@"h\:mm\:ss");
        if (elapsed.TotalMinutes >= 1)
            return $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds:D2}.{elapsed.Milliseconds:D3}s";
        return $"{elapsed.TotalSeconds:0.000}s";
    }

    /// <summary>Drop LOH / gen2 retained by header-only package loads during FMDex indexing.</summary>
    private static void ReleaseFMDexIndexMemory()
    {
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "FMDex post-index GC failed");
        }
    }

    /// <summary>Header-only package load (no uexp/ubulk) — enough for export class tags.</summary>
    private IPackage LoadPackageHeaderForFMDex(GameFile file)
    {
        var uasset = file.CreateReader();
        FArchive? noPayload = null;
        return file switch
        {
            FIoStoreEntry io when Provider is IVfsFileProvider vfs =>
                new IoPackage(uasset, io.IoStoreReader.ContainerHeader, noPayload, noPayload, vfs),
            FPakEntry or OsGameFile =>
                new Package(uasset, noPayload, noPayload, noPayload, Provider, useLazySerialization: true),
            _ => Provider.LoadPackage(file)
        };
    }

    /// <returns>1 indexed, 2 loaded but no class tags, -1 failed (non-packages are filtered before call)</returns>
    private int TryIndexEntryForFMDex(GameFile entry, ConcurrentDictionary<string, List<string>> pending)
    {
        try
        {
            var pkg = LoadPackageHeaderForFMDex(entry);
            var tags = FMDexService.CollectClassTags(pkg);
            if (tags.Count == 0)
                return 2;

            pending[entry.Path] = tags;
            return 1;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "FMDex index failed for {Path}", entry.Path);
            return -1;
        }
    }

    public void Extract(CancellationToken cancellationToken, GameFile entry, bool addNewTab = false, EBulkType bulk = EBulkType.None)
    {
        if (HasFlag(bulk, EBulkType.Auto))
            Log.Information("Bulk extract '{FullPath}'", entry.Path);
        else
            Log.Information("User DOUBLE-CLICKED to extract '{FullPath}'", entry.Path);

        var updateUi = !HasFlag(bulk, EBulkType.Auto);
        var saveProperties = HasFlag(bulk, EBulkType.Properties);
        var saveTextures = HasFlag(bulk, EBulkType.Textures);
        var saveAudio = HasFlag(bulk, EBulkType.Audio);
        var saveDecompiled = HasFlag(bulk, EBulkType.Code);

        // Bulk Auto: per-worker tab so parallel export does not race SelectedTab.Entry.
        TabItem tab;
        if (updateUi)
        {
            ApplicationService.ApplicationView.IsAssetsExplorerVisible = false;
            if (addNewTab && TabControl.CanAddTabs) TabControl.AddTab(entry);
            else TabControl.SelectedTab.SoftReset(entry);
            tab = TabControl.SelectedTab;
            tab.Highlighter = AvalonExtensions.HighlighterSelector(entry.Extension);
        }
        else
        {
            tab = new TabItem(entry, string.Empty);
        }

        switch (entry.Extension)
        {
            case "uasset":
            case "umap":
            {
                var result = Provider.GetLoadPackageResult(entry);
                if (updateUi)
                    tab.TitleExtra = result.TabTitleExtra;

                // Auto-index only when opening/viewing in the UI (updateUi). Bulk/folder/queue
                // exports pass EBulkType.Auto → updateUi=false; indexing there storms ThreadPool
                // workers with per-package Upsert+Save and contends with [ExportParallel].
                if (updateUi &&
                    UserSettings.Default.AutoIndexUnindexedOnLoad &&
                    entry.IsUePackage &&
                    !FMDexService.Instance.IsIndexed(entry.Path))
                {
                    try
                    {
                        FMDexService.Instance.BindToProvider(Provider);
                        FMDexService.Instance.IndexPackage(entry, result.Package);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "FMDex auto-index failed for {Path}", entry.Path);
                    }
                }

                if (saveProperties || updateUi)
                {
                    var metadataMode = UserSettings.Default.MetadataExportMode;
                    string json;
                    if (saveProperties && metadataMode == EMetadataExport.AppendToJson)
                    {
                        var exportsToken = JToken.Parse(JsonConvert.SerializeObject(result.GetDisplayData(true), Formatting.Indented));
                        var metadataToken = JToken.Parse(JsonConvert.SerializeObject(result.Package, Formatting.Indented));
                        json = new JObject { ["Exports"] = exportsToken, ["Metadata"] = metadataToken }.ToString(Formatting.Indented);
                    }
                    else
                    {
                        json = JsonConvert.SerializeObject(result.GetDisplayData(saveProperties), Formatting.Indented);
                    }

                    tab.SetDocumentText(json, saveProperties, updateUi);
                    if (saveProperties)
                    {
                        if (metadataMode == EMetadataExport.AutoExport)
                            ExportMetadata(entry, updateUi);
                        // Properties-only stops here; continue when other export flags are set.
                        if (!HasFlag(bulk, EBulkType.Textures) && !HasFlag(bulk, EBulkType.Meshes)
                            && !HasFlag(bulk, EBulkType.Animations) && !HasFlag(bulk, EBulkType.Audio)
                            && !HasFlag(bulk, EBulkType.Code))
                            break;
                    }
                }

                if (saveDecompiled)
                {
                    if (Decompile(entry, false))
                        tab.SaveDecompiled(updateUi);
                    // Code-only stops here; combined flags continue to CheckExport.
                    if (!HasFlag(bulk, EBulkType.Textures) && !HasFlag(bulk, EBulkType.Meshes)
                        && !HasFlag(bulk, EBulkType.Animations) && !HasFlag(bulk, EBulkType.Audio))
                        break;
                }

                for (var i = result.InclusiveStart; i < result.ExclusiveEnd; i++)
                {
                    if (CheckExport(cancellationToken, result.Package, i, bulk, tab))
                        break;
                }

                break;
            }
            case "ini" when entry.Name.Contains("BinaryConfig"):
            {
                var ar = entry.CreateReader();
                var configCache = new FConfigCacheIni(ar);

                tab.Highlighter = AvalonExtensions.HighlighterSelector("json");
                tab.SetDocumentText(JsonConvert.SerializeObject(configCache, Formatting.Indented), saveProperties, updateUi);

                break;
            }
            case "dat" when Provider.Versions.Game is EGame.GAME_Aion2:
            {
                ProcessAion2DatFile(entry, updateUi, saveProperties);
                break;
            }
            case "bytes" when Provider.Versions.Game is EGame.GAME_RocoKingdomWorld:
            {
                ProcessRocoBinFile(entry, updateUi, saveProperties);
                break;
            }
            case "dbc" when Provider.Versions.Game is EGame.GAME_AshesOfCreation:
            {
                ProcessCacheDBFile(entry, updateUi, saveProperties);
                break;
            }
            case "luac":
            case "lua":
            {
                var data = Provider.SaveAsset(entry);
                byte[] decompiled = ProcessLuaFile(data);

                using var stream = new MemoryStream(decompiled);
                using var reader = new StreamReader(stream);
                tab.SetDocumentText(reader.ReadToEnd(), saveProperties, updateUi);

                break;
            }
            case "upluginmanifest":
            case "code-workspace":
            case "projectstore":
            case "uefnproject":
            case "uproject":
            case "manifest":
            case "uplugin":
            case "archive":
            case "dnearchive": // Banishers: Ghosts of New Eden
            case "gitignore":
            case "gitattributes":
            case "LICENSE":
            case "playstats": // Dispatch
            case "template":
            case "stUMeta": // LIS: Double Exposure
            case "vmodule":
            case "glslfx":
            case "cptake":
            case "uparam": // Steel Hunters
            case "spi1d":
            case "verse":
            case "html":
            case "json5":
            case "uref":
            case "cube":
            case "usda":
            case "ocio":
            case "data" when Provider.ProjectName is "OakGame":
            case "scss":
            case "yaml":
            case "ini":
            case "txt":
            case "log":
            case "lsd": // Days Gone
            case "bat":
            case "dat":
            case "cfg":
            case "ddr":
            case "ide":
            case "ipl":
            case "zon":
            case "xml":
            case "css":
            case "csv":
            case "pem":
            case "tsv":
            case "tps":
            case "tgc": // State of Decay 2
            case "cpp":
            case "apx":
            case "udn":
            case "doc":
            case "vdf":
            case "yml":
            case "js":
            case "po":
            case "py":
            case "md":
            case "h":
            case "non" when Provider.Versions.Game is EGame.GAME_RocoKingdomWorld:
            case "cam" when Provider.Versions.Game is EGame.GAME_RocoKingdomWorld:
            // Uncharted Waters Origin
            case "crn":
            case "uwt":
            case "wvh":
            case "bf":
            case "bl":
            case "bm":
            case "br":
            case "sql":
            case "cs":
            {
                var data = Provider.SaveAsset(entry);
                using var stream = new MemoryStream(data) { Position = 0 };
                using var reader = new StreamReader(stream);

                tab.SetDocumentText(reader.ReadToEnd(), saveProperties, updateUi);

                break;
            }
            case "ebd" when Provider.Versions.Game is EGame.GAME_ArcRaiders:
            case "json":
            {
                var data = Provider.SaveAsset(entry);
                using var stream = new MemoryStream(data) { Position = 0 };
                using var reader = new StreamReader(stream);

                var parsedJson = JsonConvert.DeserializeObject(reader.ReadToEnd());
                tab.SetDocumentText(JsonConvert.SerializeObject(parsedJson, Formatting.Indented), saveProperties, updateUi);

                break;
            }
            case "locmeta":
            {
                var archive = entry.CreateReader();
                var metadata = new FTextLocalizationMetaDataResource(archive);
                tab.SetDocumentText(JsonConvert.SerializeObject(metadata, Formatting.Indented), saveProperties, updateUi);

                break;
            }
            case "locres":
            {
                var archive = entry.CreateReader();
                var locres = new FTextLocalizationResource(archive);
                tab.SetDocumentText(JsonConvert.SerializeObject(locres, Formatting.Indented), saveProperties, updateUi);

                break;
            }
            case "bin" when entry.Name.Contains("AssetRegistry", StringComparison.OrdinalIgnoreCase):
            {
                var archive = entry.CreateReader();
                var registry = new FAssetRegistryState(archive);
                tab.SetDocumentText(JsonConvert.SerializeObject(registry, Formatting.Indented), saveProperties, updateUi);

                break;
            }
            case "bin" when entry.Name.Contains("GlobalShaderCache", StringComparison.OrdinalIgnoreCase):
            {
                var archive = entry.CreateReader();
                var registry = new FGlobalShaderCache(archive);
                tab.SetDocumentText(JsonConvert.SerializeObject(registry, Formatting.Indented), saveProperties, updateUi);

                break;
            }
            case "bin" when entry.Name.Contains("GameFeatureVersePaths", StringComparison.OrdinalIgnoreCase):
            {
                var archive = entry.CreateReader();
                var versePathLookup = new FGameFeatureVersePathLookup(archive);
                tab.SetDocumentText(JsonConvert.SerializeObject(versePathLookup, Formatting.Indented), saveProperties, updateUi);
                break;
            }
            case "bank":
            {
                var archive = entry.CreateReader();
                if (!FmodProvider.TryLoadBank(archive, entry.NameWithoutExtension, out var fmodReader))
                {
                    Log.Error($"Failed to load FMOD bank {entry.Path}");
                    break;
                }

                tab.SetDocumentText(JsonConvert.SerializeObject(fmodReader, Formatting.Indented, converters: [new FmodSoundBankConverter(), new StringEnumConverter()]), saveProperties, updateUi);

                var extractedSounds = FmodProvider.ExtractBankSounds(fmodReader);
                var directory = Path.GetDirectoryName(entry.Path) ?? "/FMOD/Desktop/";
                foreach (var sound in extractedSounds)
                {
                    SaveAndPlaySound(cancellationToken, Path.Combine(directory, sound.Name), sound.Extension, sound.Data, saveAudio, updateUi);
                }

                break;
            }
            case "bnk":
            case "pck":
            {
                var archive = entry.CreateReader();
                var wwise = new WwiseReader(new FWwiseArchive(archive), new WwiseGameFileSource(entry));
                tab.SetDocumentText(JsonConvert.SerializeObject(wwise, Formatting.Indented), saveProperties, updateUi);

                var medias = WwiseProvider.ExtractBankSounds(wwise);
                foreach (var media in medias)
                {
                    SaveAndPlaySound(cancellationToken, media.OutputPath, media.Extension, media.Data?.GetData() ?? [], saveAudio, updateUi);
                }

                break;
            }
            case "awb":
            {
                var archive = entry.CreateReader();
                var awbReader = new AwbReader(archive);

                tab.SetDocumentText(JsonConvert.SerializeObject(awbReader, Formatting.Indented), saveProperties, updateUi);

                var directory = Path.GetDirectoryName(archive.Name) ?? "/Criware/";
                var extractedSounds = CriWareProvider.ExtractCriWareSounds(awbReader, archive.Name);
                foreach (var sound in extractedSounds)
                {
                    SaveAndPlaySound(cancellationToken, Path.Combine(directory, sound.Name), sound.Extension, sound.Data, saveAudio, updateUi);
                }

                break;
            }
            case "acb":
            {
                var archive = entry.CreateReader();
                var acbReader = new AcbReader(archive);

                tab.SetDocumentText(JsonConvert.SerializeObject(acbReader, Formatting.Indented), saveProperties, updateUi);

                var directory = Path.GetDirectoryName(archive.Name) ?? "/Criware/";
                var extractedSounds = CriWareProvider.ExtractCriWareSounds(acbReader, archive.Name);
                foreach (var sound in extractedSounds)
                {
                    SaveAndPlaySound(cancellationToken, Path.Combine(directory, sound.Name), sound.Extension, sound.Data, saveAudio, updateUi);
                }

                break;
            }
            case "xvag":
            case "flac":
            case "at9":
            case "wem":
            case "wav":
            case "WAV":
            case "ogg":
                // todo: CSCore.MediaFoundation.MediaFoundationException The byte stream type of the given URL is unsupported. case "aif":
            {
                var data = Provider.SaveAsset(entry);
                SaveAndPlaySound(cancellationToken, entry.PathWithoutExtension, entry.Extension, data, saveAudio, updateUi);

                break;
            }
            case "udic":
            {
                var archive = entry.CreateReader();
                var header = new FOodleDictionaryArchive(archive).Header;
                tab.SetDocumentText(JsonConvert.SerializeObject(header, Formatting.Indented), saveProperties, updateUi);

                break;
            }
            case "ustbin" when Provider.Versions.Game is EGame.GAME_DeltaForce:
            {
                var archive = entry.CreateReader();
                var ustbin = new FDeltaStringTable(archive);
                tab.SetDocumentText(JsonConvert.SerializeObject(ustbin, Formatting.Indented), saveProperties, updateUi);
                break;
            }
            case "png":
            case "jpg":
            case "bmp":
            {
                if (!updateUi && (!saveTextures || !UserSettings.Default.GroupExportLooseAssets))
                    break;

                var data = Provider.SaveAsset(entry);
                using var stream = new MemoryStream(data) { Position = 0 };
                tab.AddImage(entry.NameWithoutExtension, false, SKBitmap.Decode(stream), saveTextures, updateUi);

                break;
            }
            case "svg":
            {
                if (!updateUi && (!saveTextures || !UserSettings.Default.GroupExportLooseAssets))
                    break;

                var data = Provider.SaveAsset(entry);
                using var stream = new MemoryStream(data) { Position = 0 };
                var svg = new SKSvg();
                svg.Load(stream);

                int size = 512;
                var bitmap = new SKBitmap(size, size);
                using var canvas = new SKCanvas(bitmap);
                canvas.Clear(SKColors.Transparent);

                if (svg.Picture == null)
                    break;

                var bounds = svg.Picture.CullRect;
                float scale = Math.Min(size / bounds.Width, size / bounds.Height);
                canvas.Scale(scale);
                canvas.Translate(-bounds.Left, -bounds.Top);
                canvas.DrawPicture(svg.Picture);

                tab.AddImage(entry.NameWithoutExtension, false, bitmap, saveTextures, updateUi);

                break;
            }
            case "ufont":
            case "otf":
            case "ttf":
            {
                if (saveProperties && UserSettings.Default.GroupExportLooseAssets)
                {
                    SaveLooseBytesToProperties(entry, updateUi);
                }
                else if (updateUi)
                {
                    FLogger.Append(ELog.Warning, () =>
                        FLogger.Text($"Export '{entry.Name}' raw data and change its extension if you want it to be an installable font file", Constants.WHITE, true));
                }

                break;
            }
            case "ushaderbytecode":
            case "ushadercode":
            {
                var archive = entry.CreateReader();
                var ar = new FShaderCodeArchive(archive);
                tab.SetDocumentText(JsonConvert.SerializeObject(ar, Formatting.Indented), saveProperties, updateUi);

                break;
            }
            case "upipelinecache":
            {
                var archive = entry.CreateReader();
                var ar = new FPipelineCacheFile(archive);
                tab.SetDocumentText(JsonConvert.SerializeObject(ar, Formatting.Indented), saveProperties, updateUi);

                break;
            }
            case "stinfo":
            {
                var archive = entry.CreateReader();
                var ar = new FShaderTypeHashes(archive);
                tab.SetDocumentText(JsonConvert.SerializeObject(ar, Formatting.Indented), saveProperties, updateUi);

                break;
            }
            case "res": // just skip
            case "bytes": // wuthering waves
                break;
            default:
            {
                Log.Warning($"The package '{entry.Name}' is of an unknown type.");
                if (!UnknownExtensions.Contains(entry.Extension))
                {
                    UnknownExtensions.Add(entry.Extension);
                FLogger.Append(ELog.Warning, () =>
                        FLogger.Text($"There are some packages with an unknown type {entry.Extension}. Check Log file for a full list.", Constants.WHITE, true));
                }
                break;
            }
        }

        // Roco Kingdom: World
        void ProcessRocoBinFile(GameFile entry, bool updateUi, bool saveProperties)
        {
            tab.Highlighter = AvalonExtensions.HighlighterSelector("json");
            var nonFileName = "/" + entry.NameWithoutExtension + ".non";
            var nonPath = Provider.Files.Keys.FirstOrDefault(k => k.EndsWith(nonFileName, StringComparison.OrdinalIgnoreCase));

            // I will only get one localization file because they did not translate any languages, lol
            var locPathKey = entry.Path.Replace("/BinData/", "/BinLocalize/zh_Hans/").Replace("/BinDataCompressed/", "/BinLocalize/zh_Hans/");
            var locFileFound = Provider.Files.TryGetValue(locPathKey, out var locEntry);

            if (entry.Path is "NRC/Content/ScriptC/Data/Audio/dataconfig_audio.bytes")
            {
                var descFound = Provider.Files.TryGetValue("NRC/Content/ScriptC/Data/Audio/dataconfig_audiodesc.bytes", out var descEntry);
                var typeDescFound = Provider.Files.TryGetValue("NRC/Content/ScriptC/Data/Audio/typeDesc.bytes", out var typeDescEntry);

                if (!descFound || !typeDescFound)
                {
                    Log.Warning("Could not find associated dataconfig_audiodesc.bytes or typeDesc.bytes, cannot parse audio config");
                    return;
                }

                var data = new FRocoAudioConfig(entry.CreateReader(), descEntry.CreateReader(), typeDescEntry.CreateReader());
                tab.SetDocumentText(JsonConvert.SerializeObject(data, Formatting.Indented), saveProperties, updateUi);
                return;
            }

            if (!string.IsNullOrEmpty(nonPath) && Provider.Files.TryGetValue(nonPath, out var nonEntry))
            {
                string json = Encoding.UTF8.GetString(nonEntry.Read());
                var schema = JsonConvert.DeserializeObject<FRocoSchema>(json);
                var archive = entry.CreateReader();
                var locArchive = locFileFound ? new FRocoBinData(locEntry.CreateReader(), null, ERocoBinDataType.BinLocalize) : null;

                var data = entry.PathWithoutExtension switch
                {
                    var p when p.Contains("BinDataCompressed") => new FRocoBinData(archive, schema, ERocoBinDataType.BinDataCompressed, locArchive),
                    var p when p.Contains("BinData") => new FRocoBinData(archive, schema, ERocoBinDataType.BinData, locArchive),
                    var p when p.Contains("BinLocalize") => new FRocoBinData(archive, null, ERocoBinDataType.BinLocalize),
                    _ => null
                };

                tab.SetDocumentText(JsonConvert.SerializeObject(data, Formatting.Indented), saveProperties, updateUi);
            }
            else if (entry.PathWithoutExtension.Contains("/Bin/"))
            {
                throw new Exception($"Could not find associated .non file for {entry.Name}");
            }
        }

        void ProcessAion2DatFile(GameFile entry, bool updateUi, bool saveProperties)
        {
            tab.Highlighter = AvalonExtensions.HighlighterSelector("json");
            if (entry.NameWithoutExtension.EndsWith("_MapEvent"))
            {
                var data = Provider.SaveAsset(entry);
                FAion2DatFileArchive.DecryptData(data);
                using var stream = new MemoryStream(data) { Position = 0 };
                using var reader = new StreamReader(stream);

                tab.SetDocumentText(reader.ReadToEnd(), saveProperties, updateUi);
            }
            else if (entry.NameWithoutExtension.Equals("L10NString"))
            {
                var l10nData = new FAion2L10NFile(entry, Provider);
                tab.SetDocumentText(JsonConvert.SerializeObject(l10nData, Formatting.Indented), saveProperties, updateUi);
            }
            else if (entry.NameWithoutExtension.Equals("key_manifest"))
            {
                var keymanifest = new FAion2KeyManifestFile(entry, Provider);
                tab.SetDocumentText(JsonConvert.SerializeObject(keymanifest, Formatting.Indented), saveProperties, updateUi);
            }
            else
            {
                FAion2DataFile datfile = entry.NameWithoutExtension switch
                {
                    "MapDataHierarchy" => new FAion2MapHierarchyFile(entry),
                    "MapData" => new FAion2MapDataFile(entry, Provider),
                    _ when entry.Directory.EndsWith("Data/WorldMap", StringComparison.OrdinalIgnoreCase) => new FAion2MapDataFile(entry, Provider),
                    _ => new FAion2DataTableFile(entry, Provider)
                };

                tab.SetDocumentText(JsonConvert.SerializeObject(datfile, Formatting.Indented), saveProperties, updateUi);
            }
        }

        // Ashhes of Creation
        void ProcessCacheDBFile(GameFile entry, bool updateUi, bool saveProperties)
        {
            var data = entry.Read();
            var dbc = new FAoCDBCReader(data, Provider.MappingsForGame, Provider.Versions);
            for (var i = 0; i < dbc.Chunks.Length; i++)
            {
                if (!dbc.TryReadChunk(i, out var category, out var files))
                {
                    Log.Warning("Couldn't read {i} chuck in AoC CacheDB", i);
                    continue;
                }
                var fileName = Path.ChangeExtension(category, ".json");
                var directory = Path.Combine(UserSettings.Default.PropertiesDirectory,
                    UserSettings.Default.KeepDirectoryStructure ? entry.Directory : "", entry.Name, fileName).Replace('\\', '/');

                Directory.CreateDirectory(directory.SubstringBeforeLast('/'));

                File.WriteAllText(directory, JsonConvert.SerializeObject(files, Formatting.Indented));
            }

            tab.SetDocumentText(JsonConvert.SerializeObject(dbc, Formatting.Indented), saveProperties, updateUi);
        }
    }

    /// <summary>
    /// Bulk-save textures / meshes / audio from packages loaded through an external provider
    /// (e.g. Profile Diff) without swapping the live session provider.
    /// </summary>
    public void ExtractAssetsFromProvider(
        IFileProvider provider,
        CancellationToken cancellationToken,
        GameFile entry,
        EBulkType bulk)
    {
        bulk |= EBulkType.Auto;
        if (entry.Extension is not ("uasset" or "umap"))
            return;

        Application.Current.Dispatcher.Invoke(() =>
        {
            if (TabControl.HasNoTabs)
                TabControl.AddTab(entry);
            else
                TabControl.SelectedTab.Entry = entry;
        });

        var result = provider.GetLoadPackageResult(entry);
        // Visit every export so combined texture/mesh/audio bulk flags don't stop early.
        for (var i = result.InclusiveStart; i < result.ExclusiveEnd; i++)
            CheckExport(cancellationToken, result.Package, i, bulk);
    }

    private byte[] ProcessLuaFile(byte[] data)
    {
        var result = EUnluacErrorCode.Ok;
        byte[] output = [];
        if (BitConverter.ToUInt32(data) == UnluacHelper.LuaMagic && UnluacHelper.Instance is not null)
        {
            // opcodemap patch
            byte[] opmapData = Provider.Versions.Game switch
            {
                _ => [],
            };

            var flags = UserSettings.Default.UnluacFlags;
            var opcodemap = UserSettings.Default.CurrentDir.UnluacOpCodeMap;
            if (!string.IsNullOrWhiteSpace(opcodemap))
            {
                opmapData = Encoding.UTF8.GetBytes(opcodemap);
                flags |= EUnluacFlags.OpCodeMap;
            }
            else if (opmapData is { Length: > 12 })
            {
                flags |= EUnluacFlags.OpCodeMapPatch;
            }

            result = UnluacHelper.Decompile(data, opmapData, (uint)flags, out output, out var log);
            if (result != EUnluacErrorCode.Ok && log.Length > 0)
            {
                Log.Error(Encoding.UTF8.GetString(log));
            }
        }
        else
        {
            result = EUnluacErrorCode.Error;
        }

        var decompiled = result switch
        {
            EUnluacErrorCode.Ok => output,
#if DEBUG
            EUnluacErrorCode.PartialDecompile => output,
#endif
            _ => data,
        };

        return decompiled;
    }

    public void ExtractAndScroll(CancellationToken cancellationToken, string fullPath, string objectName, string parentExportType)
    {
        Log.Information("User CTRL-CLICKED to extract '{FullPath}'", fullPath);

        var entry = Provider[fullPath];
        TabControl.AddTab(entry, parentExportType);
        TabControl.SelectedTab.ScrollTrigger = objectName;

        var result = Provider.GetLoadPackageResult(entry, objectName);

        TabControl.SelectedTab.TitleExtra = result.TabTitleExtra;
        TabControl.SelectedTab.Highlighter = AvalonExtensions.HighlighterSelector(""); // json
        TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(result.GetDisplayData(), Formatting.Indented), false, false);

        for (var i = result.InclusiveStart; i < result.ExclusiveEnd; i++)
        {
            if (CheckExport(cancellationToken, result.Package, i))
                break;
        }
    }

    private bool CheckExport(CancellationToken cancellationToken, IPackage pkg, int index, EBulkType bulk = EBulkType.None, TabItem tab = null) // return true once you want to stop searching for exports
    {
        var isNone = bulk == EBulkType.None;
        var updateUi = !HasFlag(bulk, EBulkType.Auto);
        var saveTextures = HasFlag(bulk, EBulkType.Textures);
        var saveAudio = HasFlag(bulk, EBulkType.Audio);
        tab ??= TabControl.SelectedTab;
        // Always use the worker/tab Entry for save paths — never TabControl.SelectedTab.
        // Bulk Auto builds a per-worker TabItem; SelectedTab often remains FakeGameFile("New Tab")
        // and would dump New Tab.binka/wav (and a New Tab/ folder) into the export root.
        var packagePath = tab.Entry.PathWithoutExtension.Replace('\\', '/');
        var packageDirectory = packagePath.Contains('/') ? packagePath.SubstringBeforeLast('/') : string.Empty;

        var pointer = new FPackageIndex(pkg, index + 1).ResolvedObject;
        if (pointer?.Object is null) return false;

        var dummy = ((AbstractUePackage) pkg).ConstructObject(pointer.Class, pkg);
        switch (dummy)
        {
            case UVerseDigest when isNone && pointer.Object.Value is UVerseDigest verseDigest:
            {
                if (!TabControl.CanAddTabs) return false;

                TabControl.AddTab($"{verseDigest.Name}.verse");
                TabControl.SelectedTab.Highlighter = AvalonExtensions.HighlighterSelector("verse");
                TabControl.SelectedTab.SetDocumentText(verseDigest.ReadableCode, false, false);
                return true;
            }
            case UTexture when (isNone || saveTextures) && pointer.Object.Value is UTexture texture:
            {
                tab.AddImage(texture, saveTextures, updateUi);
                return false;
            }
            case USvgAsset when (isNone || saveTextures) && pointer.Object.Value is USvgAsset svgasset:
            {
                const int size = 512;
                var data = svgasset.GetOrDefault<byte[]>("SvgData");
                var sourceFile = svgasset.GetOrDefault<string>("SourceFile");
                using var stream = new MemoryStream(data) { Position = 0 };

                var svg = new SKSvg();
                svg.Load(stream);

                if (svg.Picture == null)
                    return false;

                var b = svg.Picture.CullRect;
                float s = Math.Min(size / b.Width, size / b.Height);

                var bitmap = new SKBitmap(size, size);
                using var canvas = new SKCanvas(bitmap);
                using var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.Medium };

                canvas.Scale(s);
                canvas.Translate(-b.Left, -b.Top);
                canvas.DrawPicture(svg.Picture, paint);

                if (saveTextures)
                {
                    var fileName = sourceFile.SubstringAfterLast('/');
                    var path = Path.Combine(UserSettings.Default.TextureDirectory,
                        UserSettings.Default.KeepDirectoryStructure ? tab.Entry.Directory : "", fileName!).Replace('\\', '/');

                    Directory.CreateDirectory(path.SubstringBeforeLast('/'));

                    using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
                    fs.Write(data, 0, data.Length);
                    if (File.Exists(path))
                    {
                        Log.Information("{FileName} successfully saved", fileName);
                        if (updateUi)
                        {
                            FLogger.Append(ELog.Information, () =>
                            {
                                FLogger.Text("Successfully saved ", Constants.WHITE);
                                FLogger.Link(fileName, path, true);
                            });
                        }
                    }
                    else
                    {
                        Log.Error("{FileName} could not be saved", fileName);
                        if (updateUi)
                            FLogger.Append(ELog.Error, () => FLogger.Text($"Could not save '{fileName}'", Constants.WHITE, true));
                    }
                }

                tab.AddImage(sourceFile.SubstringAfterLast('/'), false, bitmap, false, updateUi);
                return false;
            }
            // Supermassive Games (for example - The Dark Pictures Anthology: House of Ashes etc.)
            case UExternalSource when (isNone || saveAudio) && pointer.Object.Value is UExternalSource externalSource:
            {
                var audioName = Path.GetFileNameWithoutExtension(externalSource.ExternalSourcePath);
                var outputPath = string.IsNullOrEmpty(packageDirectory)
                    ? audioName
                    : Path.Combine(packageDirectory, audioName);
                SaveAndPlaySound(cancellationToken, outputPath, "wem", externalSource.Data?.WemFile?.GetData() ?? [], saveAudio, updateUi);
                return false;
            }
            case UAkAudioBank when (isNone || saveAudio) && pointer.Object.Value is UAkAudioBank soundBank:
            {
                var extractedSounds = WwiseProvider.ExtractBankSounds(soundBank);
                foreach (var sound in extractedSounds)
                {
                    SaveAndPlaySound(cancellationToken, sound.OutputPath, sound.Extension, sound.Data?.GetData() ?? [], saveAudio, updateUi);
                }
                return false;
            }
            case UAkAudioEvent when (isNone || saveAudio) && pointer.Object.Value is UAkAudioEvent audioEvent:
            {
                var extractedSounds = WwiseProvider.ExtractAudioEventSounds(audioEvent);
                foreach (var sound in extractedSounds)
                {
                    SaveAndPlaySound(cancellationToken, sound.OutputPath, sound.Extension, sound.Data?.GetData() ?? [], saveAudio, updateUi);
                }
                return false;
            }
            case UFMODEvent when (isNone || saveAudio) && pointer.Object.Value is UFMODEvent fmodEvent:
            {
                var extractedSounds = FmodProvider.ExtractEventSounds(fmodEvent);
                var directory = Path.GetDirectoryName(Provider.FixPath(fmodEvent.Owner?.Name ?? "/FMOD/Desktop/"));
                foreach (var sound in extractedSounds)
                {
                    SaveAndPlaySound(cancellationToken, Path.Combine(directory, sound.Name).Replace("\\", "/"), sound.Extension, sound.Data, saveAudio, updateUi);
                }
                return false;
            }
            case UFMODBank when (isNone || saveAudio) && pointer.Object.Value is UFMODBank fmodBank:
            {
                var extractedSounds = FmodProvider.ExtractBankSounds(fmodBank);
                var directory = Path.GetDirectoryName(Provider.FixPath(fmodBank.Owner?.Name ?? "/FMOD/Desktop/"));
                foreach (var sound in extractedSounds)
                {
                    SaveAndPlaySound(cancellationToken, Path.Combine(directory, sound.Name).Replace("\\", "/"), sound.Extension, sound.Data, saveAudio, updateUi);
                }
                return false;
            }
            case USoundAtomCueSheet or UAtomCueSheet or USoundAtomCue or UAtomWaveBank when (isNone || saveAudio) && pointer.Object.Value is UObject atomObject:
            {
                var extractedSounds = atomObject switch
                {
                    USoundAtomCueSheet cueSheet => CriWareProvider.ExtractCriWareSounds(cueSheet),
                    UAtomCueSheet cueSheet => CriWareProvider.ExtractCriWareSounds(cueSheet),
                    USoundAtomCue cue => CriWareProvider.ExtractCriWareSounds(cue),
                    UAtomWaveBank awb => CriWareProvider.ExtractCriWareSounds(awb),
                    _ => []
                };

                var directory = Path.GetDirectoryName(Provider.FixPath(atomObject.Owner?.Name ?? "/Criware/"));
                foreach (var sound in extractedSounds)
                {
                    SaveAndPlaySound(cancellationToken, Path.Combine(directory, sound.Name).Replace("\\", "/"), sound.Extension, sound.Data, saveAudio, updateUi);
                }
                return false;
            }
            case USQEXSEADSoundBank or USQEXSEADSound when (isNone || saveAudio) && pointer.Object.Value is UObject squareEnixObject:
            {
                var data = squareEnixObject switch
                {
                    USQEXSEADSoundBank sqexSoundBank => sqexSoundBank.SQEXSoundBankData?.ReadDataOnce() ?? [],
                    USQEXSEADSound sqexSound => sqexSound.SQEXSoundData?.ReadDataOnce() ?? [],
                    _ => [],
                };
                var sabPath = string.IsNullOrEmpty(packageDirectory)
                    ? squareEnixObject.Name
                    : Path.Combine(packageDirectory, squareEnixObject.Name);
                var extractedSounds = AudioPlayerViewModel.ExtractSquareEnixAudio(sabPath, data);
                foreach (var soundPath in extractedSounds)
                {
                    SaveAndPlaySound(cancellationToken, soundPath, "wav", File.ReadAllBytes(soundPath), saveAudio, updateUi);
                }
                return false;
            }
            case UAkMediaAssetData when isNone || saveAudio:
            case USoundWave when isNone || saveAudio:
            {
                // If UAkMediaAsset exists in the same package it should be used to handle the audio instead (because it contains actual audio name)
                if (pointer.Object.Value is UAkMediaAssetData dataObj && dataObj.Outer.Object.Value is UAkMediaAsset)
                    return false;

                var shouldDecompress = UserSettings.Default.CompressedAudioMode == ECompressedAudio.PlayDecompressed;
                pointer.Object.Value.Decode(shouldDecompress, out var audioFormat, out var data);
                var hasAf = !string.IsNullOrEmpty(audioFormat);
                if (data == null || !hasAf)
                {
                    if (hasAf) FLogger.Append(ELog.Warning, () => FLogger.Text($"Unsupported audio format '{audioFormat}'", Constants.WHITE, true));
                    return false;
                }

                SaveAndPlaySound(cancellationToken, packagePath, audioFormat, data, saveAudio, updateUi);
                return false;
            }
            case UAkMediaAsset when (isNone || saveAudio) && pointer.Object.Value is UAkMediaAsset akMediaAsset:
            {
                var audioName = akMediaAsset.MediaName ?? akMediaAsset.Name;
                var outputPath = string.IsNullOrEmpty(packageDirectory)
                    ? audioName
                    : Path.Combine(packageDirectory, audioName);
                if (akMediaAsset.CurrentMediaAssetData?.ResolvedObject?.Object?.Value is UAkMediaAssetData akMediaAssetData)
                {
                    var shouldDecompress = UserSettings.Default.CompressedAudioMode is ECompressedAudio.PlayDecompressed;
                    akMediaAssetData.Decode(shouldDecompress, out var audioFormat, out var data);

                    SaveAndPlaySound(cancellationToken, outputPath, audioFormat, data, saveAudio, updateUi);
                }
                return false;
            }
            case UAkAudioEventData when (isNone || saveAudio) && pointer.Object.Value is UAkAudioEventData akAudioEventData:
            {
                var shouldDecompress = UserSettings.Default.CompressedAudioMode is ECompressedAudio.PlayDecompressed;
                foreach (var mediaIndex in akAudioEventData.MediaList)
                {
                    if (mediaIndex?.Object?.Value is UAkMediaAsset akMediaAsset)
                    {
                        if (akMediaAsset.CurrentMediaAssetData?.ResolvedObject?.Object?.Value is UAkMediaAssetData akMediaAssetData)
                        {
                            var audioName = akMediaAsset.MediaName ?? $"{akAudioEventData.Outer.Name} ({akMediaAsset.ID})";
                            var outputPath = string.IsNullOrEmpty(packageDirectory)
                                ? audioName
                                : Path.Combine(packageDirectory, audioName);
                            akMediaAssetData.Decode(shouldDecompress, out var audioFormat, out var data);

                            SaveAndPlaySound(cancellationToken, outputPath, audioFormat, data, saveAudio, updateUi);
                        }
                    }
                }
                return false;
            }
            // Borderlands 3
            case UDialogPerformanceData when (isNone || saveAudio) && pointer.Object.Value is UDialogPerformanceData dialogPerformanceData:
            {
                var extractedSounds = WwiseProvider.ExtractDialogBorderlands3(dialogPerformanceData);
                foreach (var sound in extractedSounds)
                {
                    SaveAndPlaySound(cancellationToken, sound.OutputPath, sound.Extension, sound.Data?.GetData() ?? [], saveAudio, updateUi);
                }
                return false;
            }
            // Borderlands 4
            case UFaceFXAnimSet when (isNone || saveAudio) && pointer.Object.Value is UFaceFXAnimSet faceFXAnimSet:
            {
                if (Provider.Versions.Game is not EGame.GAME_Borderlands4)
                    return false;

                var ownerDirectory = WwiseProvider.GetOwnerDirectory(faceFXAnimSet);
                foreach (var faceFXAnimData in faceFXAnimSet.FaceFXAnimDataList)
                {
                    var extractedSounds = WwiseProvider.ExtractAudioEventBorderlands4(ownerDirectory, faceFXAnimData.ID.Name, false);
                    foreach (var sound in extractedSounds)
                    {
                        SaveAndPlaySound(cancellationToken, sound.OutputPath, sound.Extension, sound.Data?.GetData() ?? [], saveAudio, updateUi);
                    }
                }

                return false;
            }
            // Borderlands 4
            case UGbxGraphAsset when (isNone || saveAudio) && pointer.Object.Value is UGbxGraphAsset gbxGraphAsset:
            {
                var ownerDirectory = WwiseProvider.GetOwnerDirectory(gbxGraphAsset);
                foreach (var (eventName, useSoundTag) in GbxAudioUtil.GetAndClearEvents())
                {
                    var extractedSounds = WwiseProvider.ExtractAudioEventBorderlands4(ownerDirectory, eventName, useSoundTag);
                    foreach (var sound in extractedSounds)
                    {
                        SaveAndPlaySound(cancellationToken, sound.OutputPath, sound.Extension, sound.Data?.GetData() ?? [], saveAudio, updateUi);
                    }
                }

                return false;
            }
            // LEGO® Batman™: Legacy of the Dark Knight
            case UWubAudioEvent when (isNone || saveAudio) && pointer.Object.Value is UWubAudioEvent wubAudioEvent:
            {
                var extractedSounds = WwiseProvider.ExtractWubAudioEventSounds(wubAudioEvent);
                foreach (var sound in extractedSounds)
                {
                    SaveAndPlaySound(cancellationToken, sound.OutputPath, sound.Extension, sound.Data?.GetData() ?? [], saveAudio, updateUi);
                }
                return false;
            }
            case UWubDialogueEvent when (isNone || saveAudio) && pointer.Object.Value is UWubDialogueEvent wubDialogueEvent:
            {
                var files = wubDialogueEvent.Wems
                    .SelectMany(wem => Provider.Files.Values.Where(file => file.Path.EndsWith(wem.Text + ".wem", StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                foreach (var entry in files)
                {
                    SaveAndPlaySound(cancellationToken, entry.PathWithoutExtension, entry.Extension, entry.Read(), saveAudio, updateUi);
                }
                return false;
            }
            case UWorld when isNone && UserSettings.Default.PreviewWorlds:
            case UBlueprintGeneratedClass when isNone && UserSettings.Default.PreviewWorlds && TabControl.SelectedTab.ParentExportType switch
            {
                "JunoBuildInstructionsItemDefinition" => true,
                "JunoBuildingSetAccountItemDefinition" => true,
                "JunoBuildingPropAccountItemDefinition" => true,
                _ => false
            }:
            case UPaperSprite when isNone && UserSettings.Default.PreviewMaterials:
            case UStaticMesh when isNone && UserSettings.Default.PreviewStaticMeshes:
            case USkeletalMesh when isNone && UserSettings.Default.PreviewSkeletalMeshes:
            case USkeleton when isNone && UserSettings.Default.SaveSkeletonAsMesh:
            case UMaterialInstance when isNone && UserSettings.Default.PreviewMaterials && !ModelIsOverwritingMaterial &&
                                        !(Provider.ProjectName.Equals("FortniteGame", StringComparison.OrdinalIgnoreCase) &&
                                          (pkg.Name.Contains("/MI_OfferImages/", StringComparison.OrdinalIgnoreCase) ||
                                           pkg.Name.Contains("/RenderSwitch_Materials/", StringComparison.OrdinalIgnoreCase) ||
                                           pkg.Name.Contains("/MI_BPTile/", StringComparison.OrdinalIgnoreCase))):
            {
                if (SnooperViewer.TryLoadExport(cancellationToken, dummy, pointer.Object))
                    SnooperViewer.Run();
                return true;
            }
            case UMaterialInstance when isNone && ModelIsOverwritingMaterial && pointer.Object.Value is UMaterialInstance m:
            {
                SnooperViewer.Renderer.Swap(m);
                SnooperViewer.Run();
                return true;
            }
            case UAnimSequenceBase when isNone && UserSettings.Default.PreviewAnimations || ModelIsWaitingAnimation:
            {
                // animate all animations using their specified skeleton or when we explicitly asked for a loaded model to be animated (ignoring whether we wanted to preview animations)
                SnooperViewer.Renderer.Animate(pointer.Object.Value);
                SnooperViewer.Run();
                return true;
            }
            case UStaticMesh when HasFlag(bulk, EBulkType.Meshes):
            case USkeletalMesh when HasFlag(bulk, EBulkType.Meshes):
            case USkeleton when UserSettings.Default.SaveSkeletonAsMesh && HasFlag(bulk, EBulkType.Meshes):
            // case UMaterialInstance when HasFlag(bulk, EBulkType.Materials): // read the fucking json
            case UAnimSequenceBase when HasFlag(bulk, EBulkType.Animations):
            {
                SaveExport(pointer.Object.Value, updateUi);
                return true;
            }
            default:
            {
                if (!isNone && !saveTextures) return false;

                using var cPackage = new CreatorPackage(pkg.Name, dummy.ExportType, pointer.Object, UserSettings.Default.CosmeticStyle);
                if (!cPackage.TryConstructCreator(out var creator))
                    return false;

                creator.ParseForInfo();
                tab.AddImage(pointer.Object.Value.Name, false, creator.Draw(), saveTextures, updateUi);
                return true;

            }
        }
    }

    public void ShowMetadata(GameFile entry)
    {
        ApplicationService.ApplicationView.IsAssetsExplorerVisible = false;

        var package = Provider.LoadPackage(entry);

        if (TabControl.CanAddTabs) TabControl.AddTab(entry);
        else TabControl.SelectedTab.SoftReset(entry);

        TabControl.SelectedTab.TitleExtra = "Metadata";
        TabControl.SelectedTab.Highlighter = AvalonExtensions.HighlighterSelector("");

        TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(package, Formatting.Indented), false, false);
    }

    /// <summary>
    /// exports the package's metadata (same content shown by ShowMetadata) to disk as its own json file,
    /// either from the "Export Metadata" button/context menu or automatically when MetadataExportMode is AutoExport
    /// </summary>
    public void ExportMetadata(GameFile entry, bool updateUi = true)
    {
        var fileName = $"{entry.NameWithoutExtension}.metadata.json";
        var path = Path.Combine(UserSettings.Default.PropertiesDirectory,
            UserSettings.Default.KeepDirectoryStructure ? entry.Directory : "", fileName).Replace('\\', '/');

        if (IsAlreadyExported(path, fileName, updateUi)) return;

        try
        {
            var package = Provider.LoadPackage(entry);
            Directory.CreateDirectory(path.SubstringBeforeLast('/'));

            var text = JsonConvert.SerializeObject(package, Formatting.Indented);
            if (UserSettings.Default.ConvertUint64ToFloat)
            {
                try { text = Uint64FloatConverter.Convert(text); }
                catch (Exception e) { Log.Warning(e, "Failed to convert uint64 floats in {FileName} metadata, saving as-is", entry.Name); }
            }

            if (IsOverwriteProtected(path, fileName, Encoding.UTF8.GetByteCount(text), updateUi)) return;

            File.WriteAllText(path, text);

            Interlocked.Increment(ref ExportedCount);
            Log.Information("Successfully exported metadata for {FileName}", entry.Name);
            if (updateUi)
            {
                FLogger.Append(ELog.Information, () =>
                {
                    FLogger.Text("Successfully exported metadata ", Constants.WHITE);
                    FLogger.Link(fileName, path, true);
                });
            }
        }
        catch (Exception e)
        {
            Interlocked.Increment(ref FailedExportCount);
            Log.Error(e, "{FileName} metadata could not be exported", entry.Name);
            if (updateUi)
                FLogger.Append(ELog.Error, () => FLogger.Text($"Could not export metadata for '{entry.Name}'", Constants.WHITE, true));
        }
    }

    /// <summary>
    /// centralized "already exported" check used by every export entry point in this class;
    /// per SkipAlreadyExportedMode, logs a warning with a clickable link to the containing folder
    /// (same UX as a successful export) and returns true when the file should be skipped
    /// </summary>
    private bool IsAlreadyExported(string path, string label, bool updateUi)
    {
        if (!Helper.IsAlreadyExported(path))
            return false;

        Interlocked.Increment(ref SkippedExportCount);
        Log.Information("Skipped '{FileName}': already exported", label);
        if (updateUi)
        {
            FLogger.Append(ELog.Warning, () =>
            {
                FLogger.Text("Already exported ", Constants.WHITE);
                FLogger.Link(label, path, true);
            });
        }
        return true;
    }

    /// <summary>
    /// centralized overwrite-protection check used by every export entry point in this class;
    /// only relevant when SkipAlreadyExportedMode is Disabled. Refuses the write and logs a warning
    /// (same clickable-folder UX as the other checks) when a file already exists at this path with a
    /// different size than what's about to be written.
    /// </summary>
    private bool IsOverwriteProtected(string path, string label, long newContentSize, bool updateUi)
    {
        if (!Helper.IsOverwriteProtected(path, newContentSize, out var existingSize))
            return false;

        Interlocked.Increment(ref ProtectedExportCount);
        Log.Warning("Blocked '{FileName}': existing file is {ExistingSize} bytes, new export would be {NewSize} bytes - Overwrite Protection is on", label, existingSize, newContentSize);
        if (updateUi)
        {
            FLogger.Append(ELog.Warning, () =>
            {
                FLogger.Text("Files are different and Overwrite Protection is on, refusing to overwrite ", Constants.WHITE);
                FLogger.Link(label, path, true);
            });
        }
        return true;
    }

    public void FindReferences(GameFile entry)
    {
        var refs = Provider.ScanForPackageRefs(entry);
        Application.Current.Dispatcher.Invoke(delegate
        {
            var refView = Helper.GetWindow<SearchView>("Search For Packages", () => new SearchView().Show());
            refView.ChangeCollection(ESearchViewTab.RefView, refs, entry);
            refView.FocusTab(ESearchViewTab.RefView);
        });
    }


    public bool Decompile(GameFile entry, bool AddTab = true)
    {
        if (TabControl.CanAddTabs && AddTab)
        {
            ApplicationService.ApplicationView.IsAssetsExplorerVisible = false;
            TabControl.AddTab(entry);
        }
        else TabControl.SelectedTab.SoftReset(entry);

        TabControl.SelectedTab.TitleExtra = "Decompiled";
        TabControl.SelectedTab.Highlighter = AvalonExtensions.HighlighterSelector("cpp");

        UClassCookedMetaData cookedMetaData = null;
        try
        {
            var editorPkg = Provider.LoadPackage(entry.Path.Replace(".uasset", ".o.uasset"));
            cookedMetaData = editorPkg.GetExport<UClassCookedMetaData>("CookedClassMetaData");
        }
        catch
        {
            // ignored
        }

        var cppList = new List<string>();
        var pkg = Provider.LoadPackage(entry);
        for (var i = 0; i < pkg.ExportMapLength; i++)
        {
            var pointer = new FPackageIndex(pkg, i + 1).ResolvedObject;
            if (pointer?.Object is null && pointer.Class?.Object?.Value is null)
                continue;

            var dummy = ((AbstractUePackage) pkg).ConstructObject(pointer.Class, pkg);
            if (dummy is not UClass || pointer.Object.Value is not UClass blueprint)
                continue;

            cppList.Add(blueprint.DecompileBlueprintToPseudo(cookedMetaData));
        }

        if (cppList.Count == 0) return false;
        var cpp = cppList.Count > 1 ? string.Join("\n\n", cppList) : cppList.FirstOrDefault() ?? string.Empty;
        if (entry.Path.Contains("_Verse.uasset"))
        {
            cpp = Regex.Replace(cpp, "__verse_0x[a-fA-F0-9]{8}_", ""); // UnmangleCasedName
        }
        cpp = Regex.Replace(cpp, @"CallFunc_([A-Za-z0-9_]+)_ReturnValue", "$1");
        cpp = Regex.Replace(cpp, @"K2Node_DynamicCast_([A-Za-z0-9_]+)", "$1");
        cpp = Regex.Replace(cpp, @"K2Node_([A-Za-z0-9_]+)", "$1");

        TabControl.SelectedTab.SetDocumentText(cpp, false, false);
        return true;
    }

    private void SaveAndPlaySound(CancellationToken cancellationToken, string fullPath, string ext, byte[] data, bool saveAudio, bool updateUi)
    {
        if (fullPath.StartsWith('/')) fullPath = fullPath[1..];
        var extLower = ext.ToLowerInvariant();
        var baseFilePath = UserSettings.Default.KeepDirectoryStructure ? fullPath : fullPath.SubstringAfterLast('/');
        var combinedPath = Path.Combine(UserSettings.Default.AudioDirectory, baseFilePath);
        var savedAudioPath = Path.ChangeExtension(combinedPath, extLower).Replace('\\', '/');

        if (saveAudio)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var predictedPath = UserSettings.Default.ConvertAudioOnBulkExport && extLower is not "wav"
                ? Path.ChangeExtension(savedAudioPath, "wav")
                : savedAudioPath;
            if (IsAlreadyExported(predictedPath, Path.GetFileName(predictedPath), updateUi)) return;

            var directory = Path.GetDirectoryName(savedAudioPath);
            Directory.CreateDirectory(directory);

            bool conversionSuccess = true;
            if (UserSettings.Default.ConvertAudioOnBulkExport && extLower is not "wav")
            {
                if (AudioPlayerViewModel.TryConvert(savedAudioPath, data, extLower, out string wavFilePath))
                    savedAudioPath = wavFilePath;
                else
                {
                    Interlocked.Increment(ref FailedExportCount);
                    return;
                }
            }
            else
            {
                if (IsOverwriteProtected(savedAudioPath, Path.GetFileName(savedAudioPath), data.LongLength, updateUi)) return;

                using var stream = new FileStream(savedAudioPath, FileMode.Create, FileAccess.Write);
                stream.Write(data);
            }

            Interlocked.Increment(ref ExportedCount);
            Log.Information("Successfully saved {FilePath}", savedAudioPath);
            if (updateUi && conversionSuccess)
            {
                FLogger.Append(ELog.Information, () =>
                {
                    FLogger.Text("Successfully saved ", Constants.WHITE);
                    FLogger.Link(Path.GetFileName(savedAudioPath), savedAudioPath, true);
                });
            }

            return;
        }

        if (!updateUi)
            return;

        // TODO
        // since we are currently in a thread, the audio player's lifetime (memory-wise) will keep the current thread up and running until fmodel itself closes
        // the solution would be to kill the current thread at this line and then open the audio player without "Application.Current.Dispatcher.Invoke"
        // but the ThreadWorkerViewModel is an idiot and doesn't understand we want to kill the current thread inside the current thread and continue the code
        Application.Current.Dispatcher.Invoke(delegate
        {
            var audioPlayer = Helper.GetWindow<AudioPlayer>("Audio Player", () => new AudioPlayer().Show());
            audioPlayer.Load(data, savedAudioPath);
        });
    }

    private void SaveExport(UObject export, bool updateUi = true)
    {
        // best-effort pre-check: models/animations/materials can produce several files with
        // different extensions depending on the chosen format, so we look for *any* file already
        // sharing the export's expected base name in its expected folder before doing the heavy work
        if (UserSettings.Default.SkipAlreadyExportedMode != ESkipAlreadyExported.Disabled)
        {
            var packagePath = (export.Owner?.Provider?.FixPath(export.Owner?.Name ?? export.GetPathName()) ?? export.GetPathName()).SubstringBeforeLast('.');
            var relativePath = CUE4Parse_Conversion.ExporterBase.GetExportSavePath(packagePath, export.Name);
            var expectedDir = Path.Combine(UserSettings.Default.ModelDirectory, relativePath.SubstringBeforeLast('/')).Replace('\\', '/');
            var existingMatch = Directory.Exists(expectedDir)
                ? Directory.EnumerateFiles(expectedDir, $"{export.Name}.*")
                    .FirstOrDefault(f => UserSettings.Default.SkipAlreadyExportedMode != ESkipAlreadyExported.ByNameAndSize || new FileInfo(f).Length > 0)
                : null;
            if (existingMatch != null)
            {
                IsAlreadyExported(existingMatch, export.Name, updateUi);
                return;
            }
        }

        var toSave = new Exporter(export, UserSettings.Default.ExportOptions);
        var toSaveDirectory = new DirectoryInfo(UserSettings.Default.ModelDirectory);
        if (toSave.TryWriteToDir(toSaveDirectory, out var label, out var savedFilePath))
        {
            Interlocked.Increment(ref ExportedCount);
            Log.Information("Successfully saved {FilePath}", savedFilePath);
            if (updateUi)
            {
                FLogger.Append(ELog.Information, () =>
                {
                    FLogger.Text("Successfully saved ", Constants.WHITE);
                    FLogger.Link(label, savedFilePath, true);
                });
            }
        }
        else
        {
            Interlocked.Increment(ref FailedExportCount);
            Log.Error("{FileName} could not be saved", export.Name);
            FLogger.Append(ELog.Error, () => FLogger.Text($"Could not save '{export.Name}'", Constants.WHITE, true));
        }
    }

    public void ExportData(GameFile entry, bool updateUi = true)
    {
        // most raw exports are a single file mapping 1:1 to the source entry, so this predicted path
        // covers the common case and lets us skip before paying for TrySavePackage at all. Packages
        // that expand into several files (bulk data, umaps, ...) still get the per-file check below
        // as a safety net, which can only run after TrySavePackage since their names aren't known upfront.
        var primaryPath = Path.Combine(UserSettings.Default.RawDataDirectory, UserSettings.Default.KeepDirectoryStructure ? entry.Directory : "", entry.Name).Replace('\\', '/');
        if (IsAlreadyExported(primaryPath, entry.Name, updateUi)) return;

        if (Provider.TrySavePackage(entry, out var assets))
        {
            string path = UserSettings.Default.RawDataDirectory;
            var anySkipped = false;
            var anyProtected = false;
            var anyWritten = false;
            // Sequential: already nested under export LongRunning workers; unlimited Parallel.ForEach
            // multiplied concurrency past Export Max Concurrent Workers.
            foreach (var kvp in assets)
            {
                var targetPath = Path.Combine(UserSettings.Default.RawDataDirectory, UserSettings.Default.KeepDirectoryStructure ? kvp.Key : kvp.Key.SubstringAfterLast('/')).Replace('\\', '/');
                if (Helper.IsAlreadyExported(targetPath))
                {
                    anySkipped = true;
                    if (!anyWritten) path = targetPath;
                    continue;
                }

                if (Helper.IsOverwriteProtected(targetPath, kvp.Value.LongLength, out _))
                {
                    anyProtected = true;
                    if (!anyWritten) path = targetPath;
                    continue;
                }

                path = targetPath;
                Directory.CreateDirectory(path.SubstringBeforeLast('/'));
                File.WriteAllBytes(path, kvp.Value);
                anyWritten = true;
            }

            if (anyWritten)
            {
                Interlocked.Increment(ref ExportedCount);
                Log.Information("{FileName} successfully exported", entry.Name);
                if (updateUi)
                {
                    FLogger.Append(ELog.Information, () =>
                    {
                        FLogger.Text("Successfully exported ", Constants.WHITE);
                        FLogger.Link(entry.Name, path, true);
                    });
                }
            }
            else if (anySkipped)
            {
                IsAlreadyExported(path, entry.Name, updateUi);
            }
            else if (anyProtected)
            {
                Interlocked.Increment(ref ProtectedExportCount);
                Log.Warning("Blocked '{FileName}': existing file size differs from the new export and Overwrite Protection is on", entry.Name);
                if (updateUi)
                {
                    FLogger.Append(ELog.Warning, () =>
                    {
                        FLogger.Text("Files are different and Overwrite Protection is on, refusing to overwrite ", Constants.WHITE);
                        FLogger.Link(entry.Name, path, true);
                    });
                }
            }
        }
        else
        {
            Interlocked.Increment(ref FailedExportCount);
            Log.Error("{FileName} could not be exported", entry.Name);
            if (updateUi)
                FLogger.Append(ELog.Error, () => FLogger.Text($"Could not export '{entry.Name}'", Constants.WHITE, true));
        }
    }

    private static bool HasFlag(EBulkType a, EBulkType b)
    {
        return (a & b) == b;
    }
}
