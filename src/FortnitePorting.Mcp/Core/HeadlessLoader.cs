using System.Diagnostics;
using System.Net.Http.Headers;
using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.AssetRegistry;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.AssetRegistry.Objects;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.Objects.Core.i18N;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.VirtualFileSystem;
using CUE4Parse.Utils;
using CUE4Parse_Conversion.Textures.BC;
using FortnitePorting.CUE4Parse.Models.Fortnite.GameFeature;
using FortnitePorting.CUE4Parse.Models.Fortnite.Styles;
using FortnitePorting.Mcp.Config;
using Serilog;
using FGuid = CUE4Parse.UE4.Objects.Core.Misc.FGuid;

namespace FortnitePorting.Mcp.Core;

public abstract record LoadState
{
    public sealed record NotStarted : LoadState;
    public sealed record Loading(string StageName, float Percent) : LoadState;
    public sealed record Ready : LoadState;
    public sealed record Failed(string Message) : LoadState;
}

/// <summary>
/// What the on-demand (streamed-texture) stage managed to do. Reported verbatim by get_status:
/// "enabled but not mounted" is the state where every cosmetic and weapon texture silently comes
/// out at its 64x64 stub mip, and that has to be visible without reading the log.
/// </summary>
public record TextureStreamingStatus
{
    public bool Enabled { get; init; }
    public bool Authenticated { get; init; }
    public int MountedContainers { get; init; }
    public string? TocPath { get; init; }
    public string? Error { get; init; }

    public bool Mounted => MountedContainers > 0;
}

/// <summary>
/// Headless port of the GUI's CUE4ParseService loading pipeline. Only the
/// "latest installed local archive" path is supported; the live-manifest (LatestOnDemand)
/// installation type, the cache sweeper's UI, and all other UI concerns are dropped.
/// On-demand TEXTURE streaming is kept: without it every cosmetic/weapon texture exports as
/// its lowest mip, because the local paks only carry that one.
/// </summary>
public class HeadlessLoader(McpConfig config)
{
    private const EGame LatestGameVersion = EGame.GAME_UE6_0;

    public McpConfig Config { get; } = config;

    private HeadlessFileProvider? _provider;
    public AbstractVfsFileProvider Provider => _provider
        ?? throw new InvalidOperationException("Provider has not been initialized yet. Await WhenReady() first.");

    public LoadState State { get; private set; } = new LoadState.NotStarted();

    public readonly List<FPartialAssetData> AssetRegistry = [];
    public readonly List<FRarityCollection> RarityColors = [];
    public readonly Dictionary<int, FColor> BeanstalkColors = [];
    public readonly Dictionary<int, FLinearColor> BeanstalkMaterialProps = [];
    public readonly Dictionary<int, FVector> BeanstalkAtlasTextureUVs = [];
    public readonly List<UAnimMontage> MaleLobbyMontages = [];
    public readonly List<UAnimMontage> FemaleLobbyMontages = [];
    public readonly Dictionary<string, string> SetNames = [];

    private readonly FortnitePortingApi _api = new(config.DataFolder);
    private readonly EpicGamesApi _epicApi = new(config.DataFolder);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    private FortniteVersionResponse? _resolvedVersion;
    private string? _epicAuthToken;

    /// <summary>State of the streamed-texture (IoStore on-demand) mount; surfaced by get_status.</summary>
    public TextureStreamingStatus TextureStreaming { get; private set; } = new() { Enabled = config.UseTextureStreaming };

    /// <summary>
    /// The build string the FortnitePorting API reported for the archive we mounted (e.g. "37.20").
    /// Null when the API was unreachable, so callers that stamp artefacts with it must degrade to
    /// the Unreal version rather than failing.
    /// </summary>
    public string? GameVersion => _resolvedVersion?.Version;

    private static readonly List<DirectoryInfo> ExtraDirectories =
    [
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FortniteGame", "Saved", "PersistentDownloadDir", "GameCustom", "InstalledBundles"))
    ];

