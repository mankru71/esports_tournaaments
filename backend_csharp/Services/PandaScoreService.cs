using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace Services;

public sealed class PandaScoreFetchResult
{
    public JsonElement? Json { get; init; }
    public int? StatusCode { get; init; }
    public string? Message { get; init; }
    public bool Success => Json.HasValue;
}

public sealed class PandaScoreProbeResult
{
    public bool Success { get; init; }
    public int? StatusCode { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed class StreamStatusInfo
{
    public string Provider { get; init; } = "stream";
    public string Url { get; init; } = string.Empty;
    public string ChannelOrVideo { get; init; } = string.Empty;
    public bool IsLive { get; init; }
    public int? ViewerCount { get; init; }
}

public sealed class LiveDashboardSnapshot
{
    public int TotalPlayers { get; init; }
    public int ActiveTournaments { get; init; }
    public int TotalViewers { get; init; }
    public int EventsToday { get; init; }
    public string MostPopularDiscipline { get; init; } = "н/д";
    public List<object> LiveTournaments { get; init; } = new();
    public int LiveStreams { get; init; }
    public bool ViewersEstimated { get; init; }
}

public class PandaScoreService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<PandaScoreService> _logger;
    private readonly string? _token;
    private readonly string? _twitchClientId;
    private readonly string? _twitchAccessToken;
    private readonly string? _youtubeApiKey;

    private static readonly string[] LiveGames = ["csgo", "dota2", "lol", "valorant", "rl"];

    public PandaScoreService(IHttpClientFactory httpClientFactory, IConfiguration config, IMemoryCache cache, ILogger<PandaScoreService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _logger = logger;
        _token = (config["PandaScore:Token"] ?? config["PandaScore__Token"] ?? config["PANDASCORE_TOKEN"])?.Trim();
        _twitchClientId = (config["TWITCH_CLIENT_ID"] ?? config["Twitch:ClientId"])?.Trim();
        _twitchAccessToken = (config["TWITCH_ACCESS_TOKEN"] ?? config["Twitch:AccessToken"])?.Trim();
        _youtubeApiKey = (config["YOUTUBE_API_KEY"] ?? config["YouTube:ApiKey"])?.Trim();
    }

    public bool Enabled => !string.IsNullOrWhiteSpace(_token);
    public bool HasViewerProviders =>
        (!string.IsNullOrWhiteSpace(_twitchClientId) && !string.IsNullOrWhiteSpace(_twitchAccessToken)) ||
        !string.IsNullOrWhiteSpace(_youtubeApiKey);

    private HttpClient CreateClient()
    {
        var client = _httpClientFactory.CreateClient("pandascore");
        client.DefaultRequestHeaders.Authorization = null;
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (Enabled)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);

        return client;
    }

    private HttpClient CreateRawClient() => _httpClientFactory.CreateClient();

    private static string BuildCacheKey(string path, IDictionary<string, string?> query)
    {
        var q = string.Join("&", query
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .OrderBy(kv => kv.Key)
            .Select(kv => $"{kv.Key}={kv.Value}"));
        return $"pandascore:{path}:{q}";
    }

    private static string BuildUrl(string path, IDictionary<string, string?> query)
    {
        var qs = string.Join("&", query
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .Select(kv => $"{WebUtility.UrlEncode(kv.Key)}={WebUtility.UrlEncode(kv.Value)}"));
        return string.IsNullOrWhiteSpace(qs) ? path : $"{path}?{qs}";
    }

    private static string? NormalizeGameSegment(string? game)
    {
        var value = (game ?? string.Empty).Trim().ToLowerInvariant();
        return value switch
        {
            "counterstrike" => "csgo",
            "cs2" => "csgo",
            "cs:go" => "csgo",
            "csgo" => "csgo",
            "dota" => "dota2",
            "dota2" => "dota2",
            "leagueoflegends" => "lol",
            "league_of_legends" => "lol",
            "lol" => "lol",
            "valorant" => "valorant",
            "rocketleague" => "rl",
            "rocket_league" => "rl",
            "rl" => "rl",
            _ => null
        };
    }

