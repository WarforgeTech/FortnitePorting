using System.Diagnostics;
using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.AssetRegistry;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.AssetRegistry.Objects;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.Engine;
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
/// Headless port of the GUI's CUE4ParseService loading pipeline. Only the
/// "latest installed local archive" path is supported; on-demand streaming,
/// the cache sweeper, and all UI concerns are dropped.
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
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    private FortniteVersionResponse? _resolvedVersion;

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
                new("Initializing CUE4Parse", 5, InitializeProviderSetup),
                new("Loading Detex", 1, InitializeDetex),
                new("Initializing Provider", 10, InitializeProvider),
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

        _provider.LoadOnDemandTocs = false;
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

    private async Task InitializeDetex()
    {
        var detexPath = Path.Combine(Config.DependencyFolder.FullName, DetexHelper.DLL_NAME);
        if (!File.Exists(detexPath)) await DetexHelper.LoadDllAsync(detexPath);
        DetexHelper.Initialize(detexPath);
    }

    private async Task InitializeProvider()
    {
        await _provider!.InitializeAsync();
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
