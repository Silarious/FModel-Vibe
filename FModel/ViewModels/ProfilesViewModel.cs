using System.Collections.ObjectModel;
using FModel.Framework;
using FModel.Settings;

namespace FModel.ViewModels;

public class ProfilesViewModel : ViewModel
{
    public ObservableCollection<string> ProfileNames { get; } = [];

    private string _selectedProfile;
    public string SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (!SetProperty(ref _selectedProfile, value)) return;
            if (!string.IsNullOrEmpty(value) && value != UserSettings.Default.CurrentProfileName)
                Switch(value);
        }
    }

    public ProfilesViewModel()
    {
        Refresh();
    }

    public void Refresh()
    {
        ProfileNames.Clear();
        foreach (var name in ProfileManager.GetProfileNames())
            ProfileNames.Add(name);

        _selectedProfile = UserSettings.Default.CurrentProfileName;
        RaisePropertyChanged(nameof(SelectedProfile));
    }

    public void SaveAs(string name)
    {
        ProfileManager.SaveCurrentAs(name);
        Refresh();
    }

    public void Switch(string name)
    {
        if (ProfileManager.Load(name))
            Refresh();
    }

    public void Delete(string name)
    {
        ProfileManager.Delete(name);
        Refresh();
    }

    public void Rename(string oldName, string newName)
    {
        ProfileManager.Rename(oldName, newName);
        Refresh();
    }
}