    public async Task<PandaScoreProbeResult> ProbeAsync(CancellationToken ct = default)
    {
        if (!Enabled)
        {
            return new PandaScoreProbeResult
            {
                Success = false,
                StatusCode = 503,
                Message = "PandaScore token is not configured. Укажи PANDASCORE_TOKEN в .env и пересобери контейнеры."
            };
        }

        const string cacheKey = "pandascore:probe";
        if (_cache.TryGetValue(cacheKey, out PandaScoreProbeResult? cached) && cached != null)
            return cached;

        var probe = await GetJsonResponseAsync("/videogames", new Dictionary<string, string?> { ["page[size]"] = "1" }, TimeSpan.FromMinutes(1), ct, skipCache: true);
        var result = probe.Success
            ? new PandaScoreProbeResult { Success = true, StatusCode = 200, Message = "ok" }
            : new PandaScoreProbeResult { Success = false, StatusCode = probe.StatusCode ?? 503, Message = probe.Message ?? "PandaScore временно недоступен" };

        _cache.Set(cacheKey, result, TimeSpan.FromMinutes(1));
        return result;
    }

    private async Task<PandaScoreFetchResult> GetJsonResponseAsync(string path, IDictionary<string, string?> query, TimeSpan cacheTtl, CancellationToken ct, bool skipCache = false)
    {
        if (!Enabled)
            return new PandaScoreFetchResult { StatusCode = 503, Message = "PandaScore token is not configured" };

        var cacheKey = BuildCacheKey(path, query);
        if (!skipCache && _cache.TryGetValue(cacheKey, out JsonElement cached))
            return new PandaScoreFetchResult { Json = cached, StatusCode = 200, Message = "cached" };

        var client = CreateClient();
        var queryWithToken = new Dictionary<string, string?>(query) { ["token"] = _token };
        PandaScoreFetchResult? lastFailure = null;

        foreach (var currentQuery in new[] { query, queryWithToken })
        {
            var url = BuildUrl(path, currentQuery);
            try
            {
                using var resp = await client.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    lastFailure = new PandaScoreFetchResult
                    {
                        StatusCode = (int)resp.StatusCode,
                        Message = ExtractReadableMessage((int)resp.StatusCode, body)
                    };
                    continue;
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                var root = doc.RootElement.Clone();
                _cache.Set(cacheKey, root, cacheTtl);
                return new PandaScoreFetchResult { Json = root, StatusCode = 200, Message = "ok" };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PandaScore request failed for {Url}", url);
                lastFailure = new PandaScoreFetchResult { StatusCode = 503, Message = $"Ошибка соединения с PandaScore: {ex.Message}" };
            }
        }

        return lastFailure ?? new PandaScoreFetchResult { StatusCode = 503, Message = "PandaScore request failed" };
    }

