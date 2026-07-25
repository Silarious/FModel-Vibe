using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CUE4Parse_Conversion.Textures.BC;
using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.UE4.Lua.unluac;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.VirtualFileSystem;
using FModel.Extensions;
using FModel.Framework;
using FModel.Services;
using FModel.Settings;
using FModel.ViewModels.Commands;
using FModel.Views;
using FModel.Views.Resources.Controls;
using MessageBox = AdonisUI.Controls.MessageBox;
using MessageBoxButton = AdonisUI.Controls.MessageBoxButton;
using MessageBoxImage = AdonisUI.Controls.MessageBoxImage;
using Serilog;

namespace FModel.ViewModels;

public class ApplicationViewModel : ViewModel
{
    private EBuildKind _build;
    public EBuildKind Build
    {
        get => _build;
        private init
        {
            SetProperty(ref _build, value);
            RaisePropertyChanged(nameof(TitleExtra));
        }
    }

    private FStatus _status;
    public FStatus Status
    {
        get => _status;
        private init => SetProperty(ref _status, value);
    }

    public IEnumerable<EAssetCategory> Categories { get; } = AssetCategoryExtensions.GetBaseCategories();

    private bool _isAssetsExplorerVisible;
    public bool IsAssetsExplorerVisible
    {
        get => _isAssetsExplorerVisible;
        set
        {
            if (value && !UserSettings.Default.FeaturePreviewNewAssetExplorer)
                return;

            SetProperty(ref _isAssetsExplorerVisible, value);
        }
    }

    private int _selectedLeftTabIndex;
    public int SelectedLeftTabIndex
    {
        get => _selectedLeftTabIndex;
        set
        {
            if (value is < 0 or > 2) return;
            SetProperty(ref _selectedLeftTabIndex, value);
        }
    }

    public RightClickMenuCommand RightClickMenuCommand => _rightClickMenuCommand ??= new RightClickMenuCommand(this);
    private RightClickMenuCommand _rightClickMenuCommand;
    public MenuCommand MenuCommand => _menuCommand ??= new MenuCommand(this);
    private MenuCommand _menuCommand;
    public CopyCommand CopyCommand => _copyCommand ??= new CopyCommand(this);
    private CopyCommand _copyCommand;

    public string InitialWindowTitle => $"FModel ({Constants.APP_SHORT_COMMIT_ID} - {Constants.APP_BUILD_DATE:MMM d, yyyy})";
    public string GameDisplayName => CUE4Parse.Provider.GameDisplayName ?? "Unknown";
    public string TitleExtra => $"({UserSettings.Default.CurrentDir.UeVersion}){(Build != EBuildKind.Release ? $" ({Build})" : "")}";

    public LoadingModesViewModel LoadingModes { get; }
    public CustomDirectoriesViewModel CustomDirectories { get; }
    public CUE4ParseViewModel CUE4Parse { get; }
    public SettingsViewModel SettingsView { get; }
    public ProfilesViewModel ProfilesView { get; }
    public ExportQueueViewModel ExportQueue { get; }
    public AesManagerViewModel AesManager { get; }
    public AudioPlayerViewModel AudioPlayer { get; }

    public ApplicationViewModel()
    {
        Status = new FStatus();
#if DEBUG
        Build = EBuildKind.Debug;
#elif RELEASE
        Build = EBuildKind.Release;
#else
        Build = EBuildKind.Unknown;
#endif
        LoadingModes = new LoadingModesViewModel();

        UserSettings.Default.CurrentDir = AvoidEmptyGameDirectory(false);
        if (UserSettings.Default.CurrentDir is null)
        {
            //If no game is selected, many things will break before a shutdown request is processed in the normal way.
            //A hard exit is preferable to an unhandled exception in this case
            Environment.Exit(0);
        }

        CUE4Parse = new CUE4ParseViewModel();
        CUE4Parse.Provider.VfsRegistered += (sender, count) =>
        {
            if (sender is not IAesVfsReader reader) return;
            Status.UpdateStatusLabel($"{count} Archives ({reader.Name})", "Registered");
            CUE4Parse.GameDirectory.Add(reader);
        };
        CUE4Parse.Provider.VfsMounted += (sender, count) =>
        {
            if (sender is not IAesVfsReader reader) return;
            Status.UpdateStatusLabel($"{count:N0} Packages ({reader.Name})", "Mounted");
            CUE4Parse.GameDirectory.Verify(reader);
        };
        CUE4Parse.Provider.VfsUnmounted += (sender, _) =>
        {
            if (sender is not IAesVfsReader reader) return;
            CUE4Parse.GameDirectory.Disable(reader);
        };
        CustomDirectories = new CustomDirectoriesViewModel();
        SettingsView = new SettingsViewModel();
        ProfilesView = new ProfilesViewModel();
        ExportQueue = new ExportQueueViewModel();
        AesManager = new AesManagerViewModel(CUE4Parse);
        AudioPlayer = new AudioPlayerViewModel();

        Status.SetStatus(EStatusKind.Ready);
    }

