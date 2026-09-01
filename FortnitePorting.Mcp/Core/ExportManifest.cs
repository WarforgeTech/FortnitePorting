using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.Utils;
using FortnitePorting;
using FortnitePorting.Exporting;
using FortnitePorting.Exporting.Models;
using FortnitePorting.Exporting.Types;
using FortnitePorting.Mcp.Tools;
using Serilog;
using EMeshFormat = CUE4Parse_Conversion.Options.EMeshFormat;
using IoPath = System.IO.Path;

namespace FortnitePorting.Mcp.Core;

/// <summary>What <see cref="ExportManifest"/> produced for one asset.</summary>
public record ExportManifestResult
{
    /// <summary>Absolute path of the manifest.json that was written.</summary>
    public required string Path { get; init; }

    /// <summary>File name (not full path) of the mesh a consumer should import. Null when none was resolved.</summary>
    public string? PrimaryMeshFile { get; init; }

    /// <summary>Absolute path of the primary mesh file.</summary>
    public string? PrimaryMeshPath { get; init; }

    public long Bytes { get; init; }

    /// <summary>Machine-readable asset-level flags, mirrored into the tool output.</summary>
    public List<string> Notes { get; init; } = [];
}

/// <summary>
/// Serializes the in-memory export model (<c>BaseExport</c> -&gt; <c>ExportMesh</c> -&gt;
/// <c>ExportMaterial</c>) into a per-asset <c>*.manifest.json</c> beside the exported files.
/// <para>
/// This exists because the artifacts themselves lose material-binding knowledge that CUE4Parse
/// had in memory: a Gltf2 export writes material NAMES but leaves every <c>baseColorTexture</c>
/// null, exported PNGs never carry alpha (foliage opacity lives in a separate _M/_Mask texture),
/// foliage colour lives in a LUT texture or a vector parameter rather than the diffuse, and an
/// asset can export several meshes of which only one is the render mesh (CP_Apollo_BigBush also
/// writes a shadow proxy that sorts alphabetically first). The manifest states all of that
/// explicitly so an importer never has to guess from file names.
/// </para>
/// <para>
/// Nothing here mutates the export: it only reads the model that <c>CreateExport</c> /
/// <c>WaitForExports</c> already built, plus <c>File.Exists</c> checks on what landed.
/// </para>
/// </summary>
public static class ExportManifest
{
    /// <summary>Bump when the shape changes in a way a consumer must notice.</summary>
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions Writer = new() { WriteIndented = true };

    // Parameter names, in the source material, that carry per-pixel opacity rather than colour.
    private static readonly string[] MaskParameterNames =
    [
        "M", "Mask", "MaskTexture", "Masks", "OpacityMask", "Opacity", "OpacityTexture",
        "SpecularMasks", "MRAE", "MRAEMasks", "Alpha", "AlphaMask", "OpacityMap"
    ];

    private static readonly string[] DiffuseParameterNames =
    [
        "Diffuse", "DiffuseTexture", "BaseColor", "Base Color", "Albedo", "Color", "Colour",
        "Diffuse_Texture", "DiffuseMap", "Texture"
    ];

    private static readonly string[] NormalParameterNames =
    [
        "Normals", "Normal", "NormalMap", "Normal Map", "Normals_Texture", "NormalTexture"
    ];

