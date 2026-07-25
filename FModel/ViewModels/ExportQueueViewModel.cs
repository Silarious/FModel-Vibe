using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FModel.Extensions;
using FModel.Framework;
using FModel.Services;
using FModel.Settings;
using FModel.Views.Resources.Controls;

namespace FModel.ViewModels;

public class ExportQueueViewModel : ViewModel
{
    /// <summary>
    /// User-facing bulk flags shown as checkboxes (excludes Auto and the AllAssets composite).
    /// </summary>
    public static readonly EBulkType[] QueueableBulkTypes =
    [
        EBulkType.Properties,
        EBulkType.Textures,
        EBulkType.Meshes,
        EBulkType.Animations,
        EBulkType.Audio,
        EBulkType.Code,
        EBulkType.Raw,
        EBulkType.Metadata
    ];

    private bool _isQueuingMode;
    /// <summary>
    /// while true, folder export actions from the right-click menu get added to the queue instead of
    /// running immediately
    /// </summary>
    public bool IsQueuingMode
    {
        get => _isQueuingMode;
        set => SetProperty(ref _isQueuingMode, value);
    }

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        private set => SetProperty(ref _isRunning, value);
    }

    public ObservableCollection<ExportQueueItem> Items { get; } = [];

    public bool HasItems => Items.Count > 0;

    /// <summary>Combined flags from the global-export checkboxes; persisted in UserSettings.</summary>
    public EBulkType SelectedBulkType
    {
        get => UserSettings.Default.ExportQueueBulkType;
        set
        {
            if (UserSettings.Default.ExportQueueBulkType == value) return;
            UserSettings.Default.ExportQueueBulkType = value;
            RaiseSelectedBulkTypeChanged();
        }
    }

    public bool HasSelectedBulkType => (SelectedBulkType & ~EBulkType.Auto) != EBulkType.None;

    /// <summary>Bound to UserSettings.ExportQueueFlatArchiveExport (default on).</summary>
    public bool FlatArchiveExport
    {
        get => UserSettings.Default.ExportQueueFlatArchiveExport;
        set
        {
            if (UserSettings.Default.ExportQueueFlatArchiveExport == value) return;
            UserSettings.Default.ExportQueueFlatArchiveExport = value;
            RaisePropertyChanged(nameof(FlatArchiveExport));
        }
    }

    /// <summary>Bound to UserSettings.ExportQueueExcludeUmap.</summary>
    public bool ExcludeUmap
    {
        get => UserSettings.Default.ExportQueueExcludeUmap;
        set
        {
            if (UserSettings.Default.ExportQueueExcludeUmap == value) return;
            UserSettings.Default.ExportQueueExcludeUmap = value;
            RaisePropertyChanged(nameof(ExcludeUmap));
        }
    }

    /// <summary>Bound to UserSettings.ExportQueueExcludeDirectories.</summary>
    public bool ExcludeDirectories
    {
        get => UserSettings.Default.ExportQueueExcludeDirectories;
        set
        {
            if (UserSettings.Default.ExportQueueExcludeDirectories == value) return;
            UserSettings.Default.ExportQueueExcludeDirectories = value;
            RaisePropertyChanged(nameof(ExcludeDirectories));
        }
    }

    /// <summary>Bound to UserSettings.ExportQueueIgnoredFolders (profile-persisted).</summary>
    public string IgnoredFolders
    {
        get => UserSettings.Default.ExportQueueIgnoredFolders;
        set
        {
            var next = value ?? string.Empty;
            if (UserSettings.Default.ExportQueueIgnoredFolders == next) return;
            UserSettings.Default.ExportQueueIgnoredFolders = next;
            RaisePropertyChanged(nameof(IgnoredFolders));
        }
    }

    /// <summary>Bound to UserSettings.ExportQueueExcludeClasses.</summary>
    public bool ExcludeClasses
    {
        get => UserSettings.Default.ExportQueueExcludeClasses;
        set
        {
            if (UserSettings.Default.ExportQueueExcludeClasses == value) return;
            UserSettings.Default.ExportQueueExcludeClasses = value;
            RaisePropertyChanged(nameof(ExcludeClasses));
        }
    }

    /// <summary>Bound to UserSettings.ExportQueueIgnoredClasses (profile-persisted).</summary>
    public string IgnoredClasses
    {
        get => UserSettings.Default.ExportQueueIgnoredClasses;
        set
        {
            var next = value ?? string.Empty;
            if (UserSettings.Default.ExportQueueIgnoredClasses == next) return;
            UserSettings.Default.ExportQueueIgnoredClasses = next;
            RaisePropertyChanged(nameof(IgnoredClasses));
        }
    }

    /// <summary>Bound to UserSettings.ExportQueueIndexBeforeExport (default off).</summary>
    public bool IndexBeforeExport
    {
        get => UserSettings.Default.ExportQueueIndexBeforeExport;
        set
        {
            if (UserSettings.Default.ExportQueueIndexBeforeExport == value) return;
            UserSettings.Default.ExportQueueIndexBeforeExport = value;
            RaisePropertyChanged(nameof(IndexBeforeExport));
        }
    }

    public bool ExportProperties
    {
        get => HasFlag(SelectedBulkType, EBulkType.Properties);
        set => SetBulkFlag(EBulkType.Properties, value);
    }

    public bool ExportTextures
    {
        get => HasFlag(SelectedBulkType, EBulkType.Textures);
        set => SetBulkFlag(EBulkType.Textures, value);
    }

    public bool ExportMeshes
    {
        get => HasFlag(SelectedBulkType, EBulkType.Meshes);
        set => SetBulkFlag(EBulkType.Meshes, value);
    }

    public bool ExportAnimations
    {
        get => HasFlag(SelectedBulkType, EBulkType.Animations);
        set => SetBulkFlag(EBulkType.Animations, value);
    }

    public bool ExportAudio
    {
        get => HasFlag(SelectedBulkType, EBulkType.Audio);
        set => SetBulkFlag(EBulkType.Audio, value);
    }

    public bool ExportCode
    {
        get => HasFlag(SelectedBulkType, EBulkType.Code);
        set => SetBulkFlag(EBulkType.Code, value);
    }

    public bool ExportRaw
    {
        get => HasFlag(SelectedBulkType, EBulkType.Raw);
        set => SetBulkFlag(EBulkType.Raw, value);
    }

    public bool ExportMetadata
    {
        get => HasFlag(SelectedBulkType, EBulkType.Metadata);
        set => SetBulkFlag(EBulkType.Metadata, value);
    }

    public void Enqueue(TreeItem folder, EBulkType bulkType, string label)
    {
        Items.Add(new ExportQueueItem(folder, bulkType, label));
        RaisePropertyChanged(nameof(HasItems));
    }

    /// <summary>
    /// queues every currently loaded root folder for the given bulk type. Folder is intentionally left
    /// null and resolved when the queue actually runs, so late-loaded content is still covered.
    /// </summary>
    public void EnqueueGlobal(EBulkType bulkType)
    {
        var flags = bulkType & ~EBulkType.Auto;
        if (flags == EBulkType.None) return;
        Enqueue(null, flags, $"All Loaded Assets — {FormatBulkTypeLabel(flags)}");
    }

    public void EnqueueSelectedGlobal() => EnqueueGlobal(SelectedBulkType);

    public void Remove(ExportQueueItem item)
    {
        Items.Remove(item);
        RaisePropertyChanged(nameof(HasItems));
    }

    public void Clear()
    {
        Items.Clear();
        RaisePropertyChanged(nameof(HasItems));
    }

    public async Task Run()
    {
        if (Items.Count == 0 || IsRunning) return;

        var queued = Items.ToList();
        Items.Clear();
        RaisePropertyChanged(nameof(HasItems));

        IsRunning = true;
        try
        {
            await ApplicationService.ThreadWorkerView.Begin(cancellationToken =>
            {
                var cue4Parse = ApplicationService.ApplicationView.CUE4Parse;

                if (UserSettings.Default.ExportQueueIndexBeforeExport)
                    IndexSelectedFoldersBeforeExport(cue4Parse, queued, cancellationToken);

                foreach (var item in queued)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (UserSettings.Default.ExportQueueFlatArchiveExport)
                    {
                        cue4Parse.RunFlatArchiveExport(cancellationToken, item.BulkType);
                        continue;
                    }

                    var folders = item.Folder != null ? new[] { item.Folder } : cue4Parse.AssetsFolder.Folders.ToArray();
                    foreach (var folder in folders)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        cue4Parse.RunFolderExport(cancellationToken, folder, item.BulkType);
                    }
                }
            });
        }
        finally
        {
            IsRunning = false;
        }
    }

    /// <summary>
    /// FMDex-index only the folders this queue run will (or did) select — never the whole archive.
    /// Flat + global (Folder == null) items contribute no folders; we log and skip.
    /// </summary>
    private static void IndexSelectedFoldersBeforeExport(
        CUE4ParseViewModel cue4Parse,
        List<ExportQueueItem> queued,
        CancellationToken cancellationToken)
    {
        var folders = new Dictionary<string, TreeItem>(StringComparer.OrdinalIgnoreCase);
        var flat = UserSettings.Default.ExportQueueFlatArchiveExport;

        foreach (var item in queued)
        {
            if (item.Folder != null)
            {
                folders[item.Folder.PathAtThisPoint] = item.Folder;
                continue;
            }

            // Global item: expand to loaded roots only when non-flat (same folders export walks).
            // Flat dumps Provider.Files and has no folder scope — do not index the whole archive.
            if (flat) continue;

            foreach (var root in cue4Parse.AssetsFolder.Folders)
                folders[root.PathAtThisPoint] = root;
        }

        if (folders.Count == 0)
        {
            FLogger.Append(ELog.Information, () =>
                FLogger.Text("[Queue] Index Before Export: no folders selected to index — skipping.", Constants.WHITE, true));
            return;
        }

        FLogger.Append(ELog.Information, () =>
            FLogger.Text($"[Queue] Index Before Export: indexing {folders.Count} folder(s)…", Constants.WHITE, true));

        foreach (var folder in folders.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                cue4Parse.IndexFolderForFMDex(cancellationToken, folder);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                FLogger.Append(ELog.Warning, () =>
                    FLogger.Text(
                        $"[Queue] Index Before Export failed for '{folder.PathAtThisPoint}' — continuing export.",
                        Constants.WHITE, true));
            }
        }
    }

    public static string FormatBulkTypeLabel(EBulkType bulkType)
    {
        var flags = bulkType & ~EBulkType.Auto;
        if (flags == EBulkType.None) return "None";
        if (flags == EBulkType.AllAssets) return EBulkType.AllAssets.GetDescription();

        var parts = QueueableBulkTypes
            .Where(flag => HasFlag(flags, flag))
            .Select(flag => flag.GetDescription())
            .ToArray();
        return parts.Length > 0 ? string.Join(" + ", parts) : flags.ToString();
    }

    private void SetBulkFlag(EBulkType flag, bool enabled)
    {
        var current = SelectedBulkType;
        var updated = enabled ? current | flag : current & ~flag;
        if (updated == current) return;
        UserSettings.Default.ExportQueueBulkType = updated;
        RaiseSelectedBulkTypeChanged();
    }

    private void RaiseSelectedBulkTypeChanged()
    {
        RaisePropertyChanged(nameof(SelectedBulkType));
        RaisePropertyChanged(nameof(HasSelectedBulkType));
        RaisePropertyChanged(nameof(ExportProperties));
        RaisePropertyChanged(nameof(ExportTextures));
        RaisePropertyChanged(nameof(ExportMeshes));
        RaisePropertyChanged(nameof(ExportAnimations));
        RaisePropertyChanged(nameof(ExportAudio));
        RaisePropertyChanged(nameof(ExportCode));
        RaisePropertyChanged(nameof(ExportRaw));
        RaisePropertyChanged(nameof(ExportMetadata));
    }

    private static bool HasFlag(EBulkType value, EBulkType flag) => (value & flag) == flag;
}
