using System.ComponentModel;
using System.Text.Json.Nodes;
using CUE4Parse.UE4.AssetRegistry.Objects;
using FortnitePorting.Mcp.Core;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace FortnitePorting.Mcp.Tools;

[McpServerToolType]
public static class ImageTools
{
    [McpServerTool(Name = "get_asset_icon", ReadOnly = true, Title = "Asset icon")]
    [Description("""
                 Returns the in-game icon for one asset as a PNG image. Use it to zoom in on a single
                 candidate after make_contact_sheet. Icon resolution in Fortnite is genuinely patchy, so
                 the result always carries an iconSource field: "handler" and "rawTexture" are real
                 artwork, "placeholder" and "generated" mean nothing decodable was found.
                 """)]
    public static async Task<CallToolResult> GetAssetIconAsync(
        HeadlessLoader loader,
        IconResolver icons,
        [Description("Full object path from search_assets, e.g. \"/Game/Foo/Bar.Bar\".")] string objectPath,
        [Description("Longest edge of the returned image in pixels (16-1024).")] int size = 256,
        CancellationToken cancellationToken = default)
    {
        if (!await loader.TryWaitReadyAsync(cancellationToken)) return ToolResults.StillLoading(loader);

        if (string.IsNullOrWhiteSpace(objectPath))
            throw new McpException("objectPath is required. Get one from search_assets or browse_category.");

        var result = await icons.ResolveAsync(objectPath, size, cancellationToken);

        return ToolResults.Structured(
            new JsonObject
            {
                ["status"] = "ok",
                ["objectPath"] = objectPath,
                ["iconSource"] = result.SourceName,
                ["texturePath"] = result.TexturePath,
                ["size"] = size,
                ["bytes"] = result.Png.Length
            },
            ImageContentBlock.FromBytes(result.Png, "image/png"));
    }

    [McpServerTool(Name = "make_contact_sheet", ReadOnly = true, Title = "Contact sheet")]
    [Description("""
                 THE tool for visual browsing: composites up to 60 asset icons into ONE labelled grid
                 image, plus a legend mapping each numbered cell back to its objectPath. Call it either
                 with an explicit list of objectPaths, or with a category (optionally narrowed by
                 query) and a page number. Pick the cells you like, then use get_asset_icon or the
                 export tools on their objectPaths.
                 """)]
    public static async Task<CallToolResult> MakeContactSheetAsync(
        HeadlessLoader loader,
        AssetQuery assets,
        IconResolver icons,
        [Description("Explicit object paths to render. Takes priority over category.")] string[]? objectPaths = null,
        [Description("Category to sheet, e.g. \"Prop\". Ignored when objectPaths is given.")] string? category = null,
        [Description("Optional substring filter applied to the category's asset/package names.")] string? query = null,
        [Description("Zero-based page index within the filtered category.")] int page = 0,
        [Description("Cells per page. Capped at 60.")] int pageSize = 48,
        [Description("Pixel size of each grid cell (48-512).")] int cellSize = 128,
        [Description("Columns in the grid (1-12).")] int columns = 8,
        [Description("Draw the truncated display name under each cell.")] bool labels = true,
        CancellationToken cancellationToken = default)
    {
        if (!await loader.TryWaitReadyAsync(cancellationToken)) return ToolResults.StillLoading(loader);

        pageSize = Math.Clamp(pageSize, 1, ContactSheet.MaxCells);
        page = Math.Max(0, page);

        List<(string ObjectPath, string Name)> targets;
        string? resolvedCategory = null;
        var total = 0;

        if (objectPaths is { Length: > 0 })
        {
            targets = objectPaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Take(ContactSheet.MaxCells)
                .Select(path => (path, ShortName(path)))
                .ToList();
            total = objectPaths.Length;
        }
        else if (!string.IsNullOrWhiteSpace(category))
        {
            var entry = AssetQuery.ResolveCategory(category);
            resolvedCategory = entry.Type.ToString();

            IEnumerable<FPartialAssetData> rows = assets.Filtered(entry);
            if (!string.IsNullOrWhiteSpace(query))
            {
                var matcher = AssetQuery.BuildMatcher(query, "contains");
                rows = rows.Where(matcher);
            }

            var filtered = rows.ToList();
            total = filtered.Count;
            targets = filtered
                .Skip(page * pageSize)
                .Take(pageSize)
                .Select(data => (data.ObjectPath, data.AssetName.Text))
                .ToList();
        }
        else
        {
            throw new McpException("Pass either objectPaths (an explicit list) or category (optionally with query and page).");
        }

        if (targets.Count == 0)
        {
            return ToolResults.Structured(new JsonObject
            {
                ["status"] = "empty",
                ["category"] = resolvedCategory,
                ["query"] = query,
                ["total"] = total,
                ["page"] = page,
                ["message"] = "Nothing matched, so no sheet was rendered. Widen the query or lower the page number."
            });
        }

        // Resolve every icon first; the compositor is CPU-only afterwards.
        var resolved = new IconResult?[targets.Count];
        var displayNames = new string[targets.Count];

        await Parallel.ForEachAsync(
            Enumerable.Range(0, targets.Count),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2), CancellationToken = cancellationToken },
            async (i, ct) =>
            {
                var (objectPath, fallbackName) = targets[i];
                displayNames[i] = fallbackName;

                try
                {
                    resolved[i] = await icons.ResolveAsync(objectPath, cellSize, ct);

                    if (labels && await loader.Provider.SafeLoadPackageObjectAsync(objectPath) is { } asset)
                    {
                        var entry = CategoryCatalog.ForClassName(asset.ExportType);
                        displayNames[i] = DiscoveryTools.SafeDisplayName(entry, asset);
                    }
                }
                catch
                {
                    resolved[i] = null;
                }
            });

        var cells = new List<ContactSheetCell>(targets.Count);
        var legend = new JsonArray();
        var realIcons = 0;

        for (var i = 0; i < targets.Count; i++)
        {
            var index = page * pageSize + i;
            var icon = resolved[i];
            if (icon?.IsRealIcon == true) realIcons++;

            cells.Add(new ContactSheetCell(index, icon?.Png, displayNames[i]));
            legend.Add(new JsonObject
            {
                ["index"] = index,
                ["objectPath"] = targets[i].ObjectPath,
                ["displayName"] = displayNames[i],
                ["iconSource"] = icon?.SourceName ?? "generated"
            });
        }

        var sheet = ContactSheet.Render(cells, cellSize, columns, labels);

        return ToolResults.Structured(
            new JsonObject
            {
                ["status"] = "ok",
                ["category"] = resolvedCategory,
                ["query"] = query,
                ["page"] = page,
                ["pageSize"] = pageSize,
                ["total"] = total,
                ["cells"] = cells.Count,
                ["columns"] = Math.Clamp(columns, 1, 12),
                ["cellSize"] = Math.Clamp(cellSize, 48, 512),
                ["realIconCount"] = realIcons,
                ["bytes"] = sheet.Length,
                ["legend"] = legend
            },
            ImageContentBlock.FromBytes(sheet, "image/png"));
    }

    private static string ShortName(string objectPath)
    {
        var afterSlash = objectPath[(objectPath.LastIndexOf('/') + 1)..];
        var dot = afterSlash.LastIndexOf('.');
        return dot > 0 ? afterSlash[..dot] : afterSlash;
    }
}
