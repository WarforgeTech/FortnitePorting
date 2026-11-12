using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Objects.Core.Math;
using Serilog;

namespace FortnitePorting.Mcp.Core;

/// <summary>
/// Axis-aligned bounds of one mesh, in Unreal centimetres.
/// <para>
/// Two consumers need the same number and must not disagree: the per-asset export manifest (so an
/// importer can scale a mesh it has on disk) and the index dump (so an agent can tell a 4 m hedge
/// from a 40 cm one before placing it). Both read it here.
/// </para>
/// </summary>
public readonly record struct MeshBoundsInfo(FVector Origin, FVector BoxExtent, float SphereRadius)
{
    public float SizeX => BoxExtent.X * 2;
    public float SizeY => BoxExtent.Y * 2;
    public float SizeZ => BoxExtent.Z * 2;
}

public static class MeshBounds
{
    /// <summary>
    /// Bounds off an already-loaded mesh. Static meshes carry them on RenderData (null when the
    /// archive shipped no render data for the mesh); skeletal meshes carry ImportedBounds.
    /// </summary>
    public static MeshBoundsInfo? From(UObject? asset)
    {
        var bounds = asset switch
        {
            UStaticMesh staticMesh => staticMesh.RenderData?.Bounds,
            USkeletalMesh skeletalMesh => skeletalMesh.ImportedBounds,
            _ => null
        };

        return bounds is { } value
            ? new MeshBoundsInfo(value.Origin, value.BoxExtent, value.SphereRadius)
            : null;
    }

    /// <summary>
    /// Loads <paramref name="objectPath"/> through <paramref name="resolve"/> and reads its bounds.
    /// Never throws: a mesh that will not load is "no bounds", not a failed export.
    /// </summary>
    public static async Task<MeshBoundsInfo?> ReadAsync(string? objectPath, Func<string, Task<UObject?>> resolve)
    {
        try
        {
            if (string.IsNullOrEmpty(objectPath)) return null;

            return From(await resolve(objectPath));
        }
        catch (Exception e)
        {
            Log.Debug("Bounds lookup failed for {Path}: {Message}", objectPath, e.Message);
            return null;
        }
    }
}
