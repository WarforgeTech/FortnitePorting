using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CUE4Parse.Utils;
using Serilog;

namespace FortnitePorting.Mcp.Core;

public class FortniteVersionResponse
{
    public string? Version { get; set; }
    public FortniteVersionKeys? Keys { get; set; }
    public FortniteVersionMappings? Mappings { get; set; }
}

public class FortniteVersionKeys
{
    public AesKeyResponse? MainKey { get; set; }
    public List<AesKeyResponse> ExtraKeys { get; set; } = [];
}

public class FortniteVersionMappings
{
    public string? Url { get; set; }
    public string? Md5Hash { get; set; }
}

public class AesKeyResponse
{
    public string? Key { get; set; }
    public string? GUID { get; set; }
}

/// <summary>
/// Minimal HttpClient port of the GUI's FortnitePorting API client: just enough to resolve
/// the current AES keys and mappings. The last successful response is cached to disk so a
/// restart without network still loads.
/// </summary>
public class FortnitePortingApi(DirectoryInfo dataDirectory)
{
    private const string BaseUrl = "https://api.fortniteporting.app/v2/";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _client = new()
    {
        BaseAddress = new Uri(BaseUrl, UriKind.Absolute),
        Timeout = TimeSpan.FromSeconds(30)
    };

    private FileInfo CacheFile => new(Path.Combine(dataDirectory.FullName, "fortnite_version_cache.json"));

    public async Task<FortniteVersionResponse?> GetFortniteVersionAsync(string version = "latest", CancellationToken token = default)
    {
        try
        {
            var json = await _client.GetStringAsync($"fortnite/versions/{version}", token);
            var response = JsonSerializer.Deserialize<FortniteVersionResponse>(json, SerializerOptions);
            if (response?.Keys?.MainKey?.Key is not null)
            {
                await File.WriteAllTextAsync(CacheFile.FullName, json, token);
                return response;
            }

            Log.Warning("API returned a version response without a main key, falling back to cache");
        }
        catch (Exception e)
        {
            Log.Warning("Failed to query FortnitePorting API for version {Version}: {Message}", version, e.Message);
        }

        return await ReadCachedAsync(token);
    }

    private async Task<FortniteVersionResponse?> ReadCachedAsync(CancellationToken token)
    {
        if (!CacheFile.Exists) return null;

        try
        {
            var json = await File.ReadAllTextAsync(CacheFile.FullName, token);
            var response = JsonSerializer.Deserialize<FortniteVersionResponse>(json, SerializerOptions);
            if (response is not null)
                Log.Information("Using cached Fortnite version response from {Path}", CacheFile.FullName);

            return response;
        }
        catch (Exception e)
        {
            Log.Warning("Failed to read cached version response: {Message}", e.Message);
            return null;
        }
    }

    /// <summary>
    /// Port of CUE4ParseService.GetEndpointMappings: reuses the on-disk usmap when its MD5
    /// already matches, otherwise downloads it into the data directory.
    /// </summary>
    public async Task<string?> GetEndpointMappingsAsync(FortniteVersionMappings? mappings, CancellationToken token = default)
    {
        if (mappings?.Url is null) return null;

        var mappingsFilePath = Path.Combine(dataDirectory.FullName, mappings.Url.SubstringAfterLast("/"));
        if (File.Exists(mappingsFilePath) && GetFileHashMd5(mappingsFilePath).Equals(mappings.Md5Hash, StringComparison.OrdinalIgnoreCase))
            return mappingsFilePath;

        try
        {
            await using var stream = await _client.GetStreamAsync(mappings.Url, token);
            await using (var fileStream = File.Create(mappingsFilePath))
            {
                await stream.CopyToAsync(fileStream, token);
            }

            File.SetCreationTime(mappingsFilePath, DateTime.Now);
        }
        catch (Exception e)
        {
            Log.Warning("Failed to download mappings from {Url}: {Message}", mappings.Url, e.Message);
            return File.Exists(mappingsFilePath) ? mappingsFilePath : null;
        }

        return new FileInfo(mappingsFilePath) is { Exists: true, Length: > 0 } ? mappingsFilePath : null;
    }

    /// <summary>Newest .usmap already sitting in the data directory, used when the API is unreachable.</summary>
    public string? GetLocalMappings()
    {
        if (!dataDirectory.Exists) return null;

        var usmapFiles = dataDirectory.GetFiles("*.usmap");
        return usmapFiles.Length == 0 ? null : usmapFiles.MaxBy(x => x.CreationTime)?.FullName;
    }

    private static string GetFileHashMd5(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(MD5.HashData(stream)).ToLower();
    }
}
