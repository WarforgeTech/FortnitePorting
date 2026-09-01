using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.GameTypes.FN.Assets.Exports;
using Serilog;

namespace FortnitePorting.Mcp.Core.IndexDump;

/// <summary>What one prop item definition resolved to, and how far the chain got.</summary>
public sealed record PropResolution
{
    /// <summary>Blueprint CLASS object path, "_C"-suffixed - the only form UEFN will place.</summary>
    public string? BlueprintClassPath { get; init; }

    /// <summary>Package path of the same blueprint, which is what CaptureAssetImage wants.</summary>
    public string? BlueprintPackagePath { get; init; }

    /// <summary>Package path of the first static mesh the blueprint builds itself out of.</summary>
    public string? StaticMeshPath { get; init; }

    /// <summary>Rounded centimetres, X/Y/Z, or null when nothing measurable was reached.</summary>
    public int[]? Size { get; init; }

    /// <summary>Where the chain stopped, for the failure log. Null when everything resolved.</summary>
    public string? Failure { get; init; }
}

/// <summary>
/// PPID -> blueprint class -> static mesh -> bounds.
/// <para>
/// The chain is the whole reason the dump is useful: a prop item definition is what UEFN places, a
/// blueprint class path is what a blueprint placement needs, a static mesh is the only one of the
/// three <c>CaptureAssetImage</c> will render, and the bounds are how an agent knows whether the
/// thing it is about to place is a pebble or a building. Each hop degrades independently - a row
/// that reached a blueprint but no mesh still ships with its blueprint.
/// </para>
/// </summary>
public sealed class PropMeshResolver(HeadlessLoader loader)
{
    /// <summary>
    /// Reads the placement identity out of an already-loaded prop definition package. No further
    /// package loads happen here: ActorSaveRecord is a sub-object of the prop's own package and
    /// ActorClass is a soft path we keep as a string.
    /// </summary>
    public PropResolution ReadPlacement(UObject prop)
    {
        try
        {
            if (prop.GetOrDefault<ULevelSaveRecord?>("ActorSaveRecord") is not { } record)
                return new PropResolution { Failure = "no ActorSaveRecord" };

            var actorClass = FirstActorClass(record);
            if (actorClass is null)
                return new PropResolution { Failure = "no TemplateRecords[*].ActorClass" };

            var classPath = ToUefnObjectPath(actorClass);
            if (classPath is null)
                return new PropResolution { Failure = $"ActorClass \"{actorClass}\" maps to no known mount" };

            return new PropResolution
            {
                BlueprintClassPath = classPath,
                BlueprintPackagePath = PackageHalf(classPath)
            };
        }
        catch (Exception e)
        {
            return new PropResolution { Failure = $"ActorSaveRecord read threw: {e.Message}" };
        }
    }

    /// <summary>
    /// Second hop: loads the blueprint generated class and finds the first static mesh it builds
    /// itself from, then measures that mesh. Costs two package loads, so callers gate it on tier.
    /// </summary>
    public async Task<PropResolution> ResolveMeshAsync(PropResolution placement, bool wantBounds)
    {
        if (placement.BlueprintClassPath is null) return placement;

        UObject? blueprint;
        try
        {
            blueprint = await loader.Provider.SafeLoadPackageObjectAsync(placement.BlueprintClassPath);
        }
        catch (Exception e)
        {
            return placement with { Failure = $"blueprint load threw: {e.Message}" };
        }

        if (blueprint is null)
            return placement with { Failure = "blueprint class did not load" };

        var candidates = CollectStaticMeshPaths(blueprint);
        if (candidates.Count == 0)
            return placement with { Failure = "blueprint exposes no StaticMesh (SCS/ICH/CDO all empty)" };

        var mapped = candidates
            .Select(ToUefnObjectPath)
            .Where(path => path is not null)
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (mapped.Count == 0)
            return placement with { Failure = $"StaticMesh \"{candidates[0]}\" maps to no known mount" };

        // A shadow proxy or collision hull is a real mesh with roughly the right bounds and no
        // visual worth looking at - CaptureAssetImage renders it as a grey blob, which is worse
        // than showing the agent nothing. Prefer anything else; measure the proxy either way.
        var visual = mapped.FirstOrDefault(path => !IsNonVisualMesh(path));
        var forBounds = visual ?? mapped[0];

        var result = placement with { StaticMeshPath = visual is null ? null : PackageHalf(visual) };
        if (visual is null)
            result = result with
            {
                Failure = $"only non-visual meshes found ({LeafOf(mapped[0])}); sm suppressed, sz still measured from it"
            };

        if (!wantBounds) return result;

        var bounds = await MeshBounds.ReadAsync(forBounds,
            path => loader.Provider.SafeLoadPackageObjectAsync(path));

        if (bounds is not { } value) return result;

        return result with { Size = [Round(value.SizeX), Round(value.SizeY), Round(value.SizeZ)] };
    }

