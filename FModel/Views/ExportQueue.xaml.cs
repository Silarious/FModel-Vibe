using System.Windows;
using System.Windows.Controls;
using AdonisUI.Controls;
using FModel.Services;
using FModel.ViewModels;
using MessageBox = AdonisUI.Controls.MessageBox;
using MessageBoxImage = AdonisUI.Controls.MessageBoxImage;
using MessageBoxResult = AdonisUI.Controls.MessageBoxResult;

namespace FModel.Views;

public partial class ExportQueue
{
    private enum EQueueDisableChoice
    {
        RunAndDisable,
        ClearAndDisable,
        Cancel
    }

    public ExportQueue()
    {
        // reuse the single shared instance so items queued from the right-click menu show up here,
        // and toggling Queuing Mode here is reflected everywhere else
        DataContext = ApplicationService.ApplicationView.ExportQueue;
        InitializeComponent();
    }

    private async void OnQueuingModeClick(object sender, RoutedEventArgs e)
    {
        var checkBox = (CheckBox) sender;
        var queue = ApplicationService.ApplicationView.ExportQueue;
        var turningOn = checkBox.IsChecked == true;

        if (turningOn || queue.Items.Count == 0)
        {
            queue.IsQueuingMode = turningOn;
            return;
        }

        // trying to turn off with items still queued: keep it visually on until the user decides what to do
        checkBox.IsChecked = true;

        var messageBox = new MessageBoxModel
        {
            Text = $"You still have {queue.Items.Count} export(s) queued. What would you like to do before disabling Queuing Mode?",
            Caption = "Disable Queuing Mode",
            Icon = MessageBoxImage.Question,
            Buttons =
            [
                MessageBoxButtons.Custom("Run Queue & Disable", EQueueDisableChoice.RunAndDisable),
                MessageBoxButtons.Custom("Clear Queue & Disable", EQueueDisableChoice.ClearAndDisable),
                MessageBoxButtons.Custom("Cancel", EQueueDisableChoice.Cancel)
            ],
            IsSoundEnabled = false
        };
        MessageBox.Show(messageBox);

        if (messageBox.Result != MessageBoxResult.Custom) return; // dismissed without choosing: stay in queuing mode

        switch ((EQueueDisableChoice) messageBox.ButtonPressed.Id)
        {
            case EQueueDisableChoice.RunAndDisable:
                queue.IsQueuingMode = false;
                checkBox.IsChecked = false;
                await queue.Run();
                break;
            case EQueueDisableChoice.ClearAndDisable:
                queue.Clear();
                queue.IsQueuingMode = false;
                checkBox.IsChecked = false;
                break;
            case EQueueDisableChoice.Cancel:
            default:
                break; // stay in queuing mode, checkbox remains checked
        }
    }

    private void OnQueueGlobal(object sender, RoutedEventArgs e)
    {
        ApplicationService.ApplicationView.ExportQueue.EnqueueSelectedGlobal();
    }

    private void OnRemoveItem(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ExportQueueItem item }) return;
        ApplicationService.ApplicationView.ExportQueue.Remove(item);
    }

    private void OnClearQueue(object sender, RoutedEventArgs e)
    {
        ApplicationService.ApplicationView.ExportQueue.Clear();
    }

    private async void OnRunQueue(object sender, RoutedEventArgs e)
    {
        await ApplicationService.ApplicationView.ExportQueue.Run();
    }
}