    private static readonly List<string> MaleLobbyMontagePaths =
    [
        "FortniteGame/Content/Animation/Game/MainPlayer/Menu/BR/Male_Commando_Idle_01_M",
        "FortniteGame/Content/Animation/Game/MainPlayer/Menu/BR/Male_commando_Idle_2_M",
        "FortniteGame/Plugins/GameFeatures/BRCosmetics/Content/Animation/Game/MainPlayer/Menu/BR/Male_commando_Idle_01_M",
        "FortniteGame/Plugins/GameFeatures/BRCosmetics/Content/Animation/Game/MainPlayer/Menu/BR/Male_commando_Idle_2_M"
    ];

    private static readonly List<string> FemaleLobbyMontagePaths =
    [
        "FortniteGame/Content/Animation/Game/MainPlayer/Menu/BR/Female_Commando_Idle_02_Rebirth_Montage",
        "FortniteGame/Content/Animation/Game/MainPlayer/Menu/BR/Female_Commando_Idle_03_Montage",
        "FortniteGame/Plugins/GameFeatures/BRCosmetics/Content/Animation/Game/MainPlayer/Menu/BR/Female_Commando_Idle_02_Rebirth_Montage"
    ];

    private record Stage(string Name, float Weight, Func<Task> Run);

    /// <summary>Waits for the archive to finish loading, kicking off the load if nobody has yet.</summary>
    public async Task WhenReady(CancellationToken token = default)
    {
        _ = InitializeAsync(token);
        await _ready.Task.WaitAsync(token);
    }

