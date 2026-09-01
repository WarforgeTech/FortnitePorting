using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.i18N;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.Utils;
using FortnitePorting;
using FortnitePorting.CUE4Parse.Extensions;
using FortnitePorting.Exporting;
using FortnitePorting.Exporting.Models;
using FortnitePorting.Exporting.Styles;
using FortnitePorting.Exporting.Types;
using FortnitePorting.Mcp.Config;
using Serilog;
using EMeshFormat = CUE4Parse_Conversion.Options.EMeshFormat;
using IoPath = System.IO.Path;

namespace FortnitePorting.Mcp.Core;

/// <summary>Per-request export knobs. Mirrors the subset of ExportSettings the MCP tools expose.</summary>
public record ExportOptions
{
    /// <summary>When set, everything lands flat in this folder (ExportDataMeta.CustomPath).
    /// When null, exports mirror the game path under the configured ExportRoot.</summary>
    public string? OutputDir { get; init; }

    public EMeshFormat MeshFormat { get; init; } = EMeshFormat.UEFormat;
    public EImageFormat ImageFormat { get; init; } = EImageFormat.PNG;
    public ESoundFormat SoundFormat { get; init; } = ESoundFormat.WAV;
    public bool ExportMaterials { get; init; } = true;

    /// <summary>Override for the export type; when None the catalog decides.</summary>
    public EExportType ForceExportType { get; init; } = EExportType.None;
}

public record ExportedFile(string Path, long Bytes);

public record ExportedAsset
{
    public required string ObjectPath { get; init; }
    public required string DisplayName { get; init; }
    public required string ExportType { get; init; }
    public required string OutputRoot { get; init; }
    public List<ExportedFile> Files { get; init; } = [];
    public long Bytes => Files.Sum(file => file.Bytes);
}

public record ExportFailure(string ObjectPath, string Error);

public record ExportResult
{
    public required string OutputRoot { get; init; }
    public List<ExportedAsset> Assets { get; init; } = [];
    public List<ExportFailure> Failures { get; init; } = [];

    public int TotalFiles => Assets.Sum(asset => asset.Files.Count);
    public long TotalBytes => Assets.Sum(asset => asset.Bytes);
}

public record GalleryProp(string Name, string ObjectPath, UObject Object);

public record GalleryExportResult
{
    public required string GalleryObjectPath { get; init; }
    public required string GalleryName { get; init; }
    public required string OutputRoot { get; init; }

    /// <summary>Where each member prop came from: AssociatedPlaysetProps, PlaysetPropLevelSaveRecordCollection, or both.</summary>
    public required string MemberSource { get; init; }

    public int MembersFound { get; init; }
    public List<ExportedAsset> Props { get; init; } = [];
    public List<ExportFailure> Failures { get; init; } = [];

    public int TotalFiles => Props.Sum(prop => prop.Files.Count);
    public long TotalBytes => Props.Sum(prop => prop.Bytes);
}

/// <summary>
/// Headless export engine. Ports the orchestration half of the GUI's ExportService:
/// resolve object -> determine export type -> build an ExportDataMeta -> run one
/// ExportSession per asset -> await WaitForExports -> report what landed on disk.
/// Exports are deliberately serialized: the CUE4Parse conversion path keeps static
/// caches that are not safe to drive from several threads at once.
/// </summary>
public class ExportRunner(HeadlessLoader loader, HeadlessExportAssetProvider exportProvider, McpConfig config)
{
    public McpConfig Config { get; } = config;

    private readonly SemaphoreSlim _exportGate = new(1, 1);

    // ---------------------------------------------------------------- public API

