using System.ComponentModel;
using System.Text.Json.Nodes;
using CUE4Parse.GameTypes.FN.Enums;
using CUE4Parse.UE4.AssetRegistry.Objects;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.VirtualFileSystem;
using CUE4Parse.Utils;
using FortnitePorting;
using FortnitePorting.CUE4Parse.Extensions;
using FortnitePorting.CUE4Parse.Models.Fortnite.Styles;
using FortnitePorting.Mcp.Core;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Serilog;

namespace FortnitePorting.Mcp.Tools;

[McpServerToolType]
public static class DiscoveryTools
{
    private const int SearchLimitCap = 100;
    private const int BrowsePageSizeCap = 100;
    private const int FileLimitCap = 200;

    // ------------------------------------------------------------------ list_categories

    [McpServerTool(Name = "list_categories", ReadOnly = true, Title = "List asset categories")]
    [Description("""
                 Lists every asset category the server can browse, with how many asset-registry rows
                 each one holds. The `exportType` values here are exactly what search_assets,
                 browse_category and make_contact_sheet accept as their `category` argument.
                 """)]
    public static async Task<CallToolResult> ListCategoriesAsync(
        HeadlessLoader loader, AssetQuery assets, CancellationToken cancellationToken)
    {
        if (!await loader.TryWaitReadyAsync(cancellationToken)) return ToolResults.StillLoading(loader);

        var groups = new JsonArray();
        foreach (var group in CategoryCatalog.Entries.GroupBy(entry => entry.Category))
        {
            var types = new JsonArray();
            var exportTypes = new JsonArray();
            var groupCount = 0;

            foreach (var entry in group)
            {
                var count = assets.Filtered(entry).Count;
                groupCount += count;
                exportTypes.Add(entry.Type.ToString());
                types.Add(new JsonObject
                {
                    ["exportType"] = entry.Type.ToString(),
                    ["assetCount"] = count,
                    ["classNames"] = ToolResults.ToJsonArray(entry.ClassNames),
                    ["registryBacked"] = entry.ClassNames.Length > 0
                });
            }

            groups.Add(new JsonObject
            {
                ["category"] = group.Key.ToString(),
                ["exportTypes"] = exportTypes,
                ["assetCount"] = groupCount,
                ["types"] = types
            });
        }

        return ToolResults.Structured(new JsonObject
        {
            ["status"] = "ok",
            ["totalRegistryEntries"] = loader.AssetRegistry.Count,
            ["categories"] = groups,
            ["usage"] = "Pass any exportType value (e.g. \"Prop\", \"Outfit\") as the `category` argument of search_assets, browse_category or make_contact_sheet."
        });
    }

    // ------------------------------------------------------------------ search_assets

    [McpServerTool(Name = "search_assets", ReadOnly = true, Title = "Search assets")]
    [Description("""
                 Fast asset-registry search over asset names, package paths AND in-game display names.
                 Never opens a .uasset, so it stays responsive across hundreds of thousands of rows -
                 display names come from a background-built, disk-cached index (see get_status ->
                 nameIndex; while a category is still building, matching for it falls back to asset and
                 package names only and the reply says so). Every item reports matchedOn:
                 name | displayName | both. Use it to find candidates, then make_contact_sheet to
                 actually SEE them.
                 """)]
    public static async Task<CallToolResult> SearchAssetsAsync(
        HeadlessLoader loader,
        AssetQuery assets,
        DisplayNameIndex names,
        [Description("Text to look for in the asset name, package path or in-game display name, e.g. \"hedge\" or \"Peely\".")] string query,
        [Description("Optional category filter, e.g. \"Prop\" or \"Outfit\". See list_categories.")] string? category = null,
        [Description("\"contains\" (default) or \"regex\".")] string match = "contains",
        [Description("Maximum rows to return. Capped at 100.")] int limit = 25,
        [Description("Rows to skip, for paging through a large result set.")] int offset = 0,
        CancellationToken cancellationToken = default)
    {
        if (!await loader.TryWaitReadyAsync(cancellationToken)) return ToolResults.StillLoading(loader);

        limit = Math.Clamp(limit, 1, SearchLimitCap);
        offset = Math.Max(0, offset);

        var predicate = AssetQuery.BuildStringMatcher(query, match);

        IEnumerable<FPartialAssetData> source;
        string? resolvedCategory = null;
        EExportType? scopedType = null;

        if (!string.IsNullOrWhiteSpace(category))
        {
            var entry = AssetQuery.ResolveCategory(category);
            resolvedCategory = entry.Type.ToString();
            scopedType = entry.Type;
            source = assets.Filtered(entry);

            // Cheap when the category is cached on disk; otherwise this returns false quickly and
            // the search degrades to name-only matching with a note.
            await names.WhenCategoryReadyAsync(entry.Type, cancellationToken: cancellationToken);
        }
        else
        {
            source = assets.AllCategorised();

            // No single category to wait on: give the whole index a short grace (a warm run loads
            // every category off disk well inside it) and otherwise report partial coverage.
            await names.WhenAllReadyAsync(cancellationToken: cancellationToken);
        }

        // Single in-memory pass: registry substring/regex plus a dictionary hit for the display name.
        var scopedMap = scopedType is { } type ? names.MapFor(type) : null;
        var matched = new List<(FPartialAssetData Data, string? DisplayName, string MatchedOn)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var data in source)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var nameHit = predicate(data.AssetName.Text) || predicate(data.PackageName.Text);