    /// <summary>
    /// Names that mark a mesh as a stand-in with no visual worth rendering.
    /// <para>
    /// Deliberately narrow, and narrowed twice by measurement rather than taste. "blockout" and
    /// "_lod" came out first: they accounted for 348 of 351 suppressions on 42.00 and both are real
    /// geometry a builder places on purpose - Blockout is an intentional grey-box prop family, and
    /// an LOD mesh renders the same silhouette at lower detail. "collision" came out next: its only
    /// two hits on the whole archive were <c>Creative_Helipad_NoCollision</c> and
    /// <c>PlayGround_MerryGoRound_01_collisionFix</c>, both real visual meshes whose names merely
    /// contain the word. A substring rule cannot tell a collision hull from a mesh named after not
    /// having one, so it does not try.
    /// </para>
    /// <para>
    /// What is left is unambiguous: a shadow proxy renders as a featureless dark blob and is never
    /// the thing the player sees.
    /// </para>
    /// </summary>
    private static readonly string[] NonVisualMeshHints = ["proxy", "shadowcaster"];

    private static bool IsNonVisualMesh(string path)
    {
        var leaf = LeafOf(path);
        return NonVisualMeshHints.Any(hint => leaf.Contains(hint, StringComparison.OrdinalIgnoreCase));
    }

    private static string LeafOf(string path)
    {
        var slash = path.LastIndexOf('/');
        var leaf = slash >= 0 ? path[(slash + 1)..] : path;
        var dot = leaf.LastIndexOf('.');
        return dot > 0 ? leaf[..dot] : leaf;
    }

    // ---------------------------------------------------------------- internals

    // NOTE: ULevelSaveRecord.HalfBoundsExtent looked like a free per-prop size that would cover
    // rows whose blueprint or mesh never resolves. It was measured across all 26,620 canonical
    // props on 42.00 and is zero on every one of them, so there is no record-derived fallback here
    // - a row without a static mesh simply ships without a size.

    /// <summary>
    /// The first template record that names an actor class. Multi-actor props exist (a prop that
    /// places a shed plus its door); the first record is the one the exporter treats as the prop.
    /// </summary>
    private static string? FirstActorClass(ULevelSaveRecord record)
    {
        if (record.TemplateRecords is not { Count: > 0 }) return null;

        foreach (var index in record.TemplateRecords.Keys.Order())
        {
            var path = record.TemplateRecords[index]?.ActorClass.AssetPathName.Text;
            if (!string.IsNullOrWhiteSpace(path) && !path.Equals("None", StringComparison.Ordinal))
                return path;
        }

        return null;
    }

    /// <summary>
    /// Every static mesh a blueprint generated class exposes, in the order the exporter looks: the
    /// simple construction script, then inherited component records, then the class default object.
    /// Most creative props answer on the first; parented ones need the second; a handful only ever
    /// set their mesh on the CDO.
    /// <para>
    /// This collects rather than short-circuits because the FIRST mesh a blueprint names is often a
    /// shadow proxy or collision hull, and the caller needs to see the alternatives before it picks.
    /// The parent class is walked only when the child produced nothing usable, so a normal prop
    /// still costs no extra loads.
    /// </para>
    /// </summary>
    private static List<string> CollectStaticMeshPaths(UObject blueprint)
    {
        var found = new List<string>();
        CollectFrom(blueprint, found);

        // A blueprint can inherit its whole visual from its parent class - and a child that only
        // declares a proxy may inherit the real mesh, so climb on all-proxy too, not just on empty.
        if (found.Count == 0 || found.TrueForAll(IsNonVisualMesh))
        {
            try
            {
                if (blueprint is UStruct { SuperStruct: { } super } && super.TryLoad(out var parent) && parent is { } parentObject)
                    CollectFrom(parentObject, found);
            }
            catch (Exception e)
            {
                Log.Debug("Parent-class walk failed for {Name}: {Message}", blueprint.Name, e.Message);
            }
        }

        return found;
    }

