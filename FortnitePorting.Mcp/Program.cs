using System.Text.Json;
using CUE4Parse.UE4.AssetRegistry.Objects;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse_Conversion.Textures;
using FortnitePorting;
using FortnitePorting.Mcp.Config;
using FortnitePorting.Mcp.Core;
using FortnitePorting.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Serilog;
using SkiaSharp;

// RULE 1 (see mcp-spike/WIRING.md §4): first executable statement. The stdio transport takes the
// raw stdout handle via Console.OpenStandardOutput, so this redirect cannot corrupt the protocol -
// but it does stop every Console.Write in this process and in any linked library from doing so.
Console.SetOut(Console.Error);

McpConfig config;
try
{
    config = McpConfig.Load(args);
}
catch (Exception e)
{
    // Logging isn't up yet, so report this one directly on stderr.
    await Console.Error.WriteLineAsync($"Failed to load configuration: {e.Message}");
    return 1;
}

Logging.Initialize(config);

try
{
    if (args.Contains("--selftest", StringComparer.OrdinalIgnoreCase))
        return await SelfTest.RunAsync(config);

    if (args.Contains("--tools", StringComparer.OrdinalIgnoreCase))
        return CliModes.ListTools(config);

    if (args.Contains("--call", StringComparer.OrdinalIgnoreCase))
        return await CliModes.CallAsync(config, args);

    if (args.Contains("--iconcoverage", StringComparer.OrdinalIgnoreCase))
        return await CliModes.IconCoverageAsync(config, args);

    if (args.Contains("--nameindex", StringComparer.OrdinalIgnoreCase))
        return await CliModes.BuildNameIndexAsync(config, args);

    return await McpServerMode.RunAsync(config, args);
}
catch (Exception e)
{
    Log.Fatal(e, "Unhandled exception");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

/// <summary>Service registration shared by the stdio server and the CLI test harness.</summary>
internal static class McpServices
{
    public const string Instructions = """
        FortnitePorting asset server - read-only discovery, visual browsing and export of assets from
        the locally installed Fortnite archive.

        STARTUP: the archive is mounted in the background and takes roughly 7 seconds. Call get_status
        at any time (it never blocks). Every other tool waits ~2s and then returns
        status:"loading" with a stage and percent instead of failing - that is not an error, just
        retry after a few seconds.

        INTENDED WORKFLOW
          1. list_categories            - see what kinds of asset exist and how many of each.
          2. search_assets              - fast, registry-only name search; returns objectPaths.
             browse_category            - page through a category with real display names and tags.
          3. make_contact_sheet         - THE key step. Composite up to 60 candidates into a single
                                          numbered grid image so you can actually SEE them, then read
                                          the legend to map cell numbers back to objectPaths.
          4. get_asset_icon             - zoom in on one candidate at higher resolution.
          5. get_asset_info             - display name, description, rarity/series/set, tags, styles.
          6. the export tools           - export the chosen objectPath to disk.
          7. search_files               - for raw meshes, sounds, maps and animations that have no
                                          item definition and never appear in search_assets.

        NOTES
          - objectPath is always the full "package.object" string returned by search_assets. Names
            alone will not resolve.
          - Icon coverage is imperfect. Every image result reports iconSource: "handler"/"rawTexture"
            mean real artwork, "placeholder"/"generated" mean none was found for that asset.
          - Prefer one make_contact_sheet over dozens of get_asset_icon calls.
        """;

    public static void Register(IServiceCollection services, McpConfig config)
    {
        services.AddSingleton(config);
        services.AddSingleton<HeadlessLoader>();
        services.AddSingleton<DependencyManager>();
        services.AddSingleton<AssetQuery>();
        services.AddSingleton<DisplayNameIndex>();
        services.AddSingleton<IconResolver>();
        services.AddSingleton<FileIndex>();
        services.AddSingleton<HeadlessExportAssetProvider>();
        services.AddSingleton<ExportRunner>();
    }

    public static IMcpServerBuilder AddServer(IServiceCollection services) =>
        services.AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = McpServerInfo.Name,
                    Version = McpServerInfo.Version,
                    Title = McpServerInfo.Title
                };
                options.ServerInstructions = Instructions;
            })
            .WithToolsFromAssembly(typeof(McpServices).Assembly);
}

internal static class McpServerMode
{
    public static async Task<int> RunAsync(McpConfig config, string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        // RULE 2: nothing may log to stdout.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Information);

        McpServices.Register(builder.Services, config);
        builder.Services.AddHostedService<ArchiveHostedService>();

        McpServices.AddServer(builder.Services).WithStdioServerTransport();

        Log.Information("Starting MCP stdio server (archive mounts in the background)");
        await builder.Build().RunAsync();
        return 0;
    }
}

