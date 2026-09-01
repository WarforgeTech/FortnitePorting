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
                 Lists every asset category the server can browse, with how many BROWSABLE assets each
                 one holds - the same deduped count browse_category and make_contact_sheet page over,
                 not the raw registry row count. The `exportType` values here are exactly what
                 search_assets, browse_category and make_contact_sheet accept as their `category`
                 argument.
                 """)]
    public static async Task<CallToolResult> ListCategoriesAsync(
        HeadlessLoader loader, AssetQuery assets, DisplayNameIndex names, CancellationToken cancellationToken)
    {
        if (!await loader.TryWaitReadyAsync(cancellationToken)) return ToolResults.StillLoading(loader);

        // Dedupe is by display name, so give the index its usual short grace; categories that are
        // not ready report an undeduped count and say so rather than blocking the call.
        await names.WhenAllReadyAsync(cancellationToken: cancellationToken);

        var groups = new JsonArray();
        var provisional = new List<string>();

        foreach (var group in CategoryCatalog.Entries.GroupBy(entry => entry.Category))
        {
            var types = new JsonArray();
            var exportTypes = new JsonArray();
            var groupCount = 0;

            foreach (var entry in group)
            {
                var canonical = assets.CanonicalNow(entry, names);
                groupCount += canonical.Count;
                exportTypes.Add(entry.Type.ToString());

                if (!canonical.NameIndexReady && entry.DedupeDisplayNames) provisional.Add(entry.Type.ToString());

                var type = new JsonObject
                {
                    ["exportType"] = entry.Type.ToString(),
                    ["assetCount"] = canonical.Count,
                    ["registryRows"] = canonical.RegistryRows,
                    ["classNames"] = ToolResults.ToJsonArray(entry.ClassNames),
                    // Wildlife and WeaponMod have no item definitions at all: their assets are
                    // hand-authored mesh paths in the catalog, so they are backed but not by classes.
                    ["registryBacked"] = entry.ClassNames.Length > 0,
                    ["manualAssets"] = canonical.ManualRows,
                    ["deduped"] = entry.DedupeDisplayNames,
                    ["collapsedDuplicates"] = canonical.CollapsedRows
                };

                types.Add(type);
            }

            groups.Add(new JsonObject
            {
                ["category"] = group.Key.ToString(),
                ["exportTypes"] = exportTypes,
                ["assetCount"] = groupCount,
                ["types"] = types
            });
        }

        var payload = new JsonObject
        {
            ["status"] = "ok",
            ["totalRegistryEntries"] = loader.AssetRegistry.Count,
            ["categories"] = groups,
            ["usage"] = "Pass any exportType value (e.g. \"Prop\", \"Outfit\") as the `category` argument of search_assets, browse_category or make_contact_sheet.",
            ["note"] = "assetCount is the browsable count: registry rows after the category's name filters, minus rows folded onto "
                       + "an identical display name (collapsedDuplicates), plus manualAssets - hand-authored mesh paths for categories "
                       + "like Wildlife and WeaponMod that have no item definitions."
        };

        if (provisional.Count > 0)
            payload["note"] = payload["note"]!.GetValue<string>() +
                              $" Counts for {string.Join(", ", provisional)} are provisional: their display-name index is still building, so duplicates are not folded yet.";

        return ToolResults.Structured(payload);
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
        AssetCategoryEntry? scopedEntry = null;

        if (!string.IsNullOrWhiteSpace(category))
        {
            var entry = AssetQuery.ResolveCategory(category);
            scopedEntry = entry;
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

        // Rows the canonical list folded onto an earlier row (rarity/tier clones): still searchable,
        // but flagged so a client knows which hit is the one browse and the sheets actually show.
        var canonicalPaths = await CanonicalPathSetAsync(assets, names, scopedEntry, cancellationToken);

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

        // Hand-authored rows (Wildlife's creatures, WeaponMod's meshes) have no registry row at all,
        // so they can only be matched here. Without this the categories look empty to every client.
        var manualMatches = MatchManualAssets(assets, scopedEntry, predicate);

        var items = new JsonArray();
        var nonCanonical = 0;

        foreach (var manual in manualMatches.Skip(offset).Take(limit))
        {
            items.Add(new JsonObject
            {
                ["name"] = manual.AssetName,
                ["displayName"] = manual.DisplayName,
                ["objectPath"] = manual.ObjectPath,
                ["packagePath"] = manual.PackagePath,
                ["assetClass"] = manual.AssetClass,
                ["category"] = manual.AssetClass,
                ["matchedOn"] = "displayName",
                ["canonical"] = true,
                ["source"] = "manual"
            });
        }

        var registryOffset = Math.Max(0, offset - manualMatches.Count);
        var registryLimit = Math.Max(0, limit - items.Count);

        foreach (var (data, displayName, matchedOn) in matched.Skip(registryOffset).Take(registryLimit))
        {
            var canonical = canonicalPaths is null || canonicalPaths.Contains(data.ObjectPath);
            if (!canonical) nonCanonical++;

            items.Add(new JsonObject
            {
                ["name"] = data.AssetName.Text,
                ["displayName"] = displayName,
                ["objectPath"] = data.ObjectPath,
                ["packagePath"] = data.PackageName.Text,
                ["assetClass"] = data.AssetClass.Text,
                ["category"] = assets.CategoryForClass(data.AssetClass.Text),
                ["matchedOn"] = matchedOn,
                // False = another row with the same display name represents this one in
                // browse_category / make_contact_sheet. The row is still exportable.
                ["canonical"] = canonical,
                ["source"] = "registry"
            });
        }

        var total = matched.Count + manualMatches.Count;

        var payload = new JsonObject
        {
            ["status"] = "ok",
            ["query"] = query,
            ["category"] = resolvedCategory,
            ["match"] = match,
            ["total"] = total,
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
            notes.Add($"Display-name coverage is {names.Coverage}: {names.AvailableCategoryCount}/{names.TotalCategoryCount} categories indexed or disk-cached. " +
                      "Categories still building matched asset/package names only; pass `category` to wait for one specific category.");
        }

        if (nonCanonical > 0)
            notes.Add($"{nonCanonical} of the returned rows have canonical:false - they are rarity/tier clones sharing a display name " +
                      "with another row, which is the one browse_category and make_contact_sheet show. They export fine either way.");

        if (total == 0)
        {
            notes.Add($"No asset matched \"{query}\". Try a shorter or more generic term (\"hedge\" rather than \"hedge wall large\"), " +
                      "a synonym (\"foliage\", \"bush\", \"plant\"), check the spelling, drop the `category` filter, " +
                      "or use match:\"regex\" for alternatives like \"hedge|bush|shrub\". " +
                      "browse_category plus make_contact_sheet is the reliable way to browse a category visually when you do not know the vocabulary.");
        }

        if (notes.Count > 0) payload["note"] = string.Join(" ", notes);

        return ToolResults.Structured(payload);
    }

    /// <summary>
    /// Object paths the canonical list actually shows for the scoped category, so a search hit can
    /// say whether it is the row browse/sheet will display. Null (= "assume canonical") when the
    /// search is unscoped or the category does not dedupe, which keeps the cost off the hot path.
    /// </summary>
    private static async Task<HashSet<string>?> CanonicalPathSetAsync(
        AssetQuery assets, DisplayNameIndex names, AssetCategoryEntry? entry, CancellationToken cancellationToken)
    {
        if (entry is not { DedupeDisplayNames: true }) return null;

        var canonical = await assets.CanonicalAsync(entry, names, cancellationToken);
        if (!canonical.NameIndexReady) return null;

        return canonical.Items.Select(item => item.ObjectPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Matches the catalog's hand-authored rows, which never appear in the asset registry. Scoped to
    /// one category when the caller passed one, otherwise across every category that has them.
    /// </summary>
    private static List<CategoryItem> MatchManualAssets(
        AssetQuery assets, AssetCategoryEntry? scoped, Func<string, bool> predicate)
    {
        var entries = scoped is null ? CategoryCatalog.Entries : [scoped];
        var results = new List<CategoryItem>();

        foreach (var entry in entries)
        {
            if (entry.ManuallyDefinedAssets.Length == 0 && entry.ManuallyDefinedAssetsFactory is null) continue;

            foreach (var manual in assets.ManualAssets(entry))
            {
                if (!predicate(manual.Name) && !predicate(manual.AssetPath)) continue;

                results.Add(new CategoryItem
                {
                    ObjectPath = manual.AssetPath,
                    AssetName = manual.AssetPath.SubstringAfterLast('/'),
                    PackagePath = manual.AssetPath,
                    AssetClass = entry.Type.ToString(),
                    DisplayName = manual.Name,
                    DisplayNameSource = "manual",
                    IsManual = true,
                    IconPath = manual.IconPath,
                    Description = manual.Description
                });
            }
        }

        return results;
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
            ["cachedCategories"] = names.CachedCategoryCount,
            ["totalCategories"] = names.TotalCategoryCount,
            ["displayNames"] = names.TotalNames
        };
    }

    // ------------------------------------------------------------------ browse_category

    [McpServerTool(Name = "browse_category", ReadOnly = true, Title = "Browse a category")]
    [Description("""
                 Pages through one category, opening each asset to read its description and gameplay
                 tags. Pages the SAME canonical list make_contact_sheet does, so with the same page
                 and pageSize, row n here is cell n there. A page always returns pageSize rows (except
                 the last), and total/totalPages count what you can actually reach. Duplicate rows
                 sharing a display name are already folded away in categories that need it -
                 collapsedDuplicates says how many were folded onto each row (that is NOT a count of
                 style variants; use list_asset_styles for those). Slower than search_assets because
                 it loads packages - keep pageSize modest.
                 """)]
    public static async Task<CallToolResult> BrowseCategoryAsync(
        HeadlessLoader loader,
        AssetQuery assets,
        DisplayNameIndex names,
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

        var canonical = await assets.CanonicalAsync(entry, names, cancellationToken);
        var rows = CategoryPage.Filter(canonical.Items, nameFilter);
        var pageRows = rows.Skip(page * pageSize).Take(pageSize).ToList();

        var loaded = await LoadPageAsync(loader, pageRows, cancellationToken);

        var items = new JsonArray();
        for (var i = 0; i < pageRows.Count; i++)
        {
            var item = pageRows[i];
            var asset = loaded[i];

            var json = new JsonObject
            {
                ["index"] = page * pageSize + i,
                ["displayName"] = item.DisplayName,
                ["displayNameSource"] = item.DisplayNameSource,
                ["name"] = item.AssetName,
                ["objectPath"] = item.ObjectPath,
                ["description"] = asset is not null ? SafeDescription(entry, asset) : item.Description ?? "No Description.",
                ["tags"] = ToolResults.ToJsonArray(asset is not null ? ReadTags(entry, asset) : []),
                // Rows folded onto this one because they carry an identical display name (rarity /
                // tier clones). NOT style variants - list_asset_styles reports those.
                ["collapsedDuplicates"] = item.CollapsedDuplicates,
                ["hidden"] = item.Hidden
            };

            if (item.IsManual) json["source"] = "manual";

            if (asset is null)
            {
                // For a hand-authored row this means the content is not in THIS build of Fortnite
                // (Zombie Chicken and Klombo are both gone from 42.00, mesh and icon alike). Saying
                // so beats rendering a magenta placeholder and leaving the caller to guess.
                json["available"] = false;
                json[item.IsManual ? "note" : "loadFailed"] = item.IsManual
                    ? "This catalog entry's asset is not present in the installed Fortnite build; nothing to export."
                    : (JsonNode) true;
            }

            items.Add(json);
        }

        var payload = new JsonObject
        {
            ["status"] = "ok",
            ["category"] = entry.Type.ToString(),
            ["page"] = page,
            ["pageSize"] = pageSize,
            ["total"] = rows.Count,
            ["totalPages"] = (int) Math.Ceiling(rows.Count / (double) pageSize),
            ["returned"] = items.Count,
            ["registryRows"] = canonical.RegistryRows,
            ["collapsedRows"] = canonical.CollapsedRows,
            ["manualAssets"] = canonical.ManualRows,
            ["note"] = "make_contact_sheet with the same category, page and pageSize renders these rows in this order, "
                       + "so cell n is row n. collapsedDuplicates counts identically-named rows folded onto this one, not style variants.",
            ["items"] = items
        };

        if (!canonical.NameIndexReady && entry.DedupeDisplayNames)
            payload["note"] = payload["note"]!.GetValue<string>() +
                              $" The {entry.Type} display-name index is still building, so duplicates are not folded yet and this page may change once it finishes.";

        return ToolResults.Structured(payload);
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

        // Wildlife creatures and WeaponMod meshes are raw meshes with no item definition, so the
        // class lookup finds nothing; the catalog is the only source of their name, category and icon.
        var manual = assets.ManualFor(objectPath);
        var (handlerName, nameSource) = SafeDisplayNameWithSource(entry, asset);
        var displayName = manual?.DisplayName ?? handlerName;
        if (manual is not null) nameSource = "manual";

        var payload = new JsonObject
        {
            ["status"] = "ok",
            ["objectPath"] = objectPath,
            ["name"] = asset.Name,
            ["displayName"] = displayName,
            // displayName | assetName (prettified fallback, no localised name exists) | manual.
            ["displayNameSource"] = nameSource,
            ["description"] = manual?.Description
                              ?? (entry is not null ? SafeDescription(entry, asset) : ReadDefaultDescription(asset)),
            ["assetClass"] = asset.ExportType,
            ["category"] = entry?.Type.ToString() ?? manual?.AssetClass,
            ["assetCategory"] = entry?.Category.ToString()
                                ?? (manual is not null && Enum.TryParse<EExportType>(manual.AssetClass, out var manualType)
                                    ? CategoryCatalog.ForType(manualType)?.Category.ToString()
                                    : null),
            ["exportType"] = SafeExportType(asset).ToString(),
            ["tags"] = ToolResults.ToJsonArray(entry is not null ? ReadTags(entry, asset) : ReadDefaultTags(asset))
        };

        if (manual is not null)
        {
            payload["source"] = "manual";
            payload["catalogIconPath"] = manual.IconPath;
        }

        // Rarity / series / set --------------------------------------------------
        if (entry is null || !entry.HideRarity)
        {
            if (SafeRarity(asset) is { } rarity)
            {
                // EFortRarity's tokens are internal aliases (Quality == Epic, Fine == Legendary,
                // Sturdy == Rare, ...). Report the player-facing name and keep the raw token beside it.
                payload["rarity"] = rarity.GetNameText().Text;
                payload["rarityRaw"] = rarity.ToString();
            }
        }

        payload["series"] = SafeSeries(asset);
        payload["set"] = SafeSet(loader, entry, asset);

        var season = SafeIntroducedSeason(entry, asset);
        if (season is not null) payload["introducedSeason"] = season;

        // Icon textures ----------------------------------------------------------
        var icons = new JsonArray();
        AddIcon(icons, "lowRes", TryIcon(() => (entry ?? DefaultEntry).LowResIconHandler(asset)));
        AddIcon(icons, "highRes", TryIcon(() => (entry ?? DefaultEntry).HighResIconHandler(asset)));
        payload["iconTextures"] = icons;
        payload["placeholderIconPath"] = (entry ?? DefaultEntry).PlaceholderIconPath;

        // Style variants ---------------------------------------------------------
        payload["styleVariants"] = ReadStyleVariants(asset);

        // Character parts (outfits, backpacks, pets, companions) -----------------
        AddCharacterParts(payload, asset);

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

    /// <summary>
    /// Loads one page of assets in parallel, index-for-index with the canonical rows handed in. A
    /// null slot means the package would not open; the row is still reported, flagged loadFailed,
    /// so paging never silently shortens.
    /// </summary>
    internal static async Task<UObject?[]> LoadPageAsync(
        HeadlessLoader loader, List<CategoryItem> rows, CancellationToken cancellationToken)
    {
        var slots = new UObject?[rows.Count];

        await Parallel.ForEachAsync(
            Enumerable.Range(0, rows.Count),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2), CancellationToken = cancellationToken },
            async (i, ct) =>
            {
                try
                {
                    slots[i] = await loader.Provider.SafeLoadPackageObjectAsync(rows[i].ObjectPath);
                }
                catch (Exception e)
                {
                    Log.Debug("browse: failed to load {Path}: {Message}", rows[i].ObjectPath, e.Message);
                }
            });

        return slots;
    }

    /// <summary>
    /// The label for a loaded asset. Falls back to a prettified asset name rather than the raw
    /// internal one, so a dev row with no localised name reads "Guitar Figure", not "SID_Guitar_Figure".
    /// </summary>
    internal static string SafeDisplayName(AssetCategoryEntry? entry, UObject asset)
        => SafeDisplayNameWithSource(entry, asset).Name;

    internal static (string Name, string Source) SafeDisplayNameWithSource(AssetCategoryEntry? entry, UObject asset)
    {
        try
        {
            var name = (entry ?? DefaultEntry).DisplayNameHandler(asset);
            if (!string.IsNullOrWhiteSpace(name)) return (name, "displayName");
        }
        catch
        {
            // Handlers walk soft references and can throw on partially-cooked assets.
        }

        return (CategoryCatalog.PrettifyAssetName(asset.Name), "assetName");
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

    private static EFortRarity? SafeRarity(UObject asset)
    {
        try
        {
            if (asset.GetDataListItem<FName?>("Rarity") is { } dataListName &&
                Enum.TryParse<EFortRarity>(dataListName.Text.SubstringAfter("::"), out var dataListRarity))
                return dataListRarity;
        }
        catch { /* optional */ }

        try
        {
            if (asset.Properties.Any(property => property.Name.Text.Equals("Rarity", StringComparison.Ordinal)))
                return asset.GetOrDefault("Rarity", EFortRarity.Uncommon);
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

    /// <summary>
    /// Backed by the same <see cref="StyleResolver"/> the exporter uses, so the channel and option
    /// names reported here are exactly what export_assets accepts in `styles`.
    /// </summary>
    private static JsonArray ReadStyleVariants(UObject asset)
    {
        var variants = new JsonArray();
        try
        {
            foreach (var channel in StyleResolver.ReadChannels(asset))
            {
                variants.Add(new JsonObject
                {
                    ["channel"] = channel.Channel,
                    ["variantType"] = channel.VariantType,
                    ["optionCount"] = channel.Options.Count,
                    ["options"] = ToolResults.ToJsonArray(channel.Options.Select(option => option.Name))
                });
            }
        }
        catch
        {
            // Assets without variants are the norm.
        }

        return variants;
    }

    /// <summary>
    /// Character parts an outfit/backpack is built from, resolved the way the exporter resolves them
    /// (BaseCharacterParts, else the HeroDefinition specialization fallback). Surfaces body type /
    /// gender and flags any part that carries no mesh - the one case where an export comes out short.
    /// </summary>
    private static void AddCharacterParts(JsonObject payload, UObject asset)
    {
        CharacterPartSet set;
        try { set = CharacterPartInspector.Read(asset); }
        catch (Exception e)
        {
            Log.Debug("Character-part read failed for {Name}: {Message}", asset.Name, e.Message);
            return;
        }

        if (set.Parts.Count == 0 && !set.HasHeroDefinition) return;

        var parts = new JsonArray();
        foreach (var part in set.Parts)
        {
            parts.Add(new JsonObject
            {
                ["name"] = part.Name,
                ["partType"] = part.PartType,
                ["gender"] = part.Gender,
                ["objectPath"] = part.ObjectPath,
                ["skeletalMesh"] = part.SkeletalMesh,
                ["additionalData"] = part.AdditionalData
            });
        }

        payload["characterParts"] = new JsonObject
        {
            ["source"] = set.Source,
            ["hasHeroDefinition"] = set.HasHeroDefinition,
            ["partCount"] = set.Parts.Count,
            ["partTypes"] = ToolResults.ToJsonArray(set.PartTypes),
            ["bodyType"] = set.BodyGender,
            ["partsWithoutMesh"] = set.MeshlessParts.Count(),
            ["parts"] = parts
        };
    }

    /// <summary>
    /// Season a cosmetic was introduced in. Fortnite carries this as the gameplay tag
    /// <c>Cosmetics.Filter.Season.N</c>; a few definitions also expose a plain <c>Season</c> int.
    /// </summary>
    private static JsonNode? SafeIntroducedSeason(AssetCategoryEntry? entry, UObject asset)
    {
        try
        {
            var tags = (entry ?? DefaultEntry).GameplayTagHandler(asset);
            var seasonTag = tags?.GameplayTags?
                .Select(tag => tag.TagName.Text)
                .FirstOrDefault(text => text.StartsWith("Cosmetics.Filter.Season.", StringComparison.OrdinalIgnoreCase));

            if (seasonTag is not null)
            {
                var value = seasonTag.SubstringAfterLast('.');
                return new JsonObject
                {
                    ["season"] = int.TryParse(value, out var number) ? number : null,
                    ["raw"] = value,
                    ["source"] = seasonTag
                };
            }
        }
        catch { /* optional */ }

        try
        {
            if (asset.Properties.Any(property => property.Name.Text.Equals("Season", StringComparison.Ordinal)))
                return new JsonObject
                {
                    ["season"] = asset.GetOrDefault("Season", 0),
                    ["source"] = "Season property"
                };
        }
        catch { /* optional */ }

        return null;
    }
}

/// <summary>
/// The single place a category's canonical list is narrowed and sliced. browse_category and
/// make_contact_sheet both go through it, which is what keeps "browse row n == sheet cell n" true
/// for the same category/page/pageSize.
/// </summary>
public static class CategoryPage
{
    /// <summary>Applies an optional case-insensitive substring filter over name, path and label.</summary>
    public static List<CategoryItem> Filter(IReadOnlyList<CategoryItem> items, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return items as List<CategoryItem> ?? items.ToList();

        return items
            .Where(item =>
                item.AssetName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.PackagePath.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
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
