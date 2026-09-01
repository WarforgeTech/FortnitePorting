using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using FortnitePorting;
using FortnitePorting.Mcp.Config;
using Serilog;

namespace FortnitePorting.Mcp.Core;

/// <summary>Per-category build state of <see cref="DisplayNameIndex"/>.</summary>
public abstract record NameIndexState
{
    public sealed record NotBuilt : NameIndexState;
    public sealed record Building(int Done, int Total) : NameIndexState;
    public sealed record Ready(int Count, int Rows, bool FromCache) : NameIndexState;
    public sealed record Failed(string Message) : NameIndexState;

    /// <summary>
    /// Not in memory yet, but a disk cache exists and will be loaded the moment anything asks for
    /// this category. Reported by get_status so a fresh process does not claim "no display-name
    /// search available" when a search in that same process would answer instantly.
    /// </summary>
    public sealed record Cached(int Count) : NameIndexState;

    /// <summary>
    /// The category has no registry-backed classes at all (Wildlife, WeaponMod): its assets are
    /// hand-authored in the catalog and carry their names with them, so there is nothing to index
    /// and search_assets always matches them by name. Never "not built".
    /// </summary>
    public sealed record Catalog : NameIndexState;

    public string Name => this switch
    {
        Ready => "ready",
        Cached => "cached",
        Catalog => "catalog",
        Building => "building",
        Failed => "failed",
        _ => "notBuilt"
    };

    /// <summary>True when a lookup will succeed now or within a disk read.</summary>
    public bool IsUsable => this is Ready or Cached or Catalog;

    public float Percent => this switch
    {
        Ready => 100f,
        Cached => 100f,
        Catalog => 100f,
        Building building => building.Total == 0 ? 100f : building.Done * 100f / building.Total,
        _ => 0f
    };
}

/// <summary>
/// objectPath -> displayName for every catalog category, so search can match what users actually
/// type ("Peely", "Battlewood Boulevard") instead of only the internal asset name
/// (CID_349_Athena_Commando_M_Banana).
///
/// Building a category means opening every one of its registry-filtered packages and running that
/// category's DisplayNameHandler, so results are cached to
/// <c>&lt;DataDirectory&gt;\NameIndex\{category}.json</c> and only rebuilt when the validity stamp
/// (game version + that category's registry row count) changes. The background build is
/// fire-and-forget and smallest-category-first; it must never take the server down, so every
/// failure path degrades to "this category has no display names" rather than throwing.
///
/// Lookups are plain in-memory dictionary hits - nothing on the search path may load a package.
/// </summary>
public sealed class DisplayNameIndex(HeadlessLoader loader, AssetQuery assets, McpConfig config)
{
    /// <summary>How long a tool is willing to wait for a category before falling back to name-only search.</summary>
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromSeconds(3);

    private const int MaxDegreeOfParallelism = 12;
    private const int ProgressEvery = 5_000;

    /// <summary>
    /// Bumped whenever a DisplayNameHandler changes what it produces, so on-disk caches written by
    /// an older build are discarded even though the game version and row count are unchanged.
    /// v2: Banner now indexes its asset name (all ~1000 rows shared the string "Banner Icon").
    /// </summary>
    private const int SchemaVersion = 2;

