using System.Collections.Generic;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Versions;
using FModel.ViewModels.ApiEndpoints.Models;

namespace FModel.ViewModels.ProfileDiff;

/// <summary>
/// Settings needed to mount a game directory from a saved profile without touching live UserSettings.
/// </summary>
public sealed class ProfileMountConfig
{
    public string Name { get; init; } = "";
    public string GameDirectory { get; init; } = "";
    public EGame UeVersion { get; init; } = EGame.GAME_UE4_LATEST;
    public ETexturePlatform TexturePlatform { get; init; } = ETexturePlatform.DesktopMobile;
    public string AesMainKey { get; init; } = "";
    public IReadOnlyList<DynamicKey> DynamicKeys { get; init; } = [];
    public string MappingFilePath { get; init; } = "";
    public bool MappingOverwrite { get; init; }
}
