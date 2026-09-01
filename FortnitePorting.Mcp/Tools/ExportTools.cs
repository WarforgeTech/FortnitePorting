using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.i18N;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.Utils;
using FortnitePorting;
using FortnitePorting.CUE4Parse.Extensions;
using FortnitePorting.Exporting;
using FortnitePorting.Mcp.Config;
using FortnitePorting.Mcp.Core;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Serilog;
using EMeshFormat = CUE4Parse_Conversion.Options.EMeshFormat;
using IoPath = System.IO.Path;
using JsonConvert = Newtonsoft.Json.JsonConvert;
using JsonFormatting = Newtonsoft.Json.Formatting;

namespace FortnitePorting.Mcp.Tools;

/// <summary>
/// Export + property-inspection tools. Everything routes through <see cref="ExportRunner"/>,
/// which serializes exports and reports exactly what landed on disk.
/// </summary>
[McpServerToolType]
public static class ExportTools
{
    private static readonly TimeSpan ReadyGrace = TimeSpan.FromSeconds(2);

    // ------------------------------------------------------------------ export_assets

    [McpServerTool(Name = "export_assets", Destructive = false, OpenWorld = false)]
    [Description("Exports one or more Fortnite assets (props, outfits, meshes, textures, sounds, ...) to disk. "
                 + "Meshes land as .uemodel/.psk/.glb next to their PNG/TGA textures. Returns the exact file list written per asset, "
                 + "the style variants applied, and - for outfits/backpacks - the character parts (head, body, hat, ...) that came out.")]
    public static async Task<CallToolResult> ExportAssetsAsync(
        IServiceProvider services,
        [Description("Full object paths to export, e.g. 'FortniteGame/Content/.../PID_Foo.PID_Foo'.")]
        string[] objectPaths,
        [Description("Optional output folder. When set, files land flat in this folder; when omitted they mirror the game path under the configured export root.")]
        string? outputDir = null,
        [Description("Style variants for cosmetics (outfits, backpacks, pickaxes, ...). Omit for the base look. "
                     + "Pass the string \"all\" to fold in EVERY option of every channel (what a FortnitePorting folder export does), "
                     + "or an object selecting one option per channel, e.g. {\"Style\":\"Black & Gold\"}. "
                     + "Channel and option names are exactly what list_asset_styles reports (matched case- and punctuation-insensitively).")]
        JsonElement? styles = null,
        [Description("Mesh format: UEFormat (.uemodel, default), ActorX (.psk), or Gltf2 (.glb).")]
        string meshFormat = "UEFormat",
        [Description("Texture format: PNG (default) or TGA.")]
        string imageFormat = "PNG",
        [Description("Sound format: WAV (default), MP3, OGG, or FLAC. Non-WAV formats need ffmpeg on PATH.")]
        string soundFormat = "WAV",
        [Description("Export materials and their textures alongside meshes. Disable for geometry only.")]
        bool exportMaterials = true,
        [Description("Outfits only: also write the lobby idle montage as a .ueanim (the FortnitePorting 'Import Lobby Poses' option). Off by default.")]
        bool importLobbyPoses = false,
        CancellationToken cancellationToken = default)
    {
        var loader = services.GetRequiredService<HeadlessLoader>();
        if (await NotReady(loader, cancellationToken) is { } loading) return loading;

        if (objectPaths is not { Length: > 0 })
            return Failure("objectPaths must contain at least one object path.");

        if (!TryParseOptions(outputDir, meshFormat, imageFormat, soundFormat, exportMaterials, out var options, out var optionError))
            return Failure(optionError!);

        if (!TryParseStyles(styles, out var styleSelection, out var styleError))
            return Failure(styleError!);

        options = options with { Styles = styleSelection, ImportLobbyPoses = importLobbyPoses };

        try
        {
            var runner = Runner(services);
            var result = await runner.ExportAssets(objectPaths, options, cancellationToken);
            return Structured(Describe(result));
        }
        catch (Exception e)
        {
            Log.Error(e, "export_assets failed");
            return Failure($"export_assets failed: {e.Message}");
        }
    }

    // ------------------------------------------------------------------ export_gallery