    private static string ExtractReadableMessage(int statusCode, string? body)
    {
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (doc.RootElement.TryGetProperty("error", out var errorEl) && errorEl.ValueKind == JsonValueKind.String)
                        return errorEl.GetString() ?? body;
                    if (doc.RootElement.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.String)
                        return msgEl.GetString() ?? body;
                }
            }
            catch { }
        }

        return statusCode switch
        {
            401 => "PandaScore отклонил токен. Проверь PANDASCORE_TOKEN в .env.",
            403 => "PandaScore запретил доступ для текущего токена или плана.",
            404 => "Ресурс PandaScore не найден.",
            _ => string.IsNullOrWhiteSpace(body) ? $"PandaScore error {statusCode}" : body!
        };
    }

    public async Task<List<PandaTournament>> GetUpcomingTournamentsAsync(int take = 25, string? game = null, CancellationToken ct = default)
    {
        var query = new Dictionary<string, string?>
        {
            ["page[size]"] = Math.Clamp(take, 1, 50).ToString(),
            ["sort"] = "-begin_at",
        };

        var paths = new List<string>();
        var gameSegment = NormalizeGameSegment(game);
        if (!string.IsNullOrWhiteSpace(gameSegment))
            paths.Add($"/{gameSegment}/tournaments/upcoming");
        paths.Add("/tournaments/upcoming");

        foreach (var path in paths.Distinct())
        {
            var response = await GetJsonResponseAsync(path, query, TimeSpan.FromMinutes(10), ct);
            if (response.Json.HasValue && response.Json.Value.ValueKind == JsonValueKind.Array)
                return response.Json.Value.EnumerateArray().Select(PandaTournament.FromJson).Where(x => !string.IsNullOrWhiteSpace(x.Id)).ToList();
        }

        return new List<PandaTournament>();
    }

    public async Task<List<PandaTournament>> GetRunningTournamentsAsync(int take = 25, string? game = null, CancellationToken ct = default)
    {
        var query = new Dictionary<string, string?>
        {
            ["page[size]"] = Math.Clamp(take, 1, 50).ToString(),
            ["sort"] = "-begin_at",
        };

        var paths = new List<string>();
        var gameSegment = NormalizeGameSegment(game);
        if (!string.IsNullOrWhiteSpace(gameSegment))
            paths.Add($"/{gameSegment}/tournaments/running");
        paths.Add("/tournaments/running");

        foreach (var path in paths.Distinct())
        {
            var response = await GetJsonResponseAsync(path, query, TimeSpan.FromMinutes(2), ct);
            if (response.Json.HasValue && response.Json.Value.ValueKind == JsonValueKind.Array)
                return response.Json.Value.EnumerateArray().Select(PandaTournament.FromJson).Where(x => !string.IsNullOrWhiteSpace(x.Id)).ToList();
        }

        return new List<PandaTournament>();
    }

    public async Task<List<PandaTournament>> SearchTournamentsAsync(string queryText, int take = 10, string? game = null, CancellationToken ct = default)
    {
        var baseQuery = new Dictionary<string, string?>
        {
            ["page[size]"] = Math.Clamp(take, 1, 50).ToString(),
            ["sort"] = "-begin_at",
        };

        var paths = new List<string>();
        var gameSegment = NormalizeGameSegment(game);
        if (!string.IsNullOrWhiteSpace(gameSegment))
            paths.Add($"/{gameSegment}/tournaments");
        paths.Add("/tournaments");

        var normalizedQuery = queryText.Trim();
        foreach (var searchKey in new[] { "search[name]", "search[slug]" })
        {
            foreach (var path in paths.Distinct())
            {
                var query = new Dictionary<string, string?>(baseQuery) { [searchKey] = normalizedQuery };
                var response = await GetJsonResponseAsync(path, query, TimeSpan.FromMinutes(5), ct);
                if (response.Json.HasValue && response.Json.Value.ValueKind == JsonValueKind.Array)
                {
                    var list = response.Json.Value.EnumerateArray()
                        .Select(PandaTournament.FromJson)
                        .Where(x => !string.IsNullOrWhiteSpace(x.Id) && !string.IsNullOrWhiteSpace(x.Name))
                        .GroupBy(x => x.Id)
                        .Select(g => g.First())
                        .OrderByDescending(x => ScoreTournament(x, normalizedQuery))
                        .ThenByDescending(x => x.BeginAt)
                        .ToList();
                    if (list.Count > 0)
                        return list;
                }
            }
        }

        // fallback: fetch running + upcoming and fuzzy-filter locally
        var fallback = new List<PandaTournament>();
        fallback.AddRange(await GetRunningTournamentsAsync(take, game, ct));
        fallback.AddRange(await GetUpcomingTournamentsAsync(take * 2, game, ct));
        return fallback
            .GroupBy(x => x.Id)
            .Select(g => g.First())
            .Where(x => ScoreTournament(x, normalizedQuery) > 0)
            .OrderByDescending(x => ScoreTournament(x, normalizedQuery))
            .ThenByDescending(x => x.BeginAt)
            .Take(take)
            .ToList();
    }

    private static int ScoreTournament(PandaTournament tournament, string queryText)
    {
        var q = queryText.Trim().ToLowerInvariant();
        var name = (tournament.Name ?? string.Empty).ToLowerInvariant();
        var league = (tournament.LeagueName ?? string.Empty).ToLowerInvariant();
        if (name == q || league == q) return 100;
        if (name.Contains(q) || league.Contains(q)) return 80;
        var parts = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hits = parts.Count(part => name.Contains(part) || league.Contains(part));
        return hits * 10;
    }

    public async Task<List<PandaMatch>> GetMatchesForTournamentAsync(string providerTournamentId, int take = 50, string? game = null, CancellationToken ct = default)
    {
        var query = new Dictionary<string, string?>
        {
            ["page[size]"] = Math.Clamp(take, 1, 100).ToString(),
            ["filter[tournament_id]"] = providerTournamentId,
            ["sort"] = "begin_at",
        };

        var paths = new List<string>();
        var gameSegment = NormalizeGameSegment(game);
        if (!string.IsNullOrWhiteSpace(gameSegment))
            paths.Add($"/{gameSegment}/matches");
        paths.Add("/matches");

        foreach (var path in paths.Distinct())
        {
            var response = await GetJsonResponseAsync(path, query, TimeSpan.FromMinutes(2), ct);
            if (response.Json.HasValue && response.Json.Value.ValueKind == JsonValueKind.Array)
                return response.Json.Value.EnumerateArray().Select(PandaMatch.FromJson).ToList();
        }

        return new List<PandaMatch>();
    }

    public async Task<List<PandaMatch>> GetRunningMatchesAsync(int take = 50, string? game = null, CancellationToken ct = default)
    {
        var query = new Dictionary<string, string?>
        {
            ["page[size]"] = Math.Clamp(take, 1, 100).ToString(),
            ["sort"] = "-begin_at",
        };

        var paths = new List<string>();
        var gameSegment = NormalizeGameSegment(game);
        if (!string.IsNullOrWhiteSpace(gameSegment))
        {
            paths.Add($"/{gameSegment}/matches/running");
            paths.Add($"/{gameSegment}/lives");
        }
        paths.Add("/matches/running");
        paths.Add("/lives");

        foreach (var path in paths.Distinct())
        {
            var response = await GetJsonResponseAsync(path, query, TimeSpan.FromMinutes(1), ct);
            if (response.Json.HasValue && response.Json.Value.ValueKind == JsonValueKind.Array)
            {
                var parsed = response.Json.Value.EnumerateArray().Select(PandaMatch.FromJson).Where(x => !string.IsNullOrWhiteSpace(x.Id)).ToList();
                if (parsed.Count > 0)
                    return parsed;
            }
        }

        return new List<PandaMatch>();
    }

    public async Task<List<PandaPlayer>> SearchPlayersAsync(string nickname, int take = 10, string? game = null, CancellationToken ct = default)
    {
        var baseQuery = new Dictionary<string, string?> { ["page[size]"] = Math.Clamp(take, 1, 50).ToString() };

        var paths = new List<string>();
        var gameSegment = NormalizeGameSegment(game);
        if (!string.IsNullOrWhiteSpace(gameSegment))
            paths.Add($"/{gameSegment}/players");
        paths.Add("/players");

        foreach (var searchKey in new[] { "search[name]", "search[slug]", "search[first_name]", "search[last_name]" })
        {
            foreach (var path in paths.Distinct())
            {
                var query = new Dictionary<string, string?>(baseQuery) { [searchKey] = nickname };
                var response = await GetJsonResponseAsync(path, query, TimeSpan.FromMinutes(10), ct);
                if (response.Json.HasValue && response.Json.Value.ValueKind == JsonValueKind.Array)
                {
                    var list = response.Json.Value.EnumerateArray()
                        .Select(x => PandaPlayer.FromJson(x, game))
                        .Where(p => !string.IsNullOrWhiteSpace(p.Id) && !string.IsNullOrWhiteSpace(p.Name))
                        .GroupBy(p => p.Id)
                        .Select(g => g.First())
                        .ToList();
                    if (list.Count > 0)
                        return list;
                }
            }
        }

        return new List<PandaPlayer>();
    }

    public async Task<LiveDashboardSnapshot> GetLiveDashboardSnapshotAsync(CancellationToken ct = default)
    {
        const string cacheKey = "pandascore:dashboard:snapshot";
        if (_cache.TryGetValue(cacheKey, out LiveDashboardSnapshot? cached) && cached != null)
            return cached;

        if (!Enabled)
        {
            var disabled = new LiveDashboardSnapshot();
            _cache.Set(cacheKey, disabled, TimeSpan.FromSeconds(30));
            return disabled;
        }

        var tournaments = new List<PandaTournament>();
        var matches = new List<PandaMatch>();

        foreach (var game in LiveGames)
        {
            var runningTournaments = await GetRunningTournamentsAsync(10, game, ct);
            tournaments.AddRange(runningTournaments);
            var runningMatches = await GetRunningMatchesAsync(20, game, ct);
            matches.AddRange(runningMatches);
        }

        tournaments = tournaments.GroupBy(x => x.Id).Select(g => g.First()).ToList();
        matches = matches.GroupBy(x => x.Id).Select(g => g.First()).ToList();

        var discipline = tournaments
            .GroupBy(x => string.IsNullOrWhiteSpace(x.VideogameName) ? "Не указано" : x.VideogameName!)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault() ?? "н/д";

        var streamInfos = await BuildStreamStatusesAsync(matches, ct);
        var totalViewers = streamInfos.Where(x => x.ViewerCount.HasValue).Sum(x => x.ViewerCount ?? 0);

        var snapshot = new LiveDashboardSnapshot
        {
            TotalPlayers = 0,
            ActiveTournaments = tournaments.Count,
            TotalViewers = totalViewers,
            EventsToday = matches.Count,
            MostPopularDiscipline = discipline,
            LiveStreams = streamInfos.Count(x => !string.IsNullOrWhiteSpace(x.Url)),
            ViewersEstimated = !HasViewerProviders,
            LiveTournaments = tournaments.Take(6).Select(t => (object)new
            {
                id = t.Id,
                name = t.Name,
                game = t.VideogameName,
                league = t.LeagueName,
                beginAt = t.BeginAt,
                prizePool = t.PrizePool,
                status = t.Status
            }).ToList()
        };

        _cache.Set(cacheKey, snapshot, TimeSpan.FromSeconds(45));
        return snapshot;
    }

    public async Task<List<StreamStatusInfo>> BuildStreamStatusesAsync(IEnumerable<PandaMatch> matches, CancellationToken ct = default)
    {
        var streams = matches
            .Where(m => !string.IsNullOrWhiteSpace(m.StreamUrl))
            .Select(m => new
            {
                Url = m.StreamUrl!,
                Provider = DetectProvider(m.StreamUrl),
                ChannelOrVideo = ExtractChannelOrVideo(m.StreamUrl)
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.ChannelOrVideo))
            .DistinctBy(x => x.Url)
            .ToList();

        var result = new List<StreamStatusInfo>();
        foreach (var stream in streams)
        {
            int? viewers = null;
            bool isLive = false;

            if (stream.Provider == "twitch")
            {
                var info = await GetTwitchViewerCountAsync(stream.ChannelOrVideo, ct);
                viewers = info.viewerCount;
                isLive = info.isLive;
            }
            else if (stream.Provider == "youtube")
            {
                var info = await GetYouTubeViewerCountAsync(stream.ChannelOrVideo, ct);
                viewers = info.viewerCount;
                isLive = info.isLive;
            }

            result.Add(new StreamStatusInfo
            {
                Provider = stream.Provider,
                Url = stream.Url,
                ChannelOrVideo = stream.ChannelOrVideo,
                IsLive = isLive,
                ViewerCount = viewers
            });
        }

        return result;
    }

    private async Task<(bool isLive, int? viewerCount)> GetTwitchViewerCountAsync(string channel, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_twitchClientId) || string.IsNullOrWhiteSpace(_twitchAccessToken) || string.IsNullOrWhiteSpace(channel))
            return (false, null);

        var cacheKey = $"twitch:stream:{channel.ToLowerInvariant()}";
        if (_cache.TryGetValue(cacheKey, out (bool isLive, int? viewerCount) cached))
            return cached;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.twitch.tv/helix/streams?user_login={WebUtility.UrlEncode(channel)}");
            req.Headers.Add("Client-ID", _twitchClientId);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _twitchAccessToken);
            var client = CreateRawClient();
            using var resp = await client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                return (false, null);

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var data = doc.RootElement.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array
                ? dataEl.EnumerateArray().FirstOrDefault()
                : default;
            if (data.ValueKind != JsonValueKind.Object)
                return (false, null);

            var viewerCount = JsonValue.ReadNullableInt(data, "viewer_count");
            var result = (true, viewerCount);
            _cache.Set(cacheKey, result, TimeSpan.FromSeconds(45));
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Twitch viewer lookup failed for {Channel}", channel);
            return (false, null);
        }
    }

    private async Task<(bool isLive, int? viewerCount)> GetYouTubeViewerCountAsync(string videoId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_youtubeApiKey) || string.IsNullOrWhiteSpace(videoId))
            return (false, null);

        var cacheKey = $"youtube:stream:{videoId}";
        if (_cache.TryGetValue(cacheKey, out (bool isLive, int? viewerCount) cached))
            return cached;

        try
        {
            var url = $"https://www.googleapis.com/youtube/v3/videos?part=liveStreamingDetails&id={WebUtility.UrlEncode(videoId)}&key={WebUtility.UrlEncode(_youtubeApiKey)}";
            var client = CreateRawClient();
            using var resp = await client.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
                return (false, null);

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return (false, null);
            var item = items.EnumerateArray().FirstOrDefault();
            if (item.ValueKind != JsonValueKind.Object)
                return (false, null);
            if (!item.TryGetProperty("liveStreamingDetails", out var details) || details.ValueKind != JsonValueKind.Object)
                return (false, null);

            var viewerCount = JsonValue.ReadNullableInt(details, "concurrentViewers");
            var result = (viewerCount.HasValue, viewerCount);
            _cache.Set(cacheKey, result, TimeSpan.FromSeconds(45));
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "YouTube viewer lookup failed for {VideoId}", videoId);
            return (false, null);
        }
    }

    public static string DetectProvider(string? url)
    {
        var u = (url ?? string.Empty).ToLowerInvariant();
        if (u.Contains("twitch.tv")) return "twitch";
        if (u.Contains("youtube.com") || u.Contains("youtu.be")) return "youtube";
        return "stream";
    }

    public static string ExtractChannelOrVideo(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        try
        {
            var uri = new Uri(url);
            var host = uri.Host.ToLowerInvariant();
            var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (host.Contains("youtu.be"))
                return parts.FirstOrDefault() ?? string.Empty;
            if (host.Contains("youtube.com"))
            {
                if (uri.Query.Contains("v="))
                {
                    foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var bits = pair.Split('=', 2);
                        if (bits.Length == 2 && bits[0] == "v")
                            return Uri.UnescapeDataString(bits[1]);
                    }
                }
                if (parts.Length >= 2 && (parts[0] == "embed" || parts[0] == "live" || parts[0] == "watch"))
                    return parts[1];
            }
            return parts.FirstOrDefault() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}

