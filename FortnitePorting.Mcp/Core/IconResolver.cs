using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse_Conversion.Textures;
using FortnitePorting;
using FortnitePorting.CUE4Parse.Extensions;
using FortnitePorting.Mcp.Config;
using Serilog;
using SkiaSharp;

namespace FortnitePorting.Mcp.Core;

public enum IconSource
{
    /// <summary>Resolved through the category's LowRes/HighRes icon handler.</summary>
    Handler,

    /// <summary>Resolved from a raw UTexture2D on the object, or from another export in its package.</summary>
    RawTexture,

    /// <summary>Resolved from the category's PlaceholderIconPath texture.</summary>
    Placeholder,

    /// <summary>Nothing decodable was found; a flat gray cell was generated.</summary>
    Generated
}

public sealed record IconResult(byte[] Png, IconSource Source, string? TexturePath)
{
    public bool IsRealIcon => Source is IconSource.Handler or IconSource.RawTexture;

    public string SourceName => Source switch
    {
        IconSource.Handler => "handler",
        IconSource.RawTexture => "rawTexture",
        IconSource.Placeholder => "placeholder",
        _ => "generated"
    };
}

/// <summary>
/// objectPath -> PNG bytes, with the full fallback chain and a disk cache.
///
/// Chain: category handler -> the object itself if it IS a texture -> a wide property sweep ->
/// any UTexture2D export in the same package -> the category placeholder texture -> a generated cell.
/// Per-prop icon resolution in Fortnite is genuinely unreliable, so every step is best-effort.
/// </summary>
public sealed class IconResolver(HeadlessLoader loader, McpConfig config, AssetQuery assets)
{
    /// <summary>Names swept when the category handlers come up empty.</summary>
    private static readonly string[] WideIconNames =
    [
        "Icon", "LargeIcon", "SmallIcon", "SmallPreviewImage", "LargePreviewImage", "MediumPreviewImage",
        "PreviewImage", "DisplayIcon", "DetailsImage", "TileImage", "IconTexture", "Texture",
        "BackgroundTexture", "ItemPreviewActorClass", "SidePanelIcon", "ToastIcon", "DecalTexture"
    ];

    private static readonly AssetCategoryEntry FallbackEntry = new() { Type = EExportType.None };

    private readonly ConcurrentDictionary<string, byte[]> _placeholderCache = new();
    private readonly SemaphoreSlim _decodeGate = new(Math.Max(2, Environment.ProcessorCount / 2));

