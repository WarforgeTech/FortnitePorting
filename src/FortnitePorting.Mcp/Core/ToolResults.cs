using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace FortnitePorting.Mcp.Core;

/// <summary>
/// Shared helpers for building MCP tool results. Every structured payload is mirrored into a
/// TextContentBlock so clients that ignore structuredContent still see the data.
/// </summary>
public static class ToolResults
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static CallToolResult Structured(JsonObject payload) => new()
    {
        IsError = false,
        StructuredContent = JsonSerializer.SerializeToElement(payload),
        Content = [new TextContentBlock { Text = payload.ToJsonString(Indented) }]
    };

    public static CallToolResult Structured(JsonObject payload, params ContentBlock[] extraContent)
    {
        var content = new List<ContentBlock>(extraContent) { new TextContentBlock { Text = payload.ToJsonString(Indented) } };
        return new CallToolResult
        {
            IsError = false,
            StructuredContent = JsonSerializer.SerializeToElement(payload),
            Content = content
        };
    }

    public static CallToolResult Error(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }]
    };

    /// <summary>The polite "archive is still mounting" reply. Deliberately NOT an error.</summary>
    public static CallToolResult StillLoading(HeadlessLoader loader) => Structured(new JsonObject
    {
        ["status"] = "loading",
        ["stage"] = LoaderGate.StageName(loader),
        ["percent"] = LoaderGate.Percent(loader),
        ["retry_after_seconds"] = 5,
        ["message"] = $"The Fortnite archive is still mounting ({LoaderGate.StageName(loader)}, {LoaderGate.Percent(loader):N0}%). " +
                      "Initial load takes roughly 7 seconds; call get_status or simply retry."
    });

    public static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values) array.Add(value);
        return array;
    }

    public static McpException Fail(string message) => new(message);
}
