using Data;
using EsportsBackend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Models;

namespace Services;

/// <summary>
/// Синхронизация внешних турниров из Liquipedia в локальную БД.
///
/// Два уровня:
///  - SyncUpcomingAsync — список турниров (название, даты, статус, ПРИЗОВОЙ ФОНД);
///  - SyncMatchesAsync — матчи конкретного события (лениво, при первом открытии),
///    с персистом в Matches/Teams, чтобы сетка строилась из БД как у локальных.
/// </summary>
public class ExternalTournamentSyncService
{
    private readonly AppDbContext _db;
    private readonly LiquipediaService _liquipedia;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ExternalTournamentSyncService> _logger;

    private const string Provider = "liquipedia";
    private const string ListCacheKey = "liquipedia:sync:upcoming";

    public ExternalTournamentSyncService(AppDbContext db, LiquipediaService liquipedia, IMemoryCache cache, ILogger<ExternalTournamentSyncService> logger)
    {
        _db = db;
        _liquipedia = liquipedia;
        _cache = cache;
        _logger = logger;
    }

    public async Task SyncUpcomingAsync(CancellationToken ct = default)
    {
        if (_cache.TryGetValue(ListCacheKey, out _))
            return;

        try
        {
            await RemoveStalePandascoreAsync(ct);

            var parsed = await _liquipedia.GetTournamentListAsync(ct);
            if (parsed.Count == 0)
            {
                _logger.LogWarning("Liquipedia sync: parser returned 0 tournaments (сеть/разметка). Продолжаем с локальными.");
                _cache.Set(ListCacheKey, true, TimeSpan.FromMinutes(2));
                return;
            }

            // Идущие — все, завершённые недавно и ближайшие — ограниченно
            var selected = parsed.Where(t => t.Status == "live")
                .Concat(parsed.Where(t => t.Status == "finished").OrderByDescending(t => t.EndDate).Take(10))
                .Concat(parsed.Where(t => t.Status == "planned").OrderBy(t => t.StartDate).Take(25))
                .GroupBy(t => t.PageName)
                .Select(g => g.First())
                .ToList();

            var addedCount = 0;
            foreach (var t in selected)
            {
                var existing = await _db.Tournaments
                    .FirstOrDefaultAsync(x => x.Provider == Provider && x.ProviderTournamentId == t.PageName, ct);

                var startDate = t.StartDate?.ToString("yyyy-MM-dd") ?? string.Empty;

                if (existing == null)
                {
                    addedCount += 1;
                    _db.Tournaments.Add(new Tournament
                    {
                        Name = t.Name,
                        Game = "counterstrike",
                        PrizePool = t.PrizePool,
                        MaxParticipants = t.Participants,
                        CurrentParticipants = t.Participants,
                        StartDate = startDate,
                        Status = t.Status,
                        Format = "single_elimination",
                        StageType = "single",
                        IsExternal = true,
                        Provider = Provider,
                        ProviderTournamentId = t.PageName,
                        PrizeDistributionJson = DefaultPrizeDistribution()
                    });
                }
                else
                {
                    existing.Name = t.Name;
                    existing.PrizePool = t.PrizePool > 0 ? t.PrizePool : existing.PrizePool;
                    existing.MaxParticipants = t.Participants > 0 ? t.Participants : existing.MaxParticipants;
                    existing.CurrentParticipants = existing.MaxParticipants;
                    existing.StartDate = string.IsNullOrWhiteSpace(startDate) ? existing.StartDate : startDate;
                    existing.Status = t.Status;
                    existing.IsExternal = true;
                    if (string.IsNullOrWhiteSpace(existing.PrizeDistributionJson))
                        existing.PrizeDistributionJson = DefaultPrizeDistribution();
                }
            }

            // Одна агрегатная запись в ленту вместо записи на каждый турнир
            if (addedCount > 0)
            {
                _db.ActivityLogs.Add(new ActivityLog
                {
                    ActionType = "external_sync",
                    Message = $"Добавлено {addedCount} внешних турниров из Liquipedia"
                });
            }

            await _db.SaveChangesAsync(ct);
            _cache.Set(ListCacheKey, true, TimeSpan.FromMinutes(10));
            _logger.LogInformation("Liquipedia tournaments synced: {Count}", selected.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Liquipedia sync failed (will continue with local tournaments).");
            _cache.Set(ListCacheKey, true, TimeSpan.FromMinutes(2));
        }
    }

    /// <summary>
    /// Ленивая синхронизация матчей внешнего турнира в БД.
    /// Возвращает true, если в БД есть матчи (после синка или уже были).
    /// </summary>
    public async Task<bool> SyncMatchesAsync(Tournament tournament, CancellationToken ct = default)
    {
        if (!tournament.IsExternal
            || tournament.Provider != Provider
            || string.IsNullOrWhiteSpace(tournament.ProviderTournamentId))
        {
            return await _db.Matches.AnyAsync(m => m.TournamentId == tournament.Id, ct);
        }

        var markerKey = $"liquipedia:matches:synced:{tournament.Id}";
        if (_cache.TryGetValue(markerKey, out _))
            return await _db.Matches.AnyAsync(m => m.TournamentId == tournament.Id, ct);

        try
        {
            var parsed = await _liquipedia.GetMatchesAsync(tournament.ProviderTournamentId!, ct);
            if (parsed.Count == 0)
            {
                // Страница без сетки (анонс) — не дёргаем парсер чаще, чем раз в 10 минут
                _cache.Set(markerKey, true, TimeSpan.FromMinutes(10));
                return await _db.Matches.AnyAsync(m => m.TournamentId == tournament.Id, ct);
            }

            var teamsByName = await ResolveExternalTeamsAsync(parsed, ct);

            var oldMatches = await _db.Matches.Where(m => m.TournamentId == tournament.Id).ToListAsync(ct);
            if (oldMatches.Count > 0)
                _db.Matches.RemoveRange(oldMatches);

            foreach (var m in parsed)
            {
                var teamA = m.TeamA != null && teamsByName.TryGetValue(m.TeamA, out var ta) ? ta : null;
                var teamB = m.TeamB != null && teamsByName.TryGetValue(m.TeamB, out var tb) ? tb : null;
                var winner = m.WinnerName != null && teamsByName.TryGetValue(m.WinnerName, out var w) ? w : null;

                _db.Matches.Add(new Match
                {
                    TournamentId = tournament.Id,
                    Round = m.Round,
                    RoundNumber = m.RoundNumber,
                    TeamA = teamA,
                    TeamB = teamB,
                    ScoreA = m.ScoreA,
                    ScoreB = m.ScoreB,
                    Winner = winner,
                    Status = m.Status
                });
            }

            await _db.SaveChangesAsync(ct);
            _cache.Set(markerKey, true, TimeSpan.FromMinutes(30));
            _logger.LogInformation("Liquipedia matches synced for tournament {Id} ({Page}): {Count}",
                tournament.Id, tournament.ProviderTournamentId, parsed.Count);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Liquipedia match sync failed for tournament {Id}", tournament.Id);
            _cache.Set(markerKey, true, TimeSpan.FromMinutes(5));
            return await _db.Matches.AnyAsync(m => m.TournamentId == tournament.Id, ct);
        }
    }

    /// <summary>
    /// Команды внешних турниров живут в Teams с флагом IsExternal = true —
    /// локальные команды не затрагиваются и не смешиваются с ними.
    /// </summary>
    private async Task<Dictionary<string, Team>> ResolveExternalTeamsAsync(List<LpMatch> matches, CancellationToken ct)
    {
        var names = matches
            .SelectMany(m => new[] { m.TeamA, m.TeamB })
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var existing = await _db.Teams
            .Where(t => t.IsExternal && names.Contains(t.Name))
            .ToListAsync(ct);

        var map = new Dictionary<string, Team>(StringComparer.OrdinalIgnoreCase);
        foreach (var team in existing)
            map[team.Name] = team;

        foreach (var name in names)
        {
            if (map.ContainsKey(name))
                continue;
            var team = new Team { Name = name, IsExternal = true };
            _db.Teams.Add(team);
            map[name] = team;
        }

        return map;
    }

    /// <summary>Одноразовая зачистка внешних турниров старого провайдера (pandascore).</summary>
    private async Task RemoveStalePandascoreAsync(CancellationToken ct)
    {
        var stale = await _db.Tournaments
            .Where(t => t.IsExternal && t.Provider == "pandascore")
            .ToListAsync(ct);
        if (stale.Count == 0)
            return;

        var ids = stale.Select(t => t.Id).ToList();
        var matches = await _db.Matches.Where(m => ids.Contains(m.TournamentId)).ToListAsync(ct);
        var favorites = await _db.UserFavorites.Where(f => ids.Contains(f.TournamentId)).ToListAsync(ct);
        var applications = await _db.TournamentApplications.Where(a => ids.Contains(a.TournamentId)).ToListAsync(ct);

        _db.Matches.RemoveRange(matches);
        _db.UserFavorites.RemoveRange(favorites);
        _db.TournamentApplications.RemoveRange(applications);
        _db.Tournaments.RemoveRange(stale);
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Removed {Count} stale pandascore tournaments", stale.Count);
    }

    private static string DefaultPrizeDistribution()
        => "[{\"place\":\"1 место\",\"percent\":50},{\"place\":\"2 место\",\"percent\":30},{\"place\":\"3 место\",\"percent\":20}]";

    public static int GuessRoundNumber(string? roundName)
    {
        if (string.IsNullOrWhiteSpace(roundName)) return 0;
        var lower = roundName.ToLowerInvariant();
        if (lower.Contains("final") && !lower.Contains("semi") && !lower.Contains("quarter")) return 100;
        if (lower.Contains("semi")) return 90;
        if (lower.Contains("quarter")) return 80;
        if (lower.Contains("group")) return 10;

        var m = System.Text.RegularExpressions.Regex.Match(lower, @"\d+");
        if (m.Success && int.TryParse(m.Value, out var n)) return 1000 / n;
        return 50;
    }
}
