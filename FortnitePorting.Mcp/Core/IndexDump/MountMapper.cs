using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace FortnitePorting.Mcp.Core.IndexDump;

/// <summary>
/// One hand-verified statement about a mount: somebody actually ran the stock UEFN editor MCP
/// against it and watched what happened. Loaded from <c>Config/mount-verification.json</c> and
/// merged onto the generated scope table, because "we found rows under this path" and "assets under
/// this path can be searched, captured and placed" are different claims and only the second one is
/// worth anything to the customer agent.
/// </summary>
public sealed class MountVerification
{
    /// <summary>UEFN path prefix this note applies to, e.g. "/Game/Environments".</summary>
    public string UefnPath { get; set; } = string.Empty;

    /// <summary>find | capture | place | resolve, in whatever combination was actually observed.</summary>
    public List<string> Verified { get; set; } = [];

    public string? Note { get; set; }
}

public sealed class MountVerificationFile
{
    public string? Comment { get; set; }
    public List<MountVerification> Mounts { get; set; } = [];
}

/// <summary>The scope one row belongs to: a UEFN path prefix the agent can hand to find_assets.</summary>
public sealed record ScopeInfo
{
    public required string ScopeId { get; init; }
    public required string UefnPath { get; init; }
    public required string RegistryPrefix { get; init; }

    /// <summary>The path segment under the scope that reads like a content theme ("Asteria").</summary>
    public required string Theme { get; init; }

    /// <summary>Leaf asset name of the path this scope was derived from - a usable find_assets name.</summary>
    public required string Leaf { get; init; }

    /// <summary>Ranking hint: /Game and plugin roots beat /Engine, which is never worth searching first.</summary>
    public required int Priority { get; init; }
}

/// <summary>
/// Registry package path -> UEFN path, and the scope bucket a row lands in.
/// <para>
/// The registry speaks in cooked archive paths ("FortniteGame/Content/Environments/..."). UEFN's
/// content browser - and therefore <c>find_assets</c> and <c>add_to_scene_from_asset</c> - speaks in
/// mount points ("/Game/Environments/..."). Every path this dump ships has to be in the second
/// language or the customer agent cannot paste it anywhere.
/// </para>
/// </summary>
public sealed class MountMapper
{
    private const string GameFeaturePrefix = "FortniteGame/Plugins/GameFeatures/";
    private const string FortnitePluginPrefix = "FortniteGame/Plugins/";
    private const string GameContentPrefix = "FortniteGame/Content/";
    private const string EngineContentPrefix = "Engine/Content/";

    private readonly Dictionary<string, MountVerification> _verifications;

    public MountMapper(IEnumerable<MountVerification> verifications)
    {
        _verifications = new Dictionary<string, MountVerification>(StringComparer.OrdinalIgnoreCase);
        foreach (var verification in verifications)
        {
            if (string.IsNullOrWhiteSpace(verification.UefnPath)) continue;
            _verifications[Normalize(verification.UefnPath)] = verification;
        }
    }

