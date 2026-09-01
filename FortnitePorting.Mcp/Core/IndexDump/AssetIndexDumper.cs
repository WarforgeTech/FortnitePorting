using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.UObject;
using FortnitePorting;
using FortnitePorting.Mcp.Config;
using Serilog;

namespace FortnitePorting.Mcp.Core.IndexDump;

/// <summary>How far the blueprint/mesh chain is walked, and for which rows.</summary>
public enum IndexTier
{
    /// <summary>Resolve blueprints and meshes for the core subset only. Fast.</summary>
    A,

    /// <summary>Resolve blueprints and meshes for every shipped row.</summary>
    B
}

public enum BoundsScope
{
    None,
    Core,
    All
}

public sealed record IndexDumpOptions
{
    public required string OutputDirectory { get; init; }
    public IndexTier Tier { get; init; } = IndexTier.B;
    public BoundsScope Bounds { get; init; } = BoundsScope.Core;
    public int Parallelism { get; init; } = 12;
}

public sealed record IndexDumpResult
{
    public required string IndexDirectory { get; init; }
    public required IndexCounts Counts { get; init; }
    public required int RegistryRows { get; init; }
    public required TimeSpan Elapsed { get; init; }
    public required IReadOnlyDictionary<string, TimeSpan> PhaseTimings { get; init; }
}

