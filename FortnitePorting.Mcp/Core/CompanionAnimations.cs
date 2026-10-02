using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Objects.Properties;
using CUE4Parse.UE4.Objects.Engine.Animation;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.Utils;
using FortnitePorting;
using FortnitePorting.Exporting;
using FortnitePorting.Exporting.Models;
using FortnitePorting.Exporting.Types;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using EMeshFormat = CUE4Parse_Conversion.Options.EMeshFormat;
using IoPath = System.IO.Path;

namespace FortnitePorting.Mcp.Core;

// =====================================================================================================
// Sidekick (companion) animation discovery + export.
//
// A CosmeticCompanionItemDefinition never names its animations directly. They hang off a small object
// graph that is read GENERICALLY here (UObject property reflection - FPackageIndex / FSoftObjectPath /
// FStructFallback / FInstancedStruct / arrays / maps - no per-class schemas):
//
//   AnimTableAsset        ProxyTable  -> RuntimeValues[].Value{Asset|Chooser} -> sequences / ChooserTables
//   ReactionChooserProxy  ProxyTable  -> ChooserTable -> ...
//   InteractionSceneAsset ContextualAnimSceneAsset -> Sections[].AnimSets[].Tracks[]{Animation, Role}
//   ItemVariants[]        ContextualAnimSceneEmote (CAS: companion montage + PLAYER montage),
//                         AdditivePoseAnim (quirk poses), ...
//   MainAnimClass         AnimBlueprintGeneratedClass -> class + CDO properties (anim-node assets, tables)
//
// plus a folder sweep of <companion>/Animation/** and <shared rig family>/Animation/**, the family being
// derived from MainSkeletalMesh ("FullSnore_Quadruped_INVIS" -> FullSnore). ChooserTables resolve their
// rows through ProxyAssets (keys only, no value) bound to the AnimTable, so walking the PRT is what
// actually yields leaf animations; the CHTs mostly confirm which proxies are used.
// =====================================================================================================

/// <summary>One animation asset reachable from a companion, before it is exported.</summary>
public sealed class CompanionAnimRef
{
    /// <summary>Canonical "/Mount/Path/Name.Name" (or the VFS path for an unloaded sweep hit).</summary>
    public required string ObjectPath { get; set; }

    public required string Name { get; init; }

    /// <summary>What loads it: an object path, or a VFS file path for folder-sweep hits.</summary>
    public required string LoadPath { get; init; }

    /// <summary>Class name from the package header (AnimSequence, AnimMontage, BlendSpace, ...). Null when unresolved.</summary>
    public string? ClassName { get; set; }

    /// <summary>animTable | reactionChooser | cas | variant | animBlueprint | folderSweep | blendSpaceSample</summary>
    public required string Source { get; init; }

    /// <summary>Container chain it was reached through, e.g. "PRT_BandSoda_AnimTable > CHT_Base_Stops".</summary>
    public string? Via { get; init; }

    /// <summary>ContextualAnim role of the track that referenced it (CAS only).</summary>
    public string? CasRole { get; init; }

    public bool PlayerSide { get; set; }

    public string DedupeKey => CompanionAnimations.DedupeKey(ObjectPath);
}

public sealed record CompanionAnimSource(string Property, string ObjectPath, string? ClassName, string Source);

/// <summary>Everything discovery learned about a companion's animation set.</summary>
public sealed class CompanionAnimDiscovery
{
    public string? CompanionName { get; set; }
    public string? FamilyName { get; set; }
    public string? MainSkeletalMesh { get; set; }
    public string? MainAnimClass { get; set; }
    public List<CompanionAnimSource> Sources { get; } = [];
    public List<CompanionAnimRef> Animations { get; } = [];
    public List<string> ContainersVisited { get; } = [];
    public List<string> RetargetAssets { get; } = [];
    public List<string> SweepPatterns { get; } = [];
    public int FolderSweepFiles { get; set; }
    public List<string> Notes { get; } = [];
    public double Seconds { get; set; }

    public int GraphReferenceCount => Animations.Count(anim => anim.Source is not "folderSweep");
}

/// <summary>One exported (or failed) animation, as it lands in the manifest.</summary>
public sealed class CompanionAnimResult
{
    public required CompanionAnimRef Ref { get; init; }
    public string Kind { get; set; } = "sequence";
    public string Group { get; set; } = "Other";
    public string? File { get; set; }
    public List<string> Files { get; } = [];
    public string? Skeleton { get; set; }
    public string? SkeletonFile { get; set; }
    public string? SkeletonError { get; set; }
    public double? LengthSeconds { get; set; }
    public int? FrameCount { get; set; }
    public JsonObject? Additive { get; set; }
    public JsonArray? Sections { get; set; }
    public JsonArray? Samples { get; set; }
    public List<string> ExtraFiles { get; } = [];
    public string? Error { get; set; }
    public bool Ok => Error is null;
}

public sealed class CompanionSkeletonInfo
{
    public required string ObjectPath { get; init; }
    public required string Name { get; init; }
    public string? File { get; set; }
    public int BoneCount { get; set; }
    public HashSet<string> Bones { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int UsedBy { get; set; }
}

/// <summary>Result of the animation phase of one companion export.</summary>
public sealed class CompanionAnimExport
{
    public required CompanionAnimDiscovery Discovery { get; init; }
    public required string AnimationsRoot { get; init; }
    public List<CompanionAnimResult> Results { get; } = [];
    public Dictionary<string, CompanionSkeletonInfo> Skeletons { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? CompanionMeshSkeleton { get; set; }
    public string? BaseRigSkeleton { get; set; }
    public List<ExportedFile> Files { get; } = [];
    public double Seconds { get; set; }

    /// <summary>Format the animation phase actually wrote (Gltf2 falls back to UEFormat - see Notes).</summary>
    public string AnimationFormat { get; set; } = "UEFormat";

    public List<string> Notes { get; } = [];

    public int Exported => Results.Count(result => result.Ok && result.Kind is not "blendspace");
    public int BlendSpaces => Results.Count(result => result.Ok && result.Kind is "blendspace");
    public IEnumerable<CompanionAnimResult> Failed => Results.Where(result => !result.Ok);
    public long AnimationBytes => Files.Where(file => file.Path.EndsWith(".ueanim", StringComparison.OrdinalIgnoreCase)
                                                      || file.Path.EndsWith(".psa", StringComparison.OrdinalIgnoreCase))
        .Sum(file => file.Bytes);
}

public static class CompanionAnimations
{
    private const int MaxDepth = 8;
    private const int MaxContainers = 400;