    public async Task<ExportResult> ExportAssets(IEnumerable<string> objectPaths, ExportOptions opts, CancellationToken token = default)
    {
        var outputRoot = ResolveRoot(opts.OutputDir);
        Directory.CreateDirectory(outputRoot);

        var result = new ExportResult { OutputRoot = outputRoot };

        foreach (var objectPath in objectPaths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            token.ThrowIfCancellationRequested();

            UObject asset;
            try
            {
                asset = await loader.Provider.LoadPackageObjectAsync(ExportSession.FixPath(objectPath));
            }
            catch (Exception e)
            {
                result.Failures.Add(new ExportFailure(objectPath, $"Failed to load object: {e.Message}"));
                continue;
            }

            var single = await ExportOne(asset, objectPath, opts, token);
            if (single.Failure is not null) result.Failures.Add(single.Failure);
            if (single.Asset is not null) result.Assets.Add(single.Asset);
        }

        return result;
    }

    /// <summary>
    /// The flagship gallery path: instead of composing a prefab into one export, walk the
    /// playset's member props and export each one on its own into
    /// &lt;outputDir&gt;\&lt;GalleryName&gt;\&lt;PropName&gt;\ so every prop folder holds its own
    /// mesh plus its own textures.
    /// </summary>
    public async Task<GalleryExportResult> ExportGalleryAsIndividualAssets(
        string galleryObjectPath, string? outputDir, ExportOptions? options = null, CancellationToken token = default)
    {
        var opts = options ?? new ExportOptions();

        var gallery = await loader.Provider.LoadPackageObjectAsync(ExportSession.FixPath(galleryObjectPath));
        var galleryName = SanitizeFileName(DisplayNameOf(gallery) ?? gallery.Name);

        var root = IoPath.Combine(ResolveRoot(outputDir), galleryName);
        Directory.CreateDirectory(root);

        var (members, source) = ResolveGalleryMembers(gallery);

        var result = new GalleryExportResult
        {
            GalleryObjectPath = gallery.GetPathName(),
            GalleryName = galleryName,
            OutputRoot = root,
            MemberSource = source,
            MembersFound = members.Count
        };

        var usedFolders = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in members)
        {
            token.ThrowIfCancellationRequested();

            var folderName = SanitizeFileName(member.Name);
            if (usedFolders.TryGetValue(folderName, out var seen))
            {
                usedFolders[folderName] = seen + 1;
                folderName = $"{folderName}_{seen + 1}";
            }
            else
            {
                usedFolders[folderName] = 1;
            }

            var propDir = IoPath.Combine(root, folderName);
            Directory.CreateDirectory(propDir);

            var propOptions = opts with { OutputDir = propDir };
            var single = await ExportOne(member.Object, member.ObjectPath, propOptions, token);

            if (single.Failure is not null) result.Failures.Add(single.Failure);
            if (single.Asset is not null) result.Props.Add(single.Asset);

            // Do not leave a folder behind for a prop that produced nothing.
            try
            {
                if (Directory.Exists(propDir) && !Directory.EnumerateFileSystemEntries(propDir).Any())
                    Directory.Delete(propDir);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return result;
    }

    /// <summary>
    /// Resolves the member props of a playset/gallery.
    /// <para>
    /// AssociatedPlaysetProps is the authoritative flat list of prop item definitions and is
    /// used whenever it is populated. PlaysetPropLevelSaveRecordCollection - what the prefab
    /// exporter walks - is only a fallback: its Items[].LevelSaveRecord usually resolves to the
    /// <c>ULevelSaveRecord</c> sub-object (named "PlaysetPropActorSaveRecord") rather than the
    /// owning FortPlaysetPropItemDefinition, so entries are lifted to their outer before use.
    /// Merging both sources unconditionally double-counts every prop, which is why it is a
    /// fallback and not a union.
    /// </para>
    /// </summary>
    public (List<GalleryProp> Members, string Source) ResolveGalleryMembers(UObject gallery)
    {
        var members = new List<GalleryProp>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(UObject? prop)
        {
            if (prop is null) return;

            var path = prop.GetPathName();
            if (!seen.Add(path)) return;

            members.Add(new GalleryProp(DisplayNameOf(prop) ?? prop.Name, path, prop));
        }

        // Source 1: AssociatedPlaysetProps (FSoftObjectPath[]) - see FortnitePorting AssetInfo.cs.
        foreach (var softPath in gallery.GetOrDefault<FSoftObjectPath[]>("AssociatedPlaysetProps", []))
        {
            try
            {
                if (softPath.TryLoad(out var prop)) Add(prop);
            }
            catch (Exception e)
            {
                Log.Debug("AssociatedPlaysetProps entry failed to load: {Message}", e.Message);
            }
        }

        if (members.Count > 0) return (members, $"AssociatedPlaysetProps ({members.Count})");

        // Source 2 (fallback): PlaysetPropLevelSaveRecordCollection -> Items[] -> LevelSaveRecord.
        try
        {
            var collectionLazy = gallery.GetOrDefault<FPackageIndex?>("PlaysetPropLevelSaveRecordCollection");
            if (collectionLazy is { IsNull: false } && collectionLazy.TryLoad(out var collection) && collection is not null)
            {
                foreach (var item in collection.GetOrDefault<FStructFallback[]>("Items", []))
                {
                    var record = item.GetOrDefault<UObject?>("LevelSaveRecord");
                    if (record is null) continue;

                    // Lift a raw save record to the prop definition that owns it.
                    if (record.ExportType.Equals("LevelSaveRecord", StringComparison.Ordinal))
                        record = record.Outer?.Load() ?? record;

                    Add(record);
                }
            }
        }
        catch (Exception e)
        {
            Log.Debug("PlaysetPropLevelSaveRecordCollection walk failed: {Message}", e.Message);
        }

        return members.Count > 0
            ? (members, $"PlaysetPropLevelSaveRecordCollection ({members.Count}, AssociatedPlaysetProps was empty)")
            : (members, "none");
    }

    /// <summary>Registry search over FortPlaysetItemDefinition rows by asset name or display name.</summary>
    public async Task<List<(string ObjectPath, string AssetName, string DisplayName)>> FindGalleries(string query, int limit = 25)
    {
        var matches = new List<(string, string, string)>();
        var rows = loader.AssetRegistry
            .Where(data => data.AssetClass.Text.Equals("FortPlaysetItemDefinition", StringComparison.Ordinal))
            .ToList();

        // Cheap pass first: asset-name substring, no package loads at all.
        var nameHits = rows
            .Where(data => data.AssetName.Text.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // If the cheap pass found nothing, fall back to loading each row for its display name.
        var candidates = nameHits.Count > 0 ? nameHits : rows;

        foreach (var data in candidates)
        {
            if (matches.Count >= limit) break;

            var assetName = data.AssetName.Text;
            var nameMatches = assetName.Contains(query, StringComparison.OrdinalIgnoreCase);

            var asset = await loader.Provider.SafeLoadPackageObjectAsync(data.ObjectPath);
            var displayName = asset is null ? null : DisplayNameOf(asset);

            var displayMatches = displayName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false;
            if (!nameMatches && !displayMatches) continue;

            matches.Add((data.ObjectPath, assetName, displayName ?? assetName));
        }

        return matches;
    }

    public EExportType DetermineExportType(UObject asset) => CategoryCatalog.DetermineExportType(asset);

    public static string? DisplayNameOf(UObject asset)
        => asset.GetAnyOrDefault<FText?>("DisplayName", "ItemName")?.Text;

    // ---------------------------------------------------------------- internals

    private record SingleExport(ExportedAsset? Asset, ExportFailure? Failure);

    private async Task<SingleExport> ExportOne(UObject asset, string requestedPath, ExportOptions opts, CancellationToken token)
    {
        var exportType = opts.ForceExportType is not EExportType.None
            ? opts.ForceExportType
            : CategoryCatalog.DetermineExportType(asset);

        if (exportType is EExportType.None)
            return new SingleExport(null, new ExportFailure(requestedPath,
                $"Unsupported asset: no export type is defined for class '{asset.ExportType}'."));

        if (exportType is EExportType.World)
            return new SingleExport(null, new ExportFailure(requestedPath,
                "World/level exports are not yet supported by the MCP server."));

        var outputRoot = ResolveRoot(opts.OutputDir);
        var displayName = DisplayNameOf(asset) ?? asset.Name;

        await _exportGate.WaitAsync(token);
        try
        {
            var before = SnapshotFiles(outputRoot);
            var directoriesBefore = SnapshotDirectories(outputRoot);

            using var meta = BuildMeta(opts, outputRoot);
            var session = new ExportSession(meta);

            BaseExport export;
            try
            {
                export = session.CreateExport(displayName, asset, exportType, Array.Empty<ExportStyleBase>());
            }
            catch (Exception e)
            {
                return new SingleExport(null, new ExportFailure(requestedPath,
                    $"Export of type {exportType} failed: {Flatten(e)}"));
            }

            try
            {
                await export.WaitForExports();
            }
            catch (Exception e)
            {
                return new SingleExport(null, new ExportFailure(requestedPath,
                    $"Export of type {exportType} did not complete: {Flatten(e)}"));
            }

            var files = CollectFiles(outputRoot, before, export, meta);

            // ExportContext.BuildExportPath calls Directory.CreateDirectory on the path with the
            // last "/" segment stripped. In flat (CustomPath) mode there is no "/", so it creates
            // an empty folder named after every asset it writes. Sweep those away.
            if (opts.OutputDir is not null) PruneNewEmptyDirectories(outputRoot, directoriesBefore);

            return new SingleExport(new ExportedAsset
            {
                ObjectPath = asset.GetPathName(),
                DisplayName = displayName,
                ExportType = exportType.ToString(),
                OutputRoot = outputRoot,
                Files = files
            }, null);
        }
        catch (Exception e)
        {
            return new SingleExport(null, new ExportFailure(requestedPath, Flatten(e)));
        }
        finally
        {
            _exportGate.Release();
        }
    }

    private ExportDataMeta BuildMeta(ExportOptions opts, string outputRoot) => new()
    {
        AssetsRoot = opts.OutputDir ?? Config.ExportFolder.FullName,
        CustomPath = opts.OutputDir,
        ExportLocation = opts.OutputDir is null ? EExportLocation.AssetsFolder : EExportLocation.CustomFolder,
        Provider = exportProvider,
        Settings = new ExportSettings
        {
            MeshFormat = opts.MeshFormat,
            ImageFormat = opts.ImageFormat,
            SoundFormat = opts.SoundFormat,
            ExportMaterials = opts.ExportMaterials
        }
    };

    private string ResolveRoot(string? outputDir)
        => string.IsNullOrWhiteSpace(outputDir) ? Config.ExportFolder.FullName : IoPath.GetFullPath(outputDir);

    // ---------------------------------------------------------------- file capture

    private static Dictionary<string, (long Length, DateTime Written)> SnapshotFiles(string root)
    {
        var snapshot = new Dictionary<string, (long, DateTime)>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root)) return snapshot;

        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            try
            {
                var info = new FileInfo(path);
                snapshot[path] = (info.Length, info.LastWriteTimeUtc);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return snapshot;
    }

    private static HashSet<string> SnapshotDirectories(string root)
    {
        var snapshot = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root)) return snapshot;

