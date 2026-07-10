using FModel.Framework;

namespace FModel.ViewModels;

/// <summary>
/// One step of a queued export run: either a specific folder + bulk type, or (Folder == null) every
/// currently loaded root folder for that bulk type ("global" export, evaluated when the queue runs so
/// it always covers whatever is loaded at that time).
/// </summary>
public class ExportQueueItem : ViewModel
{
    public TreeItem Folder { get; }
    public EBulkType BulkType { get; }
    public string Label { get; }

    public ExportQueueItem(TreeItem folder, EBulkType bulkType, string label)
    {
        Folder = folder;
        BulkType = bulkType;
        Label = label;
    }
}