    private string CacheDirectory
    {
        get
        {
            var dir = Path.Combine(config.DataDirectory, "IconCache");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <param name="iconOverridePath">
    /// A texture path the caller already knows is the right icon for this object - what the catalog
    /// pins to a hand-authored asset (Wildlife, WeaponMod), whose mesh carries no icon property of
    /// its own and would otherwise resolve to a placeholder. Callers that already hold the
    /// <see cref="CategoryItem"/> pass it directly; <c>get_asset_icon</c>, which only gets a path,
    /// lets the resolver look it up.
    /// </param>
    public async Task<IconResult> ResolveAsync(
        string objectPath, int size, CancellationToken cancellationToken = default, string? iconOverridePath = null)
    {
        size = Math.Clamp(size, 16, 1024);

        if (TryReadCache(objectPath, size) is { } cached) return cached;

        var result = await ResolveUncachedAsync(objectPath, size, iconOverridePath, cancellationToken).ConfigureAwait(false);
        WriteCache(objectPath, size, result);
        return result;
    }

    private async Task<IconResult> ResolveUncachedAsync(
        string objectPath, int size, string? iconOverridePath, CancellationToken cancellationToken)
    {
        // 0 - a catalog-pinned icon always wins: the object it belongs to is a bare mesh, so every
        // later step in the chain would fall through to the placeholder.
        iconOverridePath ??= assets.ManualFor(objectPath)?.IconPath;
        if (!string.IsNullOrWhiteSpace(iconOverridePath))
        {
            try
            {
                if (await loader.Provider.SafeLoadPackageObjectAsync<UTexture2D>(iconOverridePath).ConfigureAwait(false) is { } pinned &&
                    await EncodeAsync(pinned, size, cancellationToken).ConfigureAwait(false) is { } pinnedPng)
                    return new IconResult(pinnedPng, IconSource.Handler, SafePath(pinned));
            }
            catch (Exception e)
            {
                Log.Debug("Icon: catalog icon {Path} failed: {Message}", iconOverridePath, e.Message);
            }
        }

        UObject? asset = null;
        try
        {
            asset = await loader.Provider.SafeLoadPackageObjectAsync(objectPath).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Log.Debug("Icon: failed to load {Path}: {Message}", objectPath, e.Message);
        }

        var entry = (asset is not null ? CategoryCatalog.ForClassName(asset.ExportType) : null) ?? FallbackEntry;

        if (asset is not null)
        {
            // 1 - the category's own handlers (the same ones the GUI grid uses).
            if (TryHandler(entry, asset) is { } handlerTexture &&
                await EncodeAsync(handlerTexture, size, cancellationToken).ConfigureAwait(false) is { } handlerPng)
                return new IconResult(handlerPng, IconSource.Handler, SafePath(handlerTexture));

            // 2 - the object is itself a texture.
            if (asset is UTexture2D selfTexture &&
                await EncodeAsync(selfTexture, size, cancellationToken).ConfigureAwait(false) is { } selfPng)
                return new IconResult(selfPng, IconSource.RawTexture, SafePath(selfTexture));

            // 3 - wide property sweep across common icon property names (data-list aware).
            if (TryWideSweep(asset) is { } sweptTexture &&
                await EncodeAsync(sweptTexture, size, cancellationToken).ConfigureAwait(false) is { } sweptPng)
                return new IconResult(sweptPng, IconSource.RawTexture, SafePath(sweptTexture));

            // 4 - any texture export sitting in the same package (props embed their thumbnail there).
            if (TryPackageTexture(objectPath) is { } packageTexture &&
                await EncodeAsync(packageTexture, size, cancellationToken).ConfigureAwait(false) is { } packagePng)
                return new IconResult(packagePng, IconSource.RawTexture, SafePath(packageTexture));
        }

        // 5 - the category placeholder texture.
        if (await ResolvePlaceholderTextureAsync(entry.PlaceholderIconPath, size, cancellationToken).ConfigureAwait(false) is { } placeholderPng)
            return new IconResult(placeholderPng, IconSource.Placeholder, entry.PlaceholderIconPath);

        // 6 - a generated gray cell so callers always get a valid PNG.
        return new IconResult(GeneratePlaceholder(size), IconSource.Generated, null);
    }

    private static UTexture2D? TryHandler(AssetCategoryEntry entry, UObject asset)
    {
        try
        {
            return entry.LowResIconHandler(asset) ?? entry.HighResIconHandler(asset);
        }
        catch (Exception e)
        {
            Log.Debug("Icon: handler threw for {Name}: {Message}", asset.Name, e.Message);
            return null;
        }
    }

    private static UTexture2D? TryWideSweep(UObject asset)
    {
        try
        {
            var fromDataList = asset.GetDataListItem<UTexture2D?>(WideIconNames);
            if (fromDataList is not null) return fromDataList;
        }
        catch { /* data lists are optional */ }

        try
        {
            var direct = asset.GetAnyOrDefault<UTexture2D?>(WideIconNames);
            if (direct is not null) return direct;
        }
        catch { /* property may be a soft path we cannot resolve */ }

        // Last resort inside the object: any property at all whose value resolves to a texture.
        foreach (var property in asset.Properties)
        {
            try
            {
                if (asset.GetOrDefault<UTexture2D?>(property.Name.Text) is { } texture) return texture;
            }
            catch { /* ignore */ }
        }

        return null;
    }

    private UTexture2D? TryPackageTexture(string objectPath)
    {
        try
        {
            var packagePath = objectPath.Contains('.') ? objectPath[..objectPath.LastIndexOf('.')] : objectPath;
            if (!loader.Provider.TryLoadPackage(packagePath, out var package)) return null;

            foreach (var export in package.GetExports())
            {
                if (export is UTexture2D texture) return texture;
            }
        }
        catch (Exception e)
        {
            Log.Debug("Icon: package sweep failed for {Path}: {Message}", objectPath, e.Message);
        }

        return null;
    }

    private async Task<byte[]?> ResolvePlaceholderTextureAsync(string placeholderPath, int size, CancellationToken cancellationToken)
    {
        var key = $"{placeholderPath}|{size}";
        if (_placeholderCache.TryGetValue(key, out var cached)) return cached;

        try
        {
            if (await loader.Provider.SafeLoadPackageObjectAsync<UTexture2D>(placeholderPath).ConfigureAwait(false) is { } texture &&
                await EncodeAsync(texture, size, cancellationToken).ConfigureAwait(false) is { } png)
            {
                _placeholderCache[key] = png;
                return png;
            }
        }
        catch (Exception e)
        {
            Log.Debug("Icon: placeholder {Path} failed: {Message}", placeholderPath, e.Message);
        }

        return null;
    }

    private async Task<byte[]?> EncodeAsync(UTexture2D texture, int size, CancellationToken cancellationToken)
    {
        await _decodeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return Encode(texture, size);
        }
        finally
        {
            _decodeGate.Release();
        }
    }

    private static byte[]? Encode(UTexture2D texture, int size)
    {
        try
        {
            // CTexture.Encode has a different signature than you'd expect; always go via SkBitmap.
            var decoded = texture.Decode(maxMipSize: size);
            if (decoded is null) return null;

            using var bitmap = decoded.ToSkBitmap();
            if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0) return null;

            using var scaled = Fit(bitmap, size);
            using var data = scaled.Encode(SKEncodedImageFormat.Png, 100);
            return data?.ToArray();
        }
        catch (Exception e)
        {
            Log.Debug("Icon: decode failed for texture {Name}: {Message}", texture.Name, e.Message);
            return null;
        }
    }