    private static readonly IReadOnlyDictionary<string, string> EmptyMap =
        new Dictionary<string, string>(0, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Categories deliberately kept out of the automatic background build (still buildable on
    /// demand). Measured build times live in the README; nothing is excluded today.
    /// </summary>
    private static readonly HashSet<EExportType> AutoBuildExclusions = [];

    private sealed class CategoryState
    {
        public required AssetCategoryEntry Entry { get; init; }
        public NameIndexState State { get; set; } = new NameIndexState.NotBuilt();
        public IReadOnlyDictionary<string, string> Map { get; set; } = EmptyMap;
        public Task? Build { get; set; }
        public readonly object Sync = new();
    }

    private readonly ConcurrentDictionary<EExportType, CategoryState> _categories = new();
    private readonly Lazy<Dictionary<string, EExportType[]>> _classToTypes = new(() =>
    {
        var map = new Dictionary<string, List<EExportType>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in CategoryCatalog.Entries)
        foreach (var className in entry.ClassNames)
        {
            if (!map.TryGetValue(className, out var list)) map[className] = list = [];
            list.Add(entry.Type);
        }

        return map.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
    });

    private int _backgroundStarted;
    private Task? _backgroundBuild;

    private DirectoryInfo CacheFolder => new(Path.Combine(config.DataDirectory, "NameIndex"));

    // ---------------------------------------------------------------- lookups (hot path)

    /// <summary>The finished map for a category, or null while it is still building.</summary>
    public IReadOnlyDictionary<string, string>? MapFor(EExportType type)
    {
        var state = Get(type);
        return state is { State: NameIndexState.Ready } ? state.Map : null;
    }

    public bool IsReady(EExportType type) => Get(type)?.State is NameIndexState.Ready;

    public NameIndexState StateFor(EExportType type) => Get(type)?.State ?? new NameIndexState.NotBuilt();

    public int CountFor(EExportType type) => Get(type)?.State is NameIndexState.Ready ready ? ready.Count : 0;

    /// <summary>Display name for an object path within one category, or null (unknown / not built).</summary>
    public string? DisplayNameFor(EExportType type, string objectPath)
        => MapFor(type)?.GetValueOrDefault(objectPath);

    /// <summary>
    /// Display name for a row when the caller has no category filter: an asset class can appear in
    /// more than one catalog entry (AthenaCharacterItemDefinition is both Outfit and FallGuysOutfit),
    /// so every entry that claims the class is probed. Still just 1-2 dictionary hits.
    /// </summary>
    public string? DisplayNameForClass(string className, string objectPath)
    {
        if (!_classToTypes.Value.TryGetValue(className, out var types)) return null;

        foreach (var type in types)
        {
            if (MapFor(type)?.GetValueOrDefault(objectPath) is { } name) return name;
        }

        return null;
    }

    /// <summary>True when at least one category that claims this class has a finished index.</summary>
    public bool IsClassCovered(string className)
    {
        if (!_classToTypes.Value.TryGetValue(className, out var types)) return false;
        return types.Any(IsReady);
    }

    // ---------------------------------------------------------------- status

    public record CategorySnapshot(string Category, NameIndexState State, int Count, int Rows);

    /// <summary>
    /// Per-category state for get_status. A category that is not in memory yet is probed on disk
    /// (a ~200-byte header read, never the whole file) and reported as "cached" with its row count,
    /// because a search in this same process would load it and answer. Reporting those as "notBuilt"
    /// told clients display-name search was unavailable when it demonstrably was not.
    /// </summary>
    public IReadOnlyList<CategorySnapshot> Snapshot()
    {
        var list = new List<CategorySnapshot>();
        foreach (var entry in CategoryCatalog.Entries)
        {
            var state = Get(entry.Type);
            var current = state?.State ?? new NameIndexState.NotBuilt();

            if (current is NameIndexState.NotBuilt or NameIndexState.Ready { Rows: 0 } && entry.ClassNames.Length == 0)
                current = new NameIndexState.Catalog();
            else if (current is NameIndexState.NotBuilt && PeekCacheCount(entry.Type) is { } cachedCount)
                current = new NameIndexState.Cached(cachedCount);

            var (count, rows) = current switch
            {
                NameIndexState.Ready ready => (ready.Count, ready.Rows),
                NameIndexState.Cached cached => (cached.Count, 0),
                NameIndexState.Building building => (state?.Map.Count ?? 0, building.Total),
                _ => (0, 0)
            };

            list.Add(new CategorySnapshot(entry.Type.ToString(), current, count, rows));
        }

        return list;
    }

    public int ReadyCategoryCount => CategoryCatalog.Entries.Count(entry => IsReady(entry.Type));
    public int TotalCategoryCount => CategoryCatalog.Entries.Count;
    public int TotalNames => CategoryCatalog.Entries.Sum(entry => CountFor(entry.Type));

    /// <summary>Categories that are not in memory but have a usable disk cache behind them.</summary>
    public int CachedCategoryCount => Snapshot().Count(snapshot => snapshot.State is NameIndexState.Cached);

    /// <summary>Categories a lookup can be answered for now or after a disk read.</summary>
    public int AvailableCategoryCount => Snapshot().Count(snapshot => snapshot.State.IsUsable);

    /// <summary>Display names held in memory plus those sitting in a disk cache.</summary>
    public int AvailableNames => Snapshot().Where(snapshot => snapshot.State.IsUsable).Sum(snapshot => snapshot.Count);

    /// <summary>Overall coverage word used in tool notes. Counts disk-cached categories as covered.</summary>
    public string Coverage => AvailableCategoryCount switch
    {
        0 => "none",
        var available when available >= TotalCategoryCount => "complete",
        _ => "partial"
    };

    /// <summary>
    /// Reads only the <c>Count</c> header out of a category's cache file, without deserialising the
    /// (up to 100k entry) name map. Returns null when there is no cache. The stamp is deliberately
    /// NOT validated here - that needs a full registry pass per category and get_status must not
    /// block; a stale cache is caught and rebuilt by <see cref="TryLoadCache"/> on first real use.
    /// </summary>
    private readonly ConcurrentDictionary<EExportType, int> _cachePeeks = new();

    private int? PeekCacheCount(EExportType type)
    {
        // Memoised: once a category is peeked, the only thing that changes its cache file is this
        // process writing it - at which point the category is Ready and never peeked again.
        if (_cachePeeks.TryGetValue(type, out var memoised)) return memoised < 0 ? null : memoised;

        var count = ReadCacheCount(type);
        _cachePeeks[type] = count ?? -1;
        return count;
    }

    private int? ReadCacheCount(EExportType type)
    {
        var path = CachePath(type);

        try
        {
            if (!File.Exists(path)) return null;

            using var stream = File.OpenRead(path);
            var buffer = new byte[512];
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read <= 0) return null;

            var reader = new Utf8JsonReader(buffer.AsSpan(0, read), isFinalBlock: false, state: default);
            while (reader.Read())
            {
                if (reader.TokenType is not JsonTokenType.PropertyName) continue;
                if (!reader.ValueTextEquals("Count")) continue;
                if (reader.Read() && reader.TokenType is JsonTokenType.Number) return reader.GetInt32();
                return null;
            }
        }
        catch (Exception e)
        {
            Log.Debug("[NAMEINDEX] {Category}: cache peek failed: {Message}", type, e.Message);
        }

        return null;
    }