internal static class JsonValue
{
    public static string? ReadString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var el) || el.ValueKind == JsonValueKind.Null || el.ValueKind == JsonValueKind.Undefined)
            return null;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => el.ToString()
        };
    }

    public static decimal? ReadNullableDecimal(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var el) || el.ValueKind == JsonValueKind.Null || el.ValueKind == JsonValueKind.Undefined)
            return null;
        if (el.ValueKind == JsonValueKind.Number)
        {
            if (el.TryGetDecimal(out var dec)) return dec;
            if (el.TryGetDouble(out var dbl)) return Convert.ToDecimal(dbl, CultureInfo.InvariantCulture);
        }
        return decimal.TryParse(el.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    public static int? ReadNullableInt(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var el) || el.ValueKind == JsonValueKind.Null || el.ValueKind == JsonValueKind.Undefined)
            return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var value))
            return value;
        return int.TryParse(el.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }
}

public record PandaTournament(
    string Id,
    string Name,
    string? Status,
    string? BeginAt,
    string? VideogameName,
    string? VideogameSlug,
    string? LeagueName,
    decimal? PrizePool
)
{
    public static PandaTournament FromJson(JsonElement t)
    {
        string id = JsonValue.ReadString(t, "id") ?? string.Empty;
        string name = JsonValue.ReadString(t, "name")
            ?? JsonValue.ReadString(t, "slug")
            ?? JsonValue.ReadString(t, "serie")
            ?? string.Empty;
        string? status = JsonValue.ReadString(t, "status");
        string? beginAt = JsonValue.ReadString(t, "begin_at");
        decimal? prize = JsonValue.ReadNullableDecimal(t, "prizepool");

        string? vgName = null;
        string? vgSlug = null;
        if (t.TryGetProperty("videogame", out var vgEl) && vgEl.ValueKind == JsonValueKind.Object)
        {
            vgName = JsonValue.ReadString(vgEl, "name");
            vgSlug = JsonValue.ReadString(vgEl, "slug");
        }
        vgName ??= JsonValue.ReadString(t, "videogame_title");
        vgSlug ??= JsonValue.ReadString(t, "videogame_slug");

        string? leagueName = null;
        if (t.TryGetProperty("league", out var lEl) && lEl.ValueKind == JsonValueKind.Object)
            leagueName = JsonValue.ReadString(lEl, "name");
        leagueName ??= JsonValue.ReadString(t, "league_name");

        return new PandaTournament(id, name, status, beginAt, vgName, vgSlug, leagueName, prize);
    }
}

