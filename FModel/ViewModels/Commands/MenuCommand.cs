using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AdonisUI.Controls;
using CUE4Parse.GameTypes.ArcRaiders.Encryption.Theia;
using CUE4Parse.UE4.Versions;
using FModel.Extensions;
using FModel.Framework;
using FModel.Services;
using FModel.Settings;
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
            case "Directory_ExportDecryptedPaks":
                await ExportDecryptedPaksAsync(contextViewModel);
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

    private static async Task ExportDecryptedPaksAsync(ApplicationViewModel contextViewModel)
    {
        if (contextViewModel.CUE4Parse.Provider.Versions.Game is not EGame.GAME_ArcRaiders)
        {
            MessageBox.Show(
                "Export Decrypted Paks is only available when the UE version is set to Arc Raiders.",
                "Export Decrypted Paks",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var sourceDir = UserSettings.Default.GameDirectory;
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
                sourceDir, destDir, progress, cancellationToken);

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
