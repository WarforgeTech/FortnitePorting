using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using CUE4Parse.UE4.AssetRegistry.Objects;
using FortnitePorting;
using ModelContextProtocol;
using Serilog;

namespace FortnitePorting.Mcp.Core;

/// <summary>
/// One browsable row of a category, whether it came from the asset registry or from the catalog's
/// hand-authored <see cref="ManuallyDefinedAsset"/> list. This is the unit every discovery tool
/// pages over, so browse_category row <c>n</c> and make_contact_sheet cell <c>n</c> are the same
/// asset by construction.
/// </summary>
public sealed record CategoryItem
{
    public required string ObjectPath { get; init; }
    public required string AssetName { get; init; }
    public required string PackagePath { get; init; }
    public required string AssetClass { get; init; }

    /// <summary>The label to show. Never null, never empty.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Where <see cref="DisplayName"/> came from: displayName | assetName | manual.</summary>
    public required string DisplayNameSource { get; init; }

    /// <summary>True for a row synthesised from the catalog rather than the asset registry.</summary>
    public bool IsManual { get; init; }

    /// <summary>Manual rows only: the icon texture the catalog pins to this asset.</summary>
    public string? IconPath { get; init; }

    /// <summary>Manual rows only: the catalog's description.</summary>
    public string? Description { get; init; }

    /// <summary>The row matches one of the category's HideNames but the category loads them anyway.</summary>
    public bool Hidden { get; init; }

    /// <summary>
    /// How many further registry rows share this row's display name and were folded onto it
    /// (rarity/tier clones). 0 means this row is unique - it is NOT a count of style variants.
    /// </summary>
    public int CollapsedDuplicates { get; init; }

    /// <summary>Object paths of the rows counted by <see cref="CollapsedDuplicates"/>.</summary>
    public IReadOnlyList<string> CollapsedPaths { get; init; } = [];
}

/// <summary>The canonical, ordered, deduped row list for one category plus how it was built.</summary>
public sealed record CanonicalList
{
    public required EExportType Type { get; init; }
    public required IReadOnlyList<CategoryItem> Items { get; init; }

    /// <summary>Registry rows before dedupe (so a client can see how much was folded away).</summary>
    public required int RegistryRows { get; init; }

    /// <summary>Rows contributed by the catalog's ManuallyDefinedAssets.</summary>
    public required int ManualRows { get; init; }

    /// <summary>False when the display-name index was not ready, so dedupe/labels are provisional.</summary>
    public required bool NameIndexReady { get; init; }

    public int Count => Items.Count;
    public int CollapsedRows => Items.Sum(item => item.CollapsedDuplicates);
}