/// <summary>
/// --tools and --call: the in-process harness used to develop and verify tools without a client.
/// </summary>
internal static class CliModes
{
    public static int ListTools(McpConfig config)
    {
        var services = new ServiceCollection();
        McpServices.Register(services, config);
        McpServices.AddServer(services);

        using var provider = services.BuildServiceProvider();

        var tools = provider.GetServices<McpServerTool>().OrderBy(tool => tool.ProtocolTool.Name, StringComparer.Ordinal).ToList();
        Log.Information("{Count} registered MCP tools:", tools.Count);

        foreach (var tool in tools)
        {
            var protocolTool = tool.ProtocolTool;
            Log.Information("--- {Name} ---", protocolTool.Name);
            Log.Information("  description: {Description}", protocolTool.Description?.ReplaceLineEndings(" ").Trim());
            Log.Information("  inputSchema: {Schema}", protocolTool.InputSchema.ToString());
        }

        return tools.Count > 0 ? 0 : 1;
    }

    public static async Task<int> CallAsync(McpConfig config, string[] args)
    {
        var index = Array.FindIndex(args, arg => arg.Equals("--call", StringComparison.OrdinalIgnoreCase));
        var toolName = index + 1 < args.Length ? args[index + 1] : null;
        if (string.IsNullOrWhiteSpace(toolName))
        {
            Log.Error("Usage: --call <toolName> [jsonArgs]");
            return 1;
        }

        JsonElement? arguments = null;
        if (index + 2 < args.Length && !args[index + 2].StartsWith("--", StringComparison.Ordinal))
        {
            try
            {
                arguments = JsonSerializer.Deserialize<JsonElement>(args[index + 2]);
            }
            catch (JsonException e)
            {
                Log.Error("Arguments are not valid JSON: {Message}", e.Message);
                return 1;
            }
        }

        var services = new ServiceCollection();
        McpServices.Register(services, config);
        await using var provider = services.BuildServiceProvider();

        var loader = provider.GetRequiredService<HeadlessLoader>();
        Log.Information("Loading archive before invoking {Tool}...", toolName);
        var started = DateTime.Now;
        await loader.WaitReadyAsync();
        Log.Information("Archive ready in {Seconds:N1}s", (DateTime.Now - started).TotalSeconds);

        // Parity with the stdio server (ArchiveHostedService): the display-name index builds in the
        // background once the archive is up, so --call exercises the exact same readiness behaviour.
        provider.GetRequiredService<DisplayNameIndex>().StartBackgroundBuild();

        CallToolResult result;
        var invokedAt = DateTime.Now;
        try
        {
            result = await ToolDispatcher.InvokeAsync(provider, toolName, arguments, CancellationToken.None);
        }
        catch (Exception e)
        {
            Log.Error(e, "Tool {Tool} threw", toolName);
            return 1;
        }

        Log.Information("{Tool} completed in {Ms:N0} ms (isError={IsError})",
            toolName, (DateTime.Now - invokedAt).TotalMilliseconds, result.IsError == true);

        var outputDirectory = Path.Combine(config.DataDirectory, "call_output");
        Directory.CreateDirectory(outputDirectory);

        var imageIndex = 0;
        foreach (var block in result.Content ?? [])
        {
            switch (block)
            {
                case TextContentBlock text:
                    await Console.Error.WriteLineAsync(text.Text);
                    break;

                case ImageContentBlock image:
                {
                    var bytes = image.DecodedData.ToArray();
                    var path = Path.Combine(outputDirectory, $"{Sanitize(toolName)}_{DateTime.Now:HHmmss}_{imageIndex++}.png");
                    await File.WriteAllBytesAsync(path, bytes);

                    var dimensions = Describe(bytes);
                    Log.Information("Image written: {Path} ({Bytes:N0} bytes, {Dimensions})", path, bytes.Length, dimensions);
                    break;
                }

                default:
                    Log.Information("Content block: {Type}", block.GetType().Name);
                    break;
            }
        }

        return result.IsError == true ? 1 : 0;
    }