    private static void CollectFrom(UObject blueprint, List<string> found)
    {
        FromConstructionScript(blueprint, found);
        FromInheritableComponents(blueprint, found);
        FromClassDefaultObject(blueprint, found);
    }

    private static void FromConstructionScript(UObject blueprint, List<string> found)
    {
        try
        {
            if (!blueprint.TryGetValue(out UObject constructionScript, "SimpleConstructionScript")) return;

            foreach (var node in constructionScript.GetOrDefault("AllNodes", Array.Empty<UObject>()))
            {
                if (node is null) continue;
                if (MeshOf(node.GetOrDefault<UObject?>("ComponentTemplate")) is { } path) found.Add(path);
            }
        }
        catch (Exception e)
        {
            Log.Debug("SCS walk failed for {Name}: {Message}", blueprint.Name, e.Message);
        }
    }

    private static void FromInheritableComponents(UObject blueprint, List<string> found)
    {
        try
        {
            if (!blueprint.TryGetValue(out UObject handler, "InheritableComponentHandler")) return;

            foreach (var record in handler.GetOrDefault("Records", Array.Empty<FStructFallback>()))
            {
                if (record is null) continue;
                if (MeshOf(record.GetOrDefault<UObject?>("ComponentTemplate")) is { } path) found.Add(path);
            }
        }
        catch (Exception e)
        {
            Log.Debug("InheritableComponentHandler walk failed for {Name}: {Message}", blueprint.Name, e.Message);
        }
    }

    private static void FromClassDefaultObject(UObject blueprint, List<string> found)
    {
        try
        {
            if (blueprint is not UBlueprintGeneratedClass generated) return;
            if (generated.ClassDefaultObject is not { } lazy || !lazy.TryLoad(out var cdo) || cdo is not { } defaults) return;

            // The CDO's own root component, then any static-mesh-shaped property hanging off it.
            if (MeshOf(defaults) is { } direct) found.Add(direct);

            foreach (var property in defaults.Properties)
            {
                var value = property.Tag?.GenericValue;
                if (value is FPackageIndex index && index.TryLoad(out var loaded) && MeshOf(loaded) is { } fromProperty)
                    found.Add(fromProperty);
            }
        }
        catch (Exception e)
        {
            Log.Debug("CDO probe failed for {Name}: {Message}", blueprint.Name, e.Message);
        }
    }

    /// <summary>The static mesh a component points at, as a path string - never loading the mesh itself.</summary>
    private static string? MeshOf(UObject? component)
    {
        if (component is null) return null;

        try
        {
            // Object reference first. Cooked components almost always store the mesh as an
            // ObjectProperty, and asking for a soft path first makes CUE4Parse log a conversion
            // warning per component - tens of thousands of them across a full dump.
            if (component.GetOrDefault<FPackageIndex?>("StaticMesh") is { IsNull: false } index &&
                index.TryLoad(out var mesh) && mesh is UStaticMesh staticMesh)
                return staticMesh.GetPathName();

            if (component.GetOrDefault<FSoftObjectPath?>("StaticMesh") is { } soft &&
                soft.AssetPathName.Text is { Length: > 0 } softPath &&
                !softPath.Equals("None", StringComparison.Ordinal))
                return softPath;
        }
        catch (Exception e)
        {
            Log.Debug("StaticMesh read failed on {Name}: {Message}", component.Name, e.Message);
        }

        return null;
    }

    /// <summary>Rewrites a raw engine path to its UEFN mount and keeps the object half intact.</summary>
    private static string? ToUefnObjectPath(string raw)
    {
        var path = raw.Trim();
        if (path.Length == 0) return null;

        var dot = path.LastIndexOf('.');
        var slash = path.LastIndexOf('/');
        var package = dot > slash ? path[..dot] : path;
        var objectName = dot > slash ? path[(dot + 1)..] : null;

        var uefn = MountMapper.ToUefnPath(package);
        if (uefn is null) return null;

        objectName ??= uefn[(uefn.LastIndexOf('/') + 1)..];
        return $"{uefn}.{objectName}";
    }

    private static string PackageHalf(string objectPath)
    {
        var dot = objectPath.LastIndexOf('.');
        var slash = objectPath.LastIndexOf('/');
        return dot > slash ? objectPath[..dot] : objectPath;
    }

    private static int Round(float value)
        => float.IsFinite(value) ? (int)Math.Round(Math.Abs(value), MidpointRounding.AwayFromZero) : 0;
}
