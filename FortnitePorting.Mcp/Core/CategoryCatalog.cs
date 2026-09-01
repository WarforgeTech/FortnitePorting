using System.Collections.Concurrent;
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
/// Per-enumeration mutable state that the HidePredicate / AddStyleHandler callbacks operate on.
/// In the GUI these lived on the AssetLoader instance; headless they are handed in explicitly
/// so the catalog itself stays immutable and reusable across concurrent MCP requests.
/// </summary>
public class AssetEnumerationState
{
    public readonly ConcurrentBag<string> FilteredAssetBag = [];
    public readonly ConcurrentDictionary<string, ConcurrentBag<string>> StyleDictionary = [];
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

    public Func<AssetEnumerationState, UObject, string, bool> HidePredicate { get; init; } = (state, asset, name) => false;
    public Action<AssetEnumerationState, UObject, string> AddStyleHandler { get; init; } = (state, asset, name) => { };

    public ManuallyDefinedAsset[] ManuallyDefinedAssets { get; init; } = [];
    public Func<HeadlessLoader, ManuallyDefinedAsset[]>? ManuallyDefinedAssetsFactory { get; init; }

    public UTexture2D? GetIcon(UObject asset) => LowResIconHandler(asset) ?? HighResIconHandler(asset);
}

/// <summary>
/// The full FortnitePorting asset category table as plain data, plus the default
/// property-resolution handlers it depends on.
/// </summary>
public static class CategoryCatalog
{
    // Shared with the Prop / Item / Trap loaders: first occurrence of a display name wins,
    // subsequent ones are folded away as styles of the first.
    private static readonly Func<AssetEnumerationState, UObject, string, bool> DedupeByDisplayName = (state, asset, name) =>
    {
        if (state.FilteredAssetBag.Contains(name)) return true;
        state.FilteredAssetBag.Add(name);
        return false;
    };

    private static readonly Action<AssetEnumerationState, UObject, string> CollectStyleByDisplayName = (state, asset, name) =>
    {
        var path = asset.GetPathName();
        state.StyleDictionary.TryAdd(name, []);
        state.StyleDictionary[name].Add(path);
    };

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
            HideRarity = true
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
            LoadHiddenAssets = true
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
            HidePredicate = DedupeByDisplayName,
            AddStyleHandler = CollectStyleByDisplayName
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
            HideNames = ["_Harvest", "Weapon_Pickaxe_", "Weapons_Pickaxe_", "Dev_WID", "Juno"],
            HidePredicate = DedupeByDisplayName,
            AddStyleHandler = CollectStyleByDisplayName
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
            HideNames = ["SurvivorItemData", "OutpostUpgrade_StormShieldAmplifier"]
        },
        new()
        {
            Type = EExportType.Trap,
            Category = EAssetCategory.Gameplay,
            ClassNames = ["FortTrapItemDefinition"],
            HideNames = ["TID_Creative", "TID_Floor_Minigame_Trigger_Plate"],
            HidePredicate = DedupeByDisplayName
        },
        new()
        {
            Type = EExportType.Vehicle,
            Category = EAssetCategory.Gameplay,
            ClassNames = ["FortVehicleItemDefinition"],
            LowResIconHandler = asset => GetVehicleMetadata<UTexture2D>(asset, "Icon", "SmallPreviewImage"),
            HighResIconHandler = asset => GetVehicleMetadata<UTexture2D>(asset, "Icon", "LargePreviewImage"),
            DisplayNameHandler = asset => GetVehicleMetadata<FText>(asset, "DisplayName", "ItemName")?.Text,
            HideRarity = true
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
            LoadHiddenAssets = true,
            HidePredicate = (state, asset, name) => asset.GetOrDefault<FSoftObjectPath?>("ParentExtractableDefinition") is not null,
            AddStyleHandler = (state, asset, name) =>
            {
                var parentDefPath = asset.GetOrDefault<FSoftObjectPath>("ParentExtractableDefinition");

                var key = asset.Name;
                if (parentDefPath.TryLoad(out var parentDef))
                    key = parentDef.Name;

                var path = asset.GetPathName();
                state.StyleDictionary.TryAdd(key, []);
                state.StyleDictionary[key].Add(path);
            }
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