    /// <summary>
    /// Builds the display-name index in the foreground with per-category timings and memory, which
    /// is what the background build does on a cold server run - just observable. Optional
    /// <c>--category &lt;Name&gt;</c> restricts it to one category.
    /// </summary>
    public static async Task<int> BuildNameIndexAsync(McpConfig config, string[] args)
    {
        var only = McpConfig.GetArgumentValue(args, "--category");

        var services = new ServiceCollection();
        McpServices.Register(services, config);
        await using var provider = services.BuildServiceProvider();

        var loader = provider.GetRequiredService<HeadlessLoader>();
        var started = DateTime.Now;
        await loader.WaitReadyAsync();
        Log.Information("Archive ready in {Seconds:N1}s", (DateTime.Now - started).TotalSeconds);

        var names = provider.GetRequiredService<DisplayNameIndex>();
        var indexStarted = DateTime.Now;

        if (string.IsNullOrWhiteSpace(only))
        {
            await names.BuildAllAsync(CancellationToken.None);
        }
        else
        {
            var entry = AssetQuery.ResolveCategory(only);
            await names.WhenCategoryReadyAsync(entry.Type, Timeout.InfiniteTimeSpan);
        }

        using var process = System.Diagnostics.Process.GetCurrentProcess();
        Log.Information("Name index finished in {Seconds:N1}s - {Names:N0} display names, {Ready}/{Total} categories ready",
            (DateTime.Now - indexStarted).TotalSeconds, names.TotalNames, names.ReadyCategoryCount, names.TotalCategoryCount);
        Log.Information("Memory: {Managed:N0} MB managed heap, {WorkingSet:N0} MB working set, {Peak:N0} MB peak working set",
            GC.GetTotalMemory(false) / (1024 * 1024), process.WorkingSet64 / (1024 * 1024), process.PeakWorkingSet64 / (1024 * 1024));

        foreach (var snapshot in names.Snapshot().OrderByDescending(x => x.Rows))
            Log.Information("  {Category,-18} {Status,-9} {Names,7:N0} names / {Rows,7:N0} rows", snapshot.Category, snapshot.State.Name, snapshot.Count, snapshot.Rows);

        return names.ReadyCategoryCount > 0 ? 0 : 1;
    }

    /// <summary>
    /// Diagnostic: pushes a random sample of one category through IconResolver and reports what
    /// fraction resolved real artwork rather than a placeholder. Used to tune the fallback chain.
    /// </summary>
    public static async Task<int> IconCoverageAsync(McpConfig config, string[] args)
    {
        var count = int.TryParse(McpConfig.GetArgumentValue(args, "--iconcoverage"), out var parsed) ? parsed : 100;
        var categoryName = McpConfig.GetArgumentValue(args, "--category") ?? "Prop";
        var seed = int.TryParse(McpConfig.GetArgumentValue(args, "--seed"), out var parsedSeed) ? parsedSeed : 1234;

        var services = new ServiceCollection();
        McpServices.Register(services, config);
        await using var provider = services.BuildServiceProvider();

        var loader = provider.GetRequiredService<HeadlessLoader>();
        await loader.WaitReadyAsync();

        var query = provider.GetRequiredService<AssetQuery>();
        var icons = provider.GetRequiredService<IconResolver>();

        var entry = AssetQuery.ResolveCategory(categoryName);
        var rows = query.Filtered(entry);
        Log.Information("Icon coverage sample: {Count} of {Total:N0} {Category} assets (seed {Seed})", count, rows.Count, entry.Type, seed);

        var random = new Random(seed);
        var sample = Enumerable.Range(0, Math.Min(count, rows.Count))
            .Select(_ => rows[random.Next(rows.Count)])
            .ToList();

        var tally = new Dictionary<string, int>();
        var started = DateTime.Now;

        foreach (var data in sample)
        {
            var result = await icons.ResolveAsync(data.ObjectPath, 128);
            tally[result.SourceName] = tally.GetValueOrDefault(result.SourceName) + 1;
        }

        var real = tally.GetValueOrDefault("handler") + tally.GetValueOrDefault("rawTexture");
        var percent = sample.Count == 0 ? 0 : real * 100.0 / sample.Count;

        foreach (var (source, hits) in tally.OrderByDescending(pair => pair.Value))
            Log.Information("  {Source,-12} {Hits,4} ({Percent:N1}%)", source, hits, hits * 100.0 / sample.Count);

        Log.Information("Real-icon coverage: {Real}/{Total} = {Percent:N1}% in {Seconds:N1}s",
            real, sample.Count, percent, (DateTime.Now - started).TotalSeconds);

        return percent >= 90 ? 0 : 2;
    }

    private static string Describe(byte[] png)
    {
        try
        {
            using var bitmap = SKBitmap.Decode(png);
            return bitmap is null ? "undecodable" : $"{bitmap.Width}x{bitmap.Height}";
        }
        catch
        {
            return "undecodable";
        }
    }

    private static string Sanitize(string value)
        => string.Concat(value.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_'));
}

internal static class SelfTest
{
    private const string ProbeClassName = "FortPlaysetPropItemDefinition";