    public DirectorySettings AvoidEmptyGameDirectory(bool bAlreadyLaunched)
    {
        ProfileManager.EnsureLiveDefaults();

        // Prefer the named profile on cold start so AppSettings.GameDirectory can't stick us on another game.
        if (!bAlreadyLaunched)
        {
            var profileName = UserSettings.Default.CurrentProfileName;
            if (!string.IsNullOrWhiteSpace(profileName) && ProfileManager.Exists(profileName))
            {
                if (ProfileManager.Load(profileName) && UserSettings.Default.CurrentDir != null)
                {
                    foreach (var ep in UserSettings.Default.CurrentDir.Endpoints ?? [])
                        ep.EnsureConfiguredValidity();
                    return UserSettings.Default.CurrentDir;
                }
            }

            var gameDirectory = ProfileManager.CanonicalizeGameDirectory(UserSettings.Default.GameDirectory ?? "");
            if (!string.IsNullOrWhiteSpace(gameDirectory) &&
                ProfileManager.TryGetPerDirectory(gameDirectory, out var currentDir))
            {
                ProfileManager.SetPerDirectory(gameDirectory, currentDir);
                UserSettings.Default.GameDirectory = ProfileManager.CanonicalizeGameDirectory(currentDir.GameDirectory ?? gameDirectory);
                currentDir.GameDirectory = UserSettings.Default.GameDirectory;
                foreach (var ep in currentDir.Endpoints ?? [])
                    ep.EnsureConfiguredValidity();
                return currentDir;
            }
        }

        var previousProfile = UserSettings.Default.CurrentProfileName;
        var previousDir = ProfileManager.CanonicalizeGameDirectory(
            UserSettings.Default.CurrentDir?.GameDirectory ?? UserSettings.Default.GameDirectory ?? "");

        Status.SetStatus(EStatusKind.Configuring);
        var gameLauncherViewModel = new GameSelectorViewModel(previousDir);
        var result = new DirectorySelector(gameLauncherViewModel).ShowDialog();
        Status.SetStatus(EStatusKind.Ready);
        if (!result.HasValue || !result.Value) return null;

        var selected = gameLauncherViewModel.SelectedDirectory;
        if (selected == null) return null;

        var selectedProfile = ProfileManager.EnsureFromDirectory(selected);
        if (string.IsNullOrWhiteSpace(selectedProfile))
            selectedProfile = ProfileManager.NormalizeProfileName(selected.GameName);

        if (string.IsNullOrWhiteSpace(selectedProfile))
            return null;

        // Always Load so export dirs / endpoints / LIVE tokens apply from the profile file.
        ProfileManager.Load(selectedProfile);
        ProfilesView?.Refresh();

        var newDir = ProfileManager.CanonicalizeGameDirectory(
            UserSettings.Default.CurrentDir?.GameDirectory ?? UserSettings.Default.GameDirectory ?? "");
        var providerMustRebuild = bAlreadyLaunched && (
            !string.Equals(previousProfile, selectedProfile, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(previousDir, newDir, StringComparison.OrdinalIgnoreCase) ||
            // Empty previous profile name used to skip restart → Fortnite "Ready" with no streamed paks.
            string.IsNullOrWhiteSpace(previousProfile));

        if (providerMustRebuild)
        {
            RestartWithWarning();
            return null;
        }

        return UserSettings.Default.CurrentDir;
    }

    public DirectorySettings AddGameDirectory(string directory)
    {
        if (Status.Kind is EStatusKind.Configuring)
        {
            var directorySelector = Helper.GetWindow<DirectorySelector>("Profile Selector", null);
            directorySelector?.AddManualGame(directory);
            return null;
        }
        else
        {
            Status.SetStatus(EStatusKind.Configuring);
            var gameLauncherViewModel = new GameSelectorViewModel(UserSettings.Default.GameDirectory);
            var directorySelector = new DirectorySelector(gameLauncherViewModel);
            directorySelector.AddManualGame(directory);
            var result = directorySelector.ShowDialog();
            Status.SetStatus(EStatusKind.Ready);
            if (!result.HasValue || !result.Value)
                return null;

            var selected = gameLauncherViewModel.SelectedDirectory;
            if (selected == null) return null;

            var previousProfile = UserSettings.Default.CurrentProfileName;
            var previousDir = ProfileManager.CanonicalizeGameDirectory(
                UserSettings.Default.CurrentDir?.GameDirectory ?? UserSettings.Default.GameDirectory ?? "");

            var profileName = ProfileManager.EnsureFromDirectory(selected);
            if (!string.IsNullOrWhiteSpace(profileName))
                ProfileManager.Load(profileName);

            ProfilesView?.Refresh();

            var newDir = ProfileManager.CanonicalizeGameDirectory(
                UserSettings.Default.CurrentDir?.GameDirectory ?? selected.GameDirectory ?? "");
            if (!string.Equals(previousProfile, profileName, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(previousDir, newDir, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(previousProfile))
            {
                RestartWithWarning();
                return null;
            }

            return UserSettings.Default.CurrentDir ?? selected;
        }
    }

    public void RestartWithWarning()
    {
        MessageBox.Show("It looks like you just changed something.\nFModel will restart to apply your changes.", "Uh oh, a restart is needed", MessageBoxButton.OK, MessageBoxImage.Warning);
        Restart();
    }

    public void Restart()
    {
        try
        {
            var path = Path.GetFullPath(Environment.GetCommandLineArgs()[0]);
            if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"\"{path}\"",
                    UseShellExecute = true
                });
            }
            else
            {
                // UseShellExecute=true so the restarted GUI actually shows a window.
                // CreateNoWindow + UseShellExecute=false left a headless zombie process.
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
            }
        }
        catch (Exception e)
        {
            Log.Error(e, "Failed to start replacement FModel process");
        }

        try
        {
            Application.Current.Shutdown();
        }
        catch
        {
            // ignored
        }

        // Guaranteed exit if WPF shutdown hangs after the window is gone.
        _ = Task.Run(async () =>
        {
            await Task.Delay(2500);
            Environment.Exit(0);
        });
        Environment.Exit(0);
    }