    [McpServerTool(Name = "export_gallery", Destructive = false, OpenWorld = false)]
    [Description("Exports a Creative gallery/prefab (FortPlaysetItemDefinition). With perAssetFolders=true (default) every member prop "
                 + "is exported on its own into <outputDir>/<GalleryName>/<PropName>/ so each folder holds one mesh plus its textures - "
                 + "ready for individual UEFN import. With perAssetFolders=false the whole gallery is exported as one composed prefab.")]
    public static async Task<CallToolResult> ExportGalleryAsync(
        IServiceProvider services,
        [Description("Full object path of the gallery's FortPlaysetItemDefinition. Provide this or galleryName.")]
        string? galleryObjectPath = null,
        [Description("Gallery name to search for, e.g. 'Battlewood'. Matched against asset names and display names in the asset registry.")]
        string? galleryName = null,
        [Description("Output folder. Defaults to the configured export root.")]
        string? outputDir = null,
        [Description("True (default) exports each member prop separately into its own folder; false exports the composed prefab.")]
        bool perAssetFolders = true,
        [Description("Mesh format: UEFormat (.uemodel, default), ActorX (.psk), or Gltf2 (.glb).")]
        string meshFormat = "UEFormat",
        [Description("Texture format: PNG (default) or TGA.")]
        string imageFormat = "PNG",
        [Description("Export materials and their textures alongside meshes.")]
        bool exportMaterials = true,
        CancellationToken cancellationToken = default)
    {
        var loader = services.GetRequiredService<HeadlessLoader>();
        if (await NotReady(loader, cancellationToken) is { } loading) return loading;

        if (string.IsNullOrWhiteSpace(galleryObjectPath) && string.IsNullOrWhiteSpace(galleryName))
            return Failure("Provide either galleryObjectPath or galleryName.");

        if (!TryParseOptions(outputDir, meshFormat, imageFormat, "WAV", exportMaterials, out var options, out var optionError))
            return Failure(optionError!);

        try
        {
            var runner = Runner(services);
            var resolvedPath = galleryObjectPath;
            JsonArray? candidates = null;

            if (string.IsNullOrWhiteSpace(resolvedPath))
            {
                var matches = await runner.FindGalleries(galleryName!, limit: 25);
                if (matches.Count == 0)
                    return Failure($"No FortPlaysetItemDefinition matched '{galleryName}'.");

                resolvedPath = matches[0].ObjectPath;
                candidates = new JsonArray(matches.Select(match => (JsonNode) new JsonObject
                {
                    ["objectPath"] = match.ObjectPath,
                    ["assetName"] = match.AssetName,
                    ["displayName"] = match.DisplayName
                }).ToArray());
            }

            if (!perAssetFolders)
            {
                var composed = await runner.ExportAssets([resolvedPath!], options with { ForceExportType = EExportType.Prefab }, cancellationToken);
                var payload = Describe(composed);
                payload["mode"] = "composed-prefab";
                payload["galleryObjectPath"] = resolvedPath;
                if (candidates is not null) payload["candidates"] = candidates;
                return Structured(payload);
            }

            var result = await runner.ExportGalleryAsIndividualAssets(resolvedPath!, outputDir, options, cancellationToken);

            var json = new JsonObject
            {
                ["mode"] = "individual-props",
                ["galleryObjectPath"] = result.GalleryObjectPath,
                ["galleryName"] = result.GalleryName,
                ["outputRoot"] = result.OutputRoot,
                ["memberSource"] = result.MemberSource,
                ["membersFound"] = result.MembersFound,
                ["propsExported"] = result.Props.Count,
                ["totalFiles"] = result.TotalFiles,
                ["totalBytes"] = result.TotalBytes,
                ["props"] = new JsonArray(result.Props.Select(DescribeAsset).ToArray()),
                ["failures"] = new JsonArray(result.Failures
                    .Select(failure => (JsonNode) new JsonObject
                    {
                        ["objectPath"] = failure.ObjectPath,
                        ["error"] = failure.Error
                    }).ToArray())
            };

            if (candidates is not null) json["candidates"] = candidates;
            return Structured(json);
        }
        catch (Exception e)
        {
            Log.Error(e, "export_gallery failed");
            return Failure($"export_gallery failed: {e.Message}");
        }
    }

    // ------------------------------------------------------------------ list_asset_styles

