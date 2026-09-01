using CUE4Parse.GameTypes.FN.Assets.Exports.DataAssets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Exports.Engine.Font;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.Sound;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.i18N;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.Engine.Animation;
using CUE4Parse.UE4.Objects.GameplayTags;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.Utils;
using FortnitePorting.CUE4Parse.Extensions;
using FortnitePorting.CUE4Parse.Models.Unreal.VirtualTexture;
using FortnitePorting.Exporting;

namespace FortnitePorting.Mcp.Core;

/// <summary>Headless copy of the GUI's EAssetCategory (UI metadata attributes dropped).</summary>
public enum EAssetCategory
{
    Cosmetics,
    Creative,
    Gameplay,
    Festival,
    RocketRacing,
    Lego,
    FallGuys,
    Misc
}

/// <summary>An asset that has no item definition and is addressed by raw mesh path.</summary>
public record ManuallyDefinedAsset
{
    public required string Name { get; init; }
    public required string AssetPath { get; init; }
    public string? IconPath { get; init; }
    public string Description { get; init; } = "No Description.";
}

/// <summary>
/// One entry of the asset category table, lifted verbatim from AssetLoaderService.Categories.
/// UI-only concerns (filters, observable collections, custom/Oshawott assets) are dropped.
/// </summary>
public record AssetCategoryEntry
{
    public required EExportType Type { get; init; }
    public EAssetCategory Category { get; init; } = EAssetCategory.Misc;

    public string[] ClassNames { get; init; } = [];
    public string[] AllowNames { get; init; } = [];
    public string[] HideNames { get; init; } = [];
    public string[] DisallowedNames { get; init; } = [];

    public bool LoadHiddenAssets { get; init; }
    public bool HideRarity { get; init; }

    public string PlaceholderIconPath { get; init; } = "FortniteGame/Content/Global/Textures/Default/DefaultUI/T_Placeholder_Generic";

    public Func<UObject, UTexture2D?> LowResIconHandler { get; init; } = CategoryCatalog.GetLowResIcon;
    public Func<UObject, UTexture2D?> HighResIconHandler { get; init; } = CategoryCatalog.GetHighResIcon;
    public Func<UObject, string?> DisplayNameHandler { get; init; } = asset => asset.GetAnyOrDefault<FText?>("DisplayName", "ItemName")?.Text;
    public Func<UObject, string?> DescriptionHandler { get; init; } = asset => asset.GetAnyOrDefault<FText?>("Description", "ItemDescription")?.Text.TrimEnd();
    public Func<UObject, FGameplayTagContainer?> GameplayTagHandler { get; init; } = CategoryCatalog.GetGameplayTags;

    public ManuallyDefinedAsset[] ManuallyDefinedAssets { get; init; } = [];
    public Func<HeadlessLoader, ManuallyDefinedAsset[]>? ManuallyDefinedAssetsFactory { get; init; }

    /// <summary>
    /// Collapse rows that share a display name onto their first occurrence (rarity/tier clones -
    /// WID_ArcadeShotgun_C / _R / _SR are one "8-Bit Shotgun"). This is the declarative form of the
    /// old <see cref="HidePredicate"/> dedupe: <see cref="AssetQuery.Canonical"/> can apply it to the
    /// WHOLE category up front, so browse_category and make_contact_sheet page the same list.
    /// </summary>
    public bool DedupeDisplayNames { get; init; }

    /// <summary>
    /// Append a short asset-name discriminator to display names that several rows share, for
    /// categories where the duplicates are genuinely different assets (Vehicle: 7 "Whiplash"es).
    /// </summary>
    public bool DisambiguateDuplicateNames { get; init; }

    public UTexture2D? GetIcon(UObject asset) => LowResIconHandler(asset) ?? HighResIconHandler(asset);

