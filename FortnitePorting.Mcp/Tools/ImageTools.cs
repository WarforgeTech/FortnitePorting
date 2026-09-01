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
        AssetQuery assets,
        IconResolver icons,
        [Description("Full object path from search_assets, e.g. \"/Game/Foo/Bar.Bar\".")] string objectPath,
        [Description("Longest edge of the returned image in pixels (16-1024).")] int size = 256,
        CancellationToken cancellationToken = default)
    {
        if (!await loader.TryWaitReadyAsync(cancellationToken)) return ToolResults.StillLoading(loader);

        if (string.IsNullOrWhiteSpace(objectPath))
            throw new McpException("objectPath is required. Get one from search_assets or browse_category.");

        // Hand-authored rows are bare meshes; the resolver picks up the catalog's pinned icon.
        var manual = assets.ManualFor(objectPath);
        var result = await icons.ResolveAsync(objectPath, size, cancellationToken, manual?.IconPath);

        var payload = new JsonObject
        {
            ["status"] = "ok",
            ["objectPath"] = objectPath,
            ["iconSource"] = result.SourceName,
            ["texturePath"] = result.TexturePath,
            ["size"] = size,
            ["bytes"] = result.Png.Length
        };

        if (manual is not null)
        {
            payload["displayName"] = manual.DisplayName;
            payload["source"] = "manual";
        }

        return ToolResults.Structured(payload, ImageContentBlock.FromBytes(result.Png, "image/png"));
    }

    [McpServerTool(Name = "make_contact_sheet", ReadOnly = true, Title = "Contact sheet")]
    [Description("""
                 THE tool for visual browsing: composites up to 60 asset icons into ONE labelled grid
                 image, plus a legend mapping each numbered cell back to its objectPath. Call it either
                 with an explicit list of objectPaths, or with a category (optionally narrowed by
                 query) and a page number. Pages the same canonical list browse_category does, so with
                 the same category, page and pageSize, cell n IS row n there. Pick the cells you like,
                 then use get_asset_icon or the export tools on their objectPaths.
                 """)]
    public static async Task<CallToolResult> MakeContactSheetAsync(
        HeadlessLoader loader,
        AssetQuery assets,
        IconResolver icons,
        DisplayNameIndex names,
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

        List<CategoryItem> targets;
        string? resolvedCategory = null;
        var total = 0;
        var totalPages = 0;
        CanonicalList? canonical = null;

        if (objectPaths is { Length: > 0 })
        {
            targets = objectPaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Take(ContactSheet.MaxCells)
                .Select(path => assets.ManualFor(path) ?? new CategoryItem
                {
                    ObjectPath = path,
                    AssetName = ShortName(path),
                    PackagePath = path,
                    AssetClass = string.Empty,
                    DisplayName = ShortName(path),
                    DisplayNameSource = "assetName"
                })
                .ToList();
            total = objectPaths.Length;
            totalPages = 1;
        }
        else if (!string.IsNullOrWhiteSpace(category))
        {
            var entry = AssetQuery.ResolveCategory(category);
            resolvedCategory = entry.Type.ToString();

            // THE canonical list - the same one browse_category slices, so cell n == row n.
            canonical = await assets.CanonicalAsync(entry, names, cancellationToken);
            var rows = CategoryPage.Filter(canonical.Items, query);

            total = rows.Count;
            totalPages = (int) Math.Ceiling(rows.Count / (double) pageSize);
            targets = rows.Skip(page * pageSize).Take(pageSize).ToList();
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
                ["totalPages"] = totalPages,
                ["page"] = page,
                ["message"] = total == 0
                    ? "This category/query matched nothing at all, so no sheet was rendered. Widen the query or check list_categories."
                    : $"Page {page} is past the end: there are {totalPages} page(s) at pageSize {pageSize}. Lower the page number."
            });
        }

        // Resolve every icon first; the compositor is CPU-only afterwards. Labels come from the
        // canonical rows, so they match browse_category exactly and need no package load.
        var resolved = new IconResult?[targets.Count];
        var displayNames = targets.Select(item => item.DisplayName).ToArray();

        await Parallel.ForEachAsync(
            Enumerable.Range(0, targets.Count),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2), CancellationToken = cancellationToken },
            async (i, ct) =>
            {
                var item = targets[i];
                try
                {
                    resolved[i] = await icons.ResolveAsync(item.ObjectPath, cellSize, ct, item.IconPath);

                    // Explicit-path mode has no canonical row to take a label from.
                    if (labels && resolvedCategory is null && !item.IsManual &&
                        await loader.Provider.SafeLoadPackageObjectAsync(item.ObjectPath) is { } asset)
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

            var row = new JsonObject
            {
                ["index"] = index,
                ["objectPath"] = targets[i].ObjectPath,
                ["displayName"] = displayNames[i],
                ["iconSource"] = icon?.SourceName ?? "generated"
            };

            if (targets[i].CollapsedDuplicates > 0) row["collapsedDuplicates"] = targets[i].CollapsedDuplicates;
            if (targets[i].IsManual)
            {
                row["source"] = "manual";

                // A catalog row whose pinned icon did not decode means the content is absent from
                // this Fortnite build, not that the icon lookup is weak.
                if (icon?.IsRealIcon != true)
                    row["note"] = "Not present in the installed Fortnite build - this cell is a placeholder and the asset cannot be exported.";
            }

            legend.Add(row);
        }

        var sheet = ContactSheet.Render(cells, cellSize, columns, labels);

        var payload = new JsonObject
        {
            ["status"] = "ok",
            ["category"] = resolvedCategory,
            ["query"] = query,
            ["page"] = page,
            ["pageSize"] = pageSize,
            ["total"] = total,
            ["totalPages"] = totalPages,
            ["cells"] = cells.Count,
            ["columns"] = Math.Clamp(columns, 1, 12),
            ["cellSize"] = Math.Clamp(cellSize, 48, 512),
            ["realIconCount"] = realIcons,
            ["bytes"] = sheet.Length,
            ["legend"] = legend
        };

        if (resolvedCategory is not null)
            payload["note"] = $"browse_category with category \"{resolvedCategory}\", page {page} and pageSize {pageSize} "
                              + "returns these same assets in this same order.";

        if (canonical is { NameIndexReady: false })
            payload["note"] = (payload["note"]?.GetValue<string>() ?? string.Empty)
                              + " The display-name index for this category is still building, so this page may change once it finishes.";

        return ToolResults.Structured(payload, ImageContentBlock.FromBytes(sheet, "image/png"));
    }

    private static string ShortName(string objectPath)
    {
        var afterSlash = objectPath[(objectPath.LastIndexOf('/') + 1)..];
        var dot = afterSlash.LastIndexOf('.');
        return dot > 0 ? afterSlash[..dot] : afterSlash;
    }
}