    public async Task InitializeAsync(CancellationToken token = default)
    {
        if (!await _loadGate.WaitAsync(0, token)) return;

        try
        {
            if (State is not LoadState.NotStarted) return;

            var stopwatch = Stopwatch.StartNew();

            var stages = new List<Stage>
            {
                new("Initializing Compression", 1, InitializeCompression),
                // Auth must precede the provider setup: OnDemandOptions captures the bearer token
                // at construction, and the provider registers the local .uondemandtoc files (which
                // immediately pull their container tocs from the CDN) the moment it initializes.
                new("Authenticating with Epic", 2, InitializeEpicAuth),
                new("Initializing CUE4Parse", 5, InitializeProviderSetup),
                new("Removing Outdated Cache Files", 1, CleanupCache),
                new("Initializing Provider", 10, InitializeProvider),
                new("Loading Texture Streaming", 5, InitializeTextureStreaming),
                new("Submitting Keys", 20, LoadKeys),
                new("Loading Virtual Paths", 15, LoadVirtualPaths),
                new("Loading Mappings", 1, LoadMappings),
                new("Loading Required Assets", 5, LoadRequiredAssets),
                new("Loading Asset Registries", 10, LoadAssetRegistries)
            };

            var totalWeight = stages.Sum(x => x.Weight);
            var completedWeight = 0.0f;

            foreach (var stage in stages)
            {
                token.ThrowIfCancellationRequested();

                completedWeight += stage.Weight;
                State = new LoadState.Loading(stage.Name, completedWeight / totalWeight * 100.0f);
                Log.Information("[STAGE] {Stage}", stage.Name);

                await stage.Run();
            }

            stopwatch.Stop();
            State = new LoadState.Ready();
            Log.Information("Archive loaded in {Elapsed:N1}s", stopwatch.Elapsed.TotalSeconds);
            _ready.TrySetResult();
        }
        catch (Exception e)
        {
            State = new LoadState.Failed(e.Message);
            Log.Error(e, "Failed to load the Fortnite archive");
            _ready.TrySetException(e);
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private async Task InitializeCompression()
    {
        // The GUI relies on the launcher-shipped oodle/zlib libraries being alongside the exe;
        // headless we fetch them into the data folder on first run.
        try
        {
            // InitializeAsync downloads the dll to the given path if it isn't there yet.
            await OodleHelper.InitializeAsync(Path.Combine(Config.DependencyFolder.FullName, OodleHelper.OodleFileName));
        }
        catch (Exception e)
        {
            Log.Warning("Failed to initialize Oodle: {Message}", e.Message);
        }

        try
        {
            var zlibPath = Path.Combine(Config.DependencyFolder.FullName, ZlibHelper.DllName);
            if (File.Exists(zlibPath))
                ZlibHelper.Initialize(zlibPath);
        }
        catch (Exception e)
        {
            Log.Warning("Failed to initialize Zlib: {Message}", e.Message);
        }
    }

    /// <summary>
    /// Port of the GUI's <c>Api.EpicGames.VerifyAuthAsync()</c> call inside InitializeProvider.
    /// Non-fatal by construction: no token simply means no streamed mips.
    /// </summary>
    private async Task InitializeEpicAuth()
    {
        if (!Config.UseTextureStreaming)
        {
            Log.Information("Texture streaming disabled by config; skipping Epic authentication");
            return;
        }

        _epicAuthToken = await _epicApi.GetTokenAsync();
        TextureStreaming = TextureStreaming with { Authenticated = _epicAuthToken is not null };

        if (_epicAuthToken is null)
        {
            TextureStreaming = TextureStreaming with
            {
                Error = "Could not obtain an Epic auth token; streamed textures will export at their lowest mip."
            };
            Log.Warning("No Epic auth token: on-demand texture streaming will be unavailable (textures export at their lowest mip)");
        }
    }

    private async Task InitializeProviderSetup()
    {
        if (!Directory.Exists(Config.ArchiveDirectory))
            throw new DirectoryNotFoundException($"Archive directory does not exist: {Config.ArchiveDirectory}");

        _provider = new HeadlessFileProvider(Config.ArchiveDirectory, ExtraDirectories, new VersionContainer(LatestGameVersion));

        _resolvedVersion = await _api.GetFortniteVersionAsync();
        if (_resolvedVersion is not null)
            Log.Information("Resolved Fortnite Version: {Version}", _resolvedVersion.Version);
        else
            Log.Warning("Failed to resolve latest Fortnite version keys/mappings from API");

        Log.Information("Archive Path: {Path}", Config.ArchiveDirectory);
        Log.Information("Unreal Version: {Version}", _provider.Versions.Game.ToString());

        ObjectTypeRegistry.RegisterEngine(typeof(UFortGameFeatureData).Assembly);

        Log.Information("Texture Streaming: {UseTextureStreaming}", Config.UseTextureStreaming);

        // OnDemandOptions has to exist before any .uondemandtoc is registered: CUE4Parse silently
        // drops the registration when it is null (AbstractVfsFileProvider.RegisterVfsAsync).
        var streamingReady = Config.UseTextureStreaming && _epicAuthToken is not null;
        if (streamingReady)
        {
            _provider.OnDemandOptions = new IoStoreOnDemandOptions
            {
                ChunkHostUri = new Uri("https://egdownload.fastly-edge.com/", UriKind.Absolute),
                ChunkCacheDirectory = Config.CacheFolder,
                Authorization = new AuthenticationHeaderValue("Bearer", _epicAuthToken),
                Timeout = TimeSpan.FromSeconds(Config.ChunkRequestTimeoutSeconds)
            };
        }

        _provider.LoadOnDemandTocs = streamingReady;
        _provider.LoadExtraDirectories = true;
        _provider.ReadNaniteData = false;

        _provider.VfsMounted += (sender, _) =>
        {
            if (sender is not IAesVfsReader reader) return;

            Log.Debug(reader.Name.Equals("plugin.utoc")
                ? $"Loading GameFeature {reader.Path.SubstringBeforeLast("\\").SubstringAfterLast("\\")}"
                : $"Loading {reader.Name}");
        };
    }

    /// <summary>Port of the GUI's CleanupCache stage: drop chunk-cache files older than the lifetime.</summary>
    private async Task CleanupCache()
    {
        try
        {
            var cutoff = DateTime.Now - TimeSpan.FromDays(Config.ChunkCacheLifetimeDays);
            foreach (var file in Config.CacheFolder.GetFiles())
            {
                if (file.LastWriteTime >= cutoff) continue;
                file.Delete();
            }
        }
        catch (Exception e)
        {
            Log.Warning("Failed to sweep the chunk cache: {Message}", e.Message);
        }

        await Task.CompletedTask;
    }

    private async Task InitializeProvider()
    {
        await _provider!.InitializeAsync();
    }

    /// <summary>
    /// Port of the GUI's <c>InitializeTextureStreaming</c> stage. Fortnite's cosmetic and weapon
    /// textures are IoStore ON-DEMAND: the installed paks hold only the lowest mip and everything
    /// above it is fetched from Epic's CDN through an on-demand toc. Two sources of that toc are
    /// tried, in the GUI's order of preference:
    ///   1. Cloud/IoStoreOnDemand.ini next to the install, whose [Endpoint] TocPath names a toc on
    ///      download.epicgames.com. Downloaded once into the cache folder.
    ///   2. The .uondemandtoc files already sitting in the Paks folder, registered by the provider
    ///      during InitializeProvider (that path needs nothing here beyond OnDemandOptions).
    /// Every failure is a warning: a load that cannot stream still exports, just at low mips.
    /// </summary>
    private async Task InitializeTextureStreaming()
    {
        if (!Config.UseTextureStreaming)
        {
            Log.Information("Texture streaming disabled by config (UseTextureStreaming=false)");
            return;
        }

        if (_provider!.OnDemandOptions is null)
        {
            // Auth failed - the message is already on TextureStreaming.Error.
            Log.Warning("Skipping texture streaming: no on-demand options (Epic authentication failed)");
            return;
        }

        try
        {
            var tocPath = await GetTocPathAsync();
            if (!string.IsNullOrEmpty(tocPath))
            {
                Log.Information("Found on-demand toc path: {TocPath}", tocPath);
                TextureStreaming = TextureStreaming with { TocPath = tocPath };

                var tocName = tocPath.SubstringAfterLast("/");
                var onDemandFile = new FileInfo(Path.Combine(Config.CacheFolder.FullName, tocName));
                if (!onDemandFile.Exists || onDemandFile.Length == 0)
                {
                    await DownloadFileAsync($"https://download.epicgames.com/{tocPath}", onDemandFile.FullName);
                    onDemandFile.Refresh();
                }

                if (onDemandFile is { Exists: true, Length: > 0 })
                    await _provider.RegisterVfsAsync(new IoChunkToc(onDemandFile.FullName, _provider.Versions));
                else
                    Log.Warning("On-demand toc {Name} could not be downloaded", tocName);
            }
            else
            {
                // Normal on an install without the Cloud ini; the local .uondemandtoc files
                // registered during InitializeProvider still provide streaming.
                Log.Information("No Cloud/IoStoreOnDemand.ini endpoint found; relying on the local .uondemandtoc files");
            }

            await _provider.MountAsync();

            var onDemandReaders = _provider.MountedVfs.OfType<IoStoreOnDemandReader>().Count();
            TextureStreaming = TextureStreaming with { MountedContainers = onDemandReaders };

            if (onDemandReaders > 0)
            {
                Log.Information("Texture streaming mounted: {Count} on-demand container(s)", onDemandReaders);
            }
            else
            {
                TextureStreaming = TextureStreaming with
                {
                    Error = "No on-demand containers mounted; streamed textures will export at their lowest mip."
                };
                Log.Warning("Texture streaming produced no on-demand containers - textures will export at their lowest mip. "
                            + "Enable \"Pre-Download Streamed Assets\" for Fortnite in the Epic Games Launcher if this persists.");
            }
        }
        catch (Exception e)
        {
            TextureStreaming = TextureStreaming with { Error = e.Message };
            Log.Warning(e, "Failed to initialize texture streaming; continuing with low-resolution mips");
        }
    }

    /// <summary>
    /// Reads [Endpoint] TocPath out of Cloud/IoStoreOnDemand.ini, three levels above the Paks
    /// folder. The GUI used UE4Config for this; the file is a two-key ini, so it is parsed inline
    /// rather than pulling the package into the headless host.
    /// </summary>
    private async Task<string> GetTocPathAsync()
    {
        var onDemandPath = Path.GetFullPath(Path.Combine(Config.ArchiveDirectory, "..", "..", "..", "Cloud", "IoStoreOnDemand.ini"));
        if (!File.Exists(onDemandPath))
        {
            Log.Information("No on-demand ini at {Path}", onDemandPath);
            return string.Empty;
        }

        var section = string.Empty;
        foreach (var rawLine in await File.ReadAllLinesAsync(onDemandPath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                continue;
            }

            if (!section.Equals("Endpoint", StringComparison.OrdinalIgnoreCase)) continue;

            var separator = line.IndexOf('=');
            if (separator < 0) continue;

            if (!line[..separator].Trim().Equals("TocPath", StringComparison.OrdinalIgnoreCase)) continue;

            return line[(separator + 1)..].Trim().Replace("\"", string.Empty);
        }

        return string.Empty;
    }

    private static async Task DownloadFileAsync(string url, string destination)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        await using var stream = await client.GetStreamAsync(url);
        await using var fileStream = File.Create(destination);
        await stream.CopyToAsync(fileStream);
    }

    private async Task LoadKeys()
    {
        var mainKeyString = Config.AesKeyOverride ?? _resolvedVersion?.Keys?.MainKey?.Key;
        if (mainKeyString is null)
        {
            Log.Warning("No main AES key available (API unreachable and no override configured); encrypted paks will stay unmounted");
            return;
        }

        Log.Information("Submitting Main Key {Key}", mainKeyString);
        await _provider!.SubmitKeyAsync(new FGuid(), new FAesKey(mainKeyString));

        var extraKeys = _resolvedVersion?.Keys?.ExtraKeys ?? [];
        foreach (var key in extraKeys)
        {
            if (key.Key is null) continue;

            if (key.GUID is not null)
            {
                Log.Information("Submitting Dynamic Key {Key} with GUID {Guid}", key.Key, key.GUID);
                await _provider.SubmitKeyAsync(new FGuid(key.GUID), new FAesKey(key.Key));
                continue;
            }

            // No GUID supplied: test the key against every archive still waiting for one.
            var aesKey = new FAesKey(key.Key);
            foreach (var vfs in _provider.UnloadedVfs.ToArray())
            {
                if (!vfs.TestAesKey(aesKey)) continue;

                Log.Information("Submitting Extra Key {Key} with GUID {Guid} for {FileName}", key.Key, vfs.EncryptionKeyGuid, vfs.Name);
                await _provider.SubmitKeyAsync(vfs.EncryptionKeyGuid, aesKey);
            }
        }
    }

    private async Task LoadVirtualPaths()
    {
        _provider!.LoadVirtualPaths();
        _provider.PostMount();

        if (Config.Language is not ELanguage.English && !_provider.TryChangeCulture(_provider.GetLanguageCode(Config.Language)))
            Log.Warning("Failed to load language \"{Language}\"", Config.Language);

        await Task.CompletedTask;
    }

    private async Task LoadMappings()
    {
        string? mappingsPath;
        if (Config.MappingsFileOverride is { } overridePath && File.Exists(overridePath))
        {
            mappingsPath = overridePath;
        }
        else
        {
            mappingsPath = await _api.GetEndpointMappingsAsync(_resolvedVersion?.Mappings) ?? _api.GetLocalMappings();
        }

        if (string.IsNullOrEmpty(mappingsPath))
        {
            Log.Warning("Failed to load mappings, path is empty");
            return;
        }

        _provider!.MappingsContainer = new FileUsmapTypeMappingsProvider(mappingsPath, StringComparer.Ordinal);
        Log.Information("Loaded Mappings: {Path}", mappingsPath);
    }

    private async Task LoadRequiredAssets()
    {
        // Each block is individually non-fatal: a missing table must not take down the whole load.
        await TryStep("RarityData", async () =>
        {
            if (await Provider.SafeLoadPackageObjectAsync("FortniteGame/Content/Balance/RarityData") is not { } rarityData) return;

            for (var i = 0; i < rarityData.Properties.Count; i++)
                RarityColors.Add(rarityData.GetByIndex<FRarityCollection>(i));
        });

        await TryStep("BeanstalkColors", async () =>
        {
            if (await Provider.SafeLoadPackageObjectAsync("/BeanstalkCosmetics/Cosmetics/DataTables/DT_BeanstalkCosmetics_Colors") is not UDataTable table) return;

            foreach (var (name, fallback) in table.RowMap)
            {
                var index = int.Parse(name.Text);
                BeanstalkColors[index] = fallback.GetOrDefault<FColor>("Color");
            }
        });

        await TryStep("BeanstalkMaterialTypes", async () =>
        {
            if (await Provider.SafeLoadPackageObjectAsync("/BeanstalkCosmetics/Cosmetics/DataTables/DT_BeanstalkCosmetics_MaterialTypes") is not UDataTable table) return;

            foreach (var (name, fallback) in table.RowMap)
            {
                var index = int.Parse(name.Text);
                var color = new FLinearColor();
                foreach (var property in fallback.Properties)
                {
                    if (property.Tag is null) continue;

                    var actualName = property.Name.Text.SubstringBefore("_");
                    switch (actualName)
                    {
                        case "Metallic":
                            color.R = (float) property.Tag.GetValue<double>();
                            break;
                        case "Roughness":
                            color.G = (float) property.Tag.GetValue<double>();
                            break;
                        case "Emissive":
                            color.B = (float) property.Tag.GetValue<double>();
                            break;
                    }
                }

                BeanstalkMaterialProps[index] = color;
            }
        });

        await TryStep("PatternAtlasTextureSlots", async () =>
        {
            if (await Provider.SafeLoadPackageObjectAsync("/BeanstalkCosmetics/Cosmetics/DataTables/DT_PatternAtlasTextureSlots") is not UDataTable table) return;

            foreach (var (name, fallback) in table.RowMap)
            {
                var index = int.Parse(name.Text);
                foreach (var property in fallback.Properties)
                {
                    if (property.Tag is null) continue;
                    if (!property.Name.Text.SubstringBefore("_").Equals("UV")) continue;

                    BeanstalkAtlasTextureUVs[index] = property.Tag.GetValue<FVector>();
                }
            }
        });

        await TryStep("CosmeticSets", async () =>
        {
            if (await Provider.SafeLoadPackageObjectAsync("FortniteGame/Content/Athena/Items/Cosmetics/Metadata/CosmeticSets") is not UDataTable table) return;

            foreach (var (tagName, data) in table.RowMap)
            {
                if (data.GetOrDefault<FText?>("DisplayName") is not { } displayName) continue;
                SetNames[tagName.Text] = displayName.Text;
            }
        });

        await TryStep("LobbyMontages", async () =>
        {
            foreach (var path in MaleLobbyMontagePaths)
            {
                if (await Provider.SafeLoadPackageObjectAsync<UAnimMontage>(path) is { } montage)
                    MaleLobbyMontages.Add(montage);
            }

            foreach (var path in FemaleLobbyMontagePaths)
            {
                if (await Provider.SafeLoadPackageObjectAsync<UAnimMontage>(path) is { } montage)
                    FemaleLobbyMontages.Add(montage);
            }
        });
    }

    private async Task LoadAssetRegistries()
    {
        var assetRegistries = Provider.Files
            .Where(x => x.Key.Contains("AssetRegistry", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (var (path, file) in assetRegistries)
        {
            if (!path.EndsWith(".bin")) continue;
            if (path.Contains("Editor", StringComparison.OrdinalIgnoreCase)) continue;

            var assetArchive = await file.SafeCreateReaderAsync();
            if (assetArchive is null) continue;

            try
            {
                var assetRegistry = new FPartialAssetRegistryState(assetArchive);
                AssetRegistry.AddRange(assetRegistry.PreallocatedAssetDataBuffers);
                Log.Information("Loaded Asset Registry: {FilePath}", file.Path);
            }
            catch (Exception e)
            {
                Log.Warning("Failed to load asset registry: {FilePath}", file.Path);
                Log.Error(e.ToString());
            }
        }
    }

    private static async Task TryStep(string name, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception e)
        {
            Log.Warning("Optional asset step \"{Name}\" failed: {Message}", name, e.Message);
        }
    }
}
