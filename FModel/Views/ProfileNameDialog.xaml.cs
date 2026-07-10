using System.Windows;
using FModel.Framework;

namespace FModel.Views;

public class ProfileNameDialogModel : ViewModel
{
    private string _dialogTitle = "Profile Name";
    public string DialogTitle
    {
        get => _dialogTitle;
        set => SetProperty(ref _dialogTitle, value);
    }

    private string _profileName = string.Empty;
    public string ProfileName
    {
        get => _profileName;
        set => SetProperty(ref _profileName, value);
    }
}

public partial class ProfileNameDialog
{
    public ProfileNameDialog(string title, string initialValue = "")
    {
        DataContext = new ProfileNameDialogModel { DialogTitle = title, ProfileName = initialValue };
        InitializeComponent();

        Activate();
        NameBox.Focus();
        NameBox.SelectAll();
    }

    public string ProfileName => ((ProfileNameDialogModel) DataContext).ProfileName;

    private void OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ProfileName)) return;

        DialogResult = true;
        Close();
    }
}