/// <summary>
/// Writes the grep-first asset index a customer agent uses to get from human words to a placed
/// prop with nothing but the stock UEFN editor MCP.
/// <para>
/// The shape of the problem: the editor MCP can search a folder, render an asset, and place an
/// object path - but it cannot tell you that "low hedge" means
/// <c>PPID_Helios_JuniperHedge_Straight</c>, cannot tell you which of that prop's three identities
/// each of those calls wants, and will hang if you search the project unscoped. All three of those
/// are answered here, offline, once per game build.
/// </para>
/// <para>
/// Every phase degrades per row. A prop whose blueprint will not load still ships with its PPID and
/// its name; the failure goes to <c>dump-report.log</c> and the dump keeps going. A dataset with
/// holes in it is worth far more than no dataset.
/// </para>
/// </summary>
public sealed class AssetIndexDumper(
    HeadlessLoader loader,
    AssetQuery assets,
    DisplayNameIndex names)
{
    /// <summary>
    /// Name families a builder reaches for constantly. Together with gallery membership these mark
    /// the "core" subset, which no longer gets a file of its own - it was measured at 99.2% of the
    /// full list, because nearly every prop belongs to some gallery - but still gates the expensive
    /// hops: <c>--tier a</c> resolves blueprints and meshes for core rows only, and
    /// <c>--bounds core</c> measures only those.
    /// </summary>
    private static readonly string[] CuratedFamilies =
    [
        "hedge", "bush", "shrub", "tree", "palm", "pine", "grass", "fern", "flower", "vine", "ivy",
        "rock", "boulder", "stone", "cliff", "pebble", "log", "stump", "branch",
        "wall", "floor", "ceiling", "roof", "door", "window", "stair", "stairs", "ramp", "railing",
        "fence", "gate", "pillar", "column", "beam", "arch", "bridge", "platform", "roadpiece",
        "bench", "chair", "table", "desk", "shelf", "crate", "barrel", "box", "cabinet", "sofa",
        "lamp", "light", "torch", "lantern", "sign", "banner", "flag", "poster",
        "road", "path", "sidewalk", "curb", "planter", "pot", "fountain", "statue", "tent", "awning",
        "water", "pool", "waterfall", "cloud", "smoke", "fire"
    ];

    /// <summary>
    /// Tokens too generic to help anybody find anything. The archive-plumbing ones ("setup",
    /// "assets", "comp") matter most: they appear in nearly every PPID path, so leaving them in
    /// would put a token on 26k rows that narrows a search to 26k rows.
    /// </summary>
    private static readonly HashSet<string> StopTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "of", "a", "an", "with", "for", "to", "in", "on", "at", "by",
        "prop", "props", "ppid", "pid", "bp", "sm", "gallery", "item", "definition",
        "new", "old", "type", "variant", "set", "01", "02", "03", "04", "05",
        "setup", "assets", "maps", "ppids", "comp", "fnec", "playsetprops", "content"
    };

    public async Task<IndexDumpResult> RunAsync(IndexDumpOptions options, CancellationToken token = default)
    {
        var overall = Stopwatch.StartNew();
        var timings = new Dictionary<string, TimeSpan>();
        var failures = new ConcurrentBag<string>();

        var indexDirectory = Path.Combine(options.OutputDirectory, "index");
        Directory.CreateDirectory(indexDirectory);

        // ---------------------------------------------------------------- P1: canonical props
        var phase = Stopwatch.StartNew();
        var propEntry = AssetQuery.ResolveCategory("Prop");
        var canonical = await assets.CanonicalAsync(propEntry, names, token);
        timings["P1 canonical"] = phase.Elapsed;

        Log.Information("[INDEX] P1: {Count:N0} canonical props from {Rows:N0} registry rows ({Collapsed:N0} folded) in {Seconds:N1}s",
            canonical.Count, canonical.RegistryRows, canonical.CollapsedRows, phase.Elapsed.TotalSeconds);

        if (!canonical.NameIndexReady)
            Log.Warning("[INDEX] Display-name index was not ready; names and dedupe are provisional");

        // Every path that could name a prop - its own and every rarity clone folded onto it - so a
        // gallery referencing the clone still joins to the surviving row.
        var propByPath = new Dictionary<string, CategoryItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in canonical.Items)
        {
            propByPath.TryAdd(PackageKey(item.ObjectPath), item);
            foreach (var clone in item.CollapsedPaths) propByPath.TryAdd(PackageKey(clone), item);
        }

        // ---------------------------------------------------------------- P2: galleries
        phase.Restart();
        var galleries = await BuildGalleriesAsync(options, propByPath, failures, token);
        timings["P2 galleries"] = phase.Elapsed;

        Log.Information("[INDEX] P2: {Count:N0} galleries, {Members:N0} membership links in {Seconds:N1}s",
            galleries.Rows.Count, galleries.MembershipByProp.Sum(pair => pair.Value.Count), phase.Elapsed.TotalSeconds);

        // ---------------------------------------------------------------- P3/P4: placement + mesh
        phase.Restart();
        var rows = await BuildPropRowsAsync(options, canonical.Items, galleries, failures, token);
        timings["P3/P4 props"] = phase.Elapsed;

        var fullRows = rows.Select(row => row.Row).ToList();

        Log.Information("[INDEX] P3/P4: {Full:N0} rows ({Core:N0} core), {Bp:N0} with a blueprint, {Sm:N0} with a mesh, {Sz:N0} with a size, in {Seconds:N1}s",
            fullRows.Count, rows.Count(row => row.IsCore),
            fullRows.Count(row => row.Bp is not null), fullRows.Count(row => row.Sm is not null),
            fullRows.Count(row => row.Sz is not null), phase.Elapsed.TotalSeconds);

        // ---------------------------------------------------------------- P5: scopes + reachability
        phase.Restart();
        var mounts = MountMapper.Load(MountVerificationPath());
        var scopes = BuildScopes(rows, mounts);

        var verifiedScopes = scopes.Count(scope => scope.Status is MountStatus.Verified);
        var missingScopes = scopes.Count(scope => scope.Status is MountStatus.Missing);
        var missingScopeIds = scopes
            .Where(scope => scope.Status is MountStatus.Missing)
            .Select(scope => scope.ScopeId)
            .ToHashSet(StringComparer.Ordinal);

        // Reachability is per identity, and that is a measured rule rather than a cautious guess:
        // placing the PPID of a row in a missing mount was probed on 2026-09-01 and FAILED with
        // "Could not load asset at path". A missing mount does not merely hide an asset from the
        // content browser - it makes every identity under it unusable, placement included.
        fullRows = fullRows.Select(row => row with { Reach = ReachOf(row, missingScopeIds) }).ToList();
        timings["P5 scopes"] = phase.Elapsed;

        var unreachable = fullRows.Count(row => row.Reach == PropRow.Unreachable);
        var partiallyReachable = fullRows.Count(row => row.Reach is not null && row.Reach != PropRow.Unreachable);

        Log.Information("[INDEX] P5: {Total} scopes - {Verified} verified, {Missing} missing, {Unverified} unverified",
            scopes.Count, verifiedScopes, missingScopes, scopes.Count - verifiedScopes - missingScopes);
        Log.Information("[INDEX] Reachability: {Full:N0} rows fully reachable, {Partial:N0} partially ({Unreachable:N0} UNREACHABLE - every identity sits in a missing mount)",
            fullRows.Count - unreachable - partiallyReachable, partiallyReachable, unreachable);

        // ---------------------------------------------------------------- P6: write
        phase.Restart();

        var galleryRows = galleries.Rows
            .Select(row => row with { N = galleries.MembershipByProp.Count(pair => pair.Value.Contains(row.Id)) })
            .OrderBy(row => row.Name, StringComparer.Ordinal)
            .ThenBy(row => row.Asset, StringComparer.Ordinal)
            .ToList();

        // props-core.jsonl used to sit beside this one, carrying gallery members plus curated
        // families. It was measured at 99.2% of props-full on 42.00 - nearly every prop belongs to
        // some gallery - so it cost 18 MB to say the same thing twice and was dropped. The core
        // subset still exists as the tier/bounds gate; it just no longer earns a file.
        var fullCount = await IndexWriters.WriteJsonLinesAsync(Path.Combine(indexDirectory, "props-full.jsonl"), fullRows);
        var galleryCount = await IndexWriters.WriteJsonLinesAsync(Path.Combine(indexDirectory, "galleries.jsonl"), galleryRows);
        await IndexWriters.WriteScopesAsync(Path.Combine(indexDirectory, "scopes.tsv"), scopes);
        await WriteFailuresAsync(Path.Combine(indexDirectory, "dump-report.log"), failures);

        var counts = new IndexCounts
        {
            FullRows = fullCount,
            Galleries = galleryCount,
            Scopes = scopes.Count,
            Failures = failures.Count,
            PartiallyReachableRows = partiallyReachable,
            UnreachableRows = unreachable
        };

        var gameVersion = loader.GameVersion ?? SafeUnrealVersion();
        await IndexWriters.WriteAtlasAsync(Path.Combine(indexDirectory, "atlas.md"), scopes, counts, gameVersion);

        // META carries the only non-deterministic field in the dataset, deliberately: two dumps of
        // the same archive must differ in generatedUtc and nowhere else.
        await IndexWriters.WriteMetaAsync(Path.Combine(indexDirectory, "META.json"), new
        {
            schemaVersion = IndexWriters.SchemaVersion,
            generatedUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            gameVersion,
            unrealVersion = SafeUnrealVersion(),
            tier = options.Tier.ToString().ToLowerInvariant(),
            bounds = options.Bounds.ToString().ToLowerInvariant(),
            registryRows = loader.AssetRegistry.Count,
            propRegistryRows = canonical.RegistryRows,
            propCollapsedRows = canonical.CollapsedRows,
            nameIndexReady = canonical.NameIndexReady,
            scopeStatus = new
            {
                verified = verifiedScopes,
                missing = missingScopes,
                unverified = scopes.Count - verifiedScopes - missingScopes
            },
            reachability = new
            {
                fullyReachable = fullRows.Count - unreachable - partiallyReachable,
                partiallyReachable,
                unreachable
            },
            files = new
            {
                propsFull = fullCount,
                galleries = galleryCount,
                scopes = scopes.Count,
                failures = failures.Count
            }
        });

        timings["P6 write"] = phase.Elapsed;
        overall.Stop();

        return new IndexDumpResult
        {
            IndexDirectory = indexDirectory,
            Counts = counts,
            RegistryRows = loader.AssetRegistry.Count,
            Elapsed = overall.Elapsed,
            PhaseTimings = timings
        };
    }

    // ---------------------------------------------------------------- P2

    private sealed record GalleryPass(List<GalleryRow> Rows, Dictionary<string, HashSet<string>> MembershipByProp, Dictionary<string, List<string>> TokensByProp);

    /// <summary>
    /// Every FortPlaysetItemDefinition, one package load each.
    /// <para>
    /// Members are read as raw <c>FSoftObjectPath</c> STRINGS and joined against the canonical prop
    /// list by path - deliberately never <c>TryLoad</c>ed. Loading them is what makes the naive
    /// version of this pass unusably slow: a gallery's members are props we are already opening in
    /// P3, so loading them here would double the archive work for nothing.
    /// </para>
    /// </summary>
    private async Task<GalleryPass> BuildGalleriesAsync(
        IndexDumpOptions options,
        Dictionary<string, CategoryItem> propByPath,
        ConcurrentBag<string> failures,
        CancellationToken token)
    {
        var registryRows = loader.AssetRegistry
            .Where(data => data.AssetClass.Text.Equals("FortPlaysetItemDefinition", StringComparison.Ordinal))
            .OrderBy(data => data.ObjectPath, StringComparer.Ordinal)
            .ToList();

        var collected = new ConcurrentBag<(GalleryRow Row, List<string> MemberKeys)>();

        await Parallel.ForEachAsync(registryRows,
            new ParallelOptions { MaxDegreeOfParallelism = options.Parallelism, CancellationToken = token },
            async (data, _) =>
            {
                var assetName = data.AssetName.Text;
                try
                {
                    var asset = await loader.Provider.SafeLoadPackageObjectAsync(data.ObjectPath);
                    if (asset is null)
                    {
                        failures.Add($"{assetName}\tgallery\tpackage did not load ({data.ObjectPath})");
                        return;
                    }

                    var uefnPath = MountMapper.ToUefnPath(data.ObjectPath);
                    if (uefnPath is null)
                    {
                        failures.Add($"{assetName}\tgallery\tno known mount for {data.ObjectPath}");
                        return;
                    }

                    var displayName = ExportRunner.DisplayNameOf(asset) ?? CategoryCatalog.PrettifyAssetName(assetName);
                    var (memberKeys, source) = ReadGalleryMembers(asset);

                    var scope = MountMapper.ScopeFor(uefnPath, data.PackageName.Text);
                    var tokens = Tokenize(displayName).Concat(Tokenize(assetName)).ToList();

                    collected.Add((new GalleryRow
                    {
                        Id = assetName,
                        Name = displayName,
                        Asset = uefnPath,
                        Sc = scope?.ScopeId,
                        Src = source,
                        Kw = Distinct(tokens)
                    }, memberKeys));
                }
                catch (Exception e)
                {
                    failures.Add($"{assetName}\tgallery\tthrew: {e.Message}");
                }
            });

        // Deterministic id assignment before anything joins on it.
        var ordered = collected
            .OrderBy(entry => entry.Row.Asset, StringComparer.Ordinal)
            .ToList();

        var idCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var rows = new List<GalleryRow>(ordered.Count);
        var membershipByProp = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var tokensByProp = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (row, memberKeys) in ordered)
        {
            var id = Uniquify(row.Id, idCounts);
            var finalRow = row with { Id = id };
            rows.Add(finalRow);

            foreach (var key in memberKeys)
            {
                if (!propByPath.TryGetValue(key, out var prop)) continue;

                var propKey = PackageKey(prop.ObjectPath);
                if (!membershipByProp.TryGetValue(propKey, out var set))
                    membershipByProp[propKey] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(id);

                // A gallery's name is the best plain-English label its members have. "Battlewood
                // Boulevard Nature" is how a human asks for the hedge; the hedge's own name does
                // not contain the word Battlewood.
                if (!tokensByProp.TryGetValue(propKey, out var list))
                    tokensByProp[propKey] = list = [];
                list.AddRange(finalRow.Kw);
            }
        }

        return new GalleryPass(rows, membershipByProp, tokensByProp);
    }

    /// <summary>
    /// AssociatedPlaysetProps is the authoritative flat member list. The save-record collection is a
    /// fallback only, for galleries that ship it empty; its entries usually resolve to the
    /// ULevelSaveRecord sub-object rather than the owning prop definition, so those are lifted to
    /// their outer. Merging both unconditionally would double-count every prop.
    /// </summary>
    private static (List<string> Keys, string Source) ReadGalleryMembers(UObject gallery)
    {
        var keys = new List<string>();

        foreach (var softPath in gallery.GetOrDefault<FSoftObjectPath[]>("AssociatedPlaysetProps", []))
        {
            var text = softPath.AssetPathName.Text;
            if (string.IsNullOrWhiteSpace(text) || text.Equals("None", StringComparison.Ordinal)) continue;

            keys.Add(PackageKey(text));
        }

        if (keys.Count > 0) return (keys, "associated");

        try
        {
            var collectionLazy = gallery.GetOrDefault<FPackageIndex?>("PlaysetPropLevelSaveRecordCollection");
            if (collectionLazy is { IsNull: false } && collectionLazy.TryLoad(out var collection) && collection is not null)
            {
                foreach (var item in collection.GetOrDefault<FStructFallback[]>("Items", []))
                {
                    var record = item.GetOrDefault<UObject?>("LevelSaveRecord");
                    if (record is null) continue;

                    if (record.ExportType.Equals("LevelSaveRecord", StringComparison.Ordinal))
                        record = record.Outer?.Load() ?? record;

                    keys.Add(PackageKey(record.GetPathName()));
                }
            }
        }
        catch (Exception e)
        {
            Log.Debug("Save-record member fallback failed for {Name}: {Message}", gallery.Name, e.Message);
        }

        return (keys, keys.Count > 0 ? "saveRecords" : "none");
    }

    // ---------------------------------------------------------------- P3/P4

    /// <summary><paramref name="Scopes"/> is every mount this row reaches: its PPID, its blueprint and its mesh.</summary>
    private sealed record BuiltRow(PropRow Row, bool IsCore, IReadOnlyList<ScopeInfo> Scopes, string AssetName);

    /// <summary>The scope of an already-UEFN path, or null when there is no path.</summary>
    private static ScopeInfo? ScopeOf(string? uefnPath)
        => uefnPath is null ? null : MountMapper.ScopeFor(uefnPath, MountMapper.PackageHalf(uefnPath));

    /// <summary>
    /// Which of a row's three identities are NOT in a known-missing mount.
    /// <para>
    /// Null - the common case - means every identity the row has is usable, and costs no bytes in
    /// the shipped file. A value names the survivors ("bp+sm"), and
    /// <see cref="PropRow.Unreachable"/> means none of them survive: that asset cannot be reached
    /// by any route this season.
    /// </para>
    /// <para>
    /// "Not known-missing" is the honest reading. An unverified mount is untested, not proven good,
    /// so this field rules routes OUT rather than promising the rest work.
    /// </para>
    /// </summary>
    private static string? ReachOf(PropRow row, HashSet<string> missingScopeIds)
    {
        var usable = new List<string>(3);
        var blocked = false;

        void Check(string label, string? path)
        {
            if (path is null) return;

            // An unmappable path is unknown, not blocked - it must not fake an unreachable row.
            if (ScopeOf(path) is not { } scope) return;

            if (missingScopeIds.Contains(scope.ScopeId)) blocked = true;
            else usable.Add(label);
        }

        Check("ppid", row.Ppid);
        Check("bp", row.Bp);
        Check("sm", row.Sm);

        if (!blocked) return null;
        return usable.Count == 0 ? PropRow.Unreachable : string.Join('+', usable);
    }

    private async Task<List<BuiltRow>> BuildPropRowsAsync(
        IndexDumpOptions options,
        IReadOnlyList<CategoryItem> items,
        GalleryPass galleries,
        ConcurrentBag<string> failures,
        CancellationToken token)
    {
        var resolver = new PropMeshResolver(loader);
        var built = new ConcurrentBag<BuiltRow>();

        await Parallel.ForEachAsync(items,
            new ParallelOptions { MaxDegreeOfParallelism = options.Parallelism, CancellationToken = token },
            async (item, _) =>
            {
                var assetName = item.AssetName;
                try
                {
                    var uefnPath = MountMapper.ToUefnPath(item.ObjectPath);
                    if (uefnPath is null)
                    {
                        failures.Add($"{assetName}\tprop\tno known mount for {item.ObjectPath}");
                        return;
                    }

                    var ppid = EnsureObjectHalf(uefnPath);
                    var scope = MountMapper.ScopeFor(uefnPath, item.PackagePath);

                    var propKey = PackageKey(item.ObjectPath);
                    var galleryIds = galleries.MembershipByProp.TryGetValue(propKey, out var ids)
                        ? ids.Order(StringComparer.Ordinal).ToList()
                        : [];

                    var nameTokens = Tokenize(item.DisplayName).Concat(Tokenize(assetName)).ToList();
                    var isCore = galleryIds.Count > 0 || IsCuratedFamily(nameTokens);

                    var wantChain = options.Tier is IndexTier.B || isCore;
                    var wantBounds = options.Bounds switch
                    {
                        BoundsScope.All => true,
                        BoundsScope.Core => isCore,
                        _ => false
                    };

                    PropResolution resolution;
                    var tags = new List<string>();

                    var asset = await loader.Provider.SafeLoadPackageObjectAsync(item.ObjectPath);
                    if (asset is null)
                    {
                        resolution = new PropResolution { Failure = "prop package did not load" };
                    }
                    else
                    {
                        resolution = resolver.ReadPlacement(asset);
                        tags = ReadCreativeTags(asset);

                        if (wantChain && resolution.BlueprintClassPath is not null)
                            resolution = await resolver.ResolveMeshAsync(resolution, wantBounds);
                    }

                    if (resolution.Failure is { } failure)
                        failures.Add($"{assetName}\tprop\t{failure}");

                    // Two vocabularies, deliberately separated. kw is what this asset IS - its own
                    // name, its theme, its creative tags - and is precise enough to rank on. gkw is
                    // everything the galleries it appears in are called, which is pure recall: a
                    // gallery named "...Prefab_Greenhouse" stamps "greenhouse" onto all 111 of its
                    // members, boomboxes and tacos included. Merged into one field, as they were,
                    // the recall tokens drown the precise ones and there is no way to rank.
                    var keywords = nameTokens
                        .Concat(scope is null || scope.Theme.Length == 0 ? [] : Tokenize(scope.Theme))
                        .Concat(tags.SelectMany(Tokenize))
                        .ToList();

                    var kw = Distinct(keywords);
                    var ownTokens = kw.ToHashSet(StringComparer.Ordinal);

                    // Anything the row already earns on its own name is not worth repeating here.
                    var galleryKeywords = Distinct(
                        (galleries.TokensByProp.TryGetValue(propKey, out var galleryTokens) ? galleryTokens : [])
                        .Where(token => !ownTokens.Contains(token)));

                    // A row can reach into three different mounts: the PPID's, the blueprint's and
                    // the mesh's. All three are search targets, so all three earn a scope row.
                    var referenced = new[]
                        {
                            scope,
                            ScopeOf(resolution.BlueprintClassPath),
                            ScopeOf(resolution.StaticMeshPath)
                        }
                        .Where(value => value is not null)
                        .Select(value => value!)
                        .DistinctBy(value => value.ScopeId)
                        .ToList();

                    built.Add(new BuiltRow(new PropRow
                    {
                        Id = assetName,
                        Name = item.DisplayName,
                        Ppid = ppid,
                        Bp = resolution.BlueprintClassPath,
                        Sm = resolution.StaticMeshPath,
                        Sz = resolution.Size,
                        Sc = scope?.ScopeId,
                        Cat = CategoryOf(tags),
                        Frag = LooksLikeFragment(nameTokens) ? true : null,
                        Gal = galleryIds,
                        Kw = kw,
                        Gkw = galleryKeywords.Count > 0 ? galleryKeywords : null
                    }, isCore, referenced, assetName));
                }
                catch (Exception e)
                {
                    failures.Add($"{assetName}\tprop\tthrew: {e.Message}");
                }
            });

        // Sort first, then hand out ids, so a duplicate name always gets the same suffix.
        var ordered = built
            .OrderBy(row => row.Row.Name, StringComparer.Ordinal)
            .ThenBy(row => row.Row.Ppid, StringComparer.Ordinal)
            .ToList();

        var idCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        return ordered
            .Select(row => row with { Row = row.Row with { Id = Uniquify(row.Row.Id, idCounts) } })
            .ToList();
    }

    /// <summary>Creative tags, which are the closest thing a prop carries to a human category.</summary>
    private static List<string> ReadCreativeTags(UObject asset)
    {
        try
        {
            var helper = asset.GetOrDefault<FStructFallback?>("CreativeTagsHelper");
            var tags = helper?.GetOrDefault<FName[]>("CreativeTags") ?? [];
            return tags.Select(tag => tag.Text).Where(text => !string.IsNullOrWhiteSpace(text)).ToList();
        }
        catch (Exception e)
        {
            Log.Debug("Creative tag read failed for {Name}: {Message}", asset.Name, e.Message);
            return [];
        }
    }

    /// <summary>"Prop.Nature.Tree" -> "Nature": the segment a human would call the category.</summary>
    private static string? CategoryOf(List<string> tags)
    {
        foreach (var tag in tags)
        {
            var segments = tag.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2) return segments[1];
            if (segments.Length == 1) return segments[0];
        }

        return null;
    }

    /// <summary>
    /// Matched on whole tokens, never substrings: a substring test makes "cat" hit "Cathedral" and
    /// "pot" hit "Spotlight", which is how a curated list stops being curated.
    /// </summary>
    private static bool IsCuratedFamily(IEnumerable<string> tokens)
        => tokens.Any(token => CuratedFamilySet.Contains(token));

    private static readonly HashSet<string> CuratedFamilySet = new(CuratedFamilies, StringComparer.Ordinal);

    // ---------------------------------------------------------------- P5

    /// <summary>
    /// Every mount any shipped path points into, with how many rows reach it.
    /// <para>
    /// A row is counted against all three of its mounts, not just its PPID's. The PPID lives in a
    /// composition plugin ("/Burd_Comp") while the blueprint and mesh usually live in the shared
    /// environment content ("/Game/Environments") - and it is the second one an agent scopes
    /// <c>find_assets</c> to when it wants a picture. Counting only the PPID's mount would leave the
    /// verified scopes out of the table entirely.
    /// </para>
    /// </summary>
    private static List<ScopeRow> BuildScopes(List<BuiltRow> rows, MountMapper mapper)
    {
        var byScope = new Dictionary<string, List<(BuiltRow Row, ScopeInfo Scope)>>(StringComparer.Ordinal);
        foreach (var row in rows)
        foreach (var scope in row.Scopes)
        {
            if (!byScope.TryGetValue(scope.ScopeId, out var list)) byScope[scope.ScopeId] = list = [];
            list.Add((row, scope));
        }

        return byScope
            .Select(pair =>
            {
                var members = pair.Value;
                var first = members.OrderBy(entry => entry.Row.Row.Ppid, StringComparer.Ordinal).First();

                // The registry prefix is per-row; the shortest one seen is the mount itself.
                var registryPrefix = members
                    .Select(entry => entry.Scope.RegistryPrefix)
                    .Where(prefix => prefix.Length > 0)
                    .OrderBy(prefix => prefix.Length).ThenBy(prefix => prefix, StringComparer.Ordinal)
                    .FirstOrDefault() ?? string.Empty;

                // Theme is per-row too (a scope spans many). Report the commonest.
                var theme = members
                    .Select(entry => entry.Scope.Theme)
                    .Where(value => value.Length > 0)
                    .GroupBy(value => value, StringComparer.Ordinal)
                    .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal)
                    .FirstOrDefault()?.Key ?? string.Empty;

                return new ScopeRow
                {
                    ScopeId = pair.Key,
                    UefnPath = first.Scope.UefnPath,
                    RegistryPrefix = registryPrefix,
                    Theme = theme,
                    RowCount = members.Count,
                    SampleAssetName = first.Scope.Leaf,
                    Verified = mapper.VerifiedFor(first.Scope.UefnPath),
                    Status = mapper.StatusFor(first.Scope.UefnPath),
                    Note = mapper.NoteFor(first.Scope.UefnPath),
                    Vocabulary = TopTokens(members.Select(entry => entry.Row).ToList(), 8)
                };
            })
            .OrderBy(scope => scope.UefnPath, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The display-name tokens that actually distinguish a scope, most frequent first.</summary>
    private static List<string> TopTokens(List<BuiltRow> rows, int take)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in rows)
        foreach (var token in Tokenize(row.Row.Name))
            counts[token] = counts.GetValueOrDefault(token) + 1;

        return counts
            .Where(pair => pair.Value > 1 && pair.Key.Length > 2)
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Take(take)
            .Select(pair => pair.Key)
            .ToList();
    }

    // ---------------------------------------------------------------- helpers

    private static async Task WriteFailuresAsync(string path, ConcurrentBag<string> failures)
    {
        var ordered = failures.OrderBy(line => line, StringComparer.Ordinal).ToList();

        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n" };

        await writer.WriteLineAsync("# id\tstage\treason - one line per row that lost a field. A row is still shipped.");
        foreach (var line in ordered) await writer.WriteLineAsync(line);
    }

    /// <summary>The verification file next to the exe, falling back to the source tree during development.</summary>
    private static string? MountVerificationPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Config", "mount-verification.json"),
            Path.Combine(AppContext.BaseDirectory, "mount-verification.json")
        };

        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    private string SafeUnrealVersion()
    {
        try { return loader.Provider.Versions.Game.ToString(); }
        catch { return "unknown"; }
    }

    /// <summary>Lowercased package half of any path shape, so registry and soft paths compare equal.</summary>
    private static string PackageKey(string path)
    {
        var trimmed = path.Trim().Replace('\\', '/').TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        var dot = trimmed.LastIndexOf('.');
        if (dot > slash) trimmed = trimmed[..dot];

        // A soft path may be mount-relative and a registry path archive-absolute; compare on the
        // tail, which is identical either way.
        var uefn = MountMapper.ToUefnPath(trimmed) ?? trimmed;
        return uefn.TrimStart('/').ToLowerInvariant();
    }

    /// <summary>"/A/B/C" -> "/A/B/C.C". UEFN placement needs the object half, not the package.</summary>
    private static string EnsureObjectHalf(string path)
    {
        var slash = path.LastIndexOf('/');
        var dot = path.LastIndexOf('.');
        if (dot > slash) return path;

        return $"{path}.{path[(slash + 1)..]}";
    }

    /// <summary>
    /// Search tokens for one string: every CamelCase part, PLUS the joined form of each
    /// underscore-delimited segment that had more than one part.
    /// <para>
    /// Both halves are load-bearing and each was learned from a failed search. Splitting alone
    /// loses the compound a human actually types - "GreenHouse" becomes green + house, so
    /// <c>greenhouse</c> matches nothing, and "PrincessCastle" becomes princess + castle, so
    /// <c>princesscastle</c> matches nothing. Joining alone loses the words - a customer asking for
    /// "a princess castle hedge" searches the parts. Emitting both costs about one extra token per
    /// compound segment and makes every phrasing hit.
    /// </para>
    /// </summary>
    private static IEnumerable<string> Tokenize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) yield break;

        var buffer = new StringBuilder();
        var segment = new List<string>();
        var emitted = new List<string>();

        void FlushPart()
        {
            if (buffer.Length == 0) return;
            segment.Add(buffer.ToString());
            buffer.Clear();
        }

        void FlushSegment()
        {
            FlushPart();
            if (segment.Count == 0) return;

            emitted.AddRange(segment);

            // The compound only says something new when it had parts to join.
            if (segment.Count > 1) emitted.Add(string.Concat(segment));

            segment.Clear();
        }

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (!char.IsLetterOrDigit(c))
            {
                FlushSegment();
                continue;
            }

            // Split CamelCase so "JuniperHedgeStraight" yields three usable tokens.
            if (char.IsUpper(c) && buffer.Length > 0 && !char.IsUpper(value[i - 1]))
                FlushPart();

            buffer.Append(char.ToLowerInvariant(c));
        }

        FlushSegment();

        foreach (var part in emitted)
        {
            if (part.Length < 2) continue;
            if (StopTokens.Contains(part)) continue;
            if (LooksLikeHash(part)) continue;
            yield return part;
        }
    }

    /// <summary>
    /// Name markers for a piece that is one unit of a larger assembly rather than a whole object.
    /// <para>
    /// Matched as a token PREFIX, so "Corner01" and "Segment" both count. Measured on 42.00 before
    /// shipping: 1,275 rows, 4.8% of the index, and a sample of the Corner hits was entirely
    /// modular kit geometry (wall corners, trim corners, stair corners) rather than false
    /// positives - which is why the flag ships rather than being dropped as noise.
    /// </para>
    /// </summary>
    private static readonly string[] FragmentMarkers = ["quarter", "half", "corner", "seg", "piece"];

    private static bool LooksLikeFragment(IEnumerable<string> nameTokens)
        => nameTokens.Any(token => FragmentMarkers.Any(marker => token.StartsWith(marker, StringComparison.Ordinal)));

    /// <summary>
    /// PPID asset names end in an 8-character uniquifying hash. Indexing it gives every row a token
    /// nobody will ever type, at 9 bytes a row across the whole dataset.
    /// </summary>
    private static bool LooksLikeHash(string token)
        => token.Length >= 8 && token.All(Uri.IsHexDigit) && token.Any(char.IsDigit) && token.Any(char.IsLetter);

    private static List<string> Distinct(IEnumerable<string> tokens)
        => tokens.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

    private static string Uniquify(string id, Dictionary<string, int> counts)
    {
        var seen = counts.GetValueOrDefault(id);
        counts[id] = seen + 1;
        return seen == 0 ? id : $"{id}#{seen + 1}";
    }
}
