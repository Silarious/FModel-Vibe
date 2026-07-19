using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.GameTypes.AshEchoes.FileProvider;
using CUE4Parse.GameTypes.HonorOfKings.FileProvider;
using CUE4Parse.MappingsProvider;
using CUE4Parse.MappingsProvider.Jmap;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using CUE4Parse.Utils;
using FModel.Settings;
using Serilog;

namespace FModel.ViewModels.ProfileDiff;

/// <summary>
/// Builds a temporary VFS provider from a profile mount config without mutating live UserSettings.
/// </summary>
public static class ProfileProviderFactory
{
    public static AbstractVfsFileProvider Create(ProfileMountConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrWhiteSpace(config.GameDirectory))
            throw new ArgumentException("Profile has no GameDirectory.", nameof(config));

        var versionContainer = new VersionContainer(
            game: config.UeVersion,
            platform: config.TexturePlatform);
        var pathComparer = StringComparer.OrdinalIgnoreCase;
        var gameDirectory = config.GameDirectory;

        AbstractVfsFileProvider provider = gameDirectory switch
        {
            Constants._FN_LIVE_TRIGGER => new StreamedFileProvider("FortniteLive", versionContainer, pathComparer),
            Constants._VAL_LIVE_TRIGGER => new StreamedFileProvider("ValorantLive", versionContainer, pathComparer),
            _ => CreateLocalProvider(gameDirectory, versionContainer, pathComparer)
        };

        provider.ReadScriptData = UserSettings.Default.ReadScriptData;
        provider.ReadShaderMaps = UserSettings.Default.ReadShaderMaps;
        provider.ReadNaniteData = true;
        return provider;
    }

    public static void InitializeAndMount(AbstractVfsFileProvider provider, ProfileMountConfig config)
    {
        provider.Initialize();

        if (config.MappingOverwrite && !string.IsNullOrWhiteSpace(config.MappingFilePath) &&
            File.Exists(config.MappingFilePath))
        {
            provider.MappingsContainer = SelectMappingsProvider(config.MappingFilePath);
        }

        var keys = BuildAesKeys(config).ToList();
        if (keys.Count > 0)
            provider.SubmitKeys(keys);

        provider.PostMount();

        if (provider.RequiredKeys.Count > 0 &&
            (string.IsNullOrWhiteSpace(Helper.FixKey(config.AesMainKey)) ||
             Helper.FixKey(config.AesMainKey).Length != 66) &&
            !(config.DynamicKeys?.Count > 0))
        {
            throw new InvalidOperationException(
                $"Profile \"{config.Name}\" has encrypted archives but no AES key is set.");
        }

        Log.Information(
            "ProfileDiff mounted {Name}: {Mounted}/{Total} archives, {Files} files",
            config.Name,
            provider.MountedVfs.Count,
            provider.MountedVfs.Count + provider.UnloadedVfs.Count,
            provider.Files.Count);
    }

    private static AbstractVfsFileProvider CreateLocalProvider(
        string gameDirectory,
        VersionContainer versionContainer,
        StringComparer pathComparer)
    {
        var project = gameDirectory.SubstringBeforeLast(gameDirectory.Contains("eFootball") ? "\\pak" : "\\Content")
            .SubstringAfterLast("\\");

        return project switch
        {
            "StateOfDecay2" => new DefaultFileProvider(new DirectoryInfo(gameDirectory),
            [
                new(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\\StateOfDecay2\\Saved\\Paks"),
                new(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\\StateOfDecay2\\Saved\\DisabledPaks")
            ], SearchOption.AllDirectories, versionContainer, pathComparer),
            "eFootball" => new DefaultFileProvider(new DirectoryInfo(gameDirectory),
            [
                new(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) + "\\KONAMI\\eFootball\\ST\\Download")
            ], SearchOption.AllDirectories, versionContainer, pathComparer),
            "DeadByDaylight" => new DefaultFileProvider(new DirectoryInfo(gameDirectory),
            [
                new(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) +
                    "\\DeadByDaylight\\Saved\\PersistentDownloadDir\\DynamicContent")
            ], SearchOption.AllDirectories, versionContainer, pathComparer),
            _ when versionContainer.Game is EGame.GAME_AshEchoes =>
                new AEDefaultFileProvider(gameDirectory, SearchOption.AllDirectories, versionContainer, pathComparer),
            _ when versionContainer.Game is EGame.GAME_BlackStigma =>
                new DefaultFileProvider(gameDirectory, SearchOption.AllDirectories, versionContainer, StringComparer.Ordinal),
            _ when versionContainer.Game is EGame.GAME_HonorofKingsWorld =>
                new HoKWDefaultFileProvider(gameDirectory, SearchOption.AllDirectories, versionContainer, pathComparer),
            _ when versionContainer.Game is EGame.GAME_ArcRaiders =>
                new ArcRaidersFileProvider(gameDirectory, SearchOption.AllDirectories, versionContainer, pathComparer),
            _ => new DefaultFileProvider(gameDirectory, SearchOption.AllDirectories, versionContainer, pathComparer)
        };
    }

    private static IEnumerable<KeyValuePair<FGuid, FAesKey>> BuildAesKeys(ProfileMountConfig config)
    {
        var main = Helper.FixKey(config.AesMainKey);
        if (main.Length == 66)
            yield return new KeyValuePair<FGuid, FAesKey>(Constants.ZERO_GUID, new FAesKey(main));

        if (config.DynamicKeys == null) yield break;
        foreach (var dyn in config.DynamicKeys)
        {
            var key = Helper.FixKey(dyn.Key);
            if (key.Length != 66 || string.IsNullOrWhiteSpace(dyn.Guid) || dyn.Guid.Length != 32)
                continue;
            FGuid guid;
            try { guid = new FGuid(dyn.Guid); }
            catch { continue; }
            yield return new KeyValuePair<FGuid, FAesKey>(guid, new FAesKey(key));
        }
    }

    private static ITypeMappingsProvider SelectMappingsProvider(string path)
    {
        if (path.EndsWith(".jmap.gz", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".jmap", StringComparison.OrdinalIgnoreCase))
            return new JmapTypeMappingsProvider(path);

        return new FileUsmapTypeMappingsProvider(path);
    }
}