    // ---------------------------------------------------------------- build control

    /// <summary>
    /// Waits (briefly) for one category, starting its build if nobody has. Returns false instead of
    /// throwing when the index is not available in time - callers degrade to name-only matching.
    /// </summary>
    public async Task<bool> WhenCategoryReadyAsync(EExportType type, TimeSpan? grace = null, CancellationToken cancellationToken = default)
    {
        var state = Get(type);
        if (state is null) return false;
        if (state.State is NameIndexState.Ready) return true;
        if (state.State is NameIndexState.Failed) return false;

        var build = EnsureBuildStarted(state);

        try
        {
            // The build task never faults (BuildSafeAsync swallows), so this only ever times out.
            await build.WaitAsync(grace ?? DefaultGrace, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception e)
        {
            Log.Debug("Name index wait for {Category} ended early: {Message}", type, e.Message);
            return false;
        }

        return state.State is NameIndexState.Ready;
    }

    /// <summary>
    /// Fire-and-forget: builds every category smallest-first once the archive is ready. Safe to call
    /// more than once; only the first call does anything.
    /// </summary>
    public void StartBackgroundBuild()
    {
        if (Interlocked.Exchange(ref _backgroundStarted, 1) != 0) return;

        _backgroundBuild = Task.Run(async () =>
        {
            try
            {
                await loader.WhenReady().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Log.Warning("Name index build skipped: the archive did not load ({Message})", e.Message);
                return;
            }

            try
            {
                await BuildAllAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                // Belt and braces: this task must never surface as an unobserved exception.
                Log.Error(e, "Background name index build failed");
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// Starts the background build if nobody has and waits (briefly) for every category. Used by the
    /// un-scoped search path, where there is no single category to wait on. On a warm run every
    /// category comes off disk well inside the grace; on a cold one this returns false and the
    /// caller reports partial coverage.
    /// </summary>
    public async Task<bool> WhenAllReadyAsync(TimeSpan? grace = null, CancellationToken cancellationToken = default)
    {
        if (ReadyCategoryCount >= TotalCategoryCount) return true;

        StartBackgroundBuild();
        if (_backgroundBuild is not { } build) return false;

        try
        {
            // BuildAllAsync is wrapped in a catch-all, so this only ever times out.
            await build.WaitAsync(grace ?? DefaultGrace, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException) { }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        catch (Exception e)
        {
            Log.Debug("Name index wait ended early: {Message}", e.Message);
        }

        return ReadyCategoryCount >= TotalCategoryCount;
    }

    /// <summary>Builds every category smallest-first, sequentially (each build is itself parallel).</summary>
    public async Task BuildAllAsync(CancellationToken cancellationToken)
    {
        var ordered = CategoryCatalog.Entries
            .Select(entry => (Entry: entry, Rows: SafeRowCount(entry)))
            .OrderBy(pair => pair.Rows)
            .ToList();

        var stopwatch = Stopwatch.StartNew();
        Log.Information("[NAMEINDEX] Building {Count} categories, smallest first ({Total:N0} rows total)",
            ordered.Count, ordered.Sum(pair => pair.Rows));

        foreach (var (entry, rows) in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (AutoBuildExclusions.Contains(entry.Type))
            {
                Log.Information("[NAMEINDEX] {Category}: skipped by auto-build exclusion ({Rows:N0} rows); build on demand", entry.Type, rows);
                continue;
            }

            var state = Get(entry.Type);
            if (state is null) continue;

            await EnsureBuildStarted(state).ConfigureAwait(false);
        }

        Log.Information("[NAMEINDEX] All categories done in {Elapsed:N1}s - {Names:N0} display names, {Ready}/{Total} categories ready, {Memory:N0} MB managed",
            stopwatch.Elapsed.TotalSeconds, TotalNames, ReadyCategoryCount, TotalCategoryCount, GC.GetTotalMemory(false) / (1024 * 1024));
    }

    // ---------------------------------------------------------------- internals

    private CategoryState? Get(EExportType type)
    {
        if (_categories.TryGetValue(type, out var existing)) return existing;
        if (CategoryCatalog.ForType(type) is not { } entry) return null;

        return _categories.GetOrAdd(type, _ => new CategoryState { Entry = entry });
    }

    private Task EnsureBuildStarted(CategoryState state)
    {
        lock (state.Sync)
        {
            return state.Build ??= Task.Run(() => BuildSafeAsync(state));
        }
    }

    private async Task BuildSafeAsync(CategoryState state)
    {
        try
        {
            await BuildAsync(state).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            state.State = new NameIndexState.Failed(e.Message);
            Log.Warning("[NAMEINDEX] {Category} failed: {Message}", state.Entry.Type, e.Message);
        }
    }

    private async Task BuildAsync(CategoryState state)
    {
        var entry = state.Entry;

        // Categories with no registry-backed classes (WeaponMod, Wildlife) are trivially complete.
        if (entry.ClassNames.Length == 0)
        {
            state.Map = EmptyMap;
            state.State = new NameIndexState.Ready(0, 0, FromCache: false);
            return;
        }

        await loader.WhenReady().ConfigureAwait(false);

        var rows = assets.Filtered(entry);
        var stamp = StampFor(rows.Count);

        if (TryLoadCache(entry.Type, stamp) is { } cached)
        {
            state.Map = cached;
            state.State = new NameIndexState.Ready(cached.Count, rows.Count, FromCache: true);
            Log.Information("[NAMEINDEX] {Category}: {Count:N0} names loaded from cache ({Rows:N0} rows)", entry.Type, cached.Count, rows.Count);
            return;
        }

        state.State = new NameIndexState.Building(0, rows.Count);

        var stopwatch = Stopwatch.StartNew();
        var memoryBefore = GC.GetTotalMemory(false);
        var map = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var done = 0;

        Log.Information("[NAMEINDEX] {Category}: building {Rows:N0} rows...", entry.Type, rows.Count);

        await Parallel.ForEachAsync(
            rows,
            new ParallelOptions { MaxDegreeOfParallelism = MaxDegreeOfParallelism },
            async (data, _) =>
            {
                try
                {
                    var asset = await loader.Provider.SafeLoadPackageObjectAsync(data.ObjectPath).ConfigureAwait(false);
                    if (asset is not null)
                    {
                        string? displayName;
                        try { displayName = entry.DisplayNameHandler(asset); }
                        catch { displayName = null; }

                        if (!string.IsNullOrWhiteSpace(displayName))
                            map[data.ObjectPath] = displayName;
                    }
                }
                catch (Exception e)
                {
                    Log.Debug("[NAMEINDEX] {Category}: {Path} failed: {Message}", entry.Type, data.ObjectPath, e.Message);
                }

                var completed = Interlocked.Increment(ref done);
                if (completed % ProgressEvery == 0)
                {
                    state.State = new NameIndexState.Building(completed, rows.Count);
                    Log.Information("[NAMEINDEX] {Category}: {Done:N0}/{Total:N0} ({Percent:N0}%), {Names:N0} names, {Memory:N0} MB",
                        entry.Type, completed, rows.Count, completed * 100.0 / rows.Count, map.Count, GC.GetTotalMemory(false) / (1024 * 1024));
                }
            }).ConfigureAwait(false);

        var finished = new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase);
        state.Map = finished;
        state.State = new NameIndexState.Ready(finished.Count, rows.Count, FromCache: false);

        Log.Information("[NAMEINDEX] {Category}: {Count:N0} names from {Rows:N0} rows in {Elapsed:N1}s ({Rate:N0} rows/s), managed heap {Before:N0} -> {After:N0} MB",
            entry.Type, finished.Count, rows.Count, stopwatch.Elapsed.TotalSeconds,
            stopwatch.Elapsed.TotalSeconds <= 0 ? 0 : rows.Count / stopwatch.Elapsed.TotalSeconds,
            memoryBefore / (1024 * 1024), GC.GetTotalMemory(false) / (1024 * 1024));

        SaveCache(entry.Type, stamp, finished);
    }

    private int SafeRowCount(AssetCategoryEntry entry)
    {
        try { return entry.ClassNames.Length == 0 ? 0 : assets.Filtered(entry).Count; }
        catch { return 0; }
    }

    /// <summary>Validity stamp: rebuild whenever the game version or this category's row count moves.</summary>
    private string StampFor(int rowCount)
    {
        var version = "unknown";
        try { version = loader.Provider.Versions.Game.ToString(); }
        catch { /* provider not up; the row count alone still guards the cache */ }

        return $"{version}|{rowCount}|v{SchemaVersion}";
    }

    private string CachePath(EExportType type) => Path.Combine(CacheFolder.FullName, $"{type}.json");

    private sealed class CacheFile
    {
        public string? Stamp { get; set; }
        public string? Category { get; set; }
        public int Count { get; set; }
        public Dictionary<string, string> Names { get; set; } = new();
    }

    private Dictionary<string, string>? TryLoadCache(EExportType type, string stamp)
    {
        var path = CachePath(type);
        if (!File.Exists(path)) return null;

        try
        {
            using var stream = File.OpenRead(path);
            var cache = JsonSerializer.Deserialize<CacheFile>(stream);
            if (cache?.Stamp is null || !cache.Stamp.Equals(stamp, StringComparison.Ordinal))
            {
                Log.Information("[NAMEINDEX] {Category}: cache stamp changed ({Old} -> {New}), rebuilding", type, cache?.Stamp ?? "none", stamp);
                return null;
            }

            return new Dictionary<string, string>(cache.Names, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e)
        {
            Log.Warning("[NAMEINDEX] {Category}: cache unreadable ({Message}), rebuilding", type, e.Message);
            return null;
        }
    }

    private void SaveCache(EExportType type, string stamp, Dictionary<string, string> names)
    {
        try
        {
            CacheFolder.Create();
            var path = CachePath(type);
            var temporaryPath = path + ".tmp";

            using (var stream = File.Create(temporaryPath))
            {
                JsonSerializer.Serialize(stream, new CacheFile
                {
                    Stamp = stamp,
                    Category = type.ToString(),
                    Count = names.Count,
                    Names = names
                });
            }

            File.Move(temporaryPath, path, overwrite: true);
            Log.Debug("[NAMEINDEX] {Category}: cache written to {Path}", type, path);
        }
        catch (Exception e)
        {
            Log.Warning("[NAMEINDEX] {Category}: failed to write cache ({Message}); it will rebuild next run", type, e.Message);
        }
    }
}
