using System.Text.Json;

namespace EsportsBackend.Services;

public class SteamApiService
{
    private readonly HttpClient _http;
    private readonly string? _apiKey;

    public SteamApiService(IHttpClientFactory httpFactory, IConfiguration config)
    {
        _http = httpFactory.CreateClient();
        _apiKey = config["Steam:ApiKey"];
    }

    public async Task<bool> ValidateOpenIdAsync(IQueryCollection query)
    {
        var parameters = new Dictionary<string, string>();
        foreach (var key in query.Keys)
        {
            parameters[key] = query[key].ToString();
        }
        parameters["openid.mode"] = "check_authentication";

        var content = new FormUrlEncodedContent(parameters);
        var response = await _http.PostAsync("https://steamcommunity.com/openid/login", content);
        
        if (!response.IsSuccessStatusCode) return false;

        var responseString = await response.Content.ReadAsStringAsync();
        return responseString.Contains("is_valid:true");
    }

    public async Task<(string? nickname, string? avatarUrl)> GetPlayerSummariesAsync(string steamId)
    {
        if (string.IsNullOrWhiteSpace(_apiKey)) return (null, null);

        var url = $"https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v0002/?key={_apiKey}&steamids={steamId}";
        try
        {
            var response = await _http.GetAsync(url);
            if (!response.IsSuccessStatusCode) return (null, null);

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var players = doc.RootElement.GetProperty("response").GetProperty("players");

            if (players.GetArrayLength() > 0)
            {
                var p = players[0];
                var nickname = p.TryGetProperty("personaname", out var n) ? n.GetString() : null;
                var avatarUrl = p.TryGetProperty("avatarfull", out var a) ? a.GetString() : null;
                return (nickname, avatarUrl);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SteamApiService] Error fetching profile: {ex.Message}");
        }
        return (null, null);
    }
}
