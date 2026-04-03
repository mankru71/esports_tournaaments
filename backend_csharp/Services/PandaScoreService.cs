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
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);

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
            "counterstrike" or "cs2" or "cs:go" or "csgo" => "csgo",
            "dota" or "dota2" => "dota2",
            "leagueoflegends" or "league_of_legends" or "lol" => "lol",
            "valorant" => "valorant",
            "rocketleague" or "rocket_league" or "rl" => "rl",
            _ => null
        };
    }

    private static string NormalizeNeedle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        return string.Join(" ", value.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
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
        var queryWithToken = new Dictionary<string, string?>(query);
        if (!string.IsNullOrWhiteSpace(_token))
            queryWithToken["token"] = _token;

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

    private async Task<List<PandaTournament>> GetTournamentCollectionAsync(IEnumerable<string> paths, IDictionary<string, string?> query, TimeSpan ttl, CancellationToken ct)
    {
        foreach (var path in paths.Distinct())
        {
            var response = await GetJsonResponseAsync(path, query, ttl, ct);
            if (response.Json.HasValue && response.Json.Value.ValueKind == JsonValueKind.Array)
            {
                return response.Json.Value.EnumerateArray().Select(PandaTournament.FromJson).ToList();
            }
        }

        return new List<PandaTournament>();
    }

    private static List<PandaTournament> RankTournamentMatches(IEnumerable<PandaTournament> source, string query, int take)
    {
        var normalizedQuery = NormalizeNeedle(query);
        if (string.IsNullOrWhiteSpace(normalizedQuery))
            return source.Where(t => !string.IsNullOrWhiteSpace(t.Id)).Take(take).ToList();

        var words = normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        static int Score(string haystack, string normalizedQuery, string[] words)
        {
            if (string.IsNullOrWhiteSpace(haystack))
                return 0;

            var text = NormalizeNeedle(haystack);
            if (text == normalizedQuery)
                return 1000;
            if (text.StartsWith(normalizedQuery))
                return 850;
            if (text.Contains(normalizedQuery))
                return 700;

            var score = 0;
            foreach (var word in words)
            {
                if (text == word)
                    score += 150;
                else if (text.StartsWith(word))
                    score += 100;
                else if (text.Contains(word))
                    score += 60;
            }

            return score;
        }

        return source
            .Where(t => !string.IsNullOrWhiteSpace(t.Id) && !string.IsNullOrWhiteSpace(t.Name))
            .GroupBy(t => t.Id)
            .Select(g => g.First())
            .Select(t => new
            {
                Tournament = t,
                Score = Score(t.Name, normalizedQuery, words)
                      + Score(t.LeagueName ?? string.Empty, normalizedQuery, words)
                      + Score(t.VideogameName ?? string.Empty, normalizedQuery, words)
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Tournament.BeginAt)
            .Select(x => x.Tournament)
            .Take(take)
            .ToList();
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

        return await GetTournamentCollectionAsync(paths, query, TimeSpan.FromMinutes(10), ct);
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

        return await GetTournamentCollectionAsync(paths, query, TimeSpan.FromMinutes(5), ct);
    }

    public async Task<List<PandaTournament>> SearchTournamentsAsync(string queryText, int take = 10, string? game = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(queryText))
            return new List<PandaTournament>();

        var normalizedQuery = NormalizeNeedle(queryText);
        var baseQuery = new Dictionary<string, string?>
        {
            ["page[size]"] = Math.Clamp(Math.Max(take, 10), 1, 50).ToString(),
            ["sort"] = "-begin_at",
        };

        var paths = new List<string>();
        var gameSegment = NormalizeGameSegment(game);
        if (!string.IsNullOrWhiteSpace(gameSegment))
            paths.Add($"/{gameSegment}/tournaments");
        paths.Add("/tournaments");

        var aggregated = new List<PandaTournament>();
        foreach (var searchKey in new[] { "search[name]", "search[slug]" })
        {
            foreach (var path in paths.Distinct())
            {
                var query = new Dictionary<string, string?>(baseQuery) { [searchKey] = normalizedQuery };
                var response = await GetJsonResponseAsync(path, query, TimeSpan.FromMinutes(5), ct);
                if (response.Json.HasValue && response.Json.Value.ValueKind == JsonValueKind.Array)
                {
                    aggregated.AddRange(response.Json.Value.EnumerateArray().Select(PandaTournament.FromJson));
                }
            }
        }

        var ranked = RankTournamentMatches(aggregated, normalizedQuery, take);
        if (ranked.Count > 0)
            return ranked;
        if (aggregated.Count > 0)
            return aggregated
                .Where(t => !string.IsNullOrWhiteSpace(t.Id))
                .GroupBy(t => t.Id)
                .Select(g => g.First())
                .OrderByDescending(t => t.BeginAt)
                .Take(take)
                .ToList();

        var fallback = new List<PandaTournament>();
        fallback.AddRange(await GetRunningTournamentsAsync(Math.Max(take * 3, 20), game, ct));
        fallback.AddRange(await GetUpcomingTournamentsAsync(Math.Max(take * 3, 20), game, ct));

        var fallbackRanked = RankTournamentMatches(fallback, normalizedQuery, take);
        if (fallbackRanked.Count > 0)
            return fallbackRanked;

        return fallback
            .Where(t => !string.IsNullOrWhiteSpace(t.Id))
            .GroupBy(t => t.Id)
            .Select(g => g.First())
            .OrderByDescending(t => t.BeginAt)
            .Take(take)
            .ToList();
    }

    public async Task<List<PandaMatch>> GetMatchesForTournamentAsync(string providerTournamentId, int take = 50, string? game = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(providerTournamentId))
            return new List<PandaMatch>();

        var gameSegment = NormalizeGameSegment(game);
        var query = new Dictionary<string, string?>
        {
            ["page[size]"] = Math.Clamp(take, 1, 100).ToString(),
            ["filter[tournament_id]"] = providerTournamentId,
            ["sort"] = "begin_at",
        };

        var filterPaths = new List<string>();
        if (!string.IsNullOrWhiteSpace(gameSegment))
            filterPaths.Add($"/{gameSegment}/matches");
        filterPaths.Add("/matches");

        foreach (var path in filterPaths.Distinct())
        {
            var response = await GetJsonResponseAsync(path, query, TimeSpan.FromMinutes(2), ct);
            if (response.Json.HasValue && response.Json.Value.ValueKind == JsonValueKind.Array)
            {
                var list = response.Json.Value.EnumerateArray().Select(PandaMatch.FromJson).ToList();
                if (list.Count > 0)
                    return list;
            }
        }

        var nestedQuery = new Dictionary<string, string?>
        {
            ["page[size]"] = Math.Clamp(take, 1, 100).ToString(),
            ["sort"] = "begin_at",
        };
        var nestedPaths = new List<string>();
        if (!string.IsNullOrWhiteSpace(gameSegment))
            nestedPaths.Add($"/{gameSegment}/tournaments/{providerTournamentId}/matches");
        nestedPaths.Add($"/tournaments/{providerTournamentId}/matches");

        foreach (var path in nestedPaths.Distinct())
        {
            var response = await GetJsonResponseAsync(path, nestedQuery, TimeSpan.FromMinutes(2), ct);
            if (response.Json.HasValue && response.Json.Value.ValueKind == JsonValueKind.Array)
            {
                var list = response.Json.Value.EnumerateArray().Select(PandaMatch.FromJson).ToList();
                if (list.Count > 0)
                    return list;
            }
        }

        return new List<PandaMatch>();
    }

    public async Task<List<PandaPlayer>> SearchPlayersAsync(string nickname, int take = 10, string? game = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(nickname))
            return new List<PandaPlayer>();

        var baseQuery = new Dictionary<string, string?>
        {
            ["page[size]"] = Math.Clamp(take, 1, 50).ToString(),
        };

        var paths = new List<string>();
        var gameSegment = NormalizeGameSegment(game);
        if (!string.IsNullOrWhiteSpace(gameSegment))
            paths.Add($"/{gameSegment}/players");
        paths.Add("/players");

        foreach (var searchKey in new[] { "search[name]", "search[slug]", "search[first_name]", "search[last_name]" })
        {
            foreach (var path in paths.Distinct())
            {
                var query = new Dictionary<string, string?>(baseQuery) { [searchKey] = nickname.Trim() };
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
        var id = TryReadString(t, "id") ?? string.Empty;
        var name = TryReadString(t, "name") ?? string.Empty;
        var status = TryReadString(t, "status");
        var beginAt = TryReadString(t, "begin_at");
        decimal? prize = null;
        if (t.TryGetProperty("prizepool", out var prizeElement) && prizeElement.ValueKind != JsonValueKind.Null)
        {
            if (prizeElement.ValueKind == JsonValueKind.Number && prizeElement.TryGetDecimal(out var dec))
                prize = dec;
            else if (decimal.TryParse(prizeElement.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
                prize = parsed;
        }

        string? videogameName = null;
        string? videogameSlug = null;
        if (t.TryGetProperty("videogame", out var videogameElement) && videogameElement.ValueKind == JsonValueKind.Object)
        {
            videogameName = TryReadString(videogameElement, "name");
            videogameSlug = TryReadString(videogameElement, "slug");
        }

        string? leagueName = null;
        if (t.TryGetProperty("league", out var leagueElement) && leagueElement.ValueKind == JsonValueKind.Object)
            leagueName = TryReadString(leagueElement, "name");

        return new PandaTournament(id, name, status, beginAt, videogameName, videogameSlug, leagueName, prize);
    }

    private static string? TryReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => value.ToString()
        };
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
        var id = TryReadString(m, "id") ?? string.Empty;
        var name = TryReadString(m, "name");
        var status = TryReadString(m, "status");
        var beginAt = TryReadString(m, "begin_at") ?? TryReadString(m, "scheduled_at");

        string? teamA = null;
        string? teamB = null;
        if (m.TryGetProperty("opponents", out var opponents) && opponents.ValueKind == JsonValueKind.Array)
        {
            var items = opponents.EnumerateArray().ToList();
            if (items.Count > 0 && items[0].TryGetProperty("opponent", out var firstOpponent) && firstOpponent.ValueKind == JsonValueKind.Object)
                teamA = TryReadString(firstOpponent, "name");
            if (items.Count > 1 && items[1].TryGetProperty("opponent", out var secondOpponent) && secondOpponent.ValueKind == JsonValueKind.Object)
                teamB = TryReadString(secondOpponent, "name");
        }

        var scoreA = 0;
        var scoreB = 0;
        if (m.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            var items = results.EnumerateArray().ToList();
            if (items.Count > 0 && items[0].TryGetProperty("score", out var score1) && int.TryParse(score1.ToString(), out var s1))
                scoreA = s1;
            if (items.Count > 1 && items[1].TryGetProperty("score", out var score2) && int.TryParse(score2.ToString(), out var s2))
                scoreB = s2;
        }

        var streamUrl = TryReadString(m, "official_stream_url") ?? TryReadString(m, "live_url");
        if (string.IsNullOrWhiteSpace(streamUrl) && m.TryGetProperty("streams_list", out var streamsList) && streamsList.ValueKind == JsonValueKind.Array)
            streamUrl = ExtractStreamUrl(streamsList);
        if (string.IsNullOrWhiteSpace(streamUrl) && m.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
            streamUrl = ExtractStreamUrl(streams);

        return new PandaMatch(id, name, status, beginAt, teamA, teamB, scoreA, scoreB, streamUrl);
    }

    private static string? ExtractStreamUrl(JsonElement streams)
    {
        foreach (var stream in streams.EnumerateArray())
        {
            var raw = TryReadString(stream, "raw_url");
            if (!string.IsNullOrWhiteSpace(raw))
                return raw;
            var embed = TryReadString(stream, "embed_url");
            if (!string.IsNullOrWhiteSpace(embed))
                return embed;
            var url = TryReadString(stream, "url");
            if (!string.IsNullOrWhiteSpace(url))
                return url;
        }
        return null;
    }

    private static string? TryReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => value.ToString()
        };
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
        var id = TryReadString(p, "id") ?? string.Empty;
        var name = TryReadString(p, "name");
        var firstName = TryReadString(p, "first_name");
        var lastName = TryReadString(p, "last_name");
        var role = TryReadString(p, "role");
        var nationality = TryReadString(p, "nationality");
        var imageUrl = TryReadString(p, "image_url");

        string? teamName = null;
        if (p.TryGetProperty("current_team", out var currentTeam) && currentTeam.ValueKind == JsonValueKind.Object)
            teamName = TryReadString(currentTeam, "name");

        return new PandaPlayer(id, name, firstName, lastName, role, nationality, imageUrl, teamName, BuildProfileUrl(game, id));
    }

    private static string? TryReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => value.ToString()
        };
    }
}