public record PandaMatch(
    string Id,
    string? Name,
    string? Status,
    string? BeginAt,
    string? OpponentA,
    string? OpponentB,
    int ScoreA,
    int ScoreB,
    string? StreamUrl
)
{
    public static PandaMatch FromJson(JsonElement m)
    {
        string id = JsonValue.ReadString(m, "id") ?? string.Empty;
        string? name = JsonValue.ReadString(m, "name") ?? JsonValue.ReadString(m, "slug");
        string? status = JsonValue.ReadString(m, "status");
        string? beginAt = JsonValue.ReadString(m, "begin_at") ?? JsonValue.ReadString(m, "scheduled_at");

        string? teamA = null;
        string? teamB = null;
        if (m.TryGetProperty("opponents", out var opps) && opps.ValueKind == JsonValueKind.Array)
        {
            var items = opps.EnumerateArray().ToList();
            if (items.Count > 0 && items[0].TryGetProperty("opponent", out var o1) && o1.ValueKind == JsonValueKind.Object)
                teamA = JsonValue.ReadString(o1, "name");
            if (items.Count > 1 && items[1].TryGetProperty("opponent", out var o2) && o2.ValueKind == JsonValueKind.Object)
                teamB = JsonValue.ReadString(o2, "name");
        }

        int scoreA = 0;
        int scoreB = 0;
        if (m.TryGetProperty("results", out var resEl) && resEl.ValueKind == JsonValueKind.Array)
        {
            var results = resEl.EnumerateArray().ToList();
            if (results.Count > 0 && results[0].TryGetProperty("score", out var s1) && int.TryParse(s1.ToString(), out var i1)) scoreA = i1;
            if (results.Count > 1 && results[1].TryGetProperty("score", out var s2) && int.TryParse(s2.ToString(), out var i2)) scoreB = i2;
        }

        string? streamUrl = JsonValue.ReadString(m, "official_stream_url") ?? JsonValue.ReadString(m, "live_url");
        if (string.IsNullOrWhiteSpace(streamUrl) && m.TryGetProperty("streams_list", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in streams.EnumerateArray())
            {
                streamUrl = JsonValue.ReadString(s, "raw_url") ?? JsonValue.ReadString(s, "embed_url") ?? JsonValue.ReadString(s, "url");
                if (!string.IsNullOrWhiteSpace(streamUrl)) break;
            }
        }
        if (string.IsNullOrWhiteSpace(streamUrl) && m.TryGetProperty("streams", out var legacyStreams) && legacyStreams.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in legacyStreams.EnumerateArray())
            {
                streamUrl = JsonValue.ReadString(s, "raw_url") ?? JsonValue.ReadString(s, "embed_url") ?? JsonValue.ReadString(s, "url");
                if (!string.IsNullOrWhiteSpace(streamUrl)) break;
            }
        }

        return new PandaMatch(id, name, status, beginAt, teamA, teamB, scoreA, scoreB, streamUrl);
    }
}