/// <summary>
/// Registry-only querying: the fast path that never touches a .uasset. All the category
/// name filters from AssetLoader.Load are reproduced here so counts line up with the GUI.
/// </summary>
public sealed class AssetQuery(HeadlessLoader loader)
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<EExportType, List<FPartialAssetData>> _filteredCache = new();
    private readonly ConcurrentDictionary<EExportType, CanonicalList> _canonicalCache = new();
    private readonly ConcurrentDictionary<EExportType, ManuallyDefinedAsset[]> _manualCache = new();
    private readonly Lazy<Dictionary<string, string>> _classToCategory = new(() =>
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in CategoryCatalog.Entries)
        foreach (var className in entry.ClassNames)
            map.TryAdd(className, entry.Type.ToString());

        return map;
    });

    private Lazy<Dictionary<string, CategoryItem>>? _manualByPath;
    private readonly Lock _manualByPathGate = new();

    public HeadlessLoader Loader { get; } = loader;

    public string? CategoryForClass(string className)
        => _classToCategory.Value.GetValueOrDefault(className);

    /// <summary>Accepts an EExportType name ("Prop", "Outfit"), tolerating a trailing plural "s".</summary>
    public static AssetCategoryEntry ResolveCategory(string category)
    {
        var trimmed = category.Trim();
        foreach (var candidate in new[] { trimmed, trimmed.TrimEnd('s', 'S') })
        {
            if (Enum.TryParse<EExportType>(candidate, ignoreCase: true, out var type) &&
                CategoryCatalog.ForType(type) is { } entry)
                return entry;
        }

        var known = string.Join(", ", CategoryCatalog.Entries.Select(x => x.Type.ToString()));
        throw new McpException($"Unknown category \"{category}\". Call list_categories; valid values are: {known}");
    }

    /// <summary>
    /// The registry rows for a category after the catalog's Allow/Hide/Disallow filters.
    /// Cached per category: this is a full pass over ~570k rows.
    /// <para>
    /// This is the RAW layer - no dedupe, no manual assets. It backs <see cref="DisplayNameIndex"/>
    /// (which cannot depend on itself) and the name-index validity stamp. Discovery tools should use
    /// <see cref="CanonicalAsync"/> instead.
    /// </para>
    /// </summary>
    public List<FPartialAssetData> Filtered(AssetCategoryEntry entry)
        => _filteredCache.GetOrAdd(entry.Type, _ => BuildFiltered(entry));

    private List<FPartialAssetData> BuildFiltered(AssetCategoryEntry entry)
    {
        if (entry.ClassNames.Length == 0) return [];

        var classNames = new HashSet<string>(entry.ClassNames, StringComparer.Ordinal);

        var result = new List<FPartialAssetData>();
        foreach (var data in Loader.AssetRegistry)
        {
            if (!classNames.Contains(data.AssetClass.Text)) continue;

            var assetName = data.AssetName.Text;
            if (assetName.EndsWith("Random", StringComparison.OrdinalIgnoreCase)) continue;

            var packageName = data.PackageName.Text;
            if (entry.DisallowedNames.Any(name => packageName.Contains(name, StringComparison.OrdinalIgnoreCase))) continue;

            if (entry.AllowNames.Length > 0 &&
                !entry.AllowNames.Any(name => packageName.Contains(name, StringComparison.OrdinalIgnoreCase))) continue;

            if (!entry.LoadHiddenAssets)
            {
                if (entry.IsHiddenName(packageName)) continue;
                if (packageName.Contains("Placeholder", StringComparison.OrdinalIgnoreCase)) continue;
            }

            result.Add(data);
        }

        return result;
    }

    // ---------------------------------------------------------------- manually defined assets

    /// <summary>
    /// The catalog's hand-authored rows for a category: Wildlife's 13 creatures (which have no item
    /// definition at all, only a mesh path) and WeaponMod's meshes, which are discovered by walking
    /// the WeaponModOverrideData data table. Materialised once per process; a failure degrades to an
    /// empty list rather than taking the category down.
    /// </summary>
    public ManuallyDefinedAsset[] ManualAssets(AssetCategoryEntry entry)
        => _manualCache.GetOrAdd(entry.Type, _ => BuildManualAssets(entry));

    private ManuallyDefinedAsset[] BuildManualAssets(AssetCategoryEntry entry)
    {
        var literal = entry.ManuallyDefinedAssets;
        if (entry.ManuallyDefinedAssetsFactory is not { } factory) return literal;

        try
        {
            var stopwatch = Stopwatch.StartNew();
            var produced = factory(Loader);
            Log.Information("[CATALOG] {Category}: manual-asset factory produced {Count} rows in {Elapsed:N1}s",
                entry.Type, produced.Length, stopwatch.Elapsed.TotalSeconds);

            return literal.Length == 0 ? produced : literal.Concat(produced).ToArray();
        }
        catch (Exception e)
        {
            Log.Warning("[CATALOG] {Category}: manual-asset factory failed ({Message}); falling back to {Count} literal rows",
                entry.Type, e.Message, literal.Length);
            return literal;
        }
    }

    /// <summary>Manual row for a raw mesh path, or null. Used by IconResolver and get_asset_info.</summary>
    public CategoryItem? ManualFor(string objectPath)
    {
        Lazy<Dictionary<string, CategoryItem>> map;
        lock (_manualByPathGate)
        {
            map = _manualByPath ??= new Lazy<Dictionary<string, CategoryItem>>(BuildManualByPath);
        }

        return map.Value.GetValueOrDefault(NormalizePath(objectPath));
    }

    private Dictionary<string, CategoryItem> BuildManualByPath()
    {
        var map = new Dictionary<string, CategoryItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in CategoryCatalog.Entries)
        {
            foreach (var manual in ManualAssets(entry))
                map.TryAdd(NormalizePath(manual.AssetPath), ToItem(entry, manual));
        }

        return map;
    }

    /// <summary>
    /// Object paths reach us in both shapes: "/Foo/Bar/Baz" from the catalog and "/Foo/Bar/Baz.Baz"
    /// from UObject.GetPathName(). Compare on the package half only.
    /// </summary>
    private static string NormalizePath(string objectPath)
    {
        var trimmed = objectPath.Trim().Replace('\\', '/').TrimEnd('/');
        var lastSlash = trimmed.LastIndexOf('/');
        var lastDot = trimmed.LastIndexOf('.');
        if (lastDot > lastSlash) trimmed = trimmed[..lastDot];

        return trimmed.TrimStart('/');
    }

    private static CategoryItem ToItem(AssetCategoryEntry entry, ManuallyDefinedAsset manual)
    {
        var assetName = manual.AssetPath.Contains('/')
            ? manual.AssetPath[(manual.AssetPath.LastIndexOf('/') + 1)..]
            : manual.AssetPath;

        return new CategoryItem
        {
            ObjectPath = manual.AssetPath,
            AssetName = assetName,
            PackagePath = manual.AssetPath,
            AssetClass = entry.Type.ToString(),
            DisplayName = manual.Name,
            DisplayNameSource = "manual",
            IsManual = true,
            IconPath = manual.IconPath,
            Description = manual.Description
        };
    }

    // ---------------------------------------------------------------- canonical list

    /// <summary>
    /// THE list. Registry rows plus manual rows, labelled with the display name search matches on,
    /// deduped where the category asks for it, in one stable order. browse_category,
    /// make_contact_sheet, list_categories and search_assets all page or count this, which is what
    /// makes "browse row n == sheet cell n" true.
    /// <para>
    /// Waits briefly for the category's display-name index because dedupe is BY display name. If it
    /// is not ready the list is still returned (labelled from asset names, undeduped) but is not
    /// cached, so the next call gets the real thing.
    /// </para>
    /// </summary>
    public async Task<CanonicalList> CanonicalAsync(
        AssetCategoryEntry entry, DisplayNameIndex names, CancellationToken cancellationToken = default)
    {
        if (_canonicalCache.TryGetValue(entry.Type, out var cached)) return cached;

        await names.WhenCategoryReadyAsync(entry.Type, cancellationToken: cancellationToken).ConfigureAwait(false);

        var built = BuildCanonical(entry, names);
        if (built.NameIndexReady) _canonicalCache[entry.Type] = built;

        return built;
    }

    /// <summary>Non-waiting variant for callers that must not block (list_categories, get_status).</summary>
    public CanonicalList CanonicalNow(AssetCategoryEntry entry, DisplayNameIndex names)
    {
        if (_canonicalCache.TryGetValue(entry.Type, out var cached)) return cached;

        var built = BuildCanonical(entry, names);
        if (built.NameIndexReady) _canonicalCache[entry.Type] = built;

        return built;
    }

    private CanonicalList BuildCanonical(AssetCategoryEntry entry, DisplayNameIndex names)
    {
        var rows = Filtered(entry);
        var map = names.MapFor(entry.Type);
        var ready = map is not null;

        var items = new List<CategoryItem>(rows.Count + 8);

        // Pass 1: label every registry row, folding duplicates onto their first occurrence.
        var firstByName = entry.DedupeDisplayNames
            ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            : null;
        var collapsed = new Dictionary<int, List<string>>();

        foreach (var data in rows)
        {
            var assetName = data.AssetName.Text;
            var localised = map?.GetValueOrDefault(data.ObjectPath);

            var displayName = string.IsNullOrWhiteSpace(localised)
                ? CategoryCatalog.PrettifyAssetName(assetName)
                : localised!;
            var source = string.IsNullOrWhiteSpace(localised) ? "assetName" : "displayName";

            if (firstByName is not null)
            {
                if (firstByName.TryGetValue(displayName, out var firstIndex))
                {
                    if (!collapsed.TryGetValue(firstIndex, out var list)) collapsed[firstIndex] = list = [];
                    list.Add(data.ObjectPath);
                    continue;
                }

                firstByName[displayName] = items.Count;
            }

            items.Add(new CategoryItem
            {
                ObjectPath = data.ObjectPath,
                AssetName = assetName,
                PackagePath = data.PackageName.Text,
                AssetClass = data.AssetClass.Text,
                DisplayName = displayName,
                DisplayNameSource = source,
                Hidden = entry.LoadHiddenAssets && entry.IsHiddenName(data.PackageName.Text)
            });
        }

        // Pass 2: attach the collapse counts recorded above.
        foreach (var (index, paths) in collapsed)
            items[index] = items[index] with { CollapsedDuplicates = paths.Count, CollapsedPaths = paths };

        // Pass 3: disambiguate names several DIFFERENT assets share (Vehicle: 7x "Whiplash").
        if (entry.DisambiguateDuplicateNames)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
                counts[item.DisplayName] = counts.GetValueOrDefault(item.DisplayName) + 1;

            for (var i = 0; i < items.Count; i++)
            {
                if (counts.GetValueOrDefault(items[i].DisplayName) <= 1) continue;
                items[i] = items[i] with { DisplayName = $"{items[i].DisplayName} ({items[i].AssetName})" };
            }
        }

        // Pass 4: the catalog's hand-authored rows. Appended, so registry paging never shifts.
        var manual = ManualAssets(entry);
        foreach (var definition in manual)
            items.Add(ToItem(entry, definition));

        return new CanonicalList
        {
            Type = entry.Type,
            Items = items,
            RegistryRows = rows.Count,
            ManualRows = manual.Length,
            NameIndexReady = ready
        };
    }

    /// <summary>Registry rows for every category-backed class (used when no category filter is given).</summary>
    public IEnumerable<FPartialAssetData> AllCategorised()
    {
        var map = _classToCategory.Value;
        foreach (var data in Loader.AssetRegistry)
        {
            if (map.ContainsKey(data.AssetClass.Text)) yield return data;
        }
    }

    public static Func<FPartialAssetData, bool> BuildMatcher(string query, string match)
    {
        var predicate = BuildStringMatcher(query, match);
        return data => predicate(data.AssetName.Text) || predicate(data.PackageName.Text);
    }

    /// <summary>
    /// The same contains/regex semantics as <see cref="BuildMatcher"/> but over a bare string, so
    /// display names out of <see cref="DisplayNameIndex"/> are matched exactly like asset names.
    /// </summary>
    public static Func<string, bool> BuildStringMatcher(string query, string match)
    {
        if (string.IsNullOrWhiteSpace(query)) return _ => true;

        if (match.Equals("regex", StringComparison.OrdinalIgnoreCase))
        {
            Regex regex;
            try
            {
                regex = new Regex(query, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
            }
            catch (ArgumentException e)
            {
                throw new McpException($"Invalid regular expression \"{query}\": {e.Message}");
            }

            return value =>
            {
                try { return regex.IsMatch(value); }
                catch (RegexMatchTimeoutException) { return false; }
            };
        }

        if (!match.Equals("contains", StringComparison.OrdinalIgnoreCase))
            throw new McpException($"Unknown match mode \"{match}\". Use \"contains\" or \"regex\".");

        return value => value.Contains(query, StringComparison.OrdinalIgnoreCase);
    }
}
