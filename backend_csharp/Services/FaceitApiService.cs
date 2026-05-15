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
    private readonly ILogger<FaceitApiService> _logger;
    private readonly string? _apiKey;

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public FaceitApiService(IHttpClientFactory httpClientFactory, IConfiguration config, ILogger<FaceitApiService> logger)
    {
        _httpClientFactory = httpClientFactory;
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
}
