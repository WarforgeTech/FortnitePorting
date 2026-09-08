using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace FortnitePorting.Mcp.Core;

/// <summary>
/// Cached Epic client-credentials token. The GUI kept the same two fields on
/// <c>AppSettings.Application.EpicAuth</c>; headless they live in a JSON file in the data
/// directory so a restart inside the token's lifetime does not hit the OAuth endpoint again.
/// </summary>
public class EpicAuthToken
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("expires_at")]
    public DateTimeOffset? ExpiresAt { get; set; }

    [JsonIgnore]
    public bool IsUsable => !string.IsNullOrWhiteSpace(AccessToken)
                            && (ExpiresAt is null || ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(5));
}

/// <summary>
/// Headless port of the GUI's <c>EpicGamesAPI</c>, trimmed to the one thing on-demand texture
/// streaming needs: a bearer token for the chunk CDN. The GUI's flow was
/// <c>VerifyAuthAsync()</c> - probe the stored token against /oauth/verify and mint a new one
/// through the client-credentials grant when it no longer verifies - and that is reproduced here
/// with the same public Fortnite launcher basic token.
/// </summary>
public class EpicGamesApi(DirectoryInfo dataDirectory)
{
    private const string OauthPostUrl = "https://account-public-service-prod03.ol.epicgames.com/account/api/oauth/token";
    private const string OauthVerifyUrl = "https://account-public-service-prod.ol.epicgames.com/account/api/oauth/verify";
    private const string BasicToken = "basic ZWM2ODRiOGM2ODdmNDc5ZmFkZWEzY2IyYWQ4M2Y1YzY6ZTFmMzFjMjExZjI4NDEzMTg2MjYyZDM3YTEzZmM4NGQ=";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(30) };

    private FileInfo TokenFile => new(Path.Combine(dataDirectory.FullName, "epic_auth.json"));

    /// <summary>
    /// Returns a usable bearer token, or null when Epic could not be reached. Never throws: a
    /// failure here only costs high-resolution mips, so the caller degrades instead of failing.
    /// </summary>
    public async Task<string?> GetTokenAsync(CancellationToken token = default)
    {
        var cached = ReadCached();
        if (cached is { IsUsable: true })
        {
            if (await VerifyAsync(cached.AccessToken!, token))
            {
                Log.Information("Reusing cached Epic auth token (expires {Expiry:u})", cached.ExpiresAt);
                return cached.AccessToken;
            }

            Log.Information("Cached Epic auth token no longer verifies, requesting a new one");
        }

        var fresh = await RequestTokenAsync(token);
        if (fresh?.AccessToken is null) return null;

        WriteCached(fresh);
        return fresh.AccessToken;
    }

    private async Task<bool> VerifyAsync(string accessToken, CancellationToken token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, OauthVerifyUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("bearer", accessToken);

            using var response = await _client.SendAsync(request, token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception e)
        {
            Log.Warning("Epic auth verification failed: {Message}", e.Message);
            return false;
        }
    }

    private async Task<EpicAuthToken?> RequestTokenAsync(CancellationToken token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, OauthPostUrl);
            request.Headers.TryAddWithoutValidation("Authorization", BasicToken);
            request.Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("grant_type", "client_credentials")]);

            using var response = await _client.SendAsync(request, token);
            var body = await response.Content.ReadAsStringAsync(token);
            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("Epic auth request returned {Status}: {Body}", (int) response.StatusCode, Truncate(body));
                return null;
            }

            var parsed = JsonSerializer.Deserialize<EpicAuthToken>(body, SerializerOptions);
            if (parsed?.AccessToken is null)
            {
                Log.Warning("Epic auth response carried no access_token");
                return null;
            }

            Log.Information("Acquired Epic auth token (expires {Expiry:u})", parsed.ExpiresAt);
            return parsed;
        }
        catch (Exception e)
        {
            Log.Warning("Failed to acquire Epic auth token: {Message}", e.Message);
            return null;
        }
    }

    private EpicAuthToken? ReadCached()
    {
        if (!TokenFile.Exists) return null;

        try
        {
            return JsonSerializer.Deserialize<EpicAuthToken>(File.ReadAllText(TokenFile.FullName), SerializerOptions);
        }
        catch (Exception e)
        {
            Log.Warning("Failed to read cached Epic auth token: {Message}", e.Message);
            return null;
        }
    }

    private void WriteCached(EpicAuthToken auth)
    {
        try
        {
            File.WriteAllText(TokenFile.FullName, JsonSerializer.Serialize(auth, SerializerOptions));
        }
        catch (Exception e)
        {
            Log.Warning("Failed to cache Epic auth token: {Message}", e.Message);
        }
    }

    private static string Truncate(string value) => value.Length <= 200 ? value : value[..200] + "...";
}