    [McpServerTool(Name = "list_asset_styles", ReadOnly = true, OpenWorld = false)]
    [Description("Lists the style channels and options an asset exposes (ItemVariants). The channel and option names it "
                 + "returns are exactly what export_assets accepts in its `styles` argument. For galleries/prefabs it also "
                 + "lists the individual member props (export those with export_gallery, not with `styles`).")]
    public static async Task<CallToolResult> ListAssetStylesAsync(
        IServiceProvider services,
        [Description("Full object path of the asset.")] string objectPath,
        CancellationToken cancellationToken = default)
    {
        var loader = services.GetRequiredService<HeadlessLoader>();
        if (await NotReady(loader, cancellationToken) is { } loading) return loading;

        UObject asset;
        try
        {
            asset = await loader.Provider.LoadPackageObjectAsync(ExportSession.FixPath(objectPath));
        }
        catch (Exception e)
        {
            return Failure($"Failed to load '{objectPath}': {e.Message}");
        }

        try
        {
            var exportType = CategoryCatalog.DetermineExportType(asset);
            var channels = new JsonArray();

            // Same resolver the exporter uses, so every option listed here is selectable.
            var styleChannels = StyleResolver.ReadChannels(asset);
            foreach (var channel in styleChannels)
            {
                channels.Add(new JsonObject
                {
                    ["channel"] = channel.Channel,
                    ["variantType"] = channel.VariantType,
                    ["multiSelect"] = false,
                    ["options"] = new JsonArray(channel.Options
                        .Select(option => (JsonNode) new JsonObject { ["name"] = option.Name })
                        .ToArray())
                });
            }

            if (exportType is EExportType.Prefab)
            {
                var (members, source) = Runner(services).ResolveGalleryMembers(asset);
                channels.Add(new JsonObject
                {
                    ["channel"] = "Individual Props",
                    ["variantType"] = source,
                    ["multiSelect"] = true,
                    ["options"] = new JsonArray(members.Select(member => (JsonNode) new JsonObject
                    {
                        ["name"] = member.Name,
                        ["objectPath"] = member.ObjectPath
                    }).ToArray())
                });
            }

            return Structured(new JsonObject
            {
                ["objectPath"] = asset.GetPathName(),
                ["displayName"] = ExportRunner.DisplayNameOf(asset) ?? asset.Name,
                ["assetClass"] = asset.ExportType,
                ["exportType"] = exportType.ToString(),
                ["channelCount"] = channels.Count,
                ["channels"] = channels,
                ["usage"] = styleChannels.Count == 0
                    ? "This asset has no style channels; export_assets will export its base look."
                    : "Pass styles:\"all\" to export_assets for every option at once, or styles:{"
                      + string.Join(", ", styleChannels.Select(channel => $"\"{channel.Channel}\":\"{channel.Options[0].Name}\""))
                      + "} to pick one option per channel."
            });
        }
        catch (Exception e)
        {
            Log.Error(e, "list_asset_styles failed");
            return Failure($"list_asset_styles failed: {e.Message}");
        }
    }

    // ------------------------------------------------------------------ get_properties_json

    [McpServerTool(Name = "get_properties_json", ReadOnly = true, OpenWorld = false)]
    [Description("Dumps every UObject export of a package as indented JSON. Truncates at maxBytes with a notice, or set saveToFile=true to write it under <ExportRoot>/Properties and return the path.")]
    public static async Task<CallToolResult> GetPropertiesJsonAsync(
        IServiceProvider services,
        [Description("Full object or package path, e.g. 'FortniteGame/Content/.../PID_Foo'.")] string objectPath,
        [Description("Maximum JSON bytes to return inline. Larger dumps are truncated with a notice.")] int maxBytes = 200000,
        [Description("Write the full JSON to <ExportRoot>/Properties/<name>.json and return the path instead of the body.")] bool saveToFile = false,
        CancellationToken cancellationToken = default)
    {
        var loader = services.GetRequiredService<HeadlessLoader>();
        if (await NotReady(loader, cancellationToken) is { } loading) return loading;

        if (maxBytes <= 0) return Failure("maxBytes must be greater than zero.");

        string json;
        int exportCount;
        try
        {
            var fixedPath = ExportSession.FixPath(objectPath);
            if (!loader.Provider.TryLoadObjectExports(fixedPath, out var exports))
                return Failure($"No package found at '{objectPath}'.");

            var list = exports.ToList();
            if (list.Count == 0) return Failure($"Package '{objectPath}' contains no object exports.");

            exportCount = list.Count;
            json = JsonConvert.SerializeObject(list, JsonFormatting.Indented);
        }
        catch (Exception e)
        {
            Log.Error(e, "get_properties_json failed for {Path}", objectPath);
            return Failure($"Failed to read properties for '{objectPath}': {e.Message}");
        }

        var totalBytes = System.Text.Encoding.UTF8.GetByteCount(json);
        var name = ExportRunner.SanitizeFileName(objectPath.SubstringAfterLast("/").SubstringBeforeLast("."));
        if (string.IsNullOrWhiteSpace(name)) name = "properties";

        if (saveToFile)
        {
            var config = services.GetRequiredService<McpConfig>();
            var directory = IoPath.Combine(config.ExportFolder.FullName, "Properties");
            Directory.CreateDirectory(directory);

            var filePath = IoPath.Combine(directory, $"{name}.json");
            await File.WriteAllTextAsync(filePath, json, cancellationToken);

            return Structured(new JsonObject
            {
                ["objectPath"] = objectPath,
                ["exportCount"] = exportCount,
                ["savedTo"] = filePath,
                ["bytes"] = totalBytes,
                ["truncated"] = false
            });
        }

        var truncated = totalBytes > maxBytes;
        var body = truncated ? TruncateUtf8(json, maxBytes) : json;

        return Structured(new JsonObject
        {
            ["objectPath"] = objectPath,
            ["exportCount"] = exportCount,
            ["bytes"] = totalBytes,
            ["returnedBytes"] = System.Text.Encoding.UTF8.GetByteCount(body),
            ["truncated"] = truncated,
            ["notice"] = truncated
                ? $"Output truncated to {maxBytes} bytes of {totalBytes}. Raise maxBytes or call again with saveToFile=true for the full dump."
                : null,
            ["json"] = body
        });
    }

