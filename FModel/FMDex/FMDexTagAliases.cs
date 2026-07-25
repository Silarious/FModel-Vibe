using System;
using System.Collections.Generic;
using System.Linq;
using CUE4Parse.FileProvider.Objects;

namespace FModel.FMDex;

/// <summary>
/// Search-only aliases for FMDex class tags. Index still stores full UE class names;
/// filters like <c>model</c> expand to mesh-related classes.
/// </summary>
public static class FMDexTagAliases
{
    /// <summary>
    /// Alias → class-name fragments to match (tag contains fragment, case-insensitive).
    /// Exact alias keys only (whole filter token), so <c>mesh</c> still also matches via normal contains.
    /// </summary>
    private static readonly Dictionary<string, string[]> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["model"] =
        [
            "StaticMesh", "SkeletalMesh", "GeometryCollection", "LandscapeComponent",
            "InstancedStaticMesh", "HierarchicalInstancedStaticMesh", "SkinnedAsset"
        ],
        ["mesh"] =
        [
            "StaticMesh", "SkeletalMesh", "GeometryCollection", "InstancedStaticMesh",
            "HierarchicalInstancedStaticMesh", "SkinnedAsset"
        ],
        ["skel"] = ["SkeletalMesh", "SkinnedAsset"],
        ["static"] = ["StaticMesh"], // only when filter is exactly "static"
        ["tex"] = ["Texture"],
        ["texture"] = ["Texture"],
        ["mat"] = ["Material"],
        ["material"] = ["Material"],
        ["anim"] = ["AnimSequence", "AnimMontage", "AnimBlueprint", "Animation"],
        ["animation"] = ["AnimSequence", "AnimMontage", "AnimBlueprint", "Animation"],
        ["sound"] = ["SoundWave", "SoundCue", "MetaSound", "Sound"],
        ["audio"] = ["SoundWave", "SoundCue", "MetaSound", "Sound"],
        ["bp"] = ["Blueprint"],
        ["blueprint"] = ["Blueprint"],
        ["particle"] = ["Niagara", "ParticleSystem"],
        ["fx"] = ["Niagara", "ParticleSystem"],
        ["ui"] = ["Widget", "UserWidget"],
        ["widget"] = ["Widget", "UserWidget"],
        ["data"] = ["DataAsset", "DataTable", "CurveTable"],
        // .umap files are matched separately; these catch map-related uassets
        ["map"] = ["Map", "World", "Level"],
    };

    public static bool IsMapFilter(string filter)
        => !string.IsNullOrWhiteSpace(filter) &&
           filter.Trim().Equals("map", StringComparison.OrdinalIgnoreCase);

    public static bool IsUMap(GameFile entry)
        => entry != null &&
           !string.IsNullOrEmpty(entry.Extension) &&
           entry.Extension.Equals("umap", StringComparison.OrdinalIgnoreCase);

    public static bool Matches(IEnumerable<string> tags, string filter)
    {
        if (tags == null || string.IsNullOrWhiteSpace(filter))
            return true;

        var f = filter.Trim();

        // Direct substring match on stored class names (existing behavior)
        if (tags.Any(t => t.Contains(f, StringComparison.OrdinalIgnoreCase)))
            return true;

        if (!Aliases.TryGetValue(f, out var fragments))
            return false;

        return tags.Any(t =>
            fragments.Any(frag => t.Contains(frag, StringComparison.OrdinalIgnoreCase)));
    }
}