    private static readonly Regex LodSuffix = new(@"[_\-]LOD[_\-]?(\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LayerCount = new(@"(\d+)\s*layer", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Builds and writes the manifest. Never throws: a manifest is diagnostic output, so a failure
    /// here must not turn a successful export into a failed one - it is logged and reported as null.
    /// </summary>
    public static async Task<ExportManifestResult?> WriteAsync(
        BaseExport export,
        ExportDataMeta meta,
        string objectPath,
        string assetName,
        string displayName,
        string exportType,
        string outputRoot,
        IReadOnlyList<ExportedFile> files,
        IReadOnlyList<string> appliedStyles,
        string? manifestFileName,
        Func<string, Task<UObject?>>? resolveObject = null)
    {
        try
        {
            return await BuildAsync(export, meta, objectPath, assetName, displayName, exportType,
                outputRoot, files, appliedStyles, manifestFileName, resolveObject);
        }
        catch (Exception e)
        {
            Log.Warning(e, "Failed to write export manifest for {Asset}", assetName);
            return null;
        }
    }

    private static async Task<ExportManifestResult?> BuildAsync(
        BaseExport export,
        ExportDataMeta meta,
        string objectPath,
        string assetName,
        string displayName,
        string exportType,
        string outputRoot,
        IReadOnlyList<ExportedFile> files,
        IReadOnlyList<string> appliedStyles,
        string? manifestFileName,
        Func<string, Task<UObject?>>? resolveObject)
    {
        var meshExtension = MeshExtension(meta.Settings.MeshFormat);
        var imageExtension = meta.Settings.ImageFormat is EImageFormat.TGA ? "tga" : "png";

        var onDisk = files.ToDictionary(file => file.Path, file => file.Bytes, StringComparer.OrdinalIgnoreCase);
        var referencedTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var baseDefaults = new Dictionary<string, JsonObject?>(StringComparer.OrdinalIgnoreCase);
        var assetNotes = new List<string>();

        // ---------------------------------------------------------------- meshes
        var entries = new List<MeshEntry>();
        if (export is MeshExport meshExport)
        {
            var overridePaths = new HashSet<string>(
                meshExport.OverrideMeshes.Select(item => item.Path).Where(path => !string.IsNullOrEmpty(path)),
                StringComparer.OrdinalIgnoreCase);

            var order = 0;
            foreach (var mesh in ExportRunner.EnumerateMeshes(meshExport))
            {
                if (string.IsNullOrEmpty(mesh.Path)) continue;

                var path = ExportRunner.ToDiskPath(mesh.Path, meshExtension, meta);
                var (role, lodIndex) = ClassifyMesh(mesh.Name, path);

                entries.Add(new MeshEntry(order++, mesh, path, role, lodIndex,
                    overridePaths.Contains(mesh.Path) ? "styleOverride" : "base"));
            }
        }

        var candidates = RankCandidates(entries);
        var renderMeshCount = entries.Count(entry => entry.Role is "render");

        // Assert a single render mesh only when there IS a single render mesh.
        var primary = renderMeshCount <= 1 ? candidates.FirstOrDefault() : null;
        if (primary is not null) primary.IsPrimary = true;

        // The mesh writer emits LOD/Nanite sidecars (CP_BigBush_LOD2.glb, ..._Nanite.uemodel) that the
        // export MODEL never names, so they exist on disk with no entry. Attribute them to their owner,
        // otherwise a consumer listing the folder still faces a "which file?" choice.
        var meshFilesOnDisk = files
            .Where(file => file.Path.EndsWith($".{meshExtension}", StringComparison.OrdinalIgnoreCase))
            .Select(file => file.Path)
            .ToList();

        var accountedMeshFiles = new HashSet<string>(entries.Select(entry => entry.DiskPath), StringComparer.OrdinalIgnoreCase);
        var sidecars = new Dictionary<MeshEntry, List<(string Path, int? Lod, string Kind)>>();

        foreach (var entry in entries)
        {
            var stem = IoPath.GetFileNameWithoutExtension(entry.DiskPath);
            var meshDirectory = IoPath.GetDirectoryName(entry.DiskPath) ?? string.Empty;
            var pattern = new Regex($@"^{Regex.Escape(stem)}(?:[_\-]LOD[_\-]?(\d+)|(_Nanite))$", RegexOptions.IgnoreCase);

            var found = new List<(string, int?, string)>();
            foreach (var path in meshFilesOnDisk)
            {
                if (accountedMeshFiles.Contains(path)) continue;
                if (!string.Equals(IoPath.GetDirectoryName(path) ?? string.Empty, meshDirectory, StringComparison.OrdinalIgnoreCase)) continue;

                var match = pattern.Match(IoPath.GetFileNameWithoutExtension(path));
                if (!match.Success) continue;

                found.Add(match.Groups[2].Success
                    ? (path, null, "nanite")
                    : (path, int.TryParse(match.Groups[1].Value, out var lod) ? lod : null, "lod"));
            }

            foreach (var (path, _, _) in found) accountedMeshFiles.Add(path);
            sidecars[entry] = found;
        }

        var unaccountedMeshFiles = meshFilesOnDisk
            .Where(path => !accountedMeshFiles.Contains(path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // ---------------------------------------------------------------- json
        var meshArray = new JsonArray();
        foreach (var entry in entries)
        {
            var mesh = entry.Mesh;
            var exists = File.Exists(entry.DiskPath);

            var materials = new JsonArray();
            foreach (var material in mesh.Materials)
                materials.Add(DescribeMaterial(material, "base", meta, imageExtension, onDisk, referencedTextures, baseDefaults));
            foreach (var material in mesh.OverrideMaterials)
                materials.Add(DescribeMaterial(material, "componentOverride", meta, imageExtension, onDisk, referencedTextures, baseDefaults));

            var json = new JsonObject
            {
                ["name"] = mesh.Name,
                ["file"] = IoPath.GetFileName(entry.DiskPath),
                ["path"] = entry.DiskPath,
                ["objectPath"] = mesh.Path,
                ["exists"] = exists,
                ["bytes"] = exists ? SafeLength(entry.DiskPath) : 0,
                ["role"] = entry.Role,
                ["lodIndex"] = entry.LodIndex,
                ["isPrimary"] = entry.IsPrimary,
                ["source"] = entry.Source,
                ["numLods"] = mesh.NumLods,
                ["materialSlotCount"] = mesh.Materials.Count,
                ["instanceCount"] = mesh.Instances.Count,
                ["location"] = Vector(mesh.Location),
                ["rotation"] = new JsonObject
                {
                    ["pitch"] = mesh.Rotation.Pitch,
                    ["yaw"] = mesh.Rotation.Yaw,
                    ["roll"] = mesh.Rotation.Roll
                },
                ["scale"] = Vector(mesh.Scale),
                ["sidecarFiles"] = new JsonArray(sidecars.GetValueOrDefault(entry, [])
                    .OrderBy(item => item.Lod ?? int.MaxValue)
                    .Select(item => (JsonNode) new JsonObject
                    {
                        ["file"] = IoPath.GetFileName(item.Path),
                        ["path"] = item.Path,
                        ["kind"] = item.Kind,
                        ["lodIndex"] = item.Lod,
                        ["bytes"] = SafeLength(item.Path)
                    }).ToArray()),
                ["materials"] = materials
            };

            if (mesh.TextureData.Count > 0)
                json["textureData"] = new JsonArray(mesh.TextureData
                    .Select(data => (JsonNode) new JsonObject
                    {
                        ["index"] = data.Index,
                        ["objectPath"] = data.Path,
                        ["diffuse"] = DescribeTexture(null, data.Diffuse, meta, imageExtension, onDisk, referencedTextures),
                        ["normal"] = DescribeTexture(null, data.Normal, meta, imageExtension, onDisk, referencedTextures),
                        ["specular"] = DescribeTexture(null, data.Specular, meta, imageExtension, onDisk, referencedTextures)
                    }).ToArray());

            if (mesh is ExportPart part)
            {
                json["partType"] = part.Type.ToString();
                json["gender"] = part.GenderPermitted.ToString();
            }

            meshArray.Add(json);
        }

        // Style-driven overrides live beside the meshes, not inside them.
        var overrideMaterials = new JsonArray();
        var overrideParameters = new JsonArray();
        if (export is MeshExport styled)
        {
            foreach (var swap in styled.OverrideMaterials)
            {
                var json = DescribeMaterial(swap.Material, "styleOverride", meta, imageExtension, onDisk, referencedTextures, baseDefaults);
                json["materialNameToSwap"] = swap.MaterialNameToSwap;
                overrideMaterials.Add(json);
            }

            foreach (var parameters in styled.OverrideParameters)
            {
                var json = DescribeParameters(parameters, meta, imageExtension, onDisk, referencedTextures);
                json["materialNameToAlter"] = parameters.MaterialNameToAlter;
                overrideParameters.Add(json);
            }
        }

        // ---------------------------------------------------------------- texture reconciliation
        var textureFilesOnDisk = files
            .Where(file => file.Path.EndsWith($".{imageExtension}", StringComparison.OrdinalIgnoreCase))
            .Select(file => file.Path)
            .ToList();

        var unreferenced = textureFilesOnDisk
            .Where(path => !referencedTextures.Contains(path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var missingTextures = referencedTextures
            .Where(path => !File.Exists(path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // ---------------------------------------------------------------- asset-level notes
        if (entries.Any(entry => entry.Role is "shadow_proxy")) assetNotes.Add("shadow_proxy_present");
        if (entries.Any(entry => entry.Role is "collision")) assetNotes.Add("collision_mesh_present");
        if (renderMeshCount > 1) assetNotes.Add("multiple_render_meshes");
        if (primary is null && renderMeshCount > 1) assetNotes.Add("import_all_render_meshes");
        if (primary is null && renderMeshCount <= 1) assetNotes.Add("no_primary_mesh");
        if (unreferenced.Count > 0) assetNotes.Add("unreferenced_texture_files");
        if (missingTextures.Count > 0) assetNotes.Add("missing_texture_files");
        if (unaccountedMeshFiles.Count > 0) assetNotes.Add("unaccounted_mesh_files");
        if (appliedStyles.Count > 0) assetNotes.Add("styles_applied");

        // ---------------------------------------------------------------- bounds
        JsonNode? bounds = null;
        var boundsSource = primary ?? candidates.FirstOrDefault();
        if (boundsSource is not null && resolveObject is not null)
            bounds = await ReadBoundsAsync(boundsSource.Mesh.Path, resolveObject);

        var manifest = new JsonObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["generator"] = $"{McpServerInfo.Name} {McpServerInfo.Version}",
            ["generatedUtc"] = DateTime.UtcNow.ToString("O"),
            ["asset"] = new JsonObject
            {
                ["objectPath"] = objectPath,
                ["name"] = assetName,
                ["displayName"] = displayName,
                ["exportType"] = exportType,
                ["appliedStyles"] = ToolResults.ToJsonArray(appliedStyles),
                // UE's own FBoxSphereBounds for the source mesh named by boundsFrom - the authored
                // bounds, which UE may pad slightly beyond the tight geometry bounds.
                ["sourceBoundsCm"] = bounds,
                ["boundsFrom"] = boundsSource is null ? null : IoPath.GetFileName(boundsSource.DiskPath)
            },
            ["export"] = new JsonObject
            {
                ["outputRoot"] = outputRoot,
                ["layout"] = meta.CustomPath is null ? "gamePath" : "flat",
                ["meshFormat"] = meta.Settings.MeshFormat.ToString(),
                ["meshExtension"] = meshExtension,
                ["imageFormat"] = meta.Settings.ImageFormat.ToString(),
                ["imageExtension"] = imageExtension,
                ["exportMaterials"] = meta.Settings.ExportMaterials
            },
            // The single field that removes the wrong-mesh trap. Null when the asset genuinely has
            // several render meshes (an outfit's head/body/hat) - import them all in that case.
            ["primaryMesh"] = primary is null ? null : IoPath.GetFileName(primary.DiskPath),
            ["primaryMeshPath"] = primary?.DiskPath,
            ["primaryMeshCandidates"] = ToolResults.ToJsonArray(candidates.Select(entry => FileNameOf(entry.DiskPath))),
            ["meshCount"] = entries.Count,
            ["renderMeshCount"] = renderMeshCount,
            ["meshes"] = meshArray,
            // Mesh files on disk that no entry above claims. Should normally be empty; anything here
            // is a file a folder-scanning importer could pick up by mistake.
            ["unaccountedMeshFiles"] = ToolResults.ToJsonArray(unaccountedMeshFiles.Select(FileNameOf)),
            ["overrideMaterials"] = overrideMaterials,
            ["overrideParameters"] = overrideParameters,
            ["textureFiles"] = new JsonObject
            {
                ["referenced"] = ToolResults.ToJsonArray(referencedTextures
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .Select(FileNameOf)),
                ["unreferencedOnDisk"] = ToolResults.ToJsonArray(unreferenced.Select(FileNameOf)),
                ["referencedButMissing"] = ToolResults.ToJsonArray(missingTextures.Select(FileNameOf))
            },
            ["notes"] = ToolResults.ToJsonArray(assetNotes),
            ["guidance"] = Guidance
        };

        // ---------------------------------------------------------------- write
        var anchor = primary ?? candidates.FirstOrDefault();
        var directory = anchor is not null && File.Exists(anchor.DiskPath)
            ? IoPath.GetDirectoryName(anchor.DiskPath) ?? outputRoot
            : outputRoot;

        Directory.CreateDirectory(directory);

        var fileName = string.IsNullOrWhiteSpace(manifestFileName)
            ? $"{ExportRunner.SanitizeFileName(assetName)}.manifest.json"
            : manifestFileName;

        var manifestPath = IoPath.Combine(directory, fileName);
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString(Writer));

        return new ExportManifestResult
        {
            Path = manifestPath,
            PrimaryMeshFile = primary is null ? null : IoPath.GetFileName(primary.DiskPath),
            PrimaryMeshPath = primary?.DiskPath,
            Bytes = SafeLength(manifestPath),
            Notes = assetNotes
        };
    }

    private const string Guidance =
        "Exported meshes carry material NAMES but no texture bindings (every glTF baseColorTexture is null), and exported "
        + "images never carry a meaningful alpha channel. Bind textures by the `parameter` name under meshes[].materials[].textures[], "
        + "not by file-name heuristics. Import the mesh named by `primaryMesh` - other entries may be shadow proxies or LODs. "
        + "When a material is flagged opacity_in_mask_texture, its opacity lives in a channel of the listed mask texture "
        + "(Fortnite convention for a _M / SpecularMasks map is R=specular, G=metallic, B=ambient occlusion, A/other=opacity or "
        + "emissive - confirm against the source shader), never in the diffuse alpha. When flagged color_via_lut, the diffuse is "
        + "near-white luminance and the colour comes from the LUT texture and/or the vector parameters listed here.";

    // ---------------------------------------------------------------- materials

    private static JsonObject DescribeMaterial(
        ExportMaterial material, string kind, ExportDataMeta meta, string imageExtension,
        IReadOnlyDictionary<string, long> onDisk, HashSet<string> referenced,
        Dictionary<string, JsonObject?> baseDefaults)
    {
        var json = DescribeParameters(material, meta, imageExtension, onDisk, referenced);

        var blendMode = material.OverrideBlendMode.ToString();
        var notes = new List<string>();

        var textureNames = material.Textures.Select(texture => texture.Name).ToList();
        var hasMask = textureNames.Any(IsMaskParameter);
        var hasDiffuse = textureNames.Any(IsDiffuseParameter);
        var hasLut = material.Textures.Any(texture =>
            texture.Name.Contains("LUT", StringComparison.OrdinalIgnoreCase)
            || texture.Texture.Path.Contains("LUT", StringComparison.OrdinalIgnoreCase));

        var masked = blendMode.Contains("Masked", StringComparison.OrdinalIgnoreCase);
        var translucent = blendMode.Contains("Translucent", StringComparison.OrdinalIgnoreCase)
                          || blendMode.Contains("Additive", StringComparison.OrdinalIgnoreCase)
                          || blendMode.Contains("AlphaComposite", StringComparison.OrdinalIgnoreCase);

        if (masked) notes.Add("masked_blend");
        if (translucent) notes.Add("translucent_blend");
        if ((masked || translucent) && hasMask) notes.Add("opacity_in_mask_texture");
        if (masked && !hasMask) notes.Add("masked_blend_without_mask_texture");
        if (hasLut) notes.Add("color_via_lut");
        if (!hasDiffuse) notes.Add("no_diffuse_texture_parameter");
        if (material.Textures.Count == 0) notes.Add("no_texture_parameters");

        var layers = LayerCount.Match(material.Name);
        if (layers.Success) notes.Add($"layered_material_{layers.Groups[1].Value}");

        json["slot"] = material.Slot;
        json["name"] = material.Name;
        json["objectPath"] = material.Path;
        json["kind"] = kind;
        json["baseMaterial"] = material.BaseMaterialPath;
        json["blendMode"] = blendMode;
        json["baseBlendMode"] = material.BaseBlendMode.ToString();
        json["shadingModel"] = material.ShadingModel.ToString();
        json["translucencyLightingMode"] = material.TranslucencyLightingMode.ToString();
        json["twoSided"] = material.BaseMaterial?.GetOrDefault("TwoSided", false) ?? false;
        json["physMaterial"] = material.PhysMaterialName ?? string.Empty;

        // A material instance only lists the parameters it OVERRIDES. CP_M_BigBush overrides no
        // colours at all, so vectors[] is empty and the tint that makes the bush green lives as a
        // default on the base material - exactly the data whose absence produced a white bush.
        // DeepClone: the cached node is shared across every material instance of the same base, and a
        // JsonNode may only have one parent.
        if (BaseMaterialDefaults(material, baseDefaults) is { } defaults)
            json["baseMaterialDefaults"] = defaults.DeepClone();

        json["notes"] = ToolResults.ToJsonArray(notes);

        // Name the parameters an importer most often needs, so it never has to pattern-match itself.
        json["roles"] = new JsonObject
        {
            ["diffuse"] = FirstMatching(material, IsDiffuseParameter),
            ["normal"] = FirstMatching(material, IsNormalParameter),
            ["mask"] = FirstMatching(material, IsMaskParameter),
            ["lut"] = material.Textures
                .FirstOrDefault(texture => texture.Name.Contains("LUT", StringComparison.OrdinalIgnoreCase)
                                           || texture.Texture.Path.Contains("LUT", StringComparison.OrdinalIgnoreCase))?.Name
        };

        return json;
    }

    private static string? FirstMatching(ParameterCollection collection, Func<string, bool> predicate)
        => collection.Textures.FirstOrDefault(texture => predicate(texture.Name))?.Name;

    /// <summary>
    /// Colour/scalar/switch DEFAULTS declared on the base UMaterial, which no material instance
    /// repeats. Cached per base-material path because <c>GetParams</c> walks the expression graph.
    /// </summary>
    private static JsonObject? BaseMaterialDefaults(ExportMaterial material, Dictionary<string, JsonObject?> cache)
    {
        var key = material.BaseMaterialPath;
        if (string.IsNullOrEmpty(key) || material.BaseMaterial is null) return null;
        if (cache.TryGetValue(key, out var cached)) return cached;

        JsonObject? result = null;
        try
        {
            var parameters = new CMaterialParams2();
            material.BaseMaterial.GetParams(parameters, EMaterialDepth.AllLayers);

            if (parameters.Colors.Count > 0 || parameters.Scalars.Count > 0 || parameters.Switches.Count > 0)
            {
                // Fortnite masters are uber-shaders: a weapon base material exposes ~4,900 colour and
                // ~9,500 scalar nodes, almost all unused. Emitting them all produced a 2.2 MB manifest,
                // so the appearance-relevant names are ranked first and the tail is cut - the totals and
                // the truncated flag say so, and get_properties_json still has the full set.
                var colors = parameters.Colors
                    .OrderByDescending(pair => LooksLikeColorParameter(pair.Key))
                    .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Take(DefaultsCap)
                    .ToList();

                var scalars = parameters.Scalars
                    .OrderByDescending(pair => LooksLikeSurfaceParameter(pair.Key))
                    .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Take(DefaultsCap)
                    .ToList();

                result = new JsonObject
                {
                    ["path"] = key,
                    ["vectorCount"] = parameters.Colors.Count,
                    ["scalarCount"] = parameters.Scalars.Count,
                    ["truncated"] = parameters.Colors.Count > colors.Count || parameters.Scalars.Count > scalars.Count,
                    ["vectors"] = new JsonArray(colors
                        .Select(pair => (JsonNode) new JsonObject
                        {
                            ["name"] = pair.Key,
                            ["r"] = pair.Value.R,
                            ["g"] = pair.Value.G,
                            ["b"] = pair.Value.B,
                            ["a"] = pair.Value.A,
                            ["hex"] = SafeHex(pair.Value)
                        }).ToArray()),
                    ["scalars"] = new JsonArray(scalars
                        .Select(pair => (JsonNode) new JsonObject
                        {
                            ["name"] = pair.Key,
                            ["value"] = pair.Value
                        }).ToArray()),
                    ["switches"] = new JsonArray(parameters.Switches
                        .Take(DefaultsCap)
                        .Select(pair => (JsonNode) new JsonObject
                        {
                            ["name"] = pair.Key,
                            ["value"] = pair.Value
                        }).ToArray())
                };
            }
        }
        catch (Exception e)
        {
            Log.Debug("Base-material default lookup failed for {Path}: {Message}", key, e.Message);
        }

        cache[key] = result;
        return result;
    }

    private static JsonObject DescribeParameters(
        ParameterCollection collection, ExportDataMeta meta, string imageExtension,
        IReadOnlyDictionary<string, long> onDisk, HashSet<string> referenced)
        => new()
        {
            ["textures"] = new JsonArray(collection.Textures
                .Select(texture => DescribeTexture(texture.Name, texture.Texture, meta, imageExtension, onDisk, referenced)!)
                .Where(node => node is not null)
                .ToArray()),
            ["scalars"] = new JsonArray(collection.Scalars
                .Select(scalar => (JsonNode) new JsonObject
                {
                    ["name"] = scalar.Name,
                    ["value"] = scalar.Value
                }).ToArray()),
            ["vectors"] = new JsonArray(collection.Vectors
                .Select(vector => (JsonNode) new JsonObject
                {
                    ["name"] = vector.Name,
                    ["r"] = vector.Value.R,
                    ["g"] = vector.Value.G,
                    ["b"] = vector.Value.B,
                    ["a"] = vector.Value.A,
                    ["hex"] = SafeHex(vector.Value)
                }).ToArray()),
            ["switches"] = new JsonArray(collection.Switches
                .Select(item => (JsonNode) new JsonObject
                {
                    ["name"] = item.Name,
                    ["value"] = item.Value
                }).ToArray()),
            ["componentMasks"] = new JsonArray(collection.ComponentMasks
                .Select(item => (JsonNode) new JsonObject
                {
                    ["name"] = item.Name,
                    ["r"] = item.Value.R,
                    ["g"] = item.Value.G,
                    ["b"] = item.Value.B,
                    ["a"] = item.Value.A
                }).ToArray())
        };

    private static JsonNode? DescribeTexture(
        string? parameterName, ExportTexture? texture, ExportDataMeta meta, string imageExtension,
        IReadOnlyDictionary<string, long> onDisk, HashSet<string> referenced)
    {
        if (texture is null || string.IsNullOrEmpty(texture.Path)) return null;

        var path = ExportRunner.ToDiskPath(texture.Path, imageExtension, meta);
        referenced.Add(path);

        var bytes = onDisk.TryGetValue(path, out var known) ? known : SafeLength(path);
        var exists = bytes > 0 || File.Exists(path);

        return new JsonObject
        {
            ["parameter"] = parameterName,
            ["file"] = IoPath.GetFileName(path),
            ["path"] = path,
            ["objectPath"] = texture.Path,
            ["sRGB"] = texture.sRGB,
            ["compressionSettings"] = texture.CompressionSettings.ToString(),
            ["exists"] = exists,
            ["bytes"] = bytes,
            // 1x1 stubs (T_Fortnite_Default_S, FlatNormal, *_Swatch_*) land at ~100 bytes and are noise.
            ["placeholder"] = exists && bytes is > 0 and < 512
        };
    }

    // ---------------------------------------------------------------- classification

    /// <summary>
    /// Decides what a mesh in the export is FOR. Shadow proxies and collision hulls are real meshes
    /// with real files, so a consumer that just takes the first file (or the alphabetically first)
    /// silently imports the wrong geometry - CP_Apollo_BigBush writes BigBushShadowProxy.glb, which
    /// sorts before CP_BigBush.glb.
    /// </summary>
    internal static (string Role, int? LodIndex) ClassifyMesh(string name, string diskPath)
    {
        var stem = IoPath.GetFileNameWithoutExtension(diskPath);
        var candidates = new[] { name ?? string.Empty, stem ?? string.Empty };

        bool Contains(string needle) => candidates.Any(value => value.Contains(needle, StringComparison.OrdinalIgnoreCase));

        if (Contains("ShadowProxy") || Contains("Shadow_Proxy") || Contains("ProxyShadow") || Contains("ShadowMesh"))
            return ("shadow_proxy", null);

        if (Contains("UCX_") || Contains("Collision") || Contains("_Collider") || Contains("BlockingVolume"))
            return ("collision", null);

        // Outfits emit the master skeleton beside the parts; it carries no materials and is not geometry
        // anyone wants to import as the asset.
        if (candidates.Any(value => value.EndsWith("Skeleton", StringComparison.OrdinalIgnoreCase)))
            return ("skeleton", null);

        foreach (var value in candidates)
        {
            var match = LodSuffix.Match(value);
            if (!match.Success) continue;

            return ("lod", int.TryParse(match.Groups[1].Value, out var index) ? index : null);
        }

        return ("render", 0);
    }

    /// <summary>
    /// Ranks the meshes a consumer might import, best first: non-proxy, non-collision, non-LOD,
    /// preferring the one carrying the most material slots.
    /// <para>
    /// Only the head of this list is promoted to <c>primaryMesh</c>, and only when the asset has a
    /// single render mesh. An outfit exports head + body + hat and a prop can export several
    /// components; naming one of those "the" mesh would be a confident lie, so those assets get a
    /// null primaryMesh, the ranked candidate list and a multiple_render_meshes note instead.
    /// </para>
    /// </summary>
    private static List<MeshEntry> RankCandidates(List<MeshEntry> entries)
    {
        var pool = entries.Where(entry => entry.Role is "render").ToList();
        if (pool.Count == 0) pool = entries.Where(entry => entry.Role is "lod").ToList();
        if (pool.Count == 0) pool = entries;

        // Prefer something that actually landed on disk.
        var present = pool.Where(entry => File.Exists(entry.DiskPath)).ToList();
        if (present.Count > 0) pool = present;

        return pool
            .OrderByDescending(entry => entry.Mesh.Materials.Count)
            .ThenBy(entry => entry.Order)
            .ToList();
    }

    /// <summary>How many base-material defaults of each kind survive the uber-shader cull.</summary>
    private const int DefaultsCap = 48;

    private static readonly string[] ColorParameterHints =
        ["color", "colour", "tint", "albedo", "diffuse", "emissive", "base"];

    private static readonly string[] SurfaceParameterHints =
        ["roughness", "metallic", "specular", "opacity", "emissive", "brightness", "alpha", "subsurface", "ao"];

    private static bool LooksLikeColorParameter(string name)
        => ColorParameterHints.Any(hint => name.Contains(hint, StringComparison.OrdinalIgnoreCase));

    private static bool LooksLikeSurfaceParameter(string name)
        => SurfaceParameterHints.Any(hint => name.Contains(hint, StringComparison.OrdinalIgnoreCase));

    private static bool IsMaskParameter(string name)
        => MaskParameterNames.Contains(name, StringComparer.OrdinalIgnoreCase)
           || name.EndsWith("_M", StringComparison.OrdinalIgnoreCase)
           || name.Contains("Mask", StringComparison.OrdinalIgnoreCase);

    private static bool IsDiffuseParameter(string name)
        => DiffuseParameterNames.Contains(name, StringComparer.OrdinalIgnoreCase)
           || name.Contains("Diffuse", StringComparison.OrdinalIgnoreCase)
           || name.Contains("BaseColor", StringComparison.OrdinalIgnoreCase)
           || name.Contains("Albedo", StringComparison.OrdinalIgnoreCase);

    private static bool IsNormalParameter(string name)
        => NormalParameterNames.Contains(name, StringComparer.OrdinalIgnoreCase)
           || name.Contains("Normal", StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- helpers

    private static async Task<JsonNode?> ReadBoundsAsync(string objectPath, Func<string, Task<UObject?>> resolve)
    {
        try
        {
            if (string.IsNullOrEmpty(objectPath)) return null;

            var asset = await resolve(objectPath);
            var bounds = asset switch
            {
                UStaticMesh staticMesh => staticMesh.RenderData?.Bounds,
                USkeletalMesh skeletalMesh => skeletalMesh.ImportedBounds,
                _ => null
            };

            if (bounds is not { } value) return null;

            return new JsonObject
            {
                ["origin"] = Vector(value.Origin),
                ["boxExtent"] = Vector(value.BoxExtent),
                ["sizeX"] = value.BoxExtent.X * 2,
                ["sizeY"] = value.BoxExtent.Y * 2,
                ["sizeZ"] = value.BoxExtent.Z * 2,
                ["sphereRadius"] = value.SphereRadius,
                ["units"] = "cm"
            };
        }
        catch (Exception e)
        {
            Log.Debug("Bounds lookup failed for {Path}: {Message}", objectPath, e.Message);
            return null;
        }
    }

    private static string FileNameOf(string path) => IoPath.GetFileName(path) ?? path;

    private static JsonObject Vector(FVector vector) => new()
    {
        ["x"] = vector.X,
        ["y"] = vector.Y,
        ["z"] = vector.Z
    };

    private static string SafeHex(FLinearColor color)
    {
        try
        {
            return color.Hex;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static long SafeLength(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0;
        }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    private static string MeshExtension(EMeshFormat format) => format switch
    {
        EMeshFormat.ActorX => "psk",
        EMeshFormat.Gltf2 => "glb",
        EMeshFormat.USD => "usda",
        _ => "uemodel"
    };

    /// <summary>Mutable view of one mesh in the export while roles are being decided.</summary>
    private sealed class MeshEntry(int order, ExportMesh mesh, string diskPath, string role, int? lodIndex, string source)
    {
        public int Order { get; } = order;
        public ExportMesh Mesh { get; } = mesh;
        public string DiskPath { get; } = diskPath;
        public string Role { get; } = role;
        public int? LodIndex { get; } = lodIndex;
        public string Source { get; } = source;
        public bool IsPrimary { get; set; }
    }
}
