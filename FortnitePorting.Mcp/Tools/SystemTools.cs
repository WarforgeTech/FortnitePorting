using System.ComponentModel;
using System.Reflection;
using System.Text.Json.Nodes;
using FortnitePorting.Mcp.Config;
using FortnitePorting.Mcp.Core;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace FortnitePorting.Mcp.Tools;

[McpServerToolType]
public static class SystemTools
{
    [McpServerTool(Name = "get_status", ReadOnly = true, Title = "Archive status")]
    [Description("""
                 Reports whether the Fortnite archive is mounted yet. Never blocks - safe to call the
                 instant the server starts. The archive takes roughly 7 seconds to mount; every other
                 tool waits briefly and then returns status:"loading" instead of failing.
                 """)]
    public static CallToolResult GetStatus(HeadlessLoader loader, McpConfig config, DisplayNameIndex names)
    {
        var state = loader.State;

        var payload = new JsonObject
        {
            ["status"] = state switch
            {
                LoadState.Ready => "ready",
                LoadState.Failed => "failed",
                LoadState.NotStarted => "not_started",
                _ => "loading"
            },
            ["stage"] = LoaderGate.StageName(loader),
            ["percent"] = Math.Round(LoaderGate.Percent(loader), 1),
            // Build stamp and schema version are echoed so a client can tell that the exe it is
            // talking to is older than the source tree it expects - a stale publish/mcp otherwise
            // just quietly omits whatever feature was added since.
            ["server"] = new JsonObject
            {
                ["name"] = McpServerInfo.Name,
                ["version"] = McpServerInfo.Version,
                ["buildTimestampUtc"] = McpServerInfo.BuildTimestampUtc,
                ["gitCommit"] = McpServerInfo.GitCommit,
                ["buildStamp"] = McpServerInfo.BuildStamp,
                ["manifestSchemaVersion"] = ExportManifest.SchemaVersion
            },
            ["archive"] = new JsonObject
            {
                ["archiveDirectory"] = config.ArchiveDirectory,
                ["dataDirectory"] = config.DataDirectory,
                ["exportDirectory"] = config.ExportFolder.FullName,
                ["language"] = config.Language.ToString()
            },
            // False means the unmanaged ACL decoder did not load, and every animation export
            // (all emotes, lobby poses) will come out without its .ueanim.
            ["nativeAnimationSupport"] = ExportRunner.NativeAnimationSupport
        };

        if (state is LoadState.Failed failed)
            payload["error"] = failed.Message;

        payload["nameIndex"] = NameIndexPayload(names);

        if (state is LoadState.Ready)
        {
            payload["counts"] = new JsonObject
            {
                ["assetRegistryEntries"] = loader.AssetRegistry.Count,
                ["cosmeticSets"] = loader.SetNames.Count,
                ["rarityColors"] = loader.RarityColors.Count,
                ["categories"] = CategoryCatalog.Entries.Count,
                ["vfsFiles"] = SafeFileCount(loader)
            };

            try
            {
                payload["archive"]!["unrealVersion"] = loader.Provider.Versions.Game.ToString();
            }
            catch
            {
                // Provider not reachable - the counts above are enough.
            }
        }
        else
        {
            payload["retry_after_seconds"] = 5;
        }

        return ToolResults.Structured(payload);
    }

    /// <summary>
    /// Per-category state of the display-name index that search_assets matches against. It builds in
    /// the background after the archive mounts and is cached on disk, so this is "ready" within
    /// seconds on a warm run and climbs through the categories on a cold one.
    /// </summary>
    private static JsonObject NameIndexPayload(DisplayNameIndex names)
    {
        var categories = new JsonObject();
        foreach (var snapshot in names.Snapshot())
        {
            var entry = new JsonObject
            {
                ["status"] = snapshot.State.Name,
                ["displayNames"] = snapshot.Count,
                ["usable"] = snapshot.State.IsUsable
            };

            if (snapshot.State is NameIndexState.Building)
            {
                entry["percent"] = Math.Round(snapshot.State.Percent, 1);
                entry["rows"] = snapshot.Rows;
            }

            if (snapshot.State is NameIndexState.Failed failure)
                entry["error"] = failure.Message;

            categories[snapshot.Category] = entry;
        }

        return new JsonObject
        {
            ["coverage"] = names.Coverage,
            ["readyCategories"] = names.ReadyCategoryCount,
            // Not in memory yet, but a disk cache is sitting there: a search in THIS process loads
            // it and answers. Reporting these as notBuilt made a warm server look cold.
            ["cachedCategories"] = names.CachedCategoryCount,
            ["usableCategories"] = names.AvailableCategoryCount,
            ["totalCategories"] = names.TotalCategoryCount,
            ["displayNames"] = names.TotalNames,
            ["availableDisplayNames"] = names.AvailableNames,
            ["categories"] = categories,
            ["note"] = "search_assets matches display names for every category whose usable flag is true - \"ready\" means already in "
                       + "memory, \"cached\" means it loads from disk on first use (effectively instant). Only \"notBuilt\", \"building\" "
                       + "and \"failed\" fall back to asset/package-name matching."
        };
    }

    private static int SafeFileCount(HeadlessLoader loader)
    {
        try { return loader.Provider.Files.Count; }
        catch { return 0; }
    }
}

public static class McpServerInfo
{
    public const string Name = "fortnite-porting";
    public const string Title = "FortnitePorting Asset Server";

    private static readonly Assembly Self = Assembly.GetExecutingAssembly();

    public static string Version { get; } = Self.GetName().Version?.ToString(3) ?? "0.1.0";

    /// <summary>UTC time this assembly was compiled, stamped by the StampBuild target in the csproj.</summary>
    public static string? BuildTimestampUtc { get; } = Metadata("BuildTimestampUtc");

    /// <summary>Short git commit the assembly was built from. Null when git was unavailable at build time.</summary>
    public static string? GitCommit { get; } = Metadata("GitCommit");

    /// <summary>
    /// One line saying exactly which binary is answering. A published exe that predated the manifest
    /// feature silently produced no manifests during UEFN round-2 validation and nothing in its output
    /// revealed that, so this is echoed by get_status and written into every export manifest.
    /// </summary>
    public static string BuildStamp { get; } = Compose();

    private static string Compose()
    {
        var parts = new List<string> { $"{Name} {Version}" };
        if (GitCommit is not null) parts.Add($"commit {GitCommit}");
        parts.Add(BuildTimestampUtc is null ? "built unknown" : $"built {BuildTimestampUtc}");
        return string.Join(", ", parts);
    }

    private static string? Metadata(string key)
        => Self.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(attribute.Key, key, StringComparison.Ordinal))
            ?.Value is { Length: > 0 } value
            ? value
            : null;
}