public record PandaPlayer(
    string Id,
    string? Name,
    string? FirstName,
    string? LastName,
    string? Role,
    string? Nationality,
    string? ImageUrl,
    string? CurrentTeam,
    string? ProfileUrl
)
{
    private static string? NormalizeProfileGameSegment(string? game)
    {
        var value = (game ?? string.Empty).Trim().ToLowerInvariant();
        return value switch
        {
            "counterstrike" or "cs2" or "cs:go" or "csgo" => "csgo",
            "dota" or "dota2" => "dota2",
            "leagueoflegends" or "league_of_legends" or "lol" => "lol",
            "valorant" => "valorant",
            "rocketleague" or "rocket_league" or "rl" => "rl",
            _ => null
        };
    }

    private static string BuildProfileUrl(string? game, string playerId)
    {
        if (string.IsNullOrWhiteSpace(playerId))
            return "https://www.pandascore.co/";

        var segment = NormalizeProfileGameSegment(game);
        return string.IsNullOrWhiteSpace(segment)
            ? $"https://www.pandascore.co/players/{playerId}"
            : $"https://www.pandascore.co/{segment}/players/{playerId}";
    }

    public static PandaPlayer FromJson(JsonElement p, string? game)
    {
        string id = JsonValue.ReadString(p, "id") ?? string.Empty;
        string? name = JsonValue.ReadString(p, "name");
        string? firstName = JsonValue.ReadString(p, "first_name");
        string? lastName = JsonValue.ReadString(p, "last_name");
        string? role = JsonValue.ReadString(p, "role");
        string? nat = JsonValue.ReadString(p, "nationality");
        string? image = JsonValue.ReadString(p, "image_url");

        string? teamName = null;
        if (p.TryGetProperty("current_team", out var ctEl) && ctEl.ValueKind == JsonValueKind.Object)
            teamName = JsonValue.ReadString(ctEl, "name");

        return new PandaPlayer(id, name, firstName, lastName, role, nat, image, teamName, BuildProfileUrl(game, id));
    }
}