            var displayName = scopedMap is not null
                ? scopedMap.GetValueOrDefault(data.ObjectPath)
                : names.DisplayNameForClass(data.AssetClass.Text, data.ObjectPath);

            var displayHit = displayName is not null && predicate(displayName);
            if (!nameHit && !displayHit) continue;

            // The registry is concatenated from several .bin files, so the same row can appear twice.
            if (!seen.Add(data.ObjectPath)) continue;

            matched.Add((data, displayName, (nameHit, displayHit) switch
            {
                (true, true) => "both",
                (false, true) => "displayName",
                _ => "name"
            }));
        }

        var items = new JsonArray();
        foreach (var (data, displayName, matchedOn) in matched.Skip(offset).Take(limit))
        {
            items.Add(new JsonObject
            {
                ["name"] = data.AssetName.Text,
                ["displayName"] = displayName,
                ["objectPath"] = data.ObjectPath,
                ["packagePath"] = data.PackageName.Text,
                ["assetClass"] = data.AssetClass.Text,
                ["category"] = assets.CategoryForClass(data.AssetClass.Text),
                ["matchedOn"] = matchedOn
            });
        }

        var payload = new JsonObject
        {
            ["status"] = "ok",
            ["query"] = query,
            ["category"] = resolvedCategory,
            ["match"] = match,
            ["total"] = matched.Count,
            ["offset"] = offset,
            ["limit"] = limit,
            ["returned"] = items.Count,
            ["nameIndex"] = NameIndexSummary(names, scopedType),
            ["items"] = items
        };

        var notes = new List<string>();

        if (scopedType is { } scoped)
        {
            if (!names.IsReady(scoped))
                notes.Add($"Display-name coverage for {scoped} is {names.StateFor(scoped).Name} ({names.StateFor(scoped).Percent:N0}%), " +
                          "so these results matched asset/package names only. Retry in a minute for display-name matches.");
        }
        else if (names.Coverage is not "complete")
        {
            notes.Add($"Display-name coverage is {names.Coverage}: {names.ReadyCategoryCount}/{names.TotalCategoryCount} categories indexed. " +
                      "Categories still building matched asset/package names only; pass `category` to wait for one specific category.");
        }

        if (matched.Count == 0)
        {
            notes.Add($"No asset matched \"{query}\". Try a shorter or more generic term (\"hedge\" rather than \"hedge wall large\"), " +
                      "a synonym (\"foliage\", \"bush\", \"plant\"), check the spelling, drop the `category` filter, " +
                      "or use match:\"regex\" for alternatives like \"hedge|bush|shrub\". " +
                      "browse_category plus make_contact_sheet is the reliable way to browse a category visually when you do not know the vocabulary.");
        }

        if (notes.Count > 0) payload["note"] = string.Join(" ", notes);

        return ToolResults.Structured(payload);
    }

    /// <summary>Compact per-request view of the display-name index, for the search reply.</summary>
    private static JsonObject NameIndexSummary(DisplayNameIndex names, EExportType? scoped)
    {
        if (scoped is { } type)
        {
            var state = names.StateFor(type);
            return new JsonObject
            {
                ["category"] = type.ToString(),
                ["status"] = state.Name,
                ["percent"] = Math.Round(state.Percent, 1),
                ["displayNames"] = names.CountFor(type)
            };
        }

        return new JsonObject
        {
            ["coverage"] = names.Coverage,
            ["readyCategories"] = names.ReadyCategoryCount,
            ["totalCategories"] = names.TotalCategoryCount,
            ["displayNames"] = names.TotalNames
        };
    }

    // ------------------------------------------------------------------ browse_category

    [McpServerTool(Name = "browse_category", ReadOnly = true, Title = "Browse a category")]
    [Description("""
                 Pages through one category, opening each asset to read its real display name,
                 description and gameplay tags. Style variants of the same item are collapsed onto the
                 first occurrence (styleCount reports how many were folded in). Slower than
                 search_assets because it loads packages - keep pageSize modest.
                 """)]
    public static async Task<CallToolResult> BrowseCategoryAsync(
        HeadlessLoader loader,
        AssetQuery assets,
        [Description("Category to browse, e.g. \"Prop\" or \"Outfit\". See list_categories.")] string category,
        [Description("Zero-based page index.")] int page = 0,
        [Description("Assets per page. Capped at 100.")] int pageSize = 50,
        [Description("Optional case-insensitive substring filter on the asset name.")] string? nameFilter = null,
        CancellationToken cancellationToken = default)
    {
        if (!await loader.TryWaitReadyAsync(cancellationToken)) return ToolResults.StillLoading(loader);

        var entry = AssetQuery.ResolveCategory(category);
        page = Math.Max(0, page);
        pageSize = Math.Clamp(pageSize, 1, BrowsePageSizeCap);

        var rows = (IEnumerable<FPartialAssetData>) assets.Filtered(entry);
        if (!string.IsNullOrWhiteSpace(nameFilter))
            rows = rows.Where(data => data.AssetName.Text.Contains(nameFilter, StringComparison.OrdinalIgnoreCase));

        var filtered = rows.ToList();
        var pageRows = filtered.Skip(page * pageSize).Take(pageSize).ToList();

        var loaded = await LoadPageAsync(loader, entry, pageRows, cancellationToken);

        var items = new JsonArray();
        foreach (var (data, asset, displayName) in loaded)
        {
            if (asset is null) continue;

            var hidden = entry.HideNames.Any(name => asset.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
                         || SafeHide(entry, loaded.State, asset, displayName);
            if (hidden && !entry.LoadHiddenAssets) continue;

            items.Add(new JsonObject
            {
                ["displayName"] = displayName,
                ["name"] = asset.Name,
                ["objectPath"] = data.ObjectPath,
                ["description"] = SafeDescription(entry, asset),
                ["tags"] = ToolResults.ToJsonArray(ReadTags(entry, asset)),
                ["styleCount"] = loaded.State.StyleDictionary.TryGetValue(displayName, out var styles) ? styles.Count : 1,
                ["hidden"] = hidden
            });
        }

        return ToolResults.Structured(new JsonObject
        {
            ["status"] = "ok",
            ["category"] = entry.Type.ToString(),
            ["page"] = page,
            ["pageSize"] = pageSize,
            ["total"] = filtered.Count,
            ["totalPages"] = (int) Math.Ceiling(filtered.Count / (double) pageSize),
            ["returned"] = items.Count,
            ["note"] = "Style variants are collapsed within the page; a name already seen on an earlier page can reappear.",
            ["items"] = items
        });
    }

    // ------------------------------------------------------------------ get_asset_info

    [McpServerTool(Name = "get_asset_info", ReadOnly = true, Title = "Asset details")]
    [Description("""
                 Full detail for one asset: display name, description, class, rarity/series/set,
                 gameplay tags, the icon textures it exposes, its style variant channels, and the
                 export type the exporter would pick for it.
                 """)]
    public static async Task<CallToolResult> GetAssetInfoAsync(
        HeadlessLoader loader,
        AssetQuery assets,
        [Description("Full object path, e.g. \"/Game/Foo/Bar.Bar\". Take these from search_assets.")] string objectPath,
        CancellationToken cancellationToken = default)
    {
        if (!await loader.TryWaitReadyAsync(cancellationToken)) return ToolResults.StillLoading(loader);

        UObject? asset;
        try
        {
            asset = await loader.Provider.SafeLoadPackageObjectAsync(objectPath);
        }
        catch (Exception e)
        {
            throw new McpException($"Failed to load \"{objectPath}\": {e.Message}");
        }

        if (asset is null)
            throw new McpException($"No asset could be loaded from \"{objectPath}\". Check the path with search_assets - it must be the full objectPath, not just the name.");

        var entry = CategoryCatalog.ForClassName(asset.ExportType);
        var displayName = SafeDisplayName(entry, asset);

        var payload = new JsonObject
        {
            ["status"] = "ok",
            ["objectPath"] = objectPath,
            ["name"] = asset.Name,
            ["displayName"] = displayName,
            ["description"] = entry is not null ? SafeDescription(entry, asset) : ReadDefaultDescription(asset),
            ["assetClass"] = asset.ExportType,
            ["category"] = entry?.Type.ToString(),
            ["assetCategory"] = entry?.Category.ToString(),
            ["exportType"] = SafeExportType(asset).ToString(),
            ["tags"] = ToolResults.ToJsonArray(entry is not null ? ReadTags(entry, asset) : ReadDefaultTags(asset))
        };

        // Rarity / series / set --------------------------------------------------
        if (entry is null || !entry.HideRarity)
        {
            var rarity = SafeRarity(asset);
            if (rarity is not null) payload["rarity"] = rarity;
        }

        payload["series"] = SafeSeries(asset);
        payload["set"] = SafeSet(loader, entry, asset);

        // Icon textures ----------------------------------------------------------
        var icons = new JsonArray();
        AddIcon(icons, "lowRes", TryIcon(() => (entry ?? DefaultEntry).LowResIconHandler(asset)));
        AddIcon(icons, "highRes", TryIcon(() => (entry ?? DefaultEntry).HighResIconHandler(asset)));
        payload["iconTextures"] = icons;
        payload["placeholderIconPath"] = (entry ?? DefaultEntry).PlaceholderIconPath;

        // Style variants ---------------------------------------------------------
        payload["styleVariants"] = ReadStyleVariants(asset);

        return ToolResults.Structured(payload);
    }

    // ------------------------------------------------------------------ search_files

    [McpServerTool(Name = "search_files", ReadOnly = true, Title = "Search raw files")]
    [Description("""
                 Searches the mounted virtual file system by path - meshes, sounds, maps, textures and
                 animations that have no item definition and therefore never show up in search_assets.
                 Skips /_Verse/ scaffolding.
                 """)]
    public static async Task<CallToolResult> SearchFilesAsync(
        HeadlessLoader loader,
        FileIndex index,
        [Description("Substring to look for in the file path, e.g. \"Foliage\".")] string query,
        [Description("Optional filter: mesh | sound | texture | map | animation.")] string? fileType = null,
        [Description("Maximum rows to return. Capped at 200.")] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (!await loader.TryWaitReadyAsync(cancellationToken)) return ToolResults.StillLoading(loader);

        limit = Math.Clamp(limit, 1, FileLimitCap);

        FileTypeFilter? filter = null;
        if (!string.IsNullOrWhiteSpace(fileType))
        {
            filter = FileTypeFilter.Resolve(fileType);
        }

        var items = new JsonArray();
        var total = 0;

        foreach (var (path, file) in loader.Provider.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var extension = path.SubstringAfterLast('.').ToLowerInvariant();
            if (extension is not ("uasset" or "umap" or "ufont")) continue;
            if (path.Contains("/_Verse/", StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrWhiteSpace(query) && !path.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;

            var assetClass = index.ClassFor(path);
            if (filter is not null && !filter.Matches(path, extension, assetClass)) continue;

            total++;
            if (items.Count >= limit) continue;

            items.Add(new JsonObject
            {
                ["path"] = path,
                ["name"] = path.SubstringAfterLast('/').SubstringBeforeLast('.'),
                ["extension"] = extension,
                ["assetClass"] = assetClass,
                ["sizeBytes"] = file.Size,
                ["container"] = file is VfsEntry vfs ? vfs.Vfs.Name : null
            });
        }

        return ToolResults.Structured(new JsonObject
        {
            ["status"] = "ok",
            ["query"] = query,
            ["fileType"] = filter?.Name,
            ["total"] = total,
            ["limit"] = limit,
            ["returned"] = items.Count,
            ["note"] = "assetClass comes from the asset registry when the file name is known there; otherwise the filter falls back to filename-prefix conventions (SM_/SK_, T_, S_/SW_, A_/AS_).",
            ["items"] = items
        });
    }

    // ------------------------------------------------------------------ shared helpers

    internal static readonly AssetCategoryEntry DefaultEntry = new() { Type = EExportType.None };

    internal sealed class LoadedPage : List<(FPartialAssetData Data, UObject? Asset, string DisplayName)>
    {
        public AssetEnumerationState State { get; } = new();
    }

    /// <summary>
    /// Loads a page of assets and primes the style dictionary. A FRESH AssetEnumerationState per
    /// call is mandatory: reusing one silently dedupes everything away on the second request.
    /// </summary>
    internal static async Task<LoadedPage> LoadPageAsync(
        HeadlessLoader loader, AssetCategoryEntry entry, List<FPartialAssetData> rows, CancellationToken cancellationToken)
    {
        var page = new LoadedPage();
        var slots = new (FPartialAssetData Data, UObject? Asset, string DisplayName)[rows.Count];

        await Parallel.ForEachAsync(
            Enumerable.Range(0, rows.Count),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2), CancellationToken = cancellationToken },
            async (i, ct) =>
            {
                var data = rows[i];
                UObject? asset = null;
                try
                {
                    asset = await loader.Provider.SafeLoadPackageObjectAsync(data.ObjectPath);
                }
                catch (Exception e)
                {
                    Log.Debug("browse: failed to load {Path}: {Message}", data.ObjectPath, e.Message);
                }

                var displayName = asset is null ? data.AssetName.Text : SafeDisplayName(entry, asset);
                slots[i] = (data, asset, displayName);
                await Task.CompletedTask;
            });

        // Style collection is a second, ordered pass so styleCount is final before items are emitted.
        foreach (var slot in slots)
        {
            if (slot.Asset is null) continue;
            try { entry.AddStyleHandler(page.State, slot.Asset, slot.DisplayName); }
            catch (Exception e) { Log.Debug("browse: style handler threw: {Message}", e.Message); }
        }

        page.AddRange(slots);
        return page;
    }

    internal static string SafeDisplayName(AssetCategoryEntry? entry, UObject asset)
    {
        try
        {
            var name = (entry ?? DefaultEntry).DisplayNameHandler(asset);
            return string.IsNullOrWhiteSpace(name) ? asset.Name : name;
        }
        catch
        {
            return asset.Name;
        }
    }

    private static string SafeDescription(AssetCategoryEntry entry, UObject asset)
    {
        try { return entry.DescriptionHandler(asset) ?? "No Description."; }
        catch { return "No Description."; }
    }

    private static string ReadDefaultDescription(UObject asset)
    {
        try { return DefaultEntry.DescriptionHandler(asset) ?? "No Description."; }
        catch { return "No Description."; }
    }

    private static bool SafeHide(AssetCategoryEntry entry, AssetEnumerationState state, UObject asset, string displayName)
    {
        try { return entry.HidePredicate(state, asset, displayName); }
        catch { return false; }
    }

    private static IEnumerable<string> ReadTags(AssetCategoryEntry entry, UObject asset)
    {
        try
        {
            var container = entry.GameplayTagHandler(asset);
            return container?.GameplayTags?.Select(tag => tag.TagName.Text).ToArray() ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static IEnumerable<string> ReadDefaultTags(UObject asset)
    {
        try
        {
            var container = CategoryCatalog.GetGameplayTags(asset);
            return container?.GameplayTags?.Select(tag => tag.TagName.Text).ToArray() ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static EExportType SafeExportType(UObject asset)
    {
        try { return CategoryCatalog.DetermineExportType(asset); }
        catch { return EExportType.None; }
    }

    private static string? SafeRarity(UObject asset)
    {
        try
        {
            if (asset.GetDataListItem<FName?>("Rarity") is { } dataListName &&
                Enum.TryParse<EFortRarity>(dataListName.Text.SubstringAfter("::"), out var dataListRarity))
                return dataListRarity.ToString();
        }
        catch { /* optional */ }

        try
        {
            if (asset.Properties.Any(property => property.Name.Text.Equals("Rarity", StringComparison.Ordinal)))
                return asset.GetOrDefault("Rarity", EFortRarity.Uncommon).ToString();
        }
        catch { /* optional */ }

        return null;
    }

    private static JsonNode? SafeSeries(UObject asset)
    {
        try
        {
            UFortItemSeriesDefinition? series = null;

            if (asset.GetDataListItem<FPackageIndex>("Series") is { } seriesPackage)
                series = seriesPackage.Load<UFortItemSeriesDefinition>();

            // Not every definition routes its series through a data list.
            series ??= asset.GetOrDefault<UFortItemSeriesDefinition?>("Series");
            if (series is null) return null;

            return new JsonObject
            {
                ["name"] = series.Name,
                ["displayName"] = series.DisplayName?.Text
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? SafeSet(HeadlessLoader loader, AssetCategoryEntry? entry, UObject asset)
    {
        try
        {
            var tags = (entry ?? DefaultEntry).GameplayTagHandler(asset);
            if (tags.GetValueOrDefault("Cosmetics.Set")?.Text is not { } setTag) return null;
            return loader.SetNames.GetValueOrDefault(setTag) ?? setTag;
        }
        catch
        {
            return null;
        }
    }

    private static UTexture2D? TryIcon(Func<UTexture2D?> handler)
    {
        try { return handler(); }
        catch { return null; }
    }

    private static void AddIcon(JsonArray target, string role, UTexture2D? texture)
    {
        if (texture is null) return;

        string path;
        try { path = texture.GetPathName(); }
        catch { path = texture.Name; }

        target.Add(new JsonObject
        {
            ["role"] = role,
            ["name"] = texture.Name,
            ["path"] = path
        });
    }

    private static JsonArray ReadStyleVariants(UObject asset)
    {
        var variants = new JsonArray();
        try
        {
            foreach (var variant in asset.GetOrDefault("ItemVariants", Array.Empty<UObject>()))
            {
                if (variant is null) continue;

                var optionsName = variant.ExportType switch
                {
                    "FortCosmeticCharacterPartVariant" => "PartOptions",
                    "FortCosmeticMaterialVariant" => "MaterialOptions",
                    "FortCosmeticParticleVariant" => "ParticleOptions",
                    "FortCosmeticMeshVariant" => "MeshOptions",
                    "FortCosmeticGameplayTagVariant" => "GenericTagOptions",
                    "FortCosmeticMorphTargetVariant" => "MorphTargetOptions",
                    "FortCosmeticLoadoutTagDrivenVariant" => "Variants",
                    _ => null
                };

                var optionCount = 0;
                if (optionsName is not null)
                {
                    try { optionCount = variant.GetOrDefault(optionsName, Array.Empty<global::CUE4Parse.UE4.Assets.Objects.FStructFallback>()).Length; }
                    catch { optionCount = 0; }
                }

                variants.Add(new JsonObject
                {
                    ["channel"] = variant.GetOrDefault("VariantChannelName", new global::CUE4Parse.UE4.Objects.Core.i18N.FText("Style")).Text,
                    ["variantType"] = variant.ExportType,
                    ["optionCount"] = optionCount
                });
            }
        }
        catch
        {
            // Assets without variants are the norm.
        }

        return variants;
    }
}

/// <summary>Maps the friendly fileType values onto asset class names plus filename conventions.</summary>
public sealed record FileTypeFilter(string Name, string[] Classes, string[] Prefixes, string[] Extensions)
{
    private static readonly FileTypeFilter[] All =
    [
        new("mesh", ["StaticMesh", "SkeletalMesh"], ["SM_", "SK_"], []),
        new("sound", ["SoundWave", "SoundCue"], ["S_", "SW_", "SC_"], []),
        new("texture", ["Texture2D", "TextureCube", "TextureRenderTarget2D"], ["T_", "TX_"], []),
        new("map", ["World", "Level"], [], ["umap"]),
        new("animation", ["AnimSequence", "AnimMontage", "AnimComposite"], ["A_", "AS_", "AM_"], [])
    ];

    public static FileTypeFilter Resolve(string value)
        => All.FirstOrDefault(filter => filter.Name.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase))
           ?? throw new McpException($"Unknown fileType \"{value}\". Use one of: {string.Join(", ", All.Select(x => x.Name))}");

    public bool Matches(string path, string extension, string? assetClass)
    {
        if (Extensions.Contains(extension)) return true;

        if (assetClass is not null)
            return Classes.Any(name => assetClass.Contains(name, StringComparison.OrdinalIgnoreCase));

        var fileName = path.SubstringAfterLast('/');
        return Prefixes.Any(prefix => fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Lazily-built file-name -> asset-class index derived from the asset registry.</summary>
public sealed class FileIndex(HeadlessLoader loader)
{
    private readonly Lazy<Dictionary<string, string>> _byAssetName = new(() =>
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var data in loader.AssetRegistry)
            map.TryAdd(data.AssetName.Text, data.AssetClass.Text);

        return map;
    });

    public string? ClassFor(string path)
    {
        var name = path.SubstringAfterLast('/').SubstringBeforeLast('.');
        return _byAssetName.Value.GetValueOrDefault(name);
    }
}
