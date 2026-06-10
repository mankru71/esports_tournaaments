using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Microsoft.Extensions.Caching.Memory;

namespace EsportsBackend.Services;

public sealed record LpTournament(
    string PageName,
    string Name,
    DateTime? StartDate,
    DateTime? EndDate,
    decimal PrizePool,
    int Participants,
    string Status);

public sealed record LpMatch(
    string Round,
    int RoundNumber,
    string? TeamA,
    string? TeamB,
    int ScoreA,
    int ScoreB,
    string Status,
    string? WinnerName);

/// <summary>
/// Парсер Liquipedia (counterstrike-вики) через MediaWiki API action=parse.
///
/// Источники:
///  - Portal:Tournaments — таблицы table2.tournaments-listing: название, страница,
///    даты, ПРИЗОВОЙ ФОНД, число участников;
///  - страница события — сетка .brkts-bracket (раунд = глубина вложенности
///    .brkts-round-body) и групповые .brkts-matchlist-match.
///
/// Правила Liquipedia: осмысленный User-Agent с контактом и gzip обязательны
/// (иначе 406), темп запросов сдерживает LiquipediaRateLimitHandler + кэш.
/// </summary>
public class LiquipediaService : ITournamentProvider
{
    private readonly HttpClient _http;
    private readonly IMemoryCache _cache;
    private readonly ILogger<LiquipediaService> _logger;

    public string ProviderName => "Liquipedia";

    private static readonly TimeSpan ListCacheTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan PageCacheTtl = TimeSpan.FromMinutes(30);

    public LiquipediaService(HttpClient http, IMemoryCache cache, ILogger<LiquipediaService> logger)
    {
        _http = http;
        _cache = cache;
        _logger = logger;
    }

    // ── ITournamentProvider (обратная совместимость DI) ────────────────
    public async Task<IEnumerable<UnifiedTournament>> GetTournamentsAsync(CancellationToken ct)
    {
        var tournaments = await GetTournamentListAsync(ct);
        return tournaments.Select(t => new UnifiedTournament(t.PageName, t.Name, t.StartDate, t.Status));
    }

    // ── Список турниров с призовыми ────────────────────────────────────
    public async Task<List<LpTournament>> GetTournamentListAsync(CancellationToken ct = default)
    {
        if (_cache.TryGetValue("lp:tournaments", out List<LpTournament>? cached) && cached != null)
            return cached;

        var html = await FetchParsedHtmlAsync("Portal:Tournaments", ct);
        if (string.IsNullOrEmpty(html))
            return new List<LpTournament>();

        var result = ParseTournamentList(html);
        _cache.Set("lp:tournaments", result, ListCacheTtl);
        _logger.LogInformation("Liquipedia: parsed {Count} tournaments from Portal:Tournaments", result.Count);
        return result;
    }

    // ── Матчи события (сетка + групповые matchlist) ────────────────────
    public async Task<List<LpMatch>> GetMatchesAsync(string pageName, CancellationToken ct = default)
    {
        var cacheKey = $"lp:matches:{pageName}";
        if (_cache.TryGetValue(cacheKey, out List<LpMatch>? cached) && cached != null)
            return cached;

        var html = await FetchParsedHtmlAsync(pageName, ct);
        if (string.IsNullOrEmpty(html))
            return new List<LpMatch>();

        var matches = ParseMatches(html);
        _cache.Set(cacheKey, matches, PageCacheTtl);
        _logger.LogInformation("Liquipedia: parsed {Count} matches from {Page}", matches.Count, pageName);
        return matches;
    }