    private static readonly HashSet<string> LeafClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "AnimSequence", "AnimMontage", "AnimComposite", "AnimStreamable",
        "BlendSpace", "BlendSpace1D", "AimOffsetBlendSpace", "AimOffsetBlendSpace1D"
    };

    /// <summary>Classes whose properties are walked for further references.</summary>
    private static bool IsContainerClass(string? className) => className is not null && (
        className.Equals("ProxyTable", StringComparison.OrdinalIgnoreCase)
        || className.Contains("ChooserTable", StringComparison.OrdinalIgnoreCase)
        || className.Equals("ContextualAnimSceneAsset", StringComparison.OrdinalIgnoreCase)
        || className.Contains("PoseSearchDatabase", StringComparison.OrdinalIgnoreCase)
        || className.Equals("AnimBlueprintGeneratedClass", StringComparison.OrdinalIgnoreCase));

    /// <summary>Item-definition properties that root the walk, and the source tag their leaves carry.</summary>
    private static readonly (string Property, string Source)[] RootProperties =
    [
        ("AnimTableAsset", "animTable"),
        ("ReactionChooserProxy", "reactionChooser"),
        ("InteractionSceneAsset", "cas"),
        ("MainAnimClass", "animBlueprint")
    ];

    /// <summary>True when the item looks like it carries a companion animation graph.</summary>
    public static bool HasAnimationGraph(UObject asset)
        => RootProperties.Any(root => asset.Properties.Any(tag => tag.Name.Text.Equals(root.Property, StringComparison.Ordinal)))
           || asset.ExportType.Equals("CosmeticCompanionItemDefinition", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ discovery

    /// <summary>
    /// Walks the companion's animation graph. <paramref name="classifySweep"/> = false keeps an info call
    /// cheap: folder-sweep files are only counted (by path), never opened.
    /// </summary>
    public static CompanionAnimDiscovery Discover(UObject itemDef, AbstractVfsFileProvider provider, bool includeFolderSweep, bool classifySweep)
    {
        var clock = Stopwatch.StartNew();
        var discovery = new CompanionAnimDiscovery
        {
            CompanionName = CompanionNameOf(itemDef)
        };

        var walker = new GraphWalker(provider, discovery);

        // MainSkeletalMesh names the shared base rig ("FullSnore_Quadruped_INVIS" -> family FullSnore).
        if (TryGetSoft(itemDef, "MainSkeletalMesh") is { } mainMesh)
        {
            discovery.MainSkeletalMesh = mainMesh;
            var meshName = mainMesh.SubstringAfterLast('/').SubstringBefore('.');
            discovery.FamilyName = meshName.Contains('_') ? meshName.SubstringBefore('_') : meshName;
        }

        if (TryGetSoft(itemDef, "MainAnimClass") is { } animClass) discovery.MainAnimClass = animClass;

        foreach (var (property, source) in RootProperties)
        {
            var tag = itemDef.Properties.FirstOrDefault(item => item.Name.Text.Equals(property, StringComparison.Ordinal));
            if (tag?.Tag is null) continue;

            var refs = new List<GraphRef>();
            CollectRefs(tag.Tag, refs, role: null);
            foreach (var reference in refs)
            {
                var className = walker.ClassOf(reference);
                discovery.Sources.Add(new CompanionAnimSource(property, reference.Path, className, source));
                walker.Visit(reference, source, depth: 0, via: null);
            }
        }

        // Variant channels: ContextualAnimSceneEmote (CAS emotes), AdditivePoseAnim (quirk poses), ...
        foreach (var variantIndex in itemDef.GetOrDefault<FPackageIndex[]>("ItemVariants", []))
        {
            UObject? variant = null;
            try { variant = variantIndex.Load(); }
            catch (Exception e) { Log.Debug("Variant {Name} failed to load: {Message}", variantIndex.Name, e.Message); }
            if (variant is null) continue;

            var refs = new List<GraphRef>();
            foreach (var tag in variant.Properties) CollectRefs(tag.Tag, refs, role: null);

            foreach (var reference in refs)
            {
                var className = walker.ClassOf(reference);
                if (className is null) continue;

                var isCas = className.Equals("ContextualAnimSceneAsset", StringComparison.OrdinalIgnoreCase);
                if (!isCas && !LeafClasses.Contains(className)) continue;

                var source = isCas ? "cas" : "variant";
                discovery.Sources.Add(new CompanionAnimSource($"{variant.ExportType}", reference.Path, className, source));
                walker.Visit(reference, source, depth: 0, via: variant.Name);
            }
        }

        if (includeFolderSweep) Sweep(provider, discovery, walker, classifySweep);

        FindRetargetAssets(provider, discovery);

        discovery.Seconds = clock.Elapsed.TotalSeconds;
        return discovery;
    }

    private static string? CompanionNameOf(UObject itemDef)
    {
        // CompanionTypeTag "Companion.Type.BandSoda" is the authored name; the asset name is the fallback.
        try
        {
            var tag = itemDef.GetOrDefault<FStructFallback?>("CompanionTypeTag")?.GetOrDefault<FName>("TagName").Text;
            if (!string.IsNullOrEmpty(tag) && tag != "None") return tag.SubstringAfterLast('.');
        }
        catch
        {
            // fall through
        }

        var name = itemDef.Name;
        return name.StartsWith("Companion_", StringComparison.OrdinalIgnoreCase) ? name["Companion_".Length..] : name;
    }

    private static string? TryGetSoft(UObject asset, string property)
    {
        try
        {
            var path = asset.GetOrDefault<FSoftObjectPath>(property).AssetPathName.Text;
            return string.IsNullOrEmpty(path) || path == "None" ? null : path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Folder sweep: every package under "/&lt;Companion&gt;/Animation/" and "/&lt;Family&gt;/Animation/" anywhere in
    /// the VFS (which also catches CreativeCompanion's "/&lt;Family&gt;/Animation/UEFN/" creative reactions).
    /// The family's own Emotes folder is skipped when the family is a different companion: those are that
    /// companion's emotes, not shared rig animation.
    /// </summary>
    private static void Sweep(AbstractVfsFileProvider provider, CompanionAnimDiscovery discovery, GraphWalker walker, bool classify)
    {
        var companion = discovery.CompanionName;
        var family = discovery.FamilyName;

        var patterns = new List<(string Pattern, bool IsFamily)>();
        if (!string.IsNullOrEmpty(companion)) patterns.Add(($"/{companion}/Animation/", false));
        if (!string.IsNullOrEmpty(family) && !family.Equals(companion, StringComparison.OrdinalIgnoreCase))
            patterns.Add(($"/{family}/Animation/", true));

        discovery.SweepPatterns.AddRange(patterns.Select(pattern => pattern.Pattern));

        foreach (var (path, _) in provider.Files)
        {
            if (!path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) continue;

            var match = patterns.FirstOrDefault(pattern => path.Contains(pattern.Pattern, StringComparison.OrdinalIgnoreCase));
            if (match.Pattern is null) continue;
            if (match.IsFamily && path.Contains("/Animation/Emotes/", StringComparison.OrdinalIgnoreCase)) continue;

            var objectPath = VfsToObjectPath(path);
            if (walker.Seen(objectPath)) continue;

            discovery.FolderSweepFiles++;
            if (!classify) continue;

            var name = path.SubstringAfterLast('/').SubstringBeforeLast('.');
            string? className = null;
            try
            {
                if (provider.TryLoadPackage(path, out var package))
                    className = ExportClass(package, name);
            }
            catch (Exception e)
            {
                Log.Debug("Sweep header load failed for {Path}: {Message}", path, e.Message);
            }

            if (className is null || !LeafClasses.Contains(className)) continue;

            walker.AddLeaf(new CompanionAnimRef
            {
                ObjectPath = objectPath,
                Name = name,
                LoadPath = path.SubstringBeforeLast('.'),
                ClassName = className,
                Source = "folderSweep",
                Via = match.Pattern
            });
        }
    }

    /// <summary>RTG_*/IK_* assets in the companion's own folder: how UE drives the visible mesh from the base rig.</summary>
    private static void FindRetargetAssets(AbstractVfsFileProvider provider, CompanionAnimDiscovery discovery)
    {
        if (string.IsNullOrEmpty(discovery.CompanionName)) return;

        var pattern = $"/{discovery.CompanionName}/";
        foreach (var (path, _) in provider.Files)
        {
            if (!path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) continue;
            if (!path.Contains(pattern, StringComparison.OrdinalIgnoreCase)) continue;
            if (!path.Contains("CosmeticCompanions", StringComparison.OrdinalIgnoreCase)) continue;

            var name = path.SubstringAfterLast('/');
            if (!name.StartsWith("RTG_", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("IK_", StringComparison.OrdinalIgnoreCase))
                continue;

            discovery.RetargetAssets.Add(VfsToObjectPath(path));
        }

        var distinct = discovery.RetargetAssets.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        discovery.RetargetAssets.Clear();
        discovery.RetargetAssets.AddRange(distinct);
    }

    /// <summary>"FortniteGame/Plugins/GameFeatures/X/Y/Content/A/B.uasset" -> "/Y/A/B.B"; FortniteGame/Content -> /Game.</summary>
    public static string VfsToObjectPath(string vfsPath)
    {
        var path = vfsPath.Replace('\\', '/');
        if (path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
            path = path.SubstringBeforeLast('.');

        var name = path.SubstringAfterLast('/');
        var contentIndex = path.IndexOf("/Content/", StringComparison.OrdinalIgnoreCase);
        if (contentIndex < 0) return $"/{path}.{name}";

        var mountDir = path[..contentIndex];
        var tail = path[(contentIndex + "/Content/".Length)..];
        var mount = mountDir.Contains("/Plugins/", StringComparison.OrdinalIgnoreCase) ? mountDir.SubstringAfterLast('/') : "Game";
        return $"/{mount}/{tail}.{name}";
    }

    /// <summary>Mount-relative, case-folded package path: the identity used to dedupe graph refs against sweep hits.</summary>
    public static string DedupeKey(string objectPath)
    {
        var path = objectPath.Replace('\\', '/');
        if (path.Contains("/Content/", StringComparison.OrdinalIgnoreCase)) path = VfsToObjectPath(path);
        var package = path.Contains('.') ? path.SubstringBeforeLast('.') : path;
        return package.TrimStart('/').ToLowerInvariant();
    }

    private static string? ExportClass(IPackage package, string objectName)
    {
        var index = package.GetExportIndex(objectName, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return null;
        return new FPackageIndex(package, index + 1).ResolvedObject?.Class?.Name.Text;
    }

    // ------------------------------------------------------------------ generic reference collection

    internal readonly record struct GraphRef(string Path, FPackageIndex? Index, FSoftObjectPath? Soft, string? Role);

    /// <summary>
    /// Recursively collects every object reference inside a property value. A struct that carries a
    /// "Role" name (ContextualAnim tracks) passes it down so CAS leaves know which character they drive.
    /// </summary>
    internal static void CollectRefs(object? value, List<GraphRef> refs, string? role, int depth = 0)
    {
        if (value is null || depth > 64) return;

        switch (value)
        {
            case FPropertyTag tag:
                CollectRefs(tag.Tag, refs, role, depth + 1);
                break;
            case FPropertyTagType tagType:
                CollectRefs(tagType.GenericValue, refs, role, depth + 1);
                break;
            case FPackageIndex index:
            {
                if (index.IsNull) break;
                var resolved = index.ResolvedObject;
                if (resolved is null) break;
                refs.Add(new GraphRef(resolved.GetPathName(), index, null, role));
                break;
            }
            case FSoftObjectPath soft:
            {
                var path = soft.AssetPathName.Text;
                if (string.IsNullOrEmpty(path) || path == "None") break;
                refs.Add(new GraphRef(path, null, soft, role));
                break;
            }
            case UScriptArray array:
                foreach (var item in array.Properties) CollectRefs(item, refs, role, depth + 1);
                break;
            case UScriptSet set:
                foreach (var item in set.Properties) CollectRefs(item, refs, role, depth + 1);
                break;
            case UScriptMap map:
                foreach (var (key, item) in map.Properties)
                {
                    CollectRefs(key, refs, role, depth + 1);
                    CollectRefs(item, refs, role, depth + 1);
                }
                break;
            case FScriptStruct script:
                CollectRefs(script.StructType, refs, role, depth + 1);
                break;
            case FInstancedStruct instanced:
                CollectRefs(instanced.ScriptStruct?.StructType, refs, role, depth + 1);
                break;
            case FStructFallback fallback:
            {
                var structRole = role;
                var roleTag = fallback.Properties.FirstOrDefault(tag => tag.Name.Text.Equals("Role", StringComparison.Ordinal));
                if (roleTag?.Tag?.GenericValue is FName roleName && !roleName.IsNone) structRole = roleName.Text;

                foreach (var tag in fallback.Properties) CollectRefs(tag.Tag, refs, structRole, depth + 1);
                break;
            }
        }
    }

    private sealed class GraphWalker(AbstractVfsFileProvider provider, CompanionAnimDiscovery discovery)
    {
        private readonly HashSet<string> _visitedContainers = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CompanionAnimRef> _leaves = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string?> _classCache = new(StringComparer.OrdinalIgnoreCase);

        public bool Seen(string objectPath) => _leaves.ContainsKey(DedupeKey(objectPath));

        public void AddLeaf(CompanionAnimRef leaf)
        {
            if (_leaves.TryAdd(leaf.DedupeKey, leaf)) discovery.Animations.Add(leaf);
        }

        public string? ClassOf(GraphRef reference)
        {
            if (_classCache.TryGetValue(reference.Path, out var cached)) return cached;

            string? className = null;
            try
            {
                if (reference.Index is { } index)
                {
                    className = index.ResolvedObject?.Class?.Name.Text;
                }
                else
                {
                    var package = reference.Path.SubstringBeforeLast('.');
                    var objectName = reference.Path.Contains('.') ? reference.Path.SubstringAfterLast('.') : package.SubstringAfterLast('/');
                    if (provider.TryLoadPackage(package, out var loaded))
                        className = ExportClass(loaded, objectName);
                }
            }
            catch (Exception e)
            {
                Log.Debug("Class lookup failed for {Path}: {Message}", reference.Path, e.Message);
            }

            _classCache[reference.Path] = className;
            return className;
        }

        private UObject? Load(GraphRef reference)
        {
            try
            {
                if (reference.Index is { } index && index.TryLoad(out var fromIndex)) return fromIndex;
                if (provider.TryLoadPackageObject(reference.Path, out var fromPath)) return fromPath;
            }
            catch (Exception e)
            {
                Log.Debug("Load failed for {Path}: {Message}", reference.Path, e.Message);
            }

            return null;
        }

        public void Visit(GraphRef reference, string source, int depth, string? via, string? primaryRole = null)
        {
            var className = ClassOf(reference);
            if (className is null) return;

            if (LeafClasses.Contains(className))
            {
                var path = reference.Path;
                var name = path.Contains('.') ? path.SubstringAfterLast('.') : path.SubstringAfterLast('/');
                var playerSide = reference.Role is not null && primaryRole is not null
                                 && !reference.Role.Equals(primaryRole, StringComparison.OrdinalIgnoreCase);
                playerSide |= path.Contains("/Player/", StringComparison.OrdinalIgnoreCase);

                AddLeaf(new CompanionAnimRef
                {
                    ObjectPath = path,
                    Name = name,
                    LoadPath = path,
                    ClassName = className,
                    Source = source,
                    Via = via,
                    CasRole = reference.Role,
                    PlayerSide = playerSide
                });
                return;
            }

            if (!IsContainerClass(className)) return;
            if (depth >= MaxDepth || _visitedContainers.Count >= MaxContainers) return;
            if (!_visitedContainers.Add(reference.Path)) return;

            var container = Load(reference);
            if (container is null) return;

            discovery.ContainersVisited.Add($"{className}:{reference.Path}");
            var chain = via is null ? container.Name : $"{via} > {container.Name}";

            var refs = new List<GraphRef>();
            foreach (var tag in container.Properties) CollectRefs(tag.Tag, refs, role: null);

            // An anim blueprint keeps its graph constants on the class default object.
            if (container is UClass uClass)
            {
                try
                {
                    if (uClass.ClassDefaultObject is { IsNull: false } cdoIndex && cdoIndex.TryLoad(out var cdo) && cdo is not null)
                        foreach (var tag in cdo.Properties) CollectRefs(tag.Tag, refs, role: null);
                }
                catch (Exception e)
                {
                    Log.Debug("CDO walk failed for {Path}: {Message}", reference.Path, e.Message);
                }
            }

            string? childPrimaryRole = null;
            if (className.Equals("ContextualAnimSceneAsset", StringComparison.OrdinalIgnoreCase))
            {
                var roleName = container.GetOrDefault<FName>("PrimaryRole");
                if (!roleName.IsNone) childPrimaryRole = roleName.Text;
            }

            foreach (var child in refs)
            {
                if (child.Path.Equals(reference.Path, StringComparison.OrdinalIgnoreCase)) continue;
                Visit(child, source, depth + 1, chain, childPrimaryRole ?? primaryRole);
            }
        }
    }

    // ------------------------------------------------------------------ grouping

    public static string GroupFor(CompanionAnimRef anim)
    {
        if (anim.PlayerSide) return "Player";

        var path = anim.ObjectPath.Replace('\\', '/');
        if (path.Contains("/CreativeCompanion/", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/Animation/UEFN/", StringComparison.OrdinalIgnoreCase))
            return "Creative";

        var animationIndex = path.IndexOf("/Animation/", StringComparison.OrdinalIgnoreCase);
        if (animationIndex >= 0)
        {
            var segment = path[(animationIndex + "/Animation/".Length)..].SubstringBefore('/');
            if (!segment.Contains('.') && MapSegment(segment) is { } fromFolder) return fromFolder;
        }

        var name = anim.Name;
        if (name.Contains("_COR_", StringComparison.OrdinalIgnoreCase)) return "Core";
        if (name.Contains("_ACT_", StringComparison.OrdinalIgnoreCase)) return "Actions";
        if (name.Contains("_AO_", StringComparison.OrdinalIgnoreCase)) return "AimOffsets";
        if (name.Contains("_RCT_", StringComparison.OrdinalIgnoreCase)) return "Reacts";
        if (name.Contains("_QRK_", StringComparison.OrdinalIgnoreCase)) return "Quirks";
        if (name.Contains("Emote", StringComparison.OrdinalIgnoreCase)) return "Emotes";
        if (path.Contains("/Player/", StringComparison.OrdinalIgnoreCase)) return "Player";
        return "Other";
    }

    private static string? MapSegment(string segment) => segment.ToLowerInvariant() switch
    {
        "emotes" or "emote" => "Emotes",
        "core" or "locomotion" => "Core",
        "actions" or "action" => "Actions",
        "aimoffsets" or "aimoffset" => "AimOffsets",
        "reacts" or "react" or "reactions" => "Reacts",
        "quirks" or "quirk" => "Quirks",
        "uefn" or "creative" => "Creative",
        _ => null
    };

    // ------------------------------------------------------------------ export

    /// <summary>
    /// Exports every discovered animation under <paramref name="animationsRoot"/>/&lt;Group&gt;/, writing each
    /// distinct skeleton once into <c>Skeletons/</c>. Montages go through AnimExport's montage path
    /// (sections + notifies), sequences through its sequence path; blend spaces export their sample
    /// sequences. Must run inside the ExportRunner gate (CUE4Parse conversion is not thread-safe).
    /// </summary>
    public static async Task<CompanionAnimExport> ExportAsync(
        UObject itemDef, CompanionAnimDiscovery discovery, ExportSettings settings,
        FortnitePorting.Exporting.Providers.IExportAssetProvider exportProvider,
        string animationsRoot, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        var provider = exportProvider.Provider;
        var result = new CompanionAnimExport { Discovery = discovery, AnimationsRoot = animationsRoot };

        var skeletonDir = IoPath.Combine(animationsRoot, "Skeletons");
        Directory.CreateDirectory(skeletonDir);

        // glTF cannot hold a skeleton-only file (GltfMeshFormat throws NotSupportedException) and the glb writer
        // carries no animation, so a Gltf2 export already got .ueanim files with no skeleton beside them. The
        // animation phase therefore writes UEFormat (.uemodel skeleton + .ueanim) for Gltf2; ActorX keeps .psk/.psa.
        if (settings.MeshFormat is EMeshFormat.Gltf2)
        {
            settings = new ExportSettings
            {
                MeshFormat = EMeshFormat.UEFormat,
                ImageFormat = settings.ImageFormat,
                SoundFormat = settings.SoundFormat,
                ExportMaterials = settings.ExportMaterials,
                CompressionFormat = settings.CompressionFormat
            };
            result.Notes.Add("meshFormat Gltf2: glTF has no skeleton-only file and the glb writer carries no animation, so the "
                             + "animation phase wrote UEFormat (.uemodel skeletons + .ueanim). The companion mesh itself is still .glb.");
        }

        result.AnimationFormat = settings.MeshFormat.ToString();

        var meshExtension = settings.MeshFormat switch
        {
            EMeshFormat.ActorX => "psk",
            EMeshFormat.Gltf2 => "glb",
            EMeshFormat.USD => "usda",
            _ => "uemodel"
        };
        var animExtension = settings.MeshFormat switch
        {
            EMeshFormat.ActorX => "psa",
            EMeshFormat.USD => "usda",
            _ => "ueanim"
        };

        ResolveRigSkeletons(itemDef, discovery, provider, result);

        // Everything converts into Skeletons/ (so a skeleton is written there exactly once - ExportContext
        // skips a file that already exists) and the animation files are then moved into their group.
        using var meta = new ExportDataMeta
        {
            AssetsRoot = skeletonDir,
            CustomPath = skeletonDir,
            ExportLocation = EExportLocation.CustomFolder,
            Provider = exportProvider,
            Settings = settings
        };
        var session = new ExportSession(meta);

        var queue = new List<CompanionAnimRef>(discovery.Animations);
        var queued = new HashSet<string>(queue.Select(anim => anim.DedupeKey), StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < queue.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var anim = queue[i];
            var entry = new CompanionAnimResult { Ref = anim, Group = GroupFor(anim) };
            result.Results.Add(entry);

            UObject? asset = null;
            try
            {
                asset = await provider.SafeLoadPackageObjectAsync(anim.LoadPath);
            }
            catch (Exception e)
            {
                entry.Error = $"failed to load: {e.Message}";
                continue;
            }

            if (asset is null)
            {
                entry.Error = $"could not be loaded from \"{anim.LoadPath}\"";
                continue;
            }

            anim.ObjectPath = asset.GetPathName();
            anim.ClassName = asset.ExportType;

            switch (asset)
            {
                case UBlendSpaceBase blendSpace:
                {
                    entry.Kind = "blendspace";
                    entry.LengthSeconds = Math.Round(blendSpace.AnimLength, 4);
                    entry.Skeleton = blendSpace.Skeleton.ResolvedObject?.GetPathName();
                    var samples = new JsonArray();
                    foreach (var sample in blendSpace.SampleData)
                    {
                        var sampleObject = sample.Animation?.ResolvedObject;
                        if (sampleObject is null) continue;

                        var samplePath = sampleObject.GetPathName();
                        samples.Add(new JsonObject
                        {
                            ["animation"] = samplePath,
                            ["value"] = new JsonArray(Math.Round(sample.SampleValue.X, 4), Math.Round(sample.SampleValue.Y, 4), Math.Round(sample.SampleValue.Z, 4))
                        });

                        var sampleRef = new CompanionAnimRef
                        {
                            ObjectPath = samplePath,
                            Name = sampleObject.Name.Text,
                            LoadPath = samplePath,
                            ClassName = sampleObject.Class?.Name.Text,
                            Source = "blendSpaceSample",
                            Via = blendSpace.Name,
                            PlayerSide = anim.PlayerSide
                        };
                        if (queued.Add(sampleRef.DedupeKey)) queue.Add(sampleRef);
                    }

                    entry.Samples = samples;
                    if (samples.Count == 0) entry.Error = "blend space has no sample animations";
                    continue;
                }
                case UAnimMontage:
                    entry.Kind = "montage";
                    break;
                case UAnimSequence sequence:
                    entry.Kind = sequence.AdditiveAnimType is not EAdditiveAnimationType.AAT_None ? "additive" : "sequence";
                    entry.FrameCount = sequence.NumFrames;
                    if (entry.Kind is "additive")
                    {
                        entry.Additive = new JsonObject
                        {
                            ["additiveAnimType"] = sequence.AdditiveAnimType.ToString(),
                            ["refPoseType"] = sequence.RefPoseType.ToString(),
                            ["refPoseSeq"] = sequence.RefPoseSeq?.GetPathName(),
                            ["refFrameIndex"] = sequence.RefFrameIndex,
                            // CUE4Parse applies the additive onto its base pose when it is a valid additive
                            // (IsValidAdditive), so the .ueanim holds the resolved full pose, not raw deltas.
                            ["bakedOntoBasePose"] = sequence.IsValidAdditive()
                        };
                    }
                    break;
                case UAnimSequenceBase:
                    entry.Kind = asset.ExportType.Equals("AnimComposite", StringComparison.OrdinalIgnoreCase) ? "composite" : "sequence";
                    break;
                default:
                    entry.Kind = "other";
                    entry.Error = $"unsupported animation class {asset.ExportType}";
                    continue;
            }

            if (asset is UAnimSequenceBase sequenceBase)
                entry.LengthSeconds = Math.Round(sequenceBase.SequenceLength, 4);

            var groupDir = IoPath.Combine(animationsRoot, entry.Group);
            Directory.CreateDirectory(groupDir);

            var before = Snapshot(skeletonDir);
            WarningCapture.Begin();
            AnimExport? export = null;
            try
            {
                export = (AnimExport) session.CreateExport(anim.Name, asset, EExportType.Animation, []);
                await export.WaitForExports();
            }
            catch (Exception e)
            {
                entry.Error = $"conversion threw: {Flatten(e)}";
            }

            var warnings = WarningCapture.End();

            if (export is not null)
            {
                // Skeleton: written (or already present) in Skeletons/.
                if (export.Skeleton?.Path is { Length: > 0 } skeletonPath)
                {
                    entry.Skeleton = skeletonPath;
                    var skeletonDisk = ExportRunner.ToDiskPath(skeletonPath, meshExtension, meta);
                    // ActorX writes a skeleton-only file as .pskx, not .psk.
                    if (!File.Exists(skeletonDisk) && File.Exists(IoPath.ChangeExtension(skeletonDisk, "pskx")))
                        skeletonDisk = IoPath.ChangeExtension(skeletonDisk, "pskx");
                    if (File.Exists(skeletonDisk)) entry.SkeletonFile = skeletonDisk;
                    TrackSkeleton(result, skeletonPath, skeletonDisk, provider);
                    if (entry.SkeletonFile is null)
                    {
                        var reason = warnings.FirstOrDefault(line => line.Contains("Exception", StringComparison.Ordinal) && line.Contains("keleton", StringComparison.Ordinal));
                        entry.SkeletonError = $"skeleton {IoPath.GetFileName(skeletonDisk)} was not written{(reason is null ? "" : ": " + Trim(reason))}";
                    }
                }

                var sections = new JsonArray();
                foreach (var section in export.Sections)
                {
                    if (string.IsNullOrEmpty(section.Path)) continue;

                    var staged = ExportRunner.ToDiskPath(section.Path, animExtension, meta);
                    var final = IoPath.Combine(groupDir, IoPath.GetFileName(staged));
                    string? landed = null;
                    if (File.Exists(staged))
                    {
                        File.Move(staged, final, overwrite: true);
                        landed = final;
                    }

                    if (landed is not null && !entry.Files.Contains(landed, StringComparer.OrdinalIgnoreCase)) entry.Files.Add(landed);
                    if (entry.Kind is "montage")
                    {
                        sections.Add(new JsonObject
                        {
                            ["name"] = section.Name,
                            ["sequence"] = section.Path,
                            ["file"] = landed,
                            ["time"] = Math.Round(section.Time, 4),
                            ["length"] = Math.Round(section.Length, 4),
                            ["loop"] = section.Loop
                        });
                    }
                }

                if (entry.Kind is "montage") entry.Sections = sections;
                entry.File = entry.Files.FirstOrDefault();

                // Anything else this conversion wrote (notify sounds, spawned prop meshes) travels with it.
                foreach (var path in NewFiles(skeletonDir, before))
                {
                    if (result.Skeletons.Values.Any(skeleton => string.Equals(skeleton.File, path, StringComparison.OrdinalIgnoreCase))) continue;
                    var final = IoPath.Combine(groupDir, IoPath.GetFileName(path));
                    try
                    {
                        File.Move(path, final, overwrite: true);
                        entry.ExtraFiles.Add(final);
                    }
                    catch (IOException e)
                    {
                        Log.Debug("Could not move {Path}: {Message}", path, e.Message);
                    }
                }

                if (entry.Error is null && entry.Files.Count == 0)
                {
                    var reason = warnings.FirstOrDefault(line => line.Contains("Exception", StringComparison.Ordinal))
                                 ?? warnings.FirstOrDefault()
                                 ?? (export.Sections.Count == 0 ? "the exporter resolved no sections" : "no file was written");
                    entry.Error = $"no .{animExtension} was written: {Trim(reason)}";
                }
            }
        }

        PruneEmptyDirectories(animationsRoot);

        // Ground truth: whatever is on disk under Animations/ now.
        foreach (var path in Directory.EnumerateFiles(animationsRoot, "*", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            result.Files.Add(new ExportedFile(path, new FileInfo(path).Length));

        result.Seconds = clock.Elapsed.TotalSeconds;
        return result;
    }

    private static void ResolveRigSkeletons(UObject itemDef, CompanionAnimDiscovery discovery, AbstractVfsFileProvider provider, CompanionAnimExport result)
    {
        try
        {
            if (discovery.MainSkeletalMesh is { } mainMesh && provider.TryLoadPackageObject<USkeletalMesh>(mainMesh, out var baseMesh))
                result.BaseRigSkeleton = baseMesh.Skeleton?.ResolvedObject?.GetPathName();
        }
        catch (Exception e)
        {
            Log.Debug("Base rig skeleton lookup failed: {Message}", e.Message);
        }

        try
        {
            foreach (var part in CharacterPartInspector.Read(itemDef).Parts)
            {
                if (part.SkeletalMesh is null) continue;
                if (!provider.TryLoadPackageObject<USkeletalMesh>(part.SkeletalMesh, out var mesh)) continue;
                result.CompanionMeshSkeleton = mesh.Skeleton?.ResolvedObject?.GetPathName();
                if (result.CompanionMeshSkeleton is not null) break;
            }
        }
        catch (Exception e)
        {
            Log.Debug("Companion mesh skeleton lookup failed: {Message}", e.Message);
        }

        foreach (var skeletonPath in new[] { result.CompanionMeshSkeleton, result.BaseRigSkeleton })
            if (skeletonPath is not null) TrackSkeleton(result, skeletonPath, null, provider, countUse: false);
    }

    private static void TrackSkeleton(CompanionAnimExport result, string skeletonPath, string? diskPath, AbstractVfsFileProvider provider, bool countUse = true)
    {
        if (!result.Skeletons.TryGetValue(skeletonPath, out var info))
        {
            info = new CompanionSkeletonInfo
            {
                ObjectPath = skeletonPath,
                Name = skeletonPath.Contains('.') ? skeletonPath.SubstringAfterLast('.') : skeletonPath.SubstringAfterLast('/')
            };

            try
            {
                if (provider.TryLoadPackageObject<USkeleton>(skeletonPath, out var skeleton))
                {
                    foreach (var bone in skeleton.ReferenceSkeleton.FinalRefBoneInfo) info.Bones.Add(bone.Name.Text);
                    info.BoneCount = skeleton.ReferenceSkeleton.FinalRefBoneInfo.Length;
                }
            }
            catch (Exception e)
            {
                Log.Debug("Skeleton {Path} failed to load: {Message}", skeletonPath, e.Message);
            }

            result.Skeletons[skeletonPath] = info;
        }

        if (diskPath is not null && File.Exists(diskPath)) info.File = diskPath;
        if (countUse) info.UsedBy++;
    }

    private static Dictionary<string, (long, DateTime)> Snapshot(string directory)
    {
        var map = new Dictionary<string, (long, DateTime)>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(path);
            map[path] = (info.Length, info.LastWriteTimeUtc);
        }

        return map;
    }

    private static IEnumerable<string> NewFiles(string directory, Dictionary<string, (long, DateTime)> before)
    {
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToList())
        {
            var info = new FileInfo(path);
            if (before.TryGetValue(path, out var previous) && previous == (info.Length, info.LastWriteTimeUtc)) continue;
            yield return path;
        }
    }

    private static void PruneEmptyDirectories(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length).ToList())
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string Trim(string text)
    {
        var line = text.Split('\n').FirstOrDefault(item => item.Trim().Length > 0)?.Trim() ?? text.Trim();
        return line.Length > 400 ? line[..400] + "..." : line;
    }

    private static string Flatten(Exception e)
    {
        var messages = new List<string>();
        for (var current = e; current is not null; current = current.InnerException)
            messages.Add($"{current.GetType().Name}: {current.Message}");
        return string.Join(" -> ", messages);
    }

    // ------------------------------------------------------------------ manifest

    public const string SchemaRevision = "2.1";

    /// <summary>
    /// Adds the animation sections to an already-written manifest (additive; schemaVersion stays 2 and
    /// schemaRevision says "2.1"). Paths in it are relative to the manifest's folder.
    /// </summary>
    public static async Task<long?> PatchManifestAsync(string manifestPath, CompanionAnimExport export)
    {
        try
        {
            var root = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
            var baseDir = IoPath.GetDirectoryName(manifestPath)!;
            string? Rel(string? path) => path is null ? null : IoPath.GetRelativePath(baseDir, path).Replace('\\', '/');

            var discovery = export.Discovery;
            root["schemaRevision"] = SchemaRevision;

            root["companionRig"] = new JsonObject
            {
                ["mainSkeletalMesh"] = discovery.MainSkeletalMesh,
                ["mainAnimClass"] = discovery.MainAnimClass,
                ["baseRigSkeleton"] = export.BaseRigSkeleton,
                ["companionMeshSkeleton"] = export.CompanionMeshSkeleton,
                ["companionName"] = discovery.CompanionName,
                ["rigFamily"] = discovery.FamilyName
            };

            root["animationSources"] = new JsonArray(discovery.Sources.Select(source => (JsonNode) new JsonObject
            {
                ["property"] = source.Property,
                ["objectPath"] = source.ObjectPath,
                ["class"] = source.ClassName,
                ["source"] = source.Source
            }).ToArray());

            root["animationsRoot"] = Rel(export.AnimationsRoot);

            root["animations"] = new JsonArray(export.Results.Select(result =>
            {
                var skeletonRole = SkeletonRole(export, result.Skeleton, result.Ref.PlayerSide);
                var json = new JsonObject
                {
                    ["name"] = result.Ref.Name,
                    ["objectPath"] = result.Ref.ObjectPath,
                    ["class"] = result.Ref.ClassName,
                    ["file"] = Rel(result.File),
                    ["group"] = result.Group,
                    ["kind"] = result.Kind,
                    ["skeleton"] = result.Skeleton,
                    ["skeletonFile"] = Rel(result.SkeletonFile),
                    ["skeletonRole"] = skeletonRole,
                    ["lengthSeconds"] = result.LengthSeconds,
                    ["frameCount"] = result.FrameCount,
                    ["source"] = result.Ref.Source,
                    ["via"] = result.Ref.Via,
                    ["playerSide"] = result.Ref.PlayerSide,
                    ["status"] = result.Ok ? "ok" : "failed"
                };

                if (result.Ref.CasRole is not null) json["casRole"] = result.Ref.CasRole;
                if (result.Files.Count > 1) json["files"] = new JsonArray(result.Files.Select(file => (JsonNode) Rel(file)!).ToArray());
                if (result.Additive is not null) json["additive"] = result.Additive;
                if (result.Sections is not null)
                {
                    foreach (var section in result.Sections.OfType<JsonObject>())
                        section["file"] = Rel(section["file"]?.GetValue<string>());
                    json["sections"] = result.Sections;
                }
                if (result.Samples is not null) json["samples"] = result.Samples;
                if (result.ExtraFiles.Count > 0) json["extraFiles"] = new JsonArray(result.ExtraFiles.Select(file => (JsonNode) Rel(file)!).ToArray());
                if (result.SkeletonError is not null) json["skeletonError"] = result.SkeletonError;
                if (result.Error is not null) json["error"] = result.Error;
                return (JsonNode) json;
            }).ToArray());

            var companionBones = export.CompanionMeshSkeleton is { } companionSkeleton && export.Skeletons.TryGetValue(companionSkeleton, out var companionInfo)
                ? companionInfo.Bones
                : null;

            root["skeletons"] = new JsonArray(export.Skeletons.Values.Select(skeleton =>
            {
                var shared = companionBones is null ? (int?) null : skeleton.Bones.Count(companionBones.Contains);
                return (JsonNode) new JsonObject
                {
                    ["name"] = skeleton.Name,
                    ["objectPath"] = skeleton.ObjectPath,
                    ["file"] = Rel(skeleton.File),
                    ["role"] = SkeletonRole(export, skeleton.ObjectPath, playerSide: false),
                    ["boneCount"] = skeleton.BoneCount,
                    ["animationsUsing"] = skeleton.UsedBy,
                    ["bonesSharedWithCompanionMesh"] = shared
                };
            }).ToArray());

            root["retargetAssets"] = new JsonArray(discovery.RetargetAssets.Select(path => (JsonNode) path).ToArray());

            root["animationSummary"] = Summary(export);

            if (root["schemaNotes"] is JsonObject notes)
            {
                notes["animations"] = "Revision 2.1 (additive): sidekick exports with includeAnimations write animations under "
                    + "animationsRoot/<group>/ and each distinct skeleton once under animationsRoot/Skeletons/. kind: sequence | montage "
                    + "(files = one per montage section, sections[] carries timing/loop) | additive (CUE4Parse bakes a valid additive "
                    + "onto its reference pose; see additive.bakedOntoBasePose) | blendspace (no file of its own - samples[] lists the "
                    + "sample sequences, which are exported as their own entries with source blendSpaceSample). skeletonRole says "
                    + "which rig an animation is authored on: companionMesh (plays directly on the visible mesh), rigFamily (the shared "
                    + "rig family's skeleton, e.g. FullSnore_Quadruped_Skeleton - the bulk of locomotion/actions/reacts; compare "
                    + "skeletons[].bonesSharedWithCompanionMesh and retarget with retargetAssets in UE), baseRig (the invisible rig "
                    + "named by companionRig.mainSkeletalMesh), player (the Fortnite player skeleton - playerSide:true, the player "
                    + "half of a two-character CAS emote), prop (an emote prop's own skeleton), other.";
            }

            await File.WriteAllTextAsync(manifestPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return new FileInfo(manifestPath).Length;
        }
        catch (Exception e)
        {
            Log.Warning(e, "Could not add animations to manifest {Path}", manifestPath);
            return null;
        }
    }

    public static string SkeletonRole(CompanionAnimExport export, string? skeletonPath, bool playerSide)
    {
        if (skeletonPath is null) return "unknown";
        if (export.CompanionMeshSkeleton is not null && skeletonPath.Equals(export.CompanionMeshSkeleton, StringComparison.OrdinalIgnoreCase)) return "companionMesh";
        if (export.BaseRigSkeleton is not null && skeletonPath.Equals(export.BaseRigSkeleton, StringComparison.OrdinalIgnoreCase)) return "baseRig";
        if (playerSide || skeletonPath.Contains("/Player/", StringComparison.OrdinalIgnoreCase) || skeletonPath.Contains("Fortnite_Base", StringComparison.OrdinalIgnoreCase)) return "player";
        if (skeletonPath.Contains("/Props/", StringComparison.OrdinalIgnoreCase)) return "prop";

        // The shared rig family's own skeleton (FullSnore_Quadruped_Skeleton for every FullSnore-rig dog): the
        // skeleton the shared library is authored on, distinct from both the invisible base rig and the visible mesh.
        var family = export.Discovery.FamilyName;
        var name = skeletonPath.Contains('.') ? skeletonPath.SubstringAfterLast('.') : skeletonPath.SubstringAfterLast('/');
        if (!string.IsNullOrEmpty(family) && name.StartsWith(family + "_", StringComparison.OrdinalIgnoreCase)) return "rigFamily";
        return "other";
    }

    public static JsonObject Summary(CompanionAnimExport export)
    {
        var groups = new JsonObject();
        foreach (var group in export.Results.Where(result => result.Ok && result.Kind is not "blendspace")
                     .GroupBy(result => result.Group).OrderBy(group => group.Key))
            groups[group.Key] = group.Count();

        var kinds = new JsonObject();
        foreach (var kind in export.Results.GroupBy(result => result.Kind).OrderBy(group => group.Key))
            kinds[kind.Key] = kind.Count();

        var sources = new JsonObject();
        foreach (var source in export.Results.GroupBy(result => result.Ref.Source).OrderBy(group => group.Key))
            sources[source.Key] = source.Count();

        var roles = new JsonObject();
        foreach (var role in export.Results.Where(result => result.Ok)
                     .GroupBy(result => SkeletonRole(export, result.Skeleton, result.Ref.PlayerSide)).OrderBy(group => group.Key))
            roles[role.Key] = role.Count();

        return new JsonObject
        {
            ["discovered"] = export.Results.Count,
            ["animationsExported"] = export.Exported,
            ["blendSpacesResolved"] = export.BlendSpaces,
            ["animationsFailed"] = export.Failed.Count(),
            ["animationFiles"] = export.Files.Count(file => file.Path.EndsWith(".ueanim", StringComparison.OrdinalIgnoreCase)
                                                            || file.Path.EndsWith(".psa", StringComparison.OrdinalIgnoreCase)),
            ["animationBytes"] = export.AnimationBytes,
            ["distinctSkeletons"] = export.Results.Where(result => result.Skeleton is not null).Select(result => result.Skeleton).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            ["byGroup"] = groups,
            ["byKind"] = kinds,
            ["bySource"] = sources,
            ["bySkeletonRole"] = roles,
            ["skeletonFilesMissing"] = export.Results.Count(result => result.SkeletonError is not null),
            ["animationFormat"] = export.AnimationFormat,
            ["notes"] = new JsonArray(export.Notes.Select(note => (JsonNode) note).ToArray()),
            ["containersVisited"] = export.Discovery.ContainersVisited.Count,
            ["discoverySeconds"] = Math.Round(export.Discovery.Seconds, 2),
            ["exportSeconds"] = Math.Round(export.Seconds, 2)
        };
    }
}

/// <summary>
/// Captures warning-level log lines while an animation converts. ExportContext swallows conversion
/// exceptions into Log.Warning, so this is the only place the REASON for a missing .ueanim survives.
/// Exports are serialized by the ExportRunner gate, so one global capture window is enough.
/// </summary>
public sealed class WarningCapture : ILogEventSink
{
    public static readonly WarningCapture Instance = new();

    private static volatile bool _active;
    private static readonly ConcurrentQueue<string> Lines = new();

    public static void Begin()
    {
        Lines.Clear();
        _active = true;
    }

    public static List<string> End()
    {
        _active = false;
        var lines = Lines.ToList();
        Lines.Clear();
        return lines;
    }

    public void Emit(LogEvent logEvent)
    {
        if (!_active || logEvent.Level < LogEventLevel.Warning) return;
        var text = logEvent.RenderMessage();
        if (logEvent.Exception is not null) text += " " + logEvent.Exception.GetType().Name + ": " + logEvent.Exception.Message;
        Lines.Enqueue(text);
    }
}
