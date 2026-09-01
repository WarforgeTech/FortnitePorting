using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using CUE4Parse.UE4.AssetRegistry.Objects;
using FortnitePorting;
using ModelContextProtocol;

namespace FortnitePorting.Mcp.Core;

/// <summary>
/// Registry-only querying: the fast path that never touches a .uasset. All the category
/// name filters from AssetLoader.Load are reproduced here so counts line up with the GUI.
/// </summary>
public sealed class AssetQuery(HeadlessLoader loader)
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<EExportType, List<FPartialAssetData>> _filteredCache = new();
    private readonly Lazy<Dictionary<string, string>> _classToCategory = new(() =>
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in CategoryCatalog.Entries)
        foreach (var className in entry.ClassNames)
            map.TryAdd(className, entry.Type.ToString());

        return map;
    });

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
                if (entry.HideNames.Any(name => packageName.Contains(name, StringComparison.OrdinalIgnoreCase))) continue;
                if (packageName.Contains("Placeholder", StringComparison.OrdinalIgnoreCase)) continue;
            }

            result.Add(data);
        }

        return result;
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

            return data =>
            {
                try { return regex.IsMatch(data.AssetName.Text) || regex.IsMatch(data.PackageName.Text); }
                catch (RegexMatchTimeoutException) { return false; }
            };
        }

        if (!match.Equals("contains", StringComparison.OrdinalIgnoreCase))
            throw new McpException($"Unknown match mode \"{match}\". Use \"contains\" or \"regex\".");

        return data => data.AssetName.Text.Contains(query, StringComparison.OrdinalIgnoreCase)
                       || data.PackageName.Text.Contains(query, StringComparison.OrdinalIgnoreCase);
    }
}
