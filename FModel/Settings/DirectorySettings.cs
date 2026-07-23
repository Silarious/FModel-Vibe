using System;
using System.Collections.Generic;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Versions;
using FModel.Framework;
using FModel.ViewModels.ApiEndpoints.Models;

namespace FModel.Settings;

public class DirectorySettings : ViewModel, ICloneable
{
    public static DirectorySettings Default(
        string gameName, string gameDir, bool manual = false, EGame ue = EGame.GAME_UE4_LATEST, string aes = "")
    {
        UserSettings.Default.PerDirectory.TryGetValue(gameDir, out var old);
        return new DirectorySettings
        {
            GameName = gameName,
            GameDirectory = gameDir,
            IsManual = manual,
            UeVersion = old?.UeVersion ?? ue,
            TexturePlatform = old?.TexturePlatform ?? ETexturePlatform.DesktopMobile,
            Versioning = old?.Versioning ?? new VersioningSettings(),
            Endpoints = old?.Endpoints ?? EndpointSettings.Default(gameName),
            Directories = old?.Directories ?? CustomDirectory.Default(gameName),
            AesKeys = old?.AesKeys ?? new AesResponse { MainKey = aes, DynamicKeys = null },
            LastAesReload = old?.LastAesReload ?? DateTime.Today.AddDays(-1),
            CriwareDecryptionKey = old?.CriwareDecryptionKey ?? 0,
            UnluacOpCodeMap = old?.UnluacOpCodeMap ?? ""
        };
    }

    private string _gameName;
    public string GameName
    {
        get => _gameName;
        set => SetProperty(ref _gameName, value);
    }

    private string _gameDirectory;
    public string GameDirectory
    {
        get => _gameDirectory;
        set => SetProperty(ref _gameDirectory, value);
    }

    private bool _isManual;
    public bool IsManual
    {
        get => _isManual;
        set => SetProperty(ref _isManual, value);
    }

    private EGame _ueVersion;
    public EGame UeVersion
    {
        get => _ueVersion;
        set => SetProperty(ref _ueVersion, value);
    }

    private ETexturePlatform _texturePlatform;
    public ETexturePlatform TexturePlatform
    {
        get => _texturePlatform;
        set => SetProperty(ref _texturePlatform, value);
    }

    private VersioningSettings _versioning = new();
    public VersioningSettings Versioning
    {
        get => _versioning ??= new VersioningSettings();
        set => SetProperty(ref _versioning, value ?? new VersioningSettings());
    }

    private EndpointSettings[] _endpoints;
    public EndpointSettings[] Endpoints
    {
        get => _endpoints ??= EndpointSettings.Default(GameName ?? "");
        set => SetProperty(ref _endpoints, value ?? EndpointSettings.Default(GameName ?? ""));
    }

    private IList<CustomDirectory> _directories;
    public IList<CustomDirectory> Directories
    {
        get => _directories ??= new List<CustomDirectory>();
        set => SetProperty(ref _directories, value ?? new List<CustomDirectory>());
    }

    private AesResponse _aesKeys;
    public AesResponse AesKeys
    {
        get => _aesKeys ??= new AesResponse { MainKey = "", DynamicKeys = null };
        set => SetProperty(ref _aesKeys, value ?? new AesResponse { MainKey = "", DynamicKeys = null });
    }

    private DateTime _lastAesReload;
    public DateTime LastAesReload
    {
        get => _lastAesReload;
        set => SetProperty(ref _lastAesReload, value);
    }

    private ulong _criwareDecryptionKey;
    public ulong CriwareDecryptionKey
    {
        get => _criwareDecryptionKey;
        set => SetProperty(ref _criwareDecryptionKey, value);
    }

    private string _unluacOpCodeMap = "";
    public string UnluacOpCodeMap
    {
        get => _unluacOpCodeMap ??= "";
        set => SetProperty(ref _unluacOpCodeMap, value ?? "");
    }

    private bool Equals(DirectorySettings other)
    {
        return GameDirectory == other.GameDirectory && UeVersion == other.UeVersion;
    }

    public override bool Equals(object obj)
    {
        return obj is DirectorySettings other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(GameDirectory, (int) UeVersion);
    }

    public override string ToString()
    {
        return GameName;
    }

    public object Clone()
    {
        return this.MemberwiseClone();
    }
}
