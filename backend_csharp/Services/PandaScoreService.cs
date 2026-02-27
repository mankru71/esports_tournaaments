using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace Services;

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
        _token = (config["PandaScore:Token"] ?? config["PANDASCORE_TOKEN"])?.Trim();
    }

    public bool Enabled => !string.IsNullOrWhiteSpace(_token);

    private HttpClient CreateClient()
    {
        var client = _httpClientFactory.CreateClient("pandascore");
        client.DefaultRequestHeaders.Authorization = null;

        if (Enabled)
        {
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
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

    private async Task<JsonElement?> GetJsonAsync(string path, IDictionary<string, string?> query, TimeSpan cacheTtl, CancellationToken ct)
    {
        if (!Enabled)
        {
            return null;
        }

        var cacheKey = BuildCacheKey(path, query);
        if (_cache.TryGetValue(cacheKey, out JsonElement cached))
        {
            return cached;
        }

        var url = BuildUrl(path, query);
        var client = CreateClient();

        try
        {
            using var resp = await client.GetAsync(url, ct);
            if ((int)resp.StatusCode == 429)
            {
                _logger.LogWarning("PandaScore rate limit hit for {Url}", url);
                return null;
            }

            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("PandaScore error {Status} for {Url}: {Body}", (int)resp.StatusCode, url, body);
                return null;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            // Most PandaScore endpoints return JSON arrays.
            var root = doc.RootElement.Clone();
            _cache.Set(cacheKey, root, cacheTtl);
            return root;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PandaScore request failed for {Url}", url);
            return null;
        }
    }

    public async Task<List<PandaTournament>> GetUpcomingTournamentsAsync(int take = 25, CancellationToken ct = default)
    {
        // Pagination uses page[size], sorting uses sort, per PandaScore docs/openapi. 
        // We'll keep it simple and fetch a small page.
        var query = new Dictionary<string, string?>
        {
            ["page[size]"] = Math.Clamp(take, 1, 50).ToString(),
            ["sort"] = "-begin_at",
        };

        var json = await GetJsonAsync("/tournaments/upcoming", query, TimeSpan.FromMinutes(10), ct);
        if (json == null || json.Value.ValueKind != JsonValueKind.Array)
        {
            return new List<PandaTournament>();
        }

        var list = new List<PandaTournament>();
        foreach (var t in json.Value.EnumerateArray())
        {
            list.Add(PandaTournament.FromJson(t));
        }
        return list;
    }

    public async Task<List<PandaTournament>> SearchTournamentsAsync(string queryText, int take = 10, CancellationToken ct = default)
    {
        var query = new Dictionary<string, string?>
        {
            ["page[size]"] = Math.Clamp(take, 1, 50).ToString(),
            ["search[name]"] = queryText,
            ["sort"] = "-begin_at",
        };

        var json = await GetJsonAsync("/tournaments", query, TimeSpan.FromMinutes(5), ct);
        if (json == null || json.Value.ValueKind != JsonValueKind.Array)
        {
            return new List<PandaTournament>();
        }

        return json.Value.EnumerateArray().Select(PandaTournament.FromJson).ToList();
    }

    public async Task<List<PandaMatch>> GetMatchesForTournamentAsync(string providerTournamentId, int take = 50, CancellationToken ct = default)
    {
        // Use filtering: /matches?filter[tournament_id]=... per PandaScore docs.
        var query = new Dictionary<string, string?>
        {
            ["page[size]"] = Math.Clamp(take, 1, 100).ToString(),
            ["filter[tournament_id]"] = providerTournamentId,
            ["sort"] = "begin_at",
        };

        var json = await GetJsonAsync("/matches", query, TimeSpan.FromMinutes(2), ct);
        if (json == null || json.Value.ValueKind != JsonValueKind.Array)
        {
            return new List<PandaMatch>();
        }

        return json.Value.EnumerateArray().Select(PandaMatch.FromJson).ToList();
    }

    public async Task<List<PandaPlayer>> SearchPlayersAsync(string nickname, int take = 10, CancellationToken ct = default)
    {
        var query = new Dictionary<string, string?>
        {
            ["page[size]"] = Math.Clamp(take, 1, 50).ToString(),
            ["search[name]"] = nickname,
        };

        var json = await GetJsonAsync("/players", query, TimeSpan.FromMinutes(10), ct);
        if (json == null || json.Value.ValueKind != JsonValueKind.Array)
        {
            return new List<PandaPlayer>();
        }

        return json.Value.EnumerateArray().Select(PandaPlayer.FromJson).ToList();
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
        string id = t.TryGetProperty("id", out var idEl) ? idEl.ToString() : "";
        string name = t.TryGetProperty("name", out var nameEl) ? (nameEl.GetString() ?? "") : "";
        string? status = t.TryGetProperty("status", out var stEl) ? stEl.GetString() : null;
        string? beginAt = t.TryGetProperty("begin_at", out var bEl) ? bEl.GetString() : null;
        decimal? prize = null;
        if (t.TryGetProperty("prizepool", out var pEl) && pEl.ValueKind != JsonValueKind.Null && pEl.TryGetDecimal(out var pd))
        {
            prize = pd;
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
        string id = m.TryGetProperty("id", out var idEl) ? idEl.ToString() : "";
        string? name = m.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
        string? status = m.TryGetProperty("status", out var stEl) ? stEl.GetString() : null;
        string? beginAt = m.TryGetProperty("begin_at", out var bEl) ? bEl.GetString() :
            (m.TryGetProperty("scheduled_at", out var sEl) ? sEl.GetString() : null);

        string? teamA = null;
        string? teamB = null;
        if (m.TryGetProperty("opponents", out var opps) && opps.ValueKind == JsonValueKind.Array)
        {
            var items = opps.EnumerateArray().ToList();
            if (items.Count > 0 && items[0].TryGetProperty("opponent", out var o1) && o1.ValueKind == JsonValueKind.Object)
                if (o1.TryGetProperty("name", out var n1)) teamA = n1.GetString();
            if (items.Count > 1 && items[1].TryGetProperty("opponent", out var o2) && o2.ValueKind == JsonValueKind.Object)
                if (o2.TryGetProperty("name", out var n2)) teamB = n2.GetString();
        }

        int scoreA = 0, scoreB = 0;
        if (m.TryGetProperty("results", out var resEl) && resEl.ValueKind == JsonValueKind.Array)
        {
            // Results can have {team_id, score}. We keep "first two results" as A/B.
            var r = resEl.EnumerateArray().ToList();
            if (r.Count > 0 && r[0].TryGetProperty("score", out var s1) && s1.TryGetInt32(out var i1)) scoreA = i1;
            if (r.Count > 1 && r[1].TryGetProperty("score", out var s2) && s2.TryGetInt32(out var i2)) scoreB = i2;
        }

        string? streamUrl = null;
        // streams_list is the preferred field per PandaScore changelog.
        if (m.TryGetProperty("streams_list", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in streams.EnumerateArray())
            {
                if (s.TryGetProperty("raw_url", out var ru))
                {
                    streamUrl = ru.GetString();
                    if (!string.IsNullOrWhiteSpace(streamUrl)) break;
                }
                if (s.TryGetProperty("url", out var u))
                {
                    streamUrl = u.GetString();
                    if (!string.IsNullOrWhiteSpace(streamUrl)) break;
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
    string? CurrentTeam
)
{
    public static PandaPlayer FromJson(JsonElement p)
    {
        string id = p.TryGetProperty("id", out var idEl) ? idEl.ToString() : "";
        string? name = p.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
        string? firstName = p.TryGetProperty("first_name", out var fn) ? fn.GetString() : null;
        string? lastName = p.TryGetProperty("last_name", out var ln) ? ln.GetString() : null;
        string? role = p.TryGetProperty("role", out var r) ? r.GetString() : null;
        string? nat = p.TryGetProperty("nationality", out var n) ? n.GetString() : null;
        string? image = p.TryGetProperty("image_url", out var img) ? img.GetString() : null;

        string? teamName = null;
        if (p.TryGetProperty("current_team", out var ctEl) && ctEl.ValueKind == JsonValueKind.Object)
        {
            if (ctEl.TryGetProperty("name", out var tn)) teamName = tn.GetString();
        }

        return new PandaPlayer(id, name, firstName, lastName, role, nat, image, teamName);
    }
}