    /// <summary>
    /// Reads the hand-maintained verification file. A missing or malformed file is not fatal: the
    /// scope table then simply says nothing is verified, which is the honest default.
    /// </summary>
    public static MountMapper Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Log.Warning("[INDEX] No mount-verification file at {Path}; every scope will read unverified", path);
            return new MountMapper([]);
        }

        try
        {
            var file = JsonSerializer.Deserialize<MountVerificationFile>(
                File.ReadAllText(path),
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });

            var mounts = file?.Mounts ?? [];
            Log.Information("[INDEX] Loaded {Count} mount verifications from {Path}", mounts.Count, path);
            return new MountMapper(mounts);
        }
        catch (Exception e)
        {
            Log.Warning("[INDEX] Mount-verification file {Path} could not be read ({Message}); continuing unverified", path, e.Message);
            return new MountMapper([]);
        }
    }

    /// <summary>
    /// Translates one registry package path into a UEFN path. Returns null only for a path that
    /// matches no known mount, which the caller records as a per-row failure rather than guessing.
    /// </summary>
    public static string? ToUefnPath(string? registryPath)
    {
        if (string.IsNullOrWhiteSpace(registryPath)) return null;

        var path = registryPath.Trim().Replace('\\', '/');

        // Already a mount-relative virtual path ("/Burd_Comp/...", "/Game/...").
        if (path.StartsWith('/')) return path;

        if (path.StartsWith(GameFeaturePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var rest = path[GameFeaturePrefix.Length..];
            var slash = rest.IndexOf('/');
            if (slash <= 0) return null;

            var plugin = rest[..slash];
            var tail = rest[(slash + 1)..];
            if (!tail.StartsWith("Content/", StringComparison.OrdinalIgnoreCase)) return null;

            return $"/{plugin}/{tail["Content/".Length..]}";
        }

        if (path.StartsWith(GameContentPrefix, StringComparison.OrdinalIgnoreCase))
            return "/Game/" + path[GameContentPrefix.Length..];

        if (path.StartsWith(EngineContentPrefix, StringComparison.OrdinalIgnoreCase))
            return "/Engine/" + path[EngineContentPrefix.Length..];

        // Non-GameFeatures Fortnite plugins mount the same way.
        if (path.StartsWith(FortnitePluginPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var rest = path[FortnitePluginPrefix.Length..];
            var slash = rest.IndexOf('/');
            if (slash <= 0) return null;

            var plugin = rest[..slash];
            var tail = rest[(slash + 1)..];
            if (!tail.StartsWith("Content/", StringComparison.OrdinalIgnoreCase)) return null;

            return $"/{plugin}/{tail["Content/".Length..]}";
        }

        return null;
    }

    /// <summary>
    /// Path segments that are archive plumbing rather than a content theme. Reporting a scope's
    /// theme as "SetupAssets" tells a reader nothing and poisons the keyword tokens of every row
    /// under it.
    /// </summary>
    private static readonly HashSet<string> StructuralSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "SetupAssets", "PlaysetProps", "PPIDs", "Maps", "Content", "Blueprints", "Meshes",
        "Materials", "Textures", "Items", "Assets"
    };

    /// <summary>
    /// The bucket a UEFN path belongs to. <c>/Game</c> is far too big to search, so it is split one
    /// level deeper ("/Game/Environments"); a plugin root is already a usable search scope on its
    /// own ("/Burd_Comp").
    /// <para>
    /// Pass the PACKAGE path, not an object path - the trailing ".Object" breaks the suffix match
    /// that recovers the registry prefix.
    /// </para>
    /// </summary>
    public static ScopeInfo? ScopeFor(string? uefnPath, string? registryPath)
    {
        if (string.IsNullOrWhiteSpace(uefnPath) || !uefnPath.StartsWith('/')) return null;

        uefnPath = PackageHalf(uefnPath);
        if (registryPath is not null) registryPath = PackageHalf(registryPath);

        var segments = uefnPath.TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return null;

        var root = segments[0];
        var isGame = root.Equals("Game", StringComparison.OrdinalIgnoreCase);
        var isEngine = root.Equals("Engine", StringComparison.OrdinalIgnoreCase);

        var depth = isGame || isEngine ? 2 : 1;
        if (segments.Length < depth) depth = segments.Length;

        var scopePath = "/" + string.Join('/', segments.Take(depth));

        // The first segment under the scope that reads like a content theme rather than plumbing.
        // The last segment is the asset itself, never a theme - without that guard a composition
        // plugin whose whole path is structural ("/Burd_Comp/SetupAssets/Maps/PPIDs/PPID_x")
        // reports its own PPID name as the theme.
        var theme = segments.Skip(depth).SkipLast(1)
            .FirstOrDefault(segment => !StructuralSegments.Contains(segment)) ?? string.Empty;

        var registryPrefix = registryPath is null ? string.Empty : RegistryPrefixFor(registryPath, uefnPath, scopePath);

        return new ScopeInfo
        {
            ScopeId = MakeScopeId(scopePath),
            UefnPath = scopePath,
            RegistryPrefix = registryPrefix,
            Theme = theme,
            Leaf = segments[^1],
            Priority = isEngine ? 2 : 0
        };
    }

    /// <summary>The archive-path half of the same prefix, so a human can trace a scope back to the pak.</summary>
    private static string RegistryPrefixFor(string registryPath, string uefnPath, string scopePath)
    {
        var path = registryPath.Replace('\\', '/');

        // The UEFN path is a suffix-preserving rewrite of the registry path, so the registry prefix
        // is whatever sits in front of the part they share.
        var tail = uefnPath.Length > scopePath.Length ? uefnPath[scopePath.Length..] : string.Empty;
        if (tail.Length > 0 && path.EndsWith(tail, StringComparison.OrdinalIgnoreCase))
            return path[..^tail.Length];

        return path;
    }

    /// <summary>Stable, filesystem- and grep-safe id for a scope path ("/Game/Environments" -> "game.environments").</summary>
    public static string MakeScopeId(string scopePath)
        => scopePath.TrimStart('/').Replace('/', '.').ToLowerInvariant();

    /// <summary>The verification verbs recorded for a scope, or an empty list when nobody has checked it.</summary>
    public IReadOnlyList<string> VerifiedFor(string uefnPath)
        => _verifications.TryGetValue(Normalize(uefnPath), out var found) ? found.Verified : [];

    public string? NoteFor(string uefnPath)
        => _verifications.TryGetValue(Normalize(uefnPath), out var found) ? found.Note : null;

    private static string Normalize(string path) => '/' + path.Trim().Replace('\\', '/').Trim('/');

    /// <summary>"/A/B/C.C" -> "/A/B/C". Leaves a path that has no object half alone.</summary>
    public static string PackageHalf(string path)
    {
        var slash = path.LastIndexOf('/');
        var dot = path.LastIndexOf('.');
        return dot > slash ? path[..dot] : path;
    }
}