        foreach (var path in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            snapshot.Add(path);

        return snapshot;
    }

    private static void PruneNewEmptyDirectories(string root, HashSet<string> before)
    {
        if (!Directory.Exists(root)) return;

        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length)
                     .ToList())
        {
            if (before.Contains(directory)) continue;

            try
            {
                if (Directory.EnumerateFileSystemEntries(directory).Any()) continue;
                Directory.Delete(directory);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Filesystem diff (files created or rewritten during this export) unioned with the
    /// paths the export model references. The union matters because ExportContext skips
    /// assets that already exist on disk: without it, re-exporting into a folder that
    /// already holds the output would report zero files.
    /// </summary>
    private static List<ExportedFile> CollectFiles(
        string root, Dictionary<string, (long Length, DateTime Written)> before, BaseExport export, ExportDataMeta meta)
    {
        var found = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in Directory.Exists(root) ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories) : [])
        {
            try
            {
                var info = new FileInfo(path);
                if (before.TryGetValue(path, out var previous) && previous.Length == info.Length && previous.Written == info.LastWriteTimeUtc)
                    continue;

                found[path] = info.Length;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        foreach (var path in ResolveModelPaths(export, meta))
        {
            if (found.ContainsKey(path)) continue;

            try
            {
                var info = new FileInfo(path);
                if (info.Exists) found[path] = info.Length;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return found
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new ExportedFile(pair.Key, pair.Value))
            .ToList();
    }

    /// <summary>
    /// Reconstructs on-disk paths from the object paths the export model carries.
    /// ExportContext.BuildExportPath uses the package path under AssetsRoot, or just the
    /// object name when CustomPath is set - mirrored here.
    /// </summary>
    private static IEnumerable<string> ResolveModelPaths(BaseExport export, ExportDataMeta meta)
    {
        if (export is not MeshExport mesh) yield break;

        var meshExtension = meta.Settings.MeshFormat switch
        {
            EMeshFormat.ActorX => "psk",
            EMeshFormat.Gltf2 => "glb",
            EMeshFormat.USD => "usda",
            _ => "uemodel"
        };

        var imageExtension = meta.Settings.ImageFormat is EImageFormat.TGA ? "tga" : "png";

        foreach (var exportMesh in EnumerateMeshes(mesh))
        {
            if (!string.IsNullOrEmpty(exportMesh.Path))
                yield return ToDiskPath(exportMesh.Path, meshExtension, meta);

            foreach (var material in exportMesh.Materials.Concat(exportMesh.OverrideMaterials))
            foreach (var texture in material.Textures)
            {
                if (string.IsNullOrEmpty(texture.Texture.Path)) continue;
                yield return ToDiskPath(texture.Texture.Path, imageExtension, meta);
            }
        }
    }

    private static IEnumerable<ExportMesh> EnumerateMeshes(MeshExport export)
    {
        var queue = new Queue<ExportMesh>(export.Meshes.Concat(export.OverrideMeshes));
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            yield return current;

            foreach (var child in current.Children) queue.Enqueue(child);
        }
    }

    private static string ToDiskPath(string objectPath, string extension, ExportDataMeta meta)
    {
        string relative;
        if (meta.CustomPath is not null)
        {
            // Flat layout: BuildExportPath is handed obj.Name only.
            relative = objectPath.Contains('.') ? objectPath.SubstringAfterLast(".") : objectPath.SubstringAfterLast("/");
        }
        else
        {
            relative = objectPath.SubstringBeforeLast(".");
            if (relative.StartsWith('/')) relative = relative[1..];
        }

        var root = meta.CustomPath ?? meta.AssetsRoot;
        return IoPath.GetFullPath(IoPath.Combine(root, $"{relative}.{extension}").Replace('/', IoPath.DirectorySeparatorChar));
    }

    // ---------------------------------------------------------------- helpers

    public static string SanitizeFileName(string name)
    {
        var invalid = IoPath.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return string.IsNullOrEmpty(cleaned) ? "Unnamed" : cleaned;
    }

    private static string Flatten(Exception e)
    {
        var messages = new List<string>();
        for (var current = e; current is not null; current = current.InnerException)
            messages.Add($"{current.GetType().Name}: {current.Message}");

        return string.Join(" -> ", messages);
    }
}
