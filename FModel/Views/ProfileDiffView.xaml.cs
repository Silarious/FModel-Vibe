using System.Windows;
using FModel.ViewModels.ProfileDiff;
using Microsoft.Win32;
using Ookii.Dialogs.Wpf;

namespace FModel.Views;

public partial class ProfileDiffView
{
    private readonly ProfileDiffViewModel _viewModel;

    public ProfileDiffView()
    {
        DataContext = _viewModel = new ProfileDiffViewModel();
        InitializeComponent();
        Loaded += (_, _) => _viewModel.RefreshProfiles();
        Closing += (_, _) => _viewModel.PersistSettings();
    }

    private async void OnRunClick(object sender, RoutedEventArgs e)
    {
        await _viewModel.RunDiffAsync();
    }

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        _viewModel.CancelDiff();
    }

    private void OnBrowseExportRoot(object sender, RoutedEventArgs e)
    {
        if (TryBrowseFolder(out var path))
            _viewModel.ExportRoot = path;
    }

    private void OnBrowseOldPak(object sender, RoutedEventArgs e)
    {
        if (TryBrowseFolder(out var path))
            _viewModel.OldPakFolder = path;
    }

    private void OnBrowseNewPak(object sender, RoutedEventArgs e)
    {
        if (TryBrowseFolder(out var path))
            _viewModel.NewPakFolder = path;
    }

    private void OnBrowseSharedMapping(object sender, RoutedEventArgs e)
    {
        if (TryBrowseMapping(out var path))
            _viewModel.SharedMappingPath = path;
    }

    private void OnBrowseOldMapping(object sender, RoutedEventArgs e)
    {
        if (TryBrowseMapping(out var path))
            _viewModel.OldMappingPath = path;
    }

    private void OnBrowseNewMapping(object sender, RoutedEventArgs e)
    {
        if (TryBrowseMapping(out var path))
            _viewModel.NewMappingPath = path;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private static bool TryBrowseFolder(out string path)
    {
        var dialog = new VistaFolderBrowserDialog { ShowNewFolderButton = true };
        if (dialog.ShowDialog() == true)
        {
            path = dialog.SelectedPath;
            return true;
        }

        path = string.Empty;
        return false;
    }

    private static bool TryBrowseMapping(out string path)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select a mapping file",
            Filter = "USMAP Files (*.usmap, *.jmap, *.jmap.gz)|*.usmap;*.jmap;*.jmap.gz|All Files (*.*)|*.*"
        };
        if (dialog.ShowDialog().GetValueOrDefault())
        {
            path = dialog.FileName;
            return true;
        }

        path = string.Empty;
        return false;
    }
}
