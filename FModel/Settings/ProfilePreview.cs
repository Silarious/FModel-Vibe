using CUE4Parse.UE4.Versions;
using FModel.Framework;

namespace FModel.Settings;

/// <summary>
/// Read-only snapshot of the fields shown on the Profiles settings page.
/// Used to preview a profile without applying it to <see cref="UserSettings.Default"/>.
/// </summary>
public sealed class ProfilePreview : ViewModel
{
    private string _name = "";
    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value ?? "");
    }

    private string _gameDirectory = "";
    public string GameDirectory
    {
        get => _gameDirectory;
        set => SetProperty(ref _gameDirectory, value ?? "");
    }

    private EGame _ueVersion = EGame.GAME_UE4_LATEST;
    public EGame UeVersion
    {
        get => _ueVersion;
        set => SetProperty(ref _ueVersion, value);
    }

    private string _mappingFilePath = "";
    public string MappingFilePath
    {
        get => _mappingFilePath;
        set => SetProperty(ref _mappingFilePath, value ?? "");
    }

    private string _aesMainKey = "";
    public string AesMainKey
    {
        get => _aesMainKey;
        set => SetProperty(ref _aesMainKey, value ?? "");
    }

    private string _rawDataDirectory = "";
    public string RawDataDirectory
    {
        get => _rawDataDirectory;
        set => SetProperty(ref _rawDataDirectory, value ?? "");
    }

    private string _propertiesDirectory = "";
    public string PropertiesDirectory
    {
        get => _propertiesDirectory;
        set => SetProperty(ref _propertiesDirectory, value ?? "");
    }

    private string _textureDirectory = "";
    public string TextureDirectory
    {
        get => _textureDirectory;
        set => SetProperty(ref _textureDirectory, value ?? "");
    }

    private string _audioDirectory = "";
    public string AudioDirectory
    {
        get => _audioDirectory;
        set => SetProperty(ref _audioDirectory, value ?? "");
    }

    private string _modelDirectory = "";
    public string ModelDirectory
    {
        get => _modelDirectory;
        set => SetProperty(ref _modelDirectory, value ?? "");
    }

    private string _codeDirectory = "";
    public string CodeDirectory
    {
        get => _codeDirectory;
        set => SetProperty(ref _codeDirectory, value ?? "");
    }

    public void CopyFromLiveSettings(string profileName)
    {
        var s = UserSettings.Default;
        Name = profileName ?? s.CurrentProfileName ?? "";
        GameDirectory = s.GameDirectory ?? "";
        UeVersion = s.CurrentDir?.UeVersion ?? EGame.GAME_UE4_LATEST;
        MappingFilePath = s.CurrentDir?.Endpoints is { Length: > 1 }
            ? s.CurrentDir.Endpoints[1].FilePath ?? ""
            : "";
        AesMainKey = s.CurrentDir?.AesKeys?.MainKey ?? "";
        RawDataDirectory = s.RawDataDirectory ?? "";
        PropertiesDirectory = s.PropertiesDirectory ?? "";
        TextureDirectory = s.TextureDirectory ?? "";
        AudioDirectory = s.AudioDirectory ?? "";
        ModelDirectory = s.ModelDirectory ?? "";
        CodeDirectory = s.CodeDirectory ?? "";
    }

    public void CopyFrom(ProfilePreview other)
    {
        if (other == null) return;
        Name = other.Name;
        GameDirectory = other.GameDirectory;
        UeVersion = other.UeVersion;
        MappingFilePath = other.MappingFilePath;
        AesMainKey = other.AesMainKey;
        RawDataDirectory = other.RawDataDirectory;
        PropertiesDirectory = other.PropertiesDirectory;
        TextureDirectory = other.TextureDirectory;
        AudioDirectory = other.AudioDirectory;
        ModelDirectory = other.ModelDirectory;
        CodeDirectory = other.CodeDirectory;
    }
}