    public async Task UpdateProvider(bool isLaunch)
    {
        if (!isLaunch && !AesManager.HasChange) return;

        CUE4Parse.ClearProvider();
        await ApplicationService.ThreadWorkerView.Begin(cancellationToken =>
        {
            // InitAes can race archive registration; don't NullRef if it failed earlier.
            var aesKeys = AesManager.AesKeys;
            if (aesKeys == null)
            {
                Log.Warning("UpdateProvider skipped: AES key list is not initialized yet");
                return;
            }

            // TODO: refactor after release, select updated keys only
            var aes = aesKeys.Select(x =>
            {
                cancellationToken.ThrowIfCancellationRequested(); // cancel if needed

                var k = x.Key?.Trim() ?? string.Empty;
                if (k.Length != 66) k = Constants.ZERO_64_CHAR;
                return new KeyValuePair<FGuid, FAesKey>(x.Guid, new FAesKey(k));
            });

            CUE4Parse.LoadVfs(aes);
            AesManager.SetAesKeys();
        });
        AesManager.HasChange = false;
        RaisePropertyChanged(nameof(GameDisplayName));
    }

    /// <summary>
    /// Apply AES from settings (e.g. Profiles tab) and remount archives when the key changed.
    /// </summary>
    public async Task RemountIfAesSettingsChangedAsync()
    {
        if (!AesManager.SyncMainKeyFromSettings()) return;
        await UpdateProvider(false);
    }

