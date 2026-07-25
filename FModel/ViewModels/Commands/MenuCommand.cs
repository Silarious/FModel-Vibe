using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AdonisUI.Controls;
using CUE4Parse.FileProvider;
using CUE4Parse.GameTypes.ArcRaiders.Encryption.Theia;
using CUE4Parse.UE4.Versions;
using FModel;
using FModel.Extensions;
using FModel.Framework;
using FModel.Services;
using FModel.Settings;
using FModel.ViewModels;
using FModel.Views;
using FModel.Views.Resources.Controls;
using Newtonsoft.Json;
using Ookii.Dialogs.Wpf;
using MessageBox = AdonisUI.Controls.MessageBox;
using MessageBoxButton = AdonisUI.Controls.MessageBoxButton;
using MessageBoxImage = AdonisUI.Controls.MessageBoxImage;

namespace FModel.ViewModels.Commands;

public class MenuCommand : ViewModelCommand<ApplicationViewModel>
{
    public MenuCommand(ApplicationViewModel contextViewModel) : base(contextViewModel)
    {
    }

    public override async void Execute(ApplicationViewModel contextViewModel, object parameter)
    {
        switch (parameter)
        {
            case "Directory_Selector":
                contextViewModel.AvoidEmptyGameDirectory(true);
                break;
            case "Directory_AES":
                Helper.OpenWindow<AdonisWindow>("AES Manager", () => new AesManager().Show());
                break;
            case "Directory_Backup":
                Helper.OpenWindow<AdonisWindow>("Backup Manager", () => new BackupManager(contextViewModel.CUE4Parse.Provider.ProjectName).Show());
                break;
            case "Directory_DiffProfiles":
                Helper.OpenWindow<AdonisWindow>("Diff Checker", () => new ProfileDiffView().Show());
                break;
            case "Directory_ArchivesInfo":
                ApplicationService.ApplicationView.IsAssetsExplorerVisible = false;
                contextViewModel.CUE4Parse.TabControl.AddTab("Archives Info");
                contextViewModel.CUE4Parse.TabControl.SelectedTab.Highlighter = AvalonExtensions.HighlighterSelector("json");
                contextViewModel.CUE4Parse.TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(contextViewModel.CUE4Parse.GameDirectory.DirectoryFiles, Formatting.Indented), false, false);
                break;
            case "Directory_IndexFMDex":
                await IndexArchiveForFMDexAsync(contextViewModel);
                break;
            case "Directory_LoadFortniteLive":
                await LoadFortniteLiveAsync(contextViewModel);
                break;
            case "Directory_CacheLiveArchives":
                await CacheLiveArchivesAsync(contextViewModel);
                break;
            case "Directory_ExportDecryptedPaks":
                await ExportDecryptedPaksAsync(contextViewModel);
                break;
            case "Packages_IndexFMDex":
                await IndexSelectedFolderForFMDexAsync(contextViewModel);
                break;
            case "Views_3dViewer":
                contextViewModel.CUE4Parse.SnooperViewer.Run();
                break;
            case "Views_AudioPlayer":
                Helper.OpenWindow<AdonisWindow>("Audio Player", () => new AudioPlayer().Show());
                break;
            case "Views_ImageMerger":
                Helper.OpenWindow<AdonisWindow>("Image Merger", () => new ImageMerger().Show());
                break;
            case "Settings":
                Helper.OpenWindow<AdonisWindow>("Settings", () => new SettingsView().Show());
                break;
            case "ExportQueue":
                Helper.OpenWindow<AdonisWindow>("Export Queue", () => new ExportQueue().Show());
                break;
            case "Help_About":
                Helper.OpenWindow<AdonisWindow>("About", () => new About().Show());
                break;
            case "Help_Donate":
                Process.Start(new ProcessStartInfo { FileName = Constants.DONATE_LINK, UseShellExecute = true });
                break;
            case "Help_Releases":
                Helper.OpenWindow<AdonisWindow>("Releases", () => new UpdateView().Show());
                break;
            case "Help_BugsReport":
                Process.Start(new ProcessStartInfo { FileName = Constants.ISSUE_LINK, UseShellExecute = true });
                break;
            case "Help_Discord":
                Process.Start(new ProcessStartInfo { FileName = Constants.DISCORD_LINK, UseShellExecute = true });
                break;
            case "ToolBox_Clear_Logs":
                FLogger.ClearLogs();
                break;
            case "ToolBox_Open_Output_Directory":
                Process.Start(new ProcessStartInfo { FileName = UserSettings.Default.OutputDirectory, UseShellExecute = true });
                break;
            // case "ToolBox_Expand_All":
            //     await ApplicationService.ThreadWorkerView.Begin(cancellationToken =>
            //     {
            //         SetFoldersIsExpanded(contextViewModel.CUE4Parse.AssetsFolder, true, cancellationToken);
            //     });
            //     break;
            case "ToolBox_Collapse_All":
                await ApplicationService.ThreadWorkerView.Begin(cancellationToken =>
                {
                    SetFoldersIsExpanded(contextViewModel.CUE4Parse.AssetsFolder, false, cancellationToken);
                });
                break;
            case TreeItem selectedFolder:
                selectedFolder.IsSelected = false;
                selectedFolder.IsSelected = true;
                break;
        }
    }

    private static async Task IndexArchiveForFMDexAsync(ApplicationViewModel contextViewModel)
    {
        var provider = contextViewModel.CUE4Parse?.Provider;
        if (provider == null || provider.Files.Count == 0)
        {
            MessageBox.Show(
                "Mount archives first (AES keys if needed), then try again.",
                "Index Archive in FMDex",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        await ApplicationService.ThreadWorkerView.Begin(cancellationToken =>
        {
            contextViewModel.CUE4Parse.IndexArchiveForFMDex(cancellationToken);
        });
    }

    private static async Task IndexSelectedFolderForFMDexAsync(ApplicationViewModel contextViewModel)
    {
        if (MainWindow.YesWeCats?.AssetsFolderName.SelectedItem is not TreeItem folder)
        {
            MessageBox.Show(
                "Select a folder in the Assets Explorer first.",
                "Index Folder in FMDex",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        await ApplicationService.ThreadWorkerView.Begin(cancellationToken =>
        {
            contextViewModel.CUE4Parse.IndexFolderForFMDex(cancellationToken, folder);
        });
    }

    private static async Task LoadFortniteLiveAsync(ApplicationViewModel contextViewModel)
    {
        var cue = contextViewModel.CUE4Parse;
        if (cue == null)
            return;

        if (cue.Provider is not StreamedFileProvider { LiveGame: "FortniteLive" })
        {
            var gd = ProfileManager.CanonicalizeGameDirectory(UserSettings.Default.GameDirectory ?? "");
            if (gd != Constants._FN_LIVE_TRIGGER)
            {
                MessageBox.Show(
                    "Switch to the Fortnite LIVE profile first (Profile Selector), then run Load Fortnite LIVE…",
                    "Load Fortnite LIVE",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }
        }

        if (cue.HasFortniteLiveManifest)
        {
            MessageBox.Show(
                "Fortnite LIVE archives are already loaded in this session.\n\n" +
                "Restart FModel or switch profiles if you need a fresh download.",
                "Load Fortnite LIVE",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            await cue.LoadFortniteLiveArchivesAsync();
            await contextViewModel.UpdateProvider(true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.GetBaseException().Message,
                "Load Fortnite LIVE failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private static async Task CacheLiveArchivesAsync(ApplicationViewModel contextViewModel)
    {
        var cue = contextViewModel.CUE4Parse;
        if (cue?.Provider is not StreamedFileProvider { LiveGame: "FortniteLive" } || !cue.HasFortniteLiveManifest)
        {
            MessageBox.Show(
                "Load the Fortnite LIVE profile first in this session, then run Save LIVE Archives to Disk.\n\n" +
                "Tip: chunks are already cached under Output\\.data — this command writes full local pak/utoc/ucas files so you can switch to a Fortnite (Cached) profile and skip LIVE streaming on startup.",
                "Save LIVE Archives to Disk",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var bytes = cue.EstimateFortniteLiveCacheBytes();
        var gb = bytes / (1024d * 1024d * 1024d);
        var confirm = MessageBox.Show(
            $"This will download/copy about {gb:0.0} GB of FortniteGame/Content/Paks into a folder you choose " +
            $"(existing complete files are skipped).\n\n" +
            "A Fortnite (Cached) profile will be created. Switch to it after this finishes for fast local startups.\n\nContinue?",
            "Save LIVE Archives to Disk",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
            return;

        var folderBrowser = new VistaFolderBrowserDialog
        {
            Description = "Choose a folder for Fortnite LIVE archives (FortniteGame\\Content\\Paks will be created under it)",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };
        if (folderBrowser.ShowDialog() != true)
            return;

        var root = folderBrowser.SelectedPath;
        string paksDir = null;
        await ApplicationService.ThreadWorkerView.Begin(cancellationToken =>
        {
            var progress = new Progress<(int done, int total, string name)>(p =>
            {
                ApplicationService.ApplicationView.Status.UpdateStatusLabel(
                    $"LIVE cache {p.done}/{p.total}: {Path.GetFileName(p.name)}", "Saving");
            });

            paksDir = cue.CacheFortniteLiveArchivesToDiskAsync(root, progress, cancellationToken)
                .ConfigureAwait(false).GetAwaiter().GetResult();
        });

        if (string.IsNullOrWhiteSpace(paksDir))
            return;

        FLogger.Append(ELog.Information, () =>
            FLogger.Text($"LIVE archives saved. Use Profile Selector → Fortnite (Cached) → {paksDir}", Constants.WHITE, true));

        MessageBox.Show(
            $"Done.\n\nPaks folder:\n{paksDir}\n\nOpen Profile Selector and choose Fortnite (Cached), then restart when prompted.",
            "Save LIVE Archives to Disk",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private static async Task ExportDecryptedPaksAsync(ApplicationViewModel contextViewModel)
    {
        var sourceDir = UserSettings.Default.GameDirectory;
        var isArcRaiders = contextViewModel.CUE4Parse.Provider.Versions.Game is EGame.GAME_ArcRaiders;
        var hasTheia = !string.IsNullOrWhiteSpace(sourceDir) &&
                       Directory.Exists(sourceDir) &&
                       TheiaPakDecryptor.DirectoryHasTheiaMeta(sourceDir);

        if (!isArcRaiders && !hasTheia)
        {
            MessageBox.Show(
                "Export Decrypted Paks requires Theia-encrypted packs (sibling .meta with metadat0 magic)\n" +
                "next to .pak / .ucas / .utoc, or UE version set to Arc Raiders.\n\n" +
                "UE 5.5 + a Paks folder with .meta is enough — you do not need GAME_ArcRaiders.",
                "Export Decrypted Paks",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
        {
            MessageBox.Show("No valid game directory is selected.", "Export Decrypted Paks", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var folderBrowser = new VistaFolderBrowserDialog
        {
            Description = "Choose a folder for decrypted .pak / .ucas (+ .utoc)",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };
        if (folderBrowser.ShowDialog() != true)
            return;

        var destDir = folderBrowser.SelectedPath;
        await ApplicationService.ThreadWorkerView.Begin(cancellationToken =>
        {
            var progress = new Progress<(int Done, int Total, string Name)>(p =>
            {
                ApplicationService.ApplicationView.Status.UpdateStatusLabel(
                    $"Theia decrypt {p.Done}/{p.Total}: {p.Name}", "Export");
            });

            var (decrypted, utocs, skipped) = TheiaPakDecryptor.DecryptDirectory(
                sourceDir, destDir, progress, ct: cancellationToken);

            FLogger.Append(ELog.Information, () =>
                FLogger.Text(
                    $"Exported decrypted packs → {destDir} (decrypted {decrypted}, utoc {utocs}, skipped {skipped})",
                    Constants.WHITE,
                    true));
            ApplicationService.ApplicationView.Status.UpdateStatusLabel(
                $"Exported {decrypted} file(s) (+{utocs} utoc, {skipped} skipped)", "Theia");
        });
    }

    private void SetFoldersIsExpanded(AssetsFolderViewModel root, bool expand, CancellationToken cancellationToken)
    {
        var nodes = new LinkedList<TreeItem>();
        foreach (TreeItem folder in root.Folders)
            nodes.AddLast(folder);

        var current = nodes.First;
        while (current != null)
        {
            var folder = current.Value;

            // Collapse top-down (reduce layout updates)
            if (!expand && folder.IsExpanded)
            {
                folder.IsExpanded = false;
                Thread.Yield();
                cancellationToken.ThrowIfCancellationRequested();
            }

            foreach (var child in folder.Folders)
            {
                nodes.AddLast(child);
            }

            current = current.Next;
        }

        if (!expand) return;

        // Expand bottom-up (reduce layout updates)
        for (var node = nodes.Last; node != null; node = node.Previous)
        {
            node.Value.IsExpanded = true;
            Thread.Yield();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
