using System.Collections.ObjectModel;
using FModel.Framework;
using FModel.Services;
using FModel.Settings;

namespace FModel.ViewModels;

public class ProfilesViewModel : ViewModel
{
    public ObservableCollection<string> ProfileNames { get; } = [];

    public ProfilePreview Preview { get; } = new();

    private string _selectedProfile;
    private bool _suppressPreviewPush;

    /// <summary>List/combo selection used for preview — does not apply settings until Load.</summary>
    public string SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (!SetProperty(ref _selectedProfile, value)) return;
            RefreshPreview();
            RaisePropertyChanged(nameof(CanLoad));
            RaisePropertyChanged(nameof(IsPreviewingOtherProfile));
            RaisePropertyChanged(nameof(StatusText));
        }
    }

    public string ActiveProfileName => UserSettings.Default.CurrentProfileName;

    /// <summary>True when the selected profile differs from the currently loaded one.</summary>
    public bool IsPreviewingOtherProfile
        => !string.IsNullOrEmpty(SelectedProfile)
           && !string.Equals(SelectedProfile, ActiveProfileName, System.StringComparison.OrdinalIgnoreCase);

    public bool CanLoad => IsPreviewingOtherProfile;

    public string StatusText
    {
        get
        {
            if (string.IsNullOrEmpty(SelectedProfile))
                return "Select a profile to preview. Save writes the fields below into a profile.";
            if (IsPreviewingOtherProfile)
                return $"Previewing “{SelectedProfile}” (not applied). Load applies it and restarts. Save writes these fields into that profile.";
            return $"Active: {SelectedProfile} — editing live settings. Save updates this profile.";
        }
    }

    public ProfilesViewModel()
    {
        Preview.PropertyChanged += (_, _) =>
        {
            if (_suppressPreviewPush || IsPreviewingOtherProfile)
                return;
            PushPreviewToLive();
        };
        Refresh();
    }

    public void Refresh()
    {
        ProfileNames.Clear();
        foreach (var name in ProfileManager.GetProfileNames())
            ProfileNames.Add(name);

        _selectedProfile = UserSettings.Default.CurrentProfileName;
        RaisePropertyChanged(nameof(SelectedProfile));
        RaisePropertyChanged(nameof(ActiveProfileName));
        RefreshPreview();
        RaisePropertyChanged(nameof(CanLoad));
        RaisePropertyChanged(nameof(IsPreviewingOtherProfile));
        RaisePropertyChanged(nameof(StatusText));
    }

    public void RefreshPreview()
    {
        _suppressPreviewPush = true;
        try
        {
            if (string.IsNullOrEmpty(SelectedProfile))
            {
                Preview.CopyFromLiveSettings(ActiveProfileName);
                return;
            }

            if (string.Equals(SelectedProfile, ActiveProfileName, System.StringComparison.OrdinalIgnoreCase)
                || !ProfileManager.TryReadPreview(SelectedProfile, out var fromFile))
            {
                Preview.CopyFromLiveSettings(SelectedProfile);
                return;
            }

            Preview.CopyFrom(fromFile);
        }
        finally
        {
            _suppressPreviewPush = false;
        }
    }

    /// <summary>Push preview fields into live settings (active profile only).</summary>
    public void PushPreviewToLive()
    {
        var s = UserSettings.Default;
        s.GameDirectory = Preview.GameDirectory ?? "";
        s.RawDataDirectory = Preview.RawDataDirectory ?? "";
        s.PropertiesDirectory = Preview.PropertiesDirectory ?? "";
        s.TextureDirectory = Preview.TextureDirectory ?? "";
        s.AudioDirectory = Preview.AudioDirectory ?? "";
        s.ModelDirectory = Preview.ModelDirectory ?? "";
        s.CodeDirectory = Preview.CodeDirectory ?? "";

        if (s.CurrentDir != null)
        {
            s.CurrentDir.UeVersion = Preview.UeVersion;
            if (s.CurrentDir.AesKeys != null)
                s.CurrentDir.AesKeys.MainKey = Preview.AesMainKey ?? "";
            if (s.CurrentDir.Endpoints is { Length: > 1 })
            {
                s.CurrentDir.Endpoints[1].FilePath = Preview.MappingFilePath ?? "";
                s.CurrentDir.Endpoints[1].Overwrite = !string.IsNullOrWhiteSpace(Preview.MappingFilePath);
            }
        }

        // Keep Settings OK path in sync when editing the active profile from this tab.
        if (ApplicationService.ApplicationView?.SettingsView != null)
            ApplicationService.ApplicationView.SettingsView.SelectedUeGame = Preview.UeVersion;

        ProfileManager.SyncCurrentDirToGameDirectory();
    }

    public void SaveAs(string name)
    {
        if (IsPreviewingOtherProfile)
        {
            ProfileManager.SavePreviewAs(name, Preview);
        }
        else
        {
            PushPreviewToLive();
            ProfileManager.SaveCurrentAs(name);
        }

        Refresh();
    }

    /// <summary>Apply the selected profile to live settings and restart FModel.</summary>
    public void LoadSelected()
    {
        if (!CanLoad) return;
        if (!ProfileManager.Load(SelectedProfile))
            return;

        Refresh();
        ApplicationService.ApplicationView?.RestartWithWarning();
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