    // ------------------------------------------------------------------ plumbing

    /// <summary>
    /// Prefers the DI-registered runner. Falls back to a process-wide lazy instance so the
    /// export tools still work if Program.cs only registers the loader and the config.
    /// </summary>
    private static ExportRunner Runner(IServiceProvider services)
        => services.GetService<ExportRunner>() ?? FallbackRunner(services);

    private static readonly Lock FallbackGate = new();
    private static ExportRunner? _fallbackRunner;

    private static ExportRunner FallbackRunner(IServiceProvider services)
    {
        lock (FallbackGate)
        {
            if (_fallbackRunner is not null) return _fallbackRunner;

            var loader = services.GetRequiredService<HeadlessLoader>();
            var config = services.GetService<McpConfig>() ?? loader.Config;
            var dependencies = services.GetService<DependencyManager>() ?? new DependencyManager(config);
            var provider = services.GetService<HeadlessExportAssetProvider>() ?? new HeadlessExportAssetProvider(loader, dependencies);

            Log.Warning("ExportRunner was not registered in DI; using a process-wide fallback instance.");
            _fallbackRunner = new ExportRunner(loader, provider, config, services.GetService<DisplayNameIndex>());
            return _fallbackRunner;
        }
    }

    private static bool TryParseOptions(
        string? outputDir, string meshFormat, string imageFormat, string soundFormat, bool exportMaterials,
        out ExportOptions options, out string? error)
    {
        options = new ExportOptions();
        error = null;

        if (!Enum.TryParse<EMeshFormat>(meshFormat, ignoreCase: true, out var mesh))
        {
            error = $"Unknown meshFormat '{meshFormat}'. Valid: {string.Join(", ", Enum.GetNames<EMeshFormat>())}.";
            return false;
        }

        if (!Enum.TryParse<EImageFormat>(imageFormat, ignoreCase: true, out var image))
        {
            error = $"Unknown imageFormat '{imageFormat}'. Valid: {string.Join(", ", Enum.GetNames<EImageFormat>())}.";
            return false;
        }

        if (!Enum.TryParse<ESoundFormat>(soundFormat, ignoreCase: true, out var sound))
        {
            error = $"Unknown soundFormat '{soundFormat}'. Valid: {string.Join(", ", Enum.GetNames<ESoundFormat>())}.";
            return false;
        }

        options = new ExportOptions
        {
            OutputDir = string.IsNullOrWhiteSpace(outputDir) ? null : outputDir,
            MeshFormat = mesh,
            ImageFormat = image,
            SoundFormat = sound,
            ExportMaterials = exportMaterials
        };

        return true;
    }

    /// <summary>
    /// Reads the `styles` argument, which is deliberately a union: absent, the string "all", or an
    /// object of channel -> option. Clients that can only send strings may send the object
    /// JSON-encoded; that is accepted too.
    /// </summary>
    private static bool TryParseStyles(JsonElement? styles, out StyleSelection? selection, out string? error)
    {
        selection = null;
        error = null;

        if (styles is not { } element || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return true;

        switch (element.ValueKind)
        {
            case JsonValueKind.String:
            {
                var text = (element.GetString() ?? string.Empty).Trim();

                if (text.Length == 0) return true;
                if (text.Equals("all", StringComparison.OrdinalIgnoreCase))
                {
                    selection = StyleSelection.Everything;
                    return true;
                }

                if (text.StartsWith('{'))
                {
                    try
                    {
                        using var document = JsonDocument.Parse(text);
                        return TryReadStyleObject(document.RootElement, out selection, out error);
                    }
                    catch (JsonException e)
                    {
                        error = $"`styles` looked like JSON but could not be parsed: {e.Message}";
                        return false;
                    }
                }

                error = $"Unknown `styles` value \"{text}\". Use \"all\", or an object like {{\"Style\":\"Black & Gold\"}}.";
                return false;
            }

            case JsonValueKind.Object:
                return TryReadStyleObject(element, out selection, out error);

            default:
                error = $"`styles` must be the string \"all\" or an object of channel -> option, not {element.ValueKind}.";
                return false;
        }
    }

    private static bool TryReadStyleObject(JsonElement element, out StyleSelection? selection, out string? error)
    {
        selection = null;
        error = null;

        var byChannel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind is not JsonValueKind.String)
            {
                error = $"`styles` channel \"{property.Name}\" must map to an option name string, not {property.Value.ValueKind}.";
                return false;
            }

            byChannel[property.Name] = property.Value.GetString() ?? string.Empty;
        }

