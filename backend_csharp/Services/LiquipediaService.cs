using Microsoft.Extensions.Caching.Memory;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Services;

public class LiquipediaService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;

    private static readonly SemaphoreSlim RateLimitLock = new(1, 1);
    private static DateTimeOffset _lastRequestAt = DateTimeOffset.MinValue;

    public LiquipediaService(IHttpClientFactory httpClientFactory, IMemoryCache cache)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
    }

    public async Task<(string? title, Dictionary<string, string> info)> GetPlayerInfoAsync(string game, string nickname, CancellationToken ct = default)
    {
        game = NormalizeGame(game);
        nickname = (nickname ?? string.Empty).Trim();
        if (nickname.Length < 2) return (null, new());

        var cacheKey = $"liq:player:{game}:{nickname}".ToLowerInvariant();
        if (_cache.TryGetValue(cacheKey, out (string? title, Dictionary<string, string> info) cached))
        {
            return cached;
        }

        // 1) Найдём страницу (на Liquipedia название может отличаться)
        var foundTitle = await SearchFirstTitleAsync(game, nickname, ct);
        if (string.IsNullOrWhiteSpace(foundTitle))
        {
            (string? title, Dictionary<string, string> info) empty = (null, new Dictionary<string, string>());
            _cache.Set(cacheKey, empty, TimeSpan.FromMinutes(5));
            return empty;
        }

        // 2) Получим wikitext и распарсим инфобокс
        var wikitext = await GetWikitextAsync(game, foundTitle, ct);
        var info = ParseInfoboxFields(wikitext, new[]
        {
            "id","name","romanized_name","fullname","country","nationality","team","team1","role","roles","status","years_active","approx_earnings"
        });

        var result = (foundTitle, info);
        _cache.Set(cacheKey, result, TimeSpan.FromMinutes(5));
        return result;
    }

    public async Task<(string? title, List<Dictionary<string, string>> streams)> GetTournamentStreamsAsync(string game, string queryOrTitle, CancellationToken ct = default)
    {
        game = NormalizeGame(game);
        queryOrTitle = (queryOrTitle ?? string.Empty).Trim();
        if (queryOrTitle.Length < 2) return (null, new());

        var cacheKey = $"liq:tournament_streams:{game}:{queryOrTitle}".ToLowerInvariant();
        if (_cache.TryGetValue(cacheKey, out (string? title, List<Dictionary<string, string>> streams) cached))
        {
            return cached;
        }

        // Если пришло полное название — можно использовать как title, но для UX сначала поиском
        var title = await SearchFirstTitleAsync(game, queryOrTitle, ct);
        if (string.IsNullOrWhiteSpace(title))
        {
            (string? title, List<Dictionary<string, string>> streams) empty = (null, new List<Dictionary<string, string>>());
            _cache.Set(cacheKey, empty, TimeSpan.FromMinutes(2));
            return empty;
        }

        var wikitext = await GetWikitextAsync(game, title, ct);
        var streams = ExtractStreams(wikitext);

        var result = (title, streams);
        _cache.Set(cacheKey, result, TimeSpan.FromMinutes(2));
        return result;
    }

    private static string NormalizeGame(string game)
    {
        var key = (game ?? string.Empty).Trim().ToLowerInvariant();
        if (key is "cs" or "csgo" or "cs2" or "counter-strike" or "counterstrike") return "counterstrike";
        if (key is "dota" or "dota2") return "dota2";
        if (key is "lol" or "league" or "leagueoflegends") return "leagueoflegends";
        return "counterstrike";
    }

    private static string ApiUrlFor(string game) => game switch
    {
        "dota2" => "https://liquipedia.net/dota2/api.php",
        "leagueoflegends" => "https://liquipedia.net/leagueoflegends/api.php",
        _ => "https://liquipedia.net/counterstrike/api.php",
    };

    private async Task<string?> SearchFirstTitleAsync(string game, string query, CancellationToken ct)
    {
        var url = ApiUrlFor(game);
        var client = CreateClient();

        var endpoint = $"{url}?action=query&list=search&srsearch={Uri.EscapeDataString(query)}&srlimit=1&format=json";
        var json = await GetStringRateLimitedAsync(client, endpoint, ct);
        if (string.IsNullOrWhiteSpace(json)) return null;

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("query", out var q)) return null;
        if (!q.TryGetProperty("search", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        if (arr.GetArrayLength() == 0) return null;
        return arr[0].GetProperty("title").GetString();
    }

    private async Task<string> GetWikitextAsync(string game, string title, CancellationToken ct)
    {
        var url = ApiUrlFor(game);
        var client = CreateClient();

        // action=query + revisions -> wikitext
        var endpoint = $"{url}?action=query&prop=revisions&rvprop=content&rvslots=main&formatversion=2&titles={Uri.EscapeDataString(title)}&format=json";
        var json = await GetStringRateLimitedAsync(client, endpoint, ct);
        if (string.IsNullOrWhiteSpace(json)) return string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var pages = doc.RootElement.GetProperty("query").GetProperty("pages");
            if (pages.ValueKind != JsonValueKind.Array || pages.GetArrayLength() == 0) return string.Empty;
            var page = pages[0];
            if (!page.TryGetProperty("revisions", out var revs) || revs.ValueKind != JsonValueKind.Array || revs.GetArrayLength() == 0) return string.Empty;
            var slots = revs[0].GetProperty("slots").GetProperty("main");
            if (!slots.TryGetProperty("content", out var content)) return string.Empty;
            return content.GetString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private HttpClient CreateClient()
    {
        var client = _httpClientFactory.CreateClient("liquipedia");
        return client;
    }

    private static Dictionary<string, string> ParseInfoboxFields(string wikitext, IEnumerable<string> keys)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(wikitext)) return dict;

        // Возьмём первые ~4000 символов — обычно инфобокс вверху.
        var head = wikitext.Length > 4000 ? wikitext[..4000] : wikitext;

        foreach (var key in keys)
        {
            var m = Regex.Match(head, @"\|\s*" + Regex.Escape(key) + @"\s*=\s*([^\n\r\|\}]+)", RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            var value = CleanupWikiValue(m.Groups[1].Value);
            if (!string.IsNullOrWhiteSpace(value) && !dict.ContainsKey(key))
            {
                dict[key] = value;
            }
        }

        return dict;
    }

    private static List<Dictionary<string, string>> ExtractStreams(string wikitext)
    {
        var results = new List<Dictionary<string, string>>();
        if (string.IsNullOrWhiteSpace(wikitext)) return results;

        // Ищем twitch/youtube поля в инфобоксе
        var head = wikitext.Length > 6000 ? wikitext[..6000] : wikitext;

        var fields = new[] { "twitch", "twitch2", "twitch3", "twitch4", "stream", "stream2", "youtube", "youtube2" };
        foreach (var f in fields)
        {
            var m = Regex.Match(head, @"\|\s*" + Regex.Escape(f) + @"\s*=\s*([^\n\r\|\}]+)", RegexOptions.IgnoreCase);
            if (!m.Success) continue;

            var raw = m.Groups[1].Value.Trim();
            if (string.IsNullOrWhiteSpace(raw)) continue;

            if (f.StartsWith("twitch", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var channel in ExtractTwitchChannels(raw))
                {
                    results.Add(new Dictionary<string, string>
                    {
                        ["provider"] = "twitch",
                        ["channel"] = channel,
                        ["url"] = $"https://twitch.tv/{channel}"
                    });
                }
            }
            else if (f.StartsWith("youtube", StringComparison.OrdinalIgnoreCase))
            {
                var url = ExtractUrl(raw);
                if (!string.IsNullOrWhiteSpace(url))
                {
                    results.Add(new Dictionary<string, string>
                    {
                        ["provider"] = "youtube",
                        ["url"] = url
                    });
                }
            }
            else
            {
                // stream/stream2 — может быть любая ссылка
                var url = ExtractUrl(raw);
                if (!string.IsNullOrWhiteSpace(url))
                {
                    results.Add(new Dictionary<string, string>
                    {
                        ["provider"] = "stream",
                        ["url"] = url
                    });
                }
            }
        }

        // Уберём дубли
        results = results
            .GroupBy(x =>
            {
                x.TryGetValue("provider", out var p);
                x.TryGetValue("channel", out var c);
                x.TryGetValue("url", out var u);
                return $"{p}|{c}|{u}";
            })
            .Select(g => g.First())
            .ToList();

        return results;
    }

    private static IEnumerable<string> ExtractTwitchChannels(string raw)
    {
        raw = CleanupWikiValue(raw);

        // {{twitch|channel}} or {{Twitch|channel}}
        foreach (Match m in Regex.Matches(raw, @"\{\{\s*twitch\s*\|\s*([^\}\|]+)\s*\}\}", RegexOptions.IgnoreCase))
        {
            var ch = m.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(ch)) yield return ch;
        }

        // https://twitch.tv/channel
        foreach (Match m in Regex.Matches(raw, @"twitch\.tv\/([A-Za-z0-9_]+)", RegexOptions.IgnoreCase))
        {
            var ch = m.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(ch)) yield return ch;
        }

        // Если просто текстом указан канал (без ссылок/шаблонов)
        if (Regex.IsMatch(raw, @"^[A-Za-z0-9_]+$"))
        {
            yield return raw;
        }
    }

    private static string ExtractUrl(string raw)
    {
        raw = raw.Trim();
        // [[https://... label]] or [https://...]
        var m = Regex.Match(raw, @"https?:\/\/[^\s\]\}<>]+", RegexOptions.IgnoreCase);
        if (m.Success) return m.Value;
        return string.Empty;
    }

    private static string CleanupWikiValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var v = value.Trim();

        // Уберём wiki-ссылки [[Page|Label]]
        v = Regex.Replace(v, @"\[\[([^\]|]+)\|([^\]]+)\]\]", "$2");
        v = Regex.Replace(v, @"\[\[([^\]]+)\]\]", "$1");

        // Уберём HTML теги и <br>
        v = Regex.Replace(v, @"<br\s*\/?>", ", ", RegexOptions.IgnoreCase);
        v = Regex.Replace(v, @"<[^>]+>", "");

        // Уберём шаблоны {{flag|...}} и др. примитивно
        v = Regex.Replace(v, @"\{\{[^\}]+\}\}", "");

        // Уберём остатки спецсимволов
        v = v.Replace("&nbsp;", " ").Replace("&amp;", "&");

        // Нормализуем пробелы и запятые
        v = Regex.Replace(v, @"\s+", " ").Trim();
        v = v.Trim(',', ' ');

        return v;
    }

    private async Task<string> GetStringRateLimitedAsync(HttpClient client, string url, CancellationToken ct)
    {
        // Liquipedia просит ограничивать запросы (пример: 1 запрос / 2 секунды) и ставить User-Agent.
        // Мы делаем мягкий rate-limit на уровне сервиса + кэширование, чтобы не «ддосить» wiki.
        await RateLimitLock.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var minDelay = TimeSpan.FromSeconds(2);
            var elapsed = now - _lastRequestAt;
            if (elapsed < minDelay)
            {
                var delay = minDelay - elapsed;
                if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
            }

            _lastRequestAt = DateTimeOffset.UtcNow;
        }
        finally
        {
            RateLimitLock.Release();
        }

        try
        {
            return await client.GetStringAsync(url, ct);
        }
        catch
        {
            return string.Empty;
        }
    }
}