    /// <summary>True when this row's package path matches one of the category's HideNames.</summary>
    public bool IsHiddenName(string packagePath)
        => HideNames.Any(name => packagePath.Contains(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The full FortnitePorting asset category table as plain data, plus the default
/// property-resolution handlers it depends on.
/// </summary>
public static class CategoryCatalog
{
    public static readonly IReadOnlyList<AssetCategoryEntry> Entries =
    [
        // ---------------- Cosmetics ----------------
        new()
        {
            Type = EExportType.Outfit,
            Category = EAssetCategory.Cosmetics,
            ClassNames = ["AthenaCharacterItemDefinition"],
            HideNames = ["_NPC", "_TBD", "CID_VIP", "_Creative", "_SG"],
            DisallowedNames = ["Bean_", "BeanCharacter"],
            PlaceholderIconPath = "FortniteGame/Content/Athena/Prototype/Textures/T_Placeholder_Item_Outfit",
            LoadHiddenAssets = true,
            LowResIconHandler = asset =>
            {
                var previewImage = GetLowResIcon(asset);
                if (previewImage is null && asset.TryGetValue(out UObject hero, "HeroDefinition"))
                    previewImage = GetLowResIcon(hero);

                return previewImage;
            },
            HighResIconHandler = asset =>
            {
                var previewImage = GetHighResIcon(asset);
                if (previewImage is null && asset.TryGetValue(out UObject hero, "HeroDefinition"))
                    previewImage = GetHighResIcon(hero);

                return previewImage;
            }
        },
        new()
        {
            Type = EExportType.Backpack,
            Category = EAssetCategory.Cosmetics,
            ClassNames = ["AthenaBackpackItemDefinition"],
            HideNames = ["_STWHeroNoDefaultBackpack", "_TEST", "Dev_", "_NPC", "_TBD", "ChaosCloth"]
        },
        new()
        {
            Type = EExportType.Pickaxe,
            Category = EAssetCategory.Cosmetics,
            ClassNames = ["AthenaPickaxeItemDefinition"],
            HideNames = ["Dev_", "TBD_"],
            LowResIconHandler = asset =>
            {
                var previewImage = GetLowResIcon(asset);
                if ((previewImage is null || previewImage.Name.Contains("Placeholder", StringComparison.OrdinalIgnoreCase)) && asset.TryGetValue(out UObject weapon, "WeaponDefinition"))
                    previewImage = GetLowResIcon(weapon);

                return previewImage;
            },
            HighResIconHandler = asset =>
            {
                var previewImage = GetHighResIcon(asset);
                if ((previewImage is null || previewImage.Name.Contains("Placeholder", StringComparison.OrdinalIgnoreCase)) && asset.TryGetValue(out UObject weapon, "WeaponDefinition"))
                    previewImage = GetHighResIcon(weapon);

                return previewImage;
            }
        },
        new()
        {
            Type = EExportType.Glider,
            Category = EAssetCategory.Cosmetics,
            ClassNames = ["AthenaGliderItemDefinition"]
        },
        new()
        {
            Type = EExportType.Pet,
            Category = EAssetCategory.Cosmetics,
            ClassNames = ["AthenaPetCarrierItemDefinition"]
        },
        new()
        {
            Type = EExportType.Toy,
            Category = EAssetCategory.Cosmetics,
            ClassNames = ["AthenaToyItemDefinition"]
        },
        new()
        {
            Type = EExportType.Emoticon,
            Category = EAssetCategory.Cosmetics,
            ClassNames = ["AthenaEmojiItemDefinition"],
            HideNames = ["Emoji_100APlus"]
        },
        new()
        {
            Type = EExportType.Spray,
            Category = EAssetCategory.Cosmetics,
            ClassNames = ["AthenaSprayItemDefinition"],
            HideNames = ["SPID_000", "SPID_001"]
        },
        new()
        {
            Type = EExportType.Banner,
            Category = EAssetCategory.Cosmetics,
            ClassNames = ["FortHomebaseBannerIconItemDefinition"],
            HideRarity = true,
            // Every one of the ~1000 FortHomebaseBannerIconItemDefinitions carries the SAME
            // localised DisplayName, the literal string "Banner Icon", so the real name is only in
            // the asset name (Banner_Akita). Using it here fixes browse labels, contact-sheet
            // legends AND the display-name index in one place.
            DisplayNameHandler = asset => PrettifyAssetName(asset.Name)
        },
        new()
        {
            Type = EExportType.LoadingScreen,
            Category = EAssetCategory.Cosmetics,
            ClassNames = ["AthenaLoadingScreenItemDefinition"]
        },
        new()
        {
            Type = EExportType.Emote,
            Category = EAssetCategory.Cosmetics,
            ClassNames = ["AthenaDanceItemDefinition"],
            HideNames = ["_CT", "_NPC", "_Sync", "_Follower", "_Owned", "Sprout"],
            LoadHiddenAssets = true,
            // LoadHiddenAssets skips HideNames entirely, so the "_CT" above never fired and page 0
            // of the category was 16/24 identical white CapturePose silhouettes. DisallowedNames is
            // applied unconditionally, which is what these internal capture rigs need.
            DisallowedNames = ["EID_CT_CapturePose", "_Creative_Test"]
        },
        new()
        {
            Type = EExportType.SideKick,
            Category = EAssetCategory.Cosmetics,
            ClassNames = ["CosmeticCompanionItemDefinition"],
            HideNames = ["Companion_SitPlant_PerfTest", "Companion_TestCompanion2_Mutable", "Companion_Placeholder"]
        },
        new()
        {
            Type = EExportType.Kicks,
            Category = EAssetCategory.Cosmetics,
            ClassNames = ["CosmeticShoesItemDefinition"],
            LoadHiddenAssets = true
        },

        // ---------------- Creative ----------------
        new()
        {
            Type = EExportType.Prop,
            Category = EAssetCategory.Creative,
            ClassNames = ["FortPlaysetPropItemDefinition"],
            HideRarity = true,
            DedupeDisplayNames = true
        },
        new()
        {
            Type = EExportType.Prefab,
            Category = EAssetCategory.Creative,
            ClassNames = ["FortPlaysetItemDefinition"],
            HideNames =
            [
                "Device", "PID_Playset", "PID_MapIndicator", "SpikyStadium", "PID_StageLight", "PID_Temp_Island",
                "PID_LimeEmptyPlot", "PID_Townscaper", "JunoPlotPlaysetItemDefintion", "LME",
                "PID_ObstacleCourse", "MW_"
            ],
            HideRarity = true,
            GameplayTagHandler = asset =>
            {
                var tagsHelper = asset.GetOrDefault<FStructFallback?>("CreativeTagsHelper");
                var tags = tagsHelper?.GetOrDefault<FName[]>("CreativeTags") ?? [];
                var gameplayTags = tags.Select(tag => new FGameplayTag(tag)).ToArray();
                return new FGameplayTagContainer(gameplayTags);
            }
        },

        // ---------------- Gameplay ----------------
        new()
        {
            Type = EExportType.Item,
            Category = EAssetCategory.Gameplay,
            ClassNames =
            [
                "AthenaGadgetItemDefinition", "FortWeaponRangedItemDefinition",
                "FortWeaponMeleeItemDefinition", "FortCreativeWeaponMeleeItemDefinition",
                "FortCreativeWeaponRangedItemDefinition", "FortWeaponMeleeDualWieldItemDefinition"
            ],
            // The three path families appended here ship no icon art at all: sampled 96 rows of
            // /SaveTheWorld/Items/Weapons/ (3,784 rows - the STW rarity x material x tier clone
            // matrix), 96 of /Sprout (234) and 48 of DIsguiseDevice_SW (61) and got realIconCount 0
            // for every one. They were 37 of the 37 icon-coverage misses in a 60-row sample.
            // They stay reachable through search_files and by direct objectPath export.
            HideNames =
            [
                "_Harvest", "Weapon_Pickaxe_", "Weapons_Pickaxe_", "Dev_WID", "Juno",
                "/SaveTheWorld/Items/Weapons/", "DIsguiseDevice_SW", "/Sprout"
            ],
            DedupeDisplayNames = true
        },
        new()
        {
            Type = EExportType.WeaponMod,
            Category = EAssetCategory.Gameplay,
            ManuallyDefinedAssetsFactory = BuildWeaponModAssets
        },
        new()
        {
            Type = EExportType.Resource,
            Category = EAssetCategory.Gameplay,
            ClassNames = ["FortIngredientItemDefinition", "FortResourceItemDefinition"],
            // Juno/LEGO ingredients (220 rows across JunoGame/JunoTabasco*/JunoNeptune*/JunoKlombo*/
            // JunoEventAnubis) and STW crafting ingredients (46 rows) are pure data with no icon:
            // sampled 142 of the 266 and every single one resolved to the placeholder texture.
            // The tail: STW RepairRadar mission parts (5 rows) and Sprout currency tokens (4 rows),
            // all placeholder-only, which between them made up the whole of the last browse page.
            HideNames =
            [
                "SurvivorItemData", "OutpostUpgrade_StormShieldAmplifier",
                "Juno", "/SaveTheWorld/Items/Ingredients/", "/SaveTheWorld/Missions/", "/Sprout"
            ]
        },
        new()
        {
            Type = EExportType.Trap,
            Category = EAssetCategory.Gameplay,
            ClassNames = ["FortTrapItemDefinition"],
            // 347 of the 444 trap definitions are the STW rarity/tier ladder under
            // /SaveTheWorld/Items/Traps/ (TID_Floor_Freeze_R_T02 ...). Sampled 96 of them across two
            // pages: realIconCount 0. Contact sheet page 8 was 24/24 magenta before this.
            HideNames = ["TID_Creative", "TID_Floor_Minigame_Trigger_Plate", "/SaveTheWorld/Items/Traps/"],
            DedupeDisplayNames = true
        },
        new()
        {
            Type = EExportType.Vehicle,
            Category = EAssetCategory.Gameplay,
            ClassNames = ["FortVehicleItemDefinition"],
            LowResIconHandler = asset => GetVehicleMetadata<UTexture2D>(asset, "Icon", "SmallPreviewImage"),
            HighResIconHandler = asset => GetVehicleMetadata<UTexture2D>(asset, "Icon", "LargePreviewImage"),
            DisplayNameHandler = asset => GetVehicleMetadata<FText>(asset, "DisplayName", "ItemName")?.Text,
            DescriptionHandler = DescribeVehicle,
            HideRarity = true,
            // 7 vehicles are called "Whiplash", 4 "Baller": without a discriminator a sheet legend
            // cannot tell you which cell is which.
            DisambiguateDuplicateNames = true
        },
        new()
        {
            Type = EExportType.Wildlife,
            Category = EAssetCategory.Gameplay,
            HideRarity = true,
            ManuallyDefinedAssets =
            [
                new() { Name = "Llama", AssetPath = "/Labrador/Meshes/Labrador_Mammal", IconPath = "FortniteGame/Content/UI/Foundation/Textures/Icons/Athena/T-T-Icon-BR-SM-Athena-SupplyLlama-01" },
                new() { Name = "Boar", AssetPath = "/Irwin/AI/Prey/Burt/Meshes/Burt_Mammal", IconPath = "/Irwin/Icons/T-Icon-Fauna-Boar" },
                new() { Name = "Chicken", AssetPath = "/Irwin/AI/Prey/Nug/Meshes/Nug_Bird", IconPath = "/Irwin/Icons/T-Icon-Fauna-Chicken" },
                new() { Name = "Zombie Chicken", AssetPath = "/NugZ/Meshes/Chicken_Zombie_Bird", IconPath = "/NugZ/Icons/T-T-Icon-BR-ChickenZombieFauna" },
                new() { Name = "Klombo", AssetPath = "FortniteGame/Plugins/GameFeatures/Juno/JunoCreature_ButterCakeMamma/Content/SkeletalMesh/Butter_Cake_Mammal", IconPath = "FortniteGame/Plugins/GameFeatures/Juno/JunoCreature_ButterCakeMamma/Content/Textures/T-T-Icon-BR-ButterCake" },
                new() { Name = "Frog", AssetPath = "/Irwin/AI/Simple/Smackie/Meshes/Smackie_Amphibian", IconPath = "/Irwin/Icons/T-Icon-Fauna-Frog" },
                new() { Name = "Crow", AssetPath = "/Irwin/AI/Prey/Crow/Meshes/Crow_Bird", IconPath = "/Irwin/Icons/T-Icon-Fauna-Crow" },
                new() { Name = "Raptor", AssetPath = "/Irwin/AI/Predators/Robert/Meshes/Jungle_Raptor_Fauna", IconPath = "/Irwin/Icons/T-Icon-Fauna-JungleRaptor" },
                new() { Name = "Wolf", AssetPath = "/Irwin/AI/Predators/Grandma/Meshes/Grandma_Mammal", IconPath = "/Irwin/Icons/T-Icon-Fauna-Wolf" },
                new() { Name = "Swarmer", AssetPath = "/ProtoSwarm/Assets/TidalCrane_Swarmer/Meshes/TidalCrane_Swarmer_Creature", IconPath = "/ProtoSwarm/NPCs/Swarm/TidalCrane_Swarm_Base/Icon/T_Icon_BR_TidalCrane_Swarmer" },
                new() { Name = "Bomber", AssetPath = "/ProtoSwarm/Assets/TidalCrane_Brute/Meshes/TidalCrane_Brute_Creature", IconPath = "/ProtoSwarm/Assets/UI/Icons/T_Icon_BR_TidalCrane_Brute" },
                new() { Name = "Roly Poly", AssetPath = "/BouncyBug/Assets/Meshes/RolyPoly_TidalCrane_Creature", IconPath = "/BouncyBug/UI/Texture/T_UI_Ping_RolyPoly" },
                new() { Name = "Swarm Queen", AssetPath = "/TidalCrane_Swarm_Boss/Assets/Meshes/TidalCrane_Boss_Creature", IconPath = "/TidalCrane_Swarm_Boss/Gameplay/Icon/T_Icon_BR_BugQueen" }
            ]
        },
        new()
        {
            Type = EExportType.Sprite,
            Category = EAssetCategory.Gameplay,
            ClassNames = ["ExtractableItemDefinition"],
            LoadHiddenAssets = true
            // Sprite variants (ESD_AirSprite_Variant_Gold ...) are separate ExtractableItemDefinitions
            // that point back at their parent through ParentExtractableDefinition. They are kept as
            // browsable rows in their own right and additionally surfaced as a channel on the parent
            // by list_asset_styles (see ExportTools.ReadSpriteVariants).
        },

        // ---------------- Festival ----------------
        new() { Type = EExportType.FestivalGuitar, Category = EAssetCategory.Festival, ClassNames = ["SparksGuitarItemDefinition"] },
        new() { Type = EExportType.FestivalBass, Category = EAssetCategory.Festival, ClassNames = ["SparksBassItemDefinition"] },
        new() { Type = EExportType.FestivalKeytar, Category = EAssetCategory.Festival, ClassNames = ["SparksKeyboardItemDefinition"] },
        new() { Type = EExportType.FestivalDrum, Category = EAssetCategory.Festival, ClassNames = ["SparksDrumItemDefinition"] },
        new() { Type = EExportType.FestivalMic, Category = EAssetCategory.Festival, ClassNames = ["SparksMicItemDefinition"] },

        // ---------------- Fall Guys ----------------
        new()
        {
            Type = EExportType.FallGuysOutfit,
            Category = EAssetCategory.FallGuys,
            ClassNames = ["AthenaCharacterItemDefinition"],
            AllowNames = ["Bean_"],
            PlaceholderIconPath = "FortniteGame/Content/Athena/Prototype/Textures/T_Placeholder_Item_Outfit",
            HideRarity = true
        }
    ];

    public static AssetCategoryEntry? ForType(EExportType type)
        => Entries.FirstOrDefault(entry => entry.Type == type);

    public static AssetCategoryEntry? ForClassName(string className)
        => Entries.FirstOrDefault(entry => entry.ClassNames.Contains(className));

    /// <summary>
    /// Human-readable fallback for an asset that has no localised display name (dev/test rows such
    /// as <c>SID_Guitar_Figure</c>, <c>CID_BentBaton_Temp</c>) and the real name for Banner, whose
    /// entire class shares the single string "Banner Icon".
    /// <para>
    /// Strips the definition-type prefix, splits on underscores and CamelCase boundaries. It is a
    /// LABEL, never an identity: the asset name is always reported alongside it, and the
    /// display-name search index still stores only genuine localised names.
    /// </para>
    /// </summary>
    public static string PrettifyAssetName(string assetName)
    {
        if (string.IsNullOrWhiteSpace(assetName)) return assetName;

        // Definition-type prefixes used across the Fortnite item definitions.
        string[] prefixes =
        [
            "Banner_", "CID_", "EID_", "BID_", "Backpack_", "Pickaxe_ID_", "Pickaxe_", "Glider_ID_",
            "Glider_", "WID_", "TID_", "VID_", "SID_", "SPID_", "PID_", "AGID_", "ESD_", "ID_",
            "Emoji_", "LSID_", "Petcarrier_", "Companion_", "Bean_", "Character_", "Trap_", "Vehicle_"
        ];

        var trimmed = assetName;
        foreach (var prefix in prefixes)
        {
            if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var candidate = trimmed[prefix.Length..];
            // Never prettify away the whole name (e.g. an asset literally called "ID_").
            if (candidate.Length >= 2) trimmed = candidate;
            break;
        }

        var builder = new System.Text.StringBuilder(trimmed.Length + 8);
        for (var i = 0; i < trimmed.Length; i++)
        {
            var c = trimmed[i];
            if (c is '_' or '-')
            {
                if (builder.Length > 0 && builder[^1] != ' ') builder.Append(' ');
                continue;
            }

            // CamelCase boundary: lower/digit followed by upper, or upper followed by upper+lower.
            if (i > 0 && char.IsUpper(c) && builder.Length > 0 && builder[^1] != ' ' &&
                (!char.IsUpper(trimmed[i - 1]) || (i + 1 < trimmed.Length && char.IsLower(trimmed[i + 1]))))
                builder.Append(' ');

            builder.Append(c);
        }

        var result = builder.ToString().Trim();
        return result.Length == 0 ? assetName : result;
    }

    // ---------------- Default handlers (AssetLoader.cs ~392-413) ----------------

    public static UTexture2D? GetLowResIcon(UObject asset)
    {
        return asset.GetDataListItem<UTexture2D?>("Icon", "LargeIcon")
               ?? asset.GetAnyOrDefault<UTexture2D?>("Icon", "SmallPreviewImage", "LargeIcon");
    }

    public static UTexture2D? GetHighResIcon(UObject asset)
    {
        return asset.GetDataListItem<UTexture2D?>("LargeIcon", "Icon")
               ?? asset.GetAnyOrDefault<UTexture2D?>("LargePreviewImage", "LargeIcon", "Icon");
    }

    public static UTexture2D? GetIcon(UObject asset) => GetLowResIcon(asset) ?? GetHighResIcon(asset);

    public static FGameplayTagContainer? GetGameplayTags(UObject asset)
    {
        return asset.GetDataListItem<FGameplayTagContainer?>("Tags")
               ?? asset.GetOrDefault<FGameplayTagContainer?>("GameplayTags");
    }

    /// <summary>
    /// FortVehicleItemDefinition genuinely carries no Description text, on the definition or on the
    /// actor blueprint's MarkerDisplay - all 109 vehicles reported "No Description." Rather than keep
    /// printing that, surface the metadata the definition DOES hold and that a caller can act on: the
    /// in-game spawn aliases (what a Creative "spawn vehicle" command accepts) and the vehicle actor
    /// class. Both are plain properties on the already-loaded object, so this costs nothing.
    /// </summary>
    private static string? DescribeVehicle(UObject asset)
    {
        var localised = GetVehicleMetadata<FText>(asset, "Description", "ItemDescription")?.Text.TrimEnd();
        if (!string.IsNullOrWhiteSpace(localised)) return localised;

        var parts = new List<string>();

        var spawnNames = asset.GetOrDefault<string[]>("SpawnVehicleNames", []);
        if (spawnNames.Length > 0) parts.Add($"Spawn names: {string.Join(", ", spawnNames)}.");

        try
        {
            if (asset.GetOrDefault<FSoftObjectPath?>("VehicleActorClass")?.AssetPathName.Text is { Length: > 0 } actorClass)
                parts.Add($"Actor class: {actorClass.SubstringAfterLast('/')}.");
        }
        catch { /* optional */ }

        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    /// <summary>Copied from the GUI's CUE4ParseExtensions: vehicles hide their metadata behind the actor blueprint.</summary>
    public static T? GetVehicleMetadata<T>(UObject asset, params string[] names) where T : class
    {
        static FStructFallback? GetMarkerDisplay(UBlueprintGeneratedClass? blueprint)
        {
            var obj = blueprint?.ClassDefaultObject.Load();
            return obj?.GetOrDefault<FStructFallback>("MarkerDisplay");
        }

        var output = asset.GetAnyOrDefault<T?>(names);
        if (output is not null) return output;

        var vehicle = asset.Get<UBlueprintGeneratedClass>("VehicleActorClass");
        output = GetMarkerDisplay(vehicle)?.GetAnyOrDefault<T?>(names);
        if (output is not null) return output;

        var vehicleSuper = vehicle.SuperStruct.Load<UBlueprintGeneratedClass>();
        output = GetMarkerDisplay(vehicleSuper)?.GetAnyOrDefault<T?>(names);
        return output;
    }

    /// <summary>Port of ExportService.DetermineExportType, resolving the fallback against this catalog.</summary>
    public static EExportType DetermineExportType(UObject asset)
    {
        var exportType = asset switch
        {
            USkeletalMesh => EExportType.Mesh,
            UStaticMesh => EExportType.Mesh,
            USkeleton => EExportType.Mesh,
            UBlueprintGeneratedClass => EExportType.Mesh,
            UWorld => EExportType.World,
            UTexture => EExportType.Texture,
            UVirtualTextureBuilder => EExportType.Texture,
            UBuildingTextureData => EExportType.Texture,
            USoundWave => EExportType.Sound,
            USoundCue => EExportType.Sound,
            UAnimMontage => EExportType.Animation,
            UAnimSequenceBase => EExportType.Animation,
            UFontFace => EExportType.Font,
            UPoseAsset => EExportType.PoseAsset,
            UMaterialInstance => EExportType.MaterialInstance,
            UMaterial => EExportType.Material,
            _ => EExportType.None
        };

        if (exportType is EExportType.None)
        {
            exportType = asset.ExportType switch
            {
                "CustomCharacterPart" => EExportType.CharacterPart,
                _ => EExportType.None
            };
        }

        if (exportType is EExportType.None && ForClassName(asset.ExportType) is { } entry)
            exportType = entry.Type;

        return exportType;
    }

    private static ManuallyDefinedAsset[] BuildWeaponModAssets(HeadlessLoader loader)
    {
        string[] weaponModClasses = ["FortWeaponModItemDefinition", "FortWeaponModItemDefinitionMagazine", "FortWeaponModItemDefinitionOptic"];
        var weaponModTable = loader.Provider.LoadPackageObject<UDataTable>("WeaponMods/DataTables/WeaponModOverrideData");
        var assetDatas = loader.AssetRegistry.Where(data => weaponModClasses.Contains(data.AssetClass.Text));

        var weaponModAssets = new List<ManuallyDefinedAsset>();
        var alreadyAddedNames = new HashSet<string>();
        foreach (var assetData in assetDatas)
        {
            if (!loader.Provider.TryLoadPackageObject(assetData.ObjectPath, out var asset)) continue;

            var icon = GetIcon(asset);
            if (icon is null) continue;

            var tag = asset.GetOrDefault<FGameplayTag>("PluginTuningTag").ToString();

            var defaultModData = asset.GetOrDefault<FStructFallback?>("DefaultModData");
            var mainModMeshData = defaultModData?.GetOrDefault<FStructFallback?>("MeshData");
            var mainModMesh = mainModMeshData?.GetOrDefault<UStaticMesh?>("ModMesh");

            var addedOverrides = false;
            foreach (var weaponModData in weaponModTable.RowMap.Values)
            {
                var weaponModTag = weaponModData.GetOrDefault<FGameplayTag>("ModTag").ToString();
                if (!tag.Equals(weaponModTag)) continue;

                var modMeshData = weaponModData.GetOrDefault<FStructFallback>("ModMeshData");
                var modMesh = modMeshData.GetOrDefault<UStaticMesh?>("ModMesh");
                modMesh ??= mainModMesh;
                if (modMesh is null) continue;

                var name = modMesh.Name;
                if (alreadyAddedNames.Contains(name)) continue;

                weaponModAssets.Add(new ManuallyDefinedAsset
                {
                    Name = name,
                    AssetPath = modMesh.GetPathName(),
                    IconPath = icon.GetPathName()
                });
                alreadyAddedNames.Add(name);
                addedOverrides = true;
            }

            if (mainModMesh is not null && !addedOverrides)
            {
                weaponModAssets.Add(new ManuallyDefinedAsset
                {
                    Name = mainModMesh.Name,
                    AssetPath = mainModMesh.GetPathName(),
                    IconPath = icon.GetPathName()
                });
            }
        }

        return weaponModAssets.ToArray();
    }
}
