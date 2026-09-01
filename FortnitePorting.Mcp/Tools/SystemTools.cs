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
    public static CallToolResult GetStatus(HeadlessLoader loader, McpConfig config)
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
            ["server"] = new JsonObject
            {
                ["name"] = McpServerInfo.Name,
                ["version"] = McpServerInfo.Version
            },
            ["archive"] = new JsonObject
            {
                ["archiveDirectory"] = config.ArchiveDirectory,
                ["dataDirectory"] = config.DataDirectory,
                ["exportDirectory"] = config.ExportFolder.FullName,
                ["language"] = config.Language.ToString()
            }
        };

        if (state is LoadState.Failed failed)
            payload["error"] = failed.Message;

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

    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";
}
