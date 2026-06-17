using System.Net.Http.Headers;
using System.Text.Json;

namespace Services;

public sealed class FaceitPlayerInfo
{
    public string PlayerId { get; init; } = string.Empty;
    public string Nickname { get; init; } = string.Empty;
    public string? Avatar { get; init; }
    public string? Country { get; init; }
    public int Elo { get; init; }
    public int Level { get; init; }
    public string FaceitUrl { get; init; } = string.Empty;
}

public sealed class FaceitApiService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<FaceitApiService> _logger;
    private readonly string? _apiKey;

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public FaceitApiService(IHttpClientFactory httpClientFactory, IConfiguration config, ILogger<FaceitApiService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _logger = logger;
        _apiKey = (config["Faceit:ApiKey"] ?? config["FACEIT_API_KEY"])?.Trim();
    }

    public bool Enabled => !string.IsNullOrWhiteSpace(_apiKey);

    /// <summary>
    /// Fetches player data from Faceit Open API v4 by nickname.
    /// Preferred game is cs2; falls back to csgo if cs2 data is absent.
    /// </summary>
    public async Task<FaceitPlayerInfo?> GetPlayerByNicknameAsync(string nickname, CancellationToken ct = default)
    {
        var client = _httpClientFactory.CreateClient("faceit");

        if (Enabled)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        string url = $"players?nickname={Uri.EscapeDataString(nickname)}";

        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync(url, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Faceit API request failed for nickname={Nickname}", nickname);
            throw new InvalidOperationException("Faceit API недоступен. Попробуйте позже.", ex);
        }

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Faceit API error {Status}: {Body}", (int)response.StatusCode, body);
            throw new InvalidOperationException($"Faceit API вернул ошибку {(int)response.StatusCode}. Проверьте API-ключ.");
        }

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;

        var playerId = root.TryGetProperty("player_id", out var pidEl) ? pidEl.GetString() ?? "" : "";
        var nick = root.TryGetProperty("nickname", out var nickEl) ? nickEl.GetString() ?? nickname : nickname;
        var avatar = root.TryGetProperty("avatar", out var avatarEl) ? avatarEl.GetString() : null;
        var country = root.TryGetProperty("country", out var cEl) ? cEl.GetString() : null;
        var faceitUrl = root.TryGetProperty("faceit_url", out var fuEl)
            ? (fuEl.GetString() ?? "").Replace("{lang}", "en")
            : $"https://www.faceit.com/en/players/{nick}";

        int elo = 0;
        int level = 0;

        if (root.TryGetProperty("games", out var games))
        {
            // Try cs2 first, then csgo
            foreach (var gameKey in new[] { "cs2", "csgo" })
            {
                if (games.TryGetProperty(gameKey, out var gameData))
                {
                    if (gameData.TryGetProperty("faceit_elo", out var eloEl))
                        elo = eloEl.TryGetInt32(out var eloInt) ? eloInt : 0;
                    if (gameData.TryGetProperty("skill_level", out var lvlEl))
                        level = lvlEl.TryGetInt32(out var lvlInt) ? lvlInt : 0;
                    if (elo > 0) break;
                }
            }
        }

        return new FaceitPlayerInfo
        {
            PlayerId = playerId,
            Nickname = nick,
            Avatar = avatar,
            Country = country,
            Elo = elo,
            Level = level,
            FaceitUrl = faceitUrl
        };
    }
    public string ClientId => _config["Faceit:ClientId"] ?? "";
    private string ClientSecret => _config["Faceit:ClientSecret"] ?? "";

    /// <summary>
    /// Obtains Faceit player info using an OAuth code.
    /// Exchanges code for token, gets userinfo (to find nickname), then fetches player details.
    /// </summary>
    public async Task<FaceitPlayerInfo?> VerifyOAuthCodeAsync(string code, string redirectUri, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(ClientId) || string.IsNullOrEmpty(ClientSecret))
            throw new InvalidOperationException("Faceit OAuth не настроен (Client ID/Secret отсутствует).");

        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        
        // 1. Exchange code for access token
        var authHeader = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{ClientId}:{ClientSecret}"));
        var tokenReq = new HttpRequestMessage(HttpMethod.Post, "https://api.faceit.com/auth/v1/oauth/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                {"grant_type", "authorization_code"},
                {"code", code},
                {"redirect_uri", redirectUri}
            })
        };
        tokenReq.Headers.Authorization = new AuthenticationHeaderValue("Basic", authHeader);
        
        var tokenRes = await client.SendAsync(tokenReq, ct);
        if (!tokenRes.IsSuccessStatusCode)
        {
            var err = await tokenRes.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Faceit OAuth token exchange failed: {Error}", err);
            throw new InvalidOperationException("Ошибка при обмене OAuth кода.");
        }

        using var tokenDoc = await JsonDocument.ParseAsync(await tokenRes.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var accessToken = tokenDoc.RootElement.GetProperty("access_token").GetString();

        // 2. Fetch UserInfo to get nickname
        var userInfoReq = new HttpRequestMessage(HttpMethod.Get, "https://api.faceit.com/auth/v1/resources/userinfo");
        userInfoReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        
        var userInfoRes = await client.SendAsync(userInfoReq, ct);
        if (!userInfoRes.IsSuccessStatusCode)
            throw new InvalidOperationException("Не удалось получить информацию о пользователе Faceit.");

        using var userDoc = await JsonDocument.ParseAsync(await userInfoRes.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var nickname = userDoc.RootElement.GetProperty("nickname").GetString();

        if (string.IsNullOrEmpty(nickname))
            throw new InvalidOperationException("Faceit вернул пустой никнейм.");

        // 3. Fetch full player stats via Data API
        return await GetPlayerByNicknameAsync(nickname, ct);
    }
}
