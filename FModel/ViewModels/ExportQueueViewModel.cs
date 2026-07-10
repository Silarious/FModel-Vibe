using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using FModel.Extensions;
using FModel.Framework;
using FModel.Services;

namespace FModel.ViewModels;

public class ExportQueueViewModel : ViewModel
{
    /// <summary>
    /// the bulk types that make sense to queue, in the order shown in the "add global export" picker
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
        => Enqueue(null, bulkType, $"All Loaded Assets — {bulkType.GetDescription()}");

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
                foreach (var item in queued)
                {
                    cancellationToken.ThrowIfCancellationRequested();

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
}