    private static SKBitmap Fit(SKBitmap bitmap, int size)
    {
        var longest = Math.Max(bitmap.Width, bitmap.Height);
        if (longest <= size) return bitmap.Copy();

        var scale = size / (float) longest;
        var info = new SKImageInfo(Math.Max(1, (int) (bitmap.Width * scale)), Math.Max(1, (int) (bitmap.Height * scale)));
        return bitmap.Resize(info, SKFilterQuality.High) ?? bitmap.Copy();
    }

    public static byte[] GeneratePlaceholder(int size)
    {
        using var bitmap = new SKBitmap(size, size);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(0x3A, 0x3A, 0x42));

        using var paint = new SKPaint { Color = new SKColor(0x5A, 0x5A, 0x66), IsAntialias = true, StrokeWidth = Math.Max(1, size / 32f), Style = SKPaintStyle.Stroke };
        var inset = size * 0.22f;
        canvas.DrawLine(inset, inset, size - inset, size - inset, paint);
        canvas.DrawLine(size - inset, inset, inset, size - inset, paint);
        canvas.DrawRect(inset * 0.6f, inset * 0.6f, size - inset * 1.2f, size - inset * 1.2f, paint);

        canvas.Flush();
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static string? SafePath(UObject obj)
    {
        try { return obj.GetPathName(); }
        catch { return obj.Name; }
    }

    // ---------------- disk cache: <DataDirectory>\IconCache\{sha1(objectPath)}_{size}[_{source}].png ----------------

    private static string Sha1(string value)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private string CachePath(string objectPath, int size, IconSource source)
        => Path.Combine(CacheDirectory, $"{Sha1(objectPath)}_{size}_{(int) source}.png");

    private IconResult? TryReadCache(string objectPath, int size)
    {
        foreach (var source in Enum.GetValues<IconSource>())
        {
            var path = CachePath(objectPath, size, source);
            try
            {
                if (!File.Exists(path)) continue;
                var bytes = File.ReadAllBytes(path);
                if (bytes.Length == 0) continue;
                return new IconResult(bytes, source, null);
            }
            catch { /* a corrupt cache entry must never break a request */ }
        }

        return null;
    }

    private void WriteCache(string objectPath, int size, IconResult result)
    {
        try
        {
            File.WriteAllBytes(CachePath(objectPath, size, result.Source), result.Png);
        }
        catch (Exception e)
        {
            Log.Debug("Icon: cache write failed for {Path}: {Message}", objectPath, e.Message);
        }
    }
}
