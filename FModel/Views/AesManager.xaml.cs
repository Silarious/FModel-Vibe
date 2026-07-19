using System.ComponentModel;
using System.Windows;
using FModel.Services;
using FModel.ViewModels;

namespace FModel.Views;

public partial class AesManager
{
    private ApplicationViewModel _applicationView => ApplicationService.ApplicationView;

    public AesManager()
    {
        DataContext = _applicationView;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Pick up a main key typed on the Profiles settings tab without reopening FModel.
        _applicationView.AesManager.SyncMainKeyFromSettings();
    }

    private void OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void OnRefreshAes(object sender, RoutedEventArgs e)
    {
        await _applicationView.CUE4Parse.RefreshAes();
        await _applicationView.AesManager.InitAes();
        _applicationView.AesManager.HasChange = true; // yes even if nothing actually changed
    }

    private async void OnClosing(object sender, CancelEventArgs e)
    {
        await _applicationView.UpdateProvider(false);
    }
}
