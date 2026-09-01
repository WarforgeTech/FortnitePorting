using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using FortnitePorting.CUE4Parse.Extensions;
using FortnitePorting.CUE4Parse.Models.Fortnite.Enums;
using Serilog;

namespace FortnitePorting.Mcp.Core;

/// <summary>One entry of an outfit's (or backpack's) character-part list.</summary>
public sealed record CharacterPartInfo
{
    public required string Name { get; init; }
    public required string ObjectPath { get; init; }

    /// <summary>Head, Body, Hat, Backpack, Charm, Face, MiscOrTail, Other.</summary>
    public required string PartType { get; init; }

    /// <summary>Male, Female, Both - what the part's GenderPermitted says.</summary>
    public required string Gender { get; init; }

    /// <summary>Null when the part carries no SkeletalMesh, which is the one case that exports nothing.</summary>
    public string? SkeletalMesh { get; init; }

    /// <summary>CustomCharacterHeadData / CustomCharacterHatData / ... - drives the exporter's per-part metadata.</summary>
    public string? AdditionalData { get; init; }
}

public sealed record CharacterPartSet
{
    /// <summary>"BaseCharacterParts", "HeroDefinition.Specializations", "CharacterParts", or "none".</summary>
    public required string Source { get; init; }

    public List<CharacterPartInfo> Parts { get; init; } = [];

    public bool HasHeroDefinition { get; init; }

    /// <summary>Body-type/gender of the body part, which is what drives skeleton choice on import.</summary>
    public string? BodyGender => Parts.FirstOrDefault(part => part.PartType is "Body")?.Gender;

    public IEnumerable<string> PartTypes => Parts.Select(part => part.PartType).Distinct();

    /// <summary>Parts the exporter will silently drop because there is no mesh to convert.</summary>
    public IEnumerable<CharacterPartInfo> MeshlessParts => Parts.Where(part => part.SkeletalMesh is null);
}

/// <summary>
/// Reads an outfit's character parts using exactly the resolution order
/// <c>MeshExport.Export</c> uses for <see cref="EExportType.Outfit"/>:
/// <c>BaseCharacterParts</c>, falling back to
/// <c>HeroDefinition.Specializations[0].CharacterParts</c> when that is empty. Backpacks, pets and
/// companions use the flat <c>CharacterParts</c> property instead.
///
/// <para>
/// Nothing here exports; it exists so the server can TELL a caller which parts an outfit has
/// (and which of them carry no mesh) without paying for a full export.
/// </para>
/// </summary>
public static class CharacterPartInspector
{
    public static CharacterPartSet Read(UObject asset)
    {
        var hasHero = false;
        UObject[] parts;
        string source;

        try { parts = asset.GetOrDefault("BaseCharacterParts", Array.Empty<UObject>()); }
        catch { parts = []; }

        source = parts.Length > 0 ? "BaseCharacterParts" : "none";

        try
        {
            if (asset.TryGetValue(out UObject heroDefinition, "HeroDefinition"))
            {
                hasHero = true;

                // MeshExport only consults the hero definition when BaseCharacterParts is empty.
                if (parts.Length == 0 && heroDefinition.TryGetValue(out UObject[] specializations, "Specializations")
                                      && specializations.Length > 0)
                {
                    parts = specializations.First().GetOrDefault("CharacterParts", Array.Empty<UObject>());
                    if (parts.Length > 0) source = "HeroDefinition.Specializations";
                }
            }
        }
        catch (Exception e)
        {
            Log.Debug("HeroDefinition walk failed for {Name}: {Message}", asset.Name, e.Message);
        }

        // Backpacks / pets / companions keep a flat CharacterParts array.
        if (parts.Length == 0)
        {
            try
            {
                parts = asset.GetOrDefault("CharacterParts", Array.Empty<UObject>());
                if (parts.Length > 0) source = "CharacterParts";
            }
            catch { /* not every definition has one */ }
        }

        var set = new CharacterPartSet { Source = source, HasHeroDefinition = hasHero };

        foreach (var part in parts)
        {
            if (part is null) continue;

            string? meshPath = null;
            string? additionalData = null;
            var partType = "Other";
            var gender = "Male";

            try { meshPath = part.GetOrDefault<USkeletalMesh?>("SkeletalMesh")?.GetPathName(); }
            catch (Exception e) { Log.Debug("Part mesh read failed for {Name}: {Message}", part.Name, e.Message); }

            try { partType = part.GetEnumOrDefault("CharacterPartType", EFortCustomPartType.Head).ToString(); }
            catch { /* keep Other */ }

            try { gender = part.GetEnumOrDefault("GenderPermitted", EFortCustomGender.Male).ToString(); }
            catch { /* keep Male */ }

            try { additionalData = part.GetOrDefault<UObject?>("AdditionalData")?.ExportType; }
            catch { /* optional */ }

            set.Parts.Add(new CharacterPartInfo
            {
                Name = part.Name,
                ObjectPath = SafePath(part),
                PartType = partType,
                Gender = gender,
                SkeletalMesh = meshPath,
                AdditionalData = additionalData
            });
        }

        return set;
    }

    private static string SafePath(UObject asset)
    {
        try { return asset.GetPathName(); }
        catch { return asset.Name; }
    }
}