    public static async Task InitVgmStream()
    {
        var vgmZipFilePath = Path.Combine(UserSettings.Default.OutputDirectory, ".data", "vgmstream-win.zip");
        var vgmFileInfo = new FileInfo(vgmZipFilePath);

        if (!vgmFileInfo.Exists || vgmFileInfo.LastWriteTimeUtc < DateTime.UtcNow.AddMonths(-4))
        {
            await ApplicationService.ApiEndpointView.DownloadFileAsync("https://github.com/vgmstream/vgmstream/releases/latest/download/vgmstream-win.zip", vgmZipFilePath);
            vgmFileInfo.Refresh();

            if (vgmFileInfo.Length > 0)
            {
                var zipDir = Path.GetDirectoryName(vgmZipFilePath)!;
                await using var zipFs = File.OpenRead(vgmZipFilePath);
                await using var zip = await ZipArchive.CreateAsync(zipFs, ZipArchiveMode.Read, true, null);
                await zip.ExtractToDirectoryAsync(zipDir, true);
            }
            else
            {
                FLogger.Append(ELog.Error, () => FLogger.Text("Could not download vgmstream", Constants.WHITE, true));
            }
        }
    }

    public static async Task InitImGuiSettings(bool forceDownload)
    {
        const string imgui = "imgui.ini";
        var imguiPath = Path.Combine(UserSettings.Default.OutputDirectory, ".data", imgui);

        if (File.Exists(imgui)) File.Move(imgui, imguiPath, true);
        if (File.Exists(imguiPath) && !forceDownload) return;

        await ApplicationService.ApiEndpointView.DownloadFileAsync($"https://cdn.fmodel.app/d/configurations/{imgui}", imguiPath);
        if (new FileInfo(imguiPath).Length == 0)
        {
            FLogger.Append(ELog.Error, () => FLogger.Text("Could not download ImGui settings", Constants.WHITE, true));
        }
    }

    public static async Task InitOodle()
    {
        var oodlePath = Path.Combine(UserSettings.Default.OutputDirectory, ".data", OodleHelper.OODLE_NAME_OLD);
        if (!File.Exists(oodlePath))
        {
            oodlePath = Path.Combine(UserSettings.Default.OutputDirectory, ".data", OodleHelper.OODLE_NAME_CURRENT);
        }

        await OodleHelper.InitializeAsync(oodlePath);
    }

    public static async Task InitZlib()
    {
        var zlibPath = Path.Combine(UserSettings.Default.OutputDirectory, ".data", ZlibHelper.DLL_NAME);
        var zlibFileInfo = new FileInfo(zlibPath);

        if (!zlibFileInfo.Exists || zlibFileInfo.LastWriteTimeUtc < DateTime.UtcNow.AddMonths(-4))
        {
            if (!await ZlibHelper.DownloadDllAsync(zlibPath))
            {
                zlibFileInfo.Refresh();
                if (!zlibFileInfo.Exists) return;
            }
        }

        await ZlibHelper.InitializeAsync(zlibPath);
    }

    public static async Task InitDetex()
    {
        var detexPath = Path.Combine(UserSettings.Default.OutputDirectory, ".data", DetexHelper.DLL_NAME);
        if (File.Exists(DetexHelper.DLL_NAME))
        {
            File.Move(DetexHelper.DLL_NAME, detexPath, true);
        }
        else if (!File.Exists(detexPath))
        {
            await DetexHelper.LoadDllAsync(detexPath);
        }

        DetexHelper.Initialize(detexPath);
    }

    public static async Task InitUnluac()
    {
        var unluacPath = Path.Combine(UserSettings.Default.OutputDirectory, ".data", UnluacHelper.DllName);
        await UnluacHelper.InitializeAsync(unluacPath).ConfigureAwait(false);
        if (UnluacHelper.Instance is null)
            FLogger.Append(ELog.Error, () => FLogger.Text("Failed to download unluac", Constants.WHITE, true));
    }
}
