using System.Text.Json;
using System.Text.Json.Serialization;
using CUE4Parse.UE4.Versions;

namespace FortnitePorting.Mcp.Config;

/// <summary>
/// Plain configuration POCO for the headless MCP host. Every field is optional;
/// anything left unset falls back to a sensible default for a local Fortnite install.
/// </summary>
public class McpConfig
{
    public const string ConfigEnvironmentVariable = "FPMCP_CONFIG";

    public string ArchiveDirectory { get; set; } = DefaultArchiveDirectory;

    public string DataDirectory { get; set; } = DefaultDataDirectory;

    /// <summary>Where exports are written. Defaults to &lt;DataDirectory&gt;\Exports if unset.</summary>
    public string? ExportRoot { get; set; }

    /// <summary>Overrides the AES main key normally fetched from the FortnitePorting API.</summary>
    public string? AesKeyOverride { get; set; }

    /// <summary>Path to a local .usmap. Takes priority over the API-provided mappings.</summary>
    public string? MappingsFileOverride { get; set; }

    public ELanguage Language { get; set; } = ELanguage.English;

    public static string DefaultArchiveDirectory =>
        Path.Combine("C:", "Program Files", "Epic Games", "Fortnite", "FortniteGame", "Content", "Paks");

    public static string DefaultDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FortnitePortingMcp");

    [JsonIgnore]
    public DirectoryInfo DataFolder => new(DataDirectory);

    [JsonIgnore]
    public DirectoryInfo ExportFolder => new(ExportRoot ?? Path.Combine(DataDirectory, "Exports"));

    [JsonIgnore]
    public DirectoryInfo LogFolder => new(Path.Combine(DataDirectory, "Logs"));

    /// <summary>Scratch folder for extracted native dependencies (binkadec/radadec/vgmstream/Detex).</summary>
    [JsonIgnore]
    public DirectoryInfo DependencyFolder => new(Path.Combine(DataDirectory, ".data"));

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Resolves the config from <c>--config &lt;path&gt;</c> or the FPMCP_CONFIG environment variable.
    /// Returns an all-defaults instance when neither is provided.
    /// </summary>
    public static McpConfig Load(string[] args)
    {
        var path = GetArgumentValue(args, "--config") ?? Environment.GetEnvironmentVariable(ConfigEnvironmentVariable);
        return LoadFrom(path);
    }

    public static McpConfig LoadFrom(string? path)
    {
        McpConfig config;
        if (string.IsNullOrWhiteSpace(path))
        {
            config = new McpConfig();
        }
        else
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"Config file does not exist: {path}", path);

            var json = File.ReadAllText(path);
            config = JsonSerializer.Deserialize<McpConfig>(json, SerializerOptions) ?? new McpConfig();
        }

        config.Normalize();
        return config;
    }

    private void Normalize()
    {
        if (string.IsNullOrWhiteSpace(ArchiveDirectory)) ArchiveDirectory = DefaultArchiveDirectory;
        if (string.IsNullOrWhiteSpace(DataDirectory)) DataDirectory = DefaultDataDirectory;
        if (string.IsNullOrWhiteSpace(ExportRoot)) ExportRoot = Path.Combine(DataDirectory, "Exports");

        DataFolder.Create();
        ExportFolder.Create();
        LogFolder.Create();
        DependencyFolder.Create();
    }

    public static string? GetArgumentValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            return i + 1 < args.Length ? args[i + 1] : null;
        }

        return null;
    }
}