    public static async Task<int> RunAsync(McpConfig config)
    {
        Log.Information("=== FortnitePorting.Mcp self-test ===");
        Log.Information("Archive Directory: {Path}", config.ArchiveDirectory);
        Log.Information("Data Directory: {Path}", config.DataDirectory);

        var startedAt = DateTime.Now;
        var loader = new HeadlessLoader(config);
        await loader.WhenReady();
        var loadSeconds = (DateTime.Now - startedAt).TotalSeconds;

        Log.Information("Load state: {State}", loader.State);
        Log.Information("Asset registry entries: {Count:N0} (loaded in {Seconds:N1}s)", loader.AssetRegistry.Count, loadSeconds);
        Log.Information("Cosmetic set names: {Count:N0}", loader.SetNames.Count);
        Log.Information("Rarity colors: {Count:N0}", loader.RarityColors.Count);
        Log.Information("Lobby montages: {Male} male / {Female} female", loader.MaleLobbyMontages.Count, loader.FemaleLobbyMontages.Count);

        if (loader.AssetRegistry.Count == 0)
        {
            Log.Error("Asset registry is empty - the archive did not mount correctly.");
            return 1;
        }

        var props = loader.AssetRegistry
            .Where(data => data.AssetClass.Text.Equals(ProbeClassName, StringComparison.Ordinal))
            .ToList();

        Log.Information("{Class} rows: {Count:N0}", ProbeClassName, props.Count);
        if (props.Count == 0)
        {
            Log.Error("Found no {Class} rows in the asset registry.", ProbeClassName);
            return 1;
        }

        foreach (var sample in props.Take(5))
            Log.Information("  sample: {AssetName}  ->  {ObjectPath}", sample.AssetName.Text, sample.ObjectPath);

        var entry = CategoryCatalog.ForType(EExportType.Prop)
                    ?? throw new InvalidOperationException("Catalog is missing the Prop entry.");
        Log.Information("Catalog entries: {Count} across {Categories} categories",
            CategoryCatalog.Entries.Count, CategoryCatalog.Entries.Select(x => x.Category).Distinct().Count());

        // Verify the embedded decoders extract and that IExportAssetProvider wires up.
        var dependencies = new DependencyManager(config);
        var exportProvider = new HeadlessExportAssetProvider(loader, dependencies);
        foreach (var decoder in new[] { exportProvider.BinkaDecoderFile, exportProvider.RadaDecoderFile })
        {
            decoder.Refresh();
            if (decoder is not { Exists: true, Length: > 0 })
            {
                Log.Error("Embedded dependency was not extracted: {Path}", decoder.FullName);
                return 1;
            }

            Log.Information("Dependency ready: {Path} ({Bytes:N0} bytes)", decoder.FullName, decoder.Length);
        }

        var iconPath = await WriteFirstIconAsync(loader, entry, props, config);
        if (iconPath is null)
        {
            Log.Error("Could not resolve and decode an icon for any of the sampled props.");
            return 1;
        }

        var iconFile = new FileInfo(iconPath);
        Log.Information("Icon written: {Path} ({Bytes:N0} bytes)", iconFile.FullName, iconFile.Length);

        if (iconFile.Length <= 1024)
        {
            Log.Error("Icon PNG is suspiciously small ({Bytes} bytes).", iconFile.Length);
            return 1;
        }

        var toolCount = ToolDispatcher.Discover().Count;
        Log.Information("Registered MCP tools: {Count} ({Names})",
            toolCount, string.Join(", ", ToolDispatcher.Discover().Select(binding => binding.Name)));

        if (toolCount == 0)
        {
            Log.Error("No [McpServerTool] methods were discovered in the assembly.");
            return 1;
        }

        Log.Information("=== self-test PASSED in {Seconds:N1}s ===", (DateTime.Now - startedAt).TotalSeconds);
        return 0;
    }

    private static async Task<string?> WriteFirstIconAsync(
        HeadlessLoader loader, AssetCategoryEntry entry, List<FPartialAssetData> props, McpConfig config)
    {
        var outputPath = Path.Combine(config.DataDirectory, "selftest_icon.png");

        // Props with a placeholder-only icon are common; walk a handful until one decodes.
        foreach (var data in props.Take(50))
        {
            try
            {
                var asset = await loader.Provider.SafeLoadPackageObjectAsync(data.ObjectPath);
                if (asset is null) continue;

                var displayName = entry.DisplayNameHandler(asset) ?? asset.Name;
                if (entry.GetIcon(asset) is not UTexture2D texture) continue;

                var decoded = texture.Decode(maxMipSize: 256);
                if (decoded is null) continue;

                using var bitmap = decoded.ToSkBitmap();
                using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
                await using var fileStream = File.Create(outputPath);
                encoded.SaveTo(fileStream);

                Log.Information("Decoded icon from \"{DisplayName}\" ({Object}) via texture {Texture}",
                    displayName, data.AssetName.Text, texture.Name);
                return outputPath;
            }
            catch (Exception e)
            {
                Log.Debug("Icon attempt failed for {Path}: {Message}", data.ObjectPath, e.Message);
            }
        }

        return null;
    }
}