    // ── HTTP: api.php?action=parse → parse.text["*"] ───────────────────
    private async Task<string?> FetchParsedHtmlAsync(string pageName, CancellationToken ct)
    {
        var url = $"counterstrike/api.php?action=parse&page={Uri.EscapeDataString(pageName)}&format=json&prop=text&redirects=1";
        try
        {
            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Liquipedia returned {Status} for page {Page}", (int)response.StatusCode, pageName);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (doc.RootElement.TryGetProperty("parse", out var parse)
                && parse.TryGetProperty("text", out var text)
                && text.TryGetProperty("*", out var inner))
            {
                // MediaWiki кодирует подчёркивания в атрибутах как &#95; —
                // без декодирования XPath по классам вида table2__row--body не матчится
                return inner.GetString()?.Replace("&#95;", "_");
            }

            _logger.LogWarning("Liquipedia: unexpected JSON shape for page {Page}", pageName);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Liquipedia request failed for page {Page}", pageName);
            return null;
        }
    }

    // ── Парсинг Portal:Tournaments ─────────────────────────────────────
    private List<LpTournament> ParseTournamentList(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var result = new List<LpTournament>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var rows = doc.DocumentNode.SelectNodes(
            "//div[contains(@class,'tournaments-listing')]//tr[contains(@class,'table2__row--body')]");
        if (rows == null)
        {
            _logger.LogWarning("Liquipedia: tournaments-listing rows not found — разметка могла измениться");
            return result;
        }

        foreach (var row in rows)
        {
            try
            {
                var link = row.SelectSingleNode(".//td[contains(@class,'column__tournament')]//a[@href]");
                if (link == null)
                    continue;

                var href = link.GetAttributeValue("href", string.Empty);
                var pageName = href.StartsWith("/counterstrike/") ? href["/counterstrike/".Length..] : null;
                var name = HtmlEntity.DeEntitize(link.InnerText).Trim();
                if (string.IsNullOrWhiteSpace(pageName) || string.IsNullOrWhiteSpace(name) || !seen.Add(pageName))
                    continue;

                var cells = row.SelectNodes("./td");
                if (cells == null)
                    continue;

                DateTime? start = null, end = null;
                decimal prize = 0m;
                var participants = 0;

                foreach (var cell in cells)
                {
                    var cellText = HtmlEntity.DeEntitize(cell.InnerText).Trim();
                    if (prize == 0m && cellText.StartsWith('$'))
                        prize = ParsePrize(cellText);
                    else if (start == null && TryParseDateRange(cellText, out var s, out var e))
                    {
                        start = s;
                        end = e;
                    }
                    else if (participants == 0 && int.TryParse(cellText, out var p) && p is > 1 and < 1024)
                        participants = p;
                }

                result.Add(new LpTournament(
                    Uri.UnescapeDataString(pageName),
                    name,
                    start,
                    end,
                    prize,
                    participants,
                    GuessStatus(start, end)));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Liquipedia: failed to parse a tournament row, skipping");
            }
        }

        return result;
    }

    private static decimal ParsePrize(string text)
    {
        // "$25,000" / "$1,399.08 USD"
        var match = Regex.Match(text, @"\$\s*([\d,]+(?:\.\d+)?)");
        if (!match.Success)
            return 0m;
        return decimal.TryParse(match.Groups[1].Value.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0m;
    }

    // "May 25 – Jun 06, 2026" | "May 30–31, 2026" | "May 31, 2026"
    private static bool TryParseDateRange(string text, out DateTime? start, out DateTime? end)
    {
        start = null;
        end = null;
        var m = Regex.Match(text,
            @"^([A-Z][a-z]{2})\s+(\d{1,2})(?:\s*[–—-]\s*(?:([A-Z][a-z]{2})\s+)?(\d{1,2}))?,\s*(\d{4})$");
        if (!m.Success)
            return false;

        var year = int.Parse(m.Groups[5].Value);
        if (!TryMonth(m.Groups[1].Value, out var startMonth))
            return false;

        var startDay = int.Parse(m.Groups[2].Value);
        start = SafeDate(year, startMonth, startDay);

        if (m.Groups[4].Success)
        {
            var endMonth = m.Groups[3].Success && TryMonth(m.Groups[3].Value, out var em) ? em : startMonth;
            end = SafeDate(year, endMonth, int.Parse(m.Groups[4].Value));
        }
        else
        {
            end = start;
        }

        return start != null;
    }

    private static bool TryMonth(string mmm, out int month)
    {
        month = DateTime.TryParseExact(mmm, "MMM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? dt.Month
            : 0;
        return month > 0;
    }

    private static DateTime? SafeDate(int year, int month, int day)
    {
        try { return new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc); }
        catch { return null; }
    }

    private static string GuessStatus(DateTime? start, DateTime? end)
    {
        var today = DateTime.UtcNow.Date;
        if (end.HasValue && end.Value.Date < today)
            return "finished";
        if (start.HasValue && start.Value.Date > today)
            return "planned";
        if (start.HasValue)
            return "live";
        return "planned";
    }

    // ── Парсинг матчей события ─────────────────────────────────────────
    private List<LpMatch> ParseMatches(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var result = new List<LpMatch>();

        ParseGroupMatchlists(doc, result);
        ParseBrackets(doc, result);

        return result;
    }

    /// <summary>Групповые/швейцарские матчи: .brkts-matchlist-match (ячейки: A, счёт A, счёт B, B).</summary>
    private void ParseGroupMatchlists(HtmlDocument doc, List<LpMatch> result)
    {
        var nodes = doc.DocumentNode.SelectNodes("//div[contains(@class,'brkts-matchlist-match')]");
        if (nodes == null)
            return;

        var sectionTitles = new List<string>();
        foreach (var node in nodes)
        {
            var titleNode = node.SelectSingleNode("preceding::div[contains(@class,'brkts-matchlist-title')][1]");
            // Заголовок лежит в <b>; сам div содержит ещё кнопки «Show/Hide»
            var titleText = titleNode?.SelectSingleNode(".//b")?.InnerText ?? titleNode?.InnerText;
            var round = CleanRoundLabel(titleText == null ? "Group Stage" : HtmlEntity.DeEntitize(titleText));
            var sectionIndex = sectionTitles.IndexOf(round);
            if (sectionIndex < 0)
            {
                sectionTitles.Add(round);
                sectionIndex = sectionTitles.Count - 1;
            }

            var opponents = node.SelectNodes("./div[contains(@class,'brkts-matchlist-opponent')]");
            var scores = node.SelectNodes("./div[contains(@class,'brkts-matchlist-score')]");
            if (opponents == null || opponents.Count < 2)
                continue;

            var teamA = ExtractTeamName(opponents[0].GetAttributeValue("aria-label", ""));
            var teamB = ExtractTeamName(opponents[1].GetAttributeValue("aria-label", ""));
            var scoreA = ParseScore(scores is { Count: > 0 } ? scores[0].InnerText : null);
            var scoreB = ParseScore(scores is { Count: > 1 } ? scores[1].InnerText : null);
            var winnerA = HasClassToken(opponents[0], "brkts-matchlist-slot-winner");
            var winnerB = HasClassToken(opponents[1], "brkts-matchlist-slot-winner");

            result.Add(BuildMatch(round, 10 + sectionIndex, teamA, teamB, scoreA, scoreB, winnerA, winnerB));
        }
    }

    /// <summary>
    /// Сетка плей-офф: .brkts-bracket. Liquipedia рендерит дерево, где каждый матч
    /// обёрнут в .brkts-round-body; глубина вложенности убывает к финалу
    /// (первый раунд — самые глубокие узлы). Раунд = maxDepth - depth.
    /// </summary>
    private void ParseBrackets(HtmlDocument doc, List<LpMatch> result)
    {
        var brackets = doc.DocumentNode.SelectNodes(
            "//div[contains(concat(' ',normalize-space(@class),' '),' brkts-bracket ')]");
        if (brackets == null)
            return;

        foreach (var bracket in brackets)
        {
            var headers = new List<string>();
            var headerNodes = bracket.SelectNodes(
                ".//div[contains(@class,'brkts-round-header')]/div[contains(@class,'brkts-header')]");
            if (headerNodes != null)
            {
                foreach (var headerNode in headerNodes)
                {
                    // Название раунда — прямой текст узла (вложенные div'ы — алиасы «QF», «Semis»)
                    var label = string.Concat(headerNode.ChildNodes
                        .Where(c => c.NodeType == HtmlNodeType.Text)
                        .Select(c => c.InnerText));
                    label = CleanRoundLabel(HtmlEntity.DeEntitize(label));
                    if (!string.IsNullOrWhiteSpace(label) && label != "Round")
                        headers.Add(label);
                }
            }

            var matchNodes = bracket.SelectNodes(
                ".//div[contains(concat(' ',normalize-space(@class),' '),' brkts-match ')]");
            if (matchNodes == null)
                continue;

            var parsed = new List<(int Depth, HtmlNode Node)>();
            foreach (var node in matchNodes)
            {
                // Пропускаем попапы с деталями карт — там тоже есть opponent-entry
                if (node.Ancestors("div").Any(a => HasClassToken(a, "brkts-popup")))
                    continue;
                var depth = node.Ancestors("div").Count(a => HasClassToken(a, "brkts-round-body"));
                parsed.Add((depth, node));
            }

            if (parsed.Count == 0)
                continue;

            var maxDepth = parsed.Max(p => p.Depth);
            foreach (var (depth, node) in parsed)
            {
                var roundIndex = maxDepth - depth;
                var round = roundIndex < headers.Count
                    ? headers[roundIndex]
                    : $"Round {roundIndex + 1}";

                var opponents = node.SelectNodes("./div[contains(@class,'brkts-opponent-entry')]");
                if (opponents == null || opponents.Count < 2)
                    continue;

                var teamA = ExtractTeamName(opponents[0].GetAttributeValue("aria-label", ""));
                var teamB = ExtractTeamName(opponents[1].GetAttributeValue("aria-label", ""));
                var scoreA = ParseScore(opponents[0]
                    .SelectSingleNode(".//div[contains(@class,'brkts-opponent-score-inner')]")?.InnerText);
                var scoreB = ParseScore(opponents[1]
                    .SelectSingleNode(".//div[contains(@class,'brkts-opponent-score-inner')]")?.InnerText);
                var winnerA = opponents[0].SelectSingleNode(".//div[contains(@class,'brkts-opponent-win')]") != null;
                var winnerB = opponents[1].SelectSingleNode(".//div[contains(@class,'brkts-opponent-win')]") != null;

                // 60+ — чтобы плей-офф шёл после групповых секций (10+)
                result.Add(BuildMatch(round, 60 + roundIndex, teamA, teamB, scoreA, scoreB, winnerA, winnerB));
            }
        }
    }

    private static LpMatch BuildMatch(string round, int roundNumber, string? teamA, string? teamB, int scoreA, int scoreB, bool winnerA, bool winnerB)
    {
        var status = winnerA || winnerB
            ? "finished"
            : scoreA == 0 && scoreB == 0
                ? "planned"
                : "live";

        var winnerName = winnerA ? teamA : winnerB ? teamB : null;
        return new LpMatch(round, roundNumber, teamA, teamB, scoreA, scoreB, status, winnerName);
    }

    private static string? ExtractTeamName(string ariaLabel)
    {
        var name = HtmlEntity.DeEntitize(ariaLabel ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Equals("TBD", StringComparison.OrdinalIgnoreCase))
            return null;
        return name;
    }

    private static int ParseScore(string? text)
    {
        var value = HtmlEntity.DeEntitize(text ?? string.Empty).Trim();
        // "W"/"FF"/"—" → 0; нормальные значения "0".."99"
        return int.TryParse(value, out var score) ? score : 0;
    }

    private static string CleanRoundLabel(string label)
    {
        var clean = Regex.Replace(label ?? string.Empty, @"\s+", " ").Trim();
        if (clean.EndsWith(" Matches", StringComparison.OrdinalIgnoreCase))
            clean = clean[..^" Matches".Length];
        return string.IsNullOrWhiteSpace(clean) ? "Round" : clean;
    }

    /// <summary>true, если у узла есть класс className или класс с этим префиксом (brkts-popup → brkts-popup-container).</summary>
    private static bool HasClassToken(HtmlNode node, string className)
    {
        var tokens = node.GetAttributeValue("class", string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return tokens.Any(t => t == className || t.StartsWith(className + "-", StringComparison.Ordinal));
    }
}
