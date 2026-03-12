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

public class PandaScoreService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<PandaScoreService> _logger;
    private readonly string? _token;

    public PandaScoreService(IHttpClientFactory httpClientFactory, IConfiguration config, IMemoryCache cache, ILogger<PandaScoreService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _logger = logger;
        _token = (config["PandaScore:Token"] ?? config["PandaScore__Token"] ?? config["PANDASCORE_TOKEN"])?.Trim();
    }

    public bool Enabled => !string.IsNullOrWhiteSpace(_token);

    private HttpClient CreateClient()
    {
        var client = _httpClientFactory.CreateClient("pandascore");
        client.DefaultRequestHeaders.Authorization = null;
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (Enabled)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        }

        return client;
    }

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
            _ => null
        };
    }

    private static string BuildPlayerProfileUrl(string? game, string playerId)
    {
        var segment = NormalizeGameSegment(game) ?? "players";
        return $"https://developers.pandascore.co/reference/get_{segment}_players-player-id";
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
        if (_cache.TryGetValue(cacheKey, out PandaScoreProbeResult cached) && cached != null)
        {
            return cached;
        }

        var probe = await GetJsonResponseAsync("/videogames", new Dictionary<string, string?> { ["page[size]"] = "1" }, TimeSpan.FromMinutes(1), ct, skipCache: true);
        var result = probe.Success
            ? new PandaScoreProbeResult { Success = true, StatusCode = 200, Message = "ok" }
            : new PandaScoreProbeResult
            {
                Success = false,
                StatusCode = probe.StatusCode ?? 503,
                Message = probe.Message ?? "PandaScore временно недоступен"
            };

        _cache.Set(cacheKey, result, TimeSpan.FromMinutes(1));
        return result;
    }

    private async Task<PandaScoreFetchResult> GetJsonResponseAsync(string path, IDictionary<string, string?> query, TimeSpan cacheTtl, CancellationToken ct, bool skipCache = false)
    {
        if (!Enabled)
        {
            return new PandaScoreFetchResult { StatusCode = 503, Message = "PandaScore token is not configured" };
        }

        var cacheKey = BuildCacheKey(path, query);
        if (!skipCache && _cache.TryGetValue(cacheKey, out JsonElement cached))
        {
            return new PandaScoreFetchResult { Json = cached, StatusCode = 200, Message = "cached" };
        }

        var client = CreateClient();
        var queryWithToken = new Dictionary<string, string?>(query);
        if (!string.IsNullOrWhiteSpace(_token))
        {
            queryWithToken["token"] = _token;
        }

        var urlsToTry = new[]
        {
            BuildUrl(path, query),
            BuildUrl(path, queryWithToken)
        }.Distinct().ToList();

        PandaScoreFetchResult? lastFailure = null;

        foreach (var url in urlsToTry)
        {
            try
            {
                using var resp = await client.GetAsync(url, ct);
                if ((int)resp.StatusCode == 429)
                {
                    return new PandaScoreFetchResult
                    {
                        StatusCode = 429,
                        Message = "PandaScore rate limit exceeded. Подожди немного и повтори запрос."
                    };
                }

                if (!resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    _logger.LogWarning("PandaScore error {Status} for {Url}: {Body}", (int)resp.StatusCode, url, body);
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
                lastFailure = new PandaScoreFetchResult
                {
                    StatusCode = 503,
                    Message = $"Ошибка соединения с PandaScore: {ex.Message}"
                };
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
            catch
            {
            }
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
        {
            paths.Add($"/{gameSegment}/tournaments/upcoming");
        }
        paths.Add("/tournaments/upcoming");

        foreach (var path in paths.Distinct())
        {
            var response = await GetJsonResponseAsync(path, query, TimeSpan.FromMinutes(10), ct);
            if (response.Json.HasValue && response.Json.Value.ValueKind == JsonValueKind.Array)
            {
                return response.Json.Value.EnumerateArray().Select(PandaTournament.FromJson).ToList();
            }
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
        {
            paths.Add($"/{gameSegment}/tournaments");
        }
        paths.Add("/tournaments");

        foreach (var searchKey in new[] { "search[name]", "search[slug]" })
        {
            foreach (var path in paths.Distinct())
            {
                var query = new Dictionary<string, string?>(baseQuery) { [searchKey] = queryText };
                var response = await GetJsonResponseAsync(path, query, TimeSpan.FromMinutes(5), ct);
                if (response.Json.HasValue && response.Json.Value.ValueKind == JsonValueKind.Array)
                {
                    var list = response.Json.Value.EnumerateArray().Select(PandaTournament.FromJson).ToList();
                    if (list.Count > 0)
                        return list;
                }
            }
        }

        return new List<PandaTournament>();
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
        {
            paths.Add($"/{gameSegment}/matches");
        }
        paths.Add("/matches");

        foreach (var path in paths.Distinct())
        {
            var response = await GetJsonResponseAsync(path, query, TimeSpan.FromMinutes(2), ct);
            if (response.Json.HasValue && response.Json.Value.ValueKind == JsonValueKind.Array)
            {
                return response.Json.Value.EnumerateArray().Select(PandaMatch.FromJson).ToList();
            }
        }

        return new List<PandaMatch>();
    }

    public async Task<List<PandaPlayer>> SearchPlayersAsync(string nickname, int take = 10, string? game = null, CancellationToken ct = default)
    {
        var baseQuery = new Dictionary<string, string?>
        {
            ["page[size]"] = Math.Clamp(take, 1, 50).ToString(),
        };

        var paths = new List<string>();
        var gameSegment = NormalizeGameSegment(game);
        if (!string.IsNullOrWhiteSpace(gameSegment))
        {
            paths.Add($"/{gameSegment}/players");
        }
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
        string id = t.TryGetProperty("id", out var idEl) ? idEl.ToString() : string.Empty;
        string name = t.TryGetProperty("name", out var nameEl) ? (nameEl.GetString() ?? string.Empty) : string.Empty;
        string? status = t.TryGetProperty("status", out var stEl) ? stEl.GetString() : null;
        string? beginAt = t.TryGetProperty("begin_at", out var bEl) ? bEl.GetString() : null;
        decimal? prize = null;
        if (t.TryGetProperty("prizepool", out var pEl) && pEl.ValueKind != JsonValueKind.Null)
        {
            if (pEl.TryGetDecimal(out var dec)) prize = dec;
            else if (decimal.TryParse(pEl.ToString(), out var parsed)) prize = parsed;
        }

        string? vgName = null;
        string? vgSlug = null;
        if (t.TryGetProperty("videogame", out var vgEl) && vgEl.ValueKind == JsonValueKind.Object)
        {
            if (vgEl.TryGetProperty("name", out var vgn)) vgName = vgn.GetString();
            if (vgEl.TryGetProperty("slug", out var vgs)) vgSlug = vgs.GetString();
        }

        string? leagueName = null;
        if (t.TryGetProperty("league", out var lEl) && lEl.ValueKind == JsonValueKind.Object)
        {
            if (lEl.TryGetProperty("name", out var ln)) leagueName = ln.GetString();
        }

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
        string id = m.TryGetProperty("id", out var idEl) ? idEl.ToString() : string.Empty;
        string? name = m.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
        string? status = m.TryGetProperty("status", out var stEl) ? stEl.GetString() : null;
        string? beginAt = m.TryGetProperty("begin_at", out var bEl) ? bEl.GetString() : (m.TryGetProperty("scheduled_at", out var sEl) ? sEl.GetString() : null);

        string? teamA = null;
        string? teamB = null;
        if (m.TryGetProperty("opponents", out var opps) && opps.ValueKind == JsonValueKind.Array)
        {
            var items = opps.EnumerateArray().ToList();
            if (items.Count > 0 && items[0].TryGetProperty("opponent", out var o1) && o1.ValueKind == JsonValueKind.Object && o1.TryGetProperty("name", out var n1))
                teamA = n1.GetString();
            if (items.Count > 1 && items[1].TryGetProperty("opponent", out var o2) && o2.ValueKind == JsonValueKind.Object && o2.TryGetProperty("name", out var n2))
                teamB = n2.GetString();
        }

        int scoreA = 0;
        int scoreB = 0;
        if (m.TryGetProperty("results", out var resEl) && resEl.ValueKind == JsonValueKind.Array)
        {
            var results = resEl.EnumerateArray().ToList();
            if (results.Count > 0 && results[0].TryGetProperty("score", out var s1) && int.TryParse(s1.ToString(), out var i1)) scoreA = i1;
            if (results.Count > 1 && results[1].TryGetProperty("score", out var s2) && int.TryParse(s2.ToString(), out var i2)) scoreB = i2;
        }

        string? streamUrl = null;
        if (m.TryGetProperty("official_stream_url", out var officialStream) && officialStream.ValueKind == JsonValueKind.String)
            streamUrl = officialStream.GetString();
        if (string.IsNullOrWhiteSpace(streamUrl) && m.TryGetProperty("live_url", out var liveUrl) && liveUrl.ValueKind == JsonValueKind.String)
            streamUrl = liveUrl.GetString();
        if (string.IsNullOrWhiteSpace(streamUrl) && m.TryGetProperty("streams_list", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in streams.EnumerateArray())
            {
                if (s.TryGetProperty("raw_url", out var raw) && raw.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(raw.GetString()))
                {
                    streamUrl = raw.GetString();
                    break;
                }
                if (s.TryGetProperty("embed_url", out var embed) && embed.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(embed.GetString()))
                {
                    streamUrl = embed.GetString();
                    break;
                }
                if (s.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(url.GetString()))
                {
                    streamUrl = url.GetString();
                    break;
                }
            }
        }
        if (string.IsNullOrWhiteSpace(streamUrl) && m.TryGetProperty("streams", out var legacyStreams) && legacyStreams.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in legacyStreams.EnumerateArray())
            {
                if (s.TryGetProperty("raw_url", out var raw) && raw.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(raw.GetString()))
                {
                    streamUrl = raw.GetString();
                    break;
                }
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
    public static PandaPlayer FromJson(JsonElement p, string? game)
    {
        string id = p.TryGetProperty("id", out var idEl) ? idEl.ToString() : string.Empty;
        string? name = p.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
        string? firstName = p.TryGetProperty("first_name", out var fn) ? fn.GetString() : null;
        string? lastName = p.TryGetProperty("last_name", out var ln) ? ln.GetString() : null;
        string? role = p.TryGetProperty("role", out var r) ? r.GetString() : null;
        string? nat = p.TryGetProperty("nationality", out var n) ? n.GetString() : null;
        string? image = p.TryGetProperty("image_url", out var img) ? img.GetString() : null;

        string? teamName = null;
        if (p.TryGetProperty("current_team", out var ctEl) && ctEl.ValueKind == JsonValueKind.Object && ctEl.TryGetProperty("name", out var tn))
            teamName = tn.GetString();

        return new PandaPlayer(id, name, firstName, lastName, role, nat, image, teamName, BuildPlayerProfileUrl(game, id));
    }
}