        if (byChannel.Count == 0) return true;

        selection = new StyleSelection { ByChannel = byChannel };
        return true;
    }

    private static JsonObject Describe(ExportResult result) => new()
    {
        ["outputRoot"] = result.OutputRoot,
        ["assetsExported"] = result.Assets.Count,
        ["totalFiles"] = result.TotalFiles,
        ["totalBytes"] = result.TotalBytes,
        ["assets"] = new JsonArray(result.Assets.Select(DescribeAsset).ToArray()),
        ["failures"] = new JsonArray(result.Failures
            .Select(failure => (JsonNode) new JsonObject
            {
                ["objectPath"] = failure.ObjectPath,
                ["error"] = failure.Error
            }).ToArray())
    };

    private static JsonNode DescribeAsset(ExportedAsset asset)
    {
        var json = new JsonObject
        {
            ["objectPath"] = asset.ObjectPath,
            ["displayName"] = asset.DisplayName,
            ["exportType"] = asset.ExportType,
            ["outputRoot"] = asset.OutputRoot,
            ["fileCount"] = asset.Files.Count,
            ["bytes"] = asset.Bytes,
            ["files"] = new JsonArray(asset.Files
                .Select(file => (JsonNode) new JsonObject
                {
                    ["path"] = file.Path,
                    ["bytes"] = file.Bytes
                }).ToArray())
        };

        if (asset.AppliedStyles.Count > 0)
            json["appliedStyles"] = ToolResults.ToJsonArray(asset.AppliedStyles);

        if (asset.Notes.Count > 0)
            json["notes"] = ToolResults.ToJsonArray(asset.Notes);

        if (asset.Parts.Count > 0)
        {
            json["partCount"] = asset.Parts.Count;
            json["parts"] = new JsonArray(asset.Parts
                .Select(part => (JsonNode) new JsonObject
                {
                    ["name"] = part.Name,
                    ["partType"] = part.PartType,
                    ["gender"] = part.Gender,
                    ["fromStyle"] = part.IsOverride,
                    ["poseAsset"] = part.PoseAsset,
                    ["materials"] = part.MaterialCount,
                    ["overrideMaterials"] = part.OverrideMaterialCount
                }).ToArray());
        }

        return json;
    }

    /// <summary>"Not ready" is a retryable status, not an error - see WIRING.md §7c.</summary>
    private static async Task<CallToolResult?> NotReady(HeadlessLoader loader, CancellationToken token)
    {
        if (loader.State is LoadState.Ready) return null;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(ReadyGrace);
            await loader.WhenReady(timeout.Token);
            return null;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            var stage = loader.State is LoadState.Loading loading ? loading.StageName : "starting";
            var percent = loader.State is LoadState.Loading progress ? progress.Percent : 0f;

            return Structured(new JsonObject
            {
                ["status"] = "loading",
                ["stage"] = stage,
                ["percent"] = Math.Round(percent, 1),
                ["retry_after_seconds"] = 5,
                ["message"] = $"The Fortnite archive is still loading (stage: {stage}, {percent:N0}%). Retry shortly."
            });
        }
        catch (Exception e)
        {
            return Failure($"The Fortnite archive failed to load: {e.Message}");
        }
    }

    private static CallToolResult Structured(JsonObject payload) => new()
    {
        IsError = false,
        StructuredContent = JsonSerializer.SerializeToElement(payload),
        Content = [new TextContentBlock { Text = payload.ToJsonString() }]
    };

    private static CallToolResult Failure(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }]
    };

    private static string TruncateUtf8(string value, int maxBytes)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        if (bytes.Length <= maxBytes) return value;

        var count = maxBytes;
        // Do not split a multi-byte sequence.
        while (count > 0 && (bytes[count] & 0xC0) == 0x80) count--;

        return System.Text.Encoding.UTF8.GetString(bytes, 0, count);
    }

}
