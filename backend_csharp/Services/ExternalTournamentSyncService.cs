using Data;
using EsportsBackend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Models;

namespace Services;

/// <summary>
/// Синхронизация внешних турниров из Liquipedia, Pandascore и Faceit в локальную БД.
/// </summary>
public class ExternalTournamentSyncService
{
    private readonly AppDbContext _db;
    private readonly LiquipediaService _liquipedia;
    private readonly PandaScoreService _pandascore;
    private readonly IEnumerable<ITournamentProvider> _providers;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ExternalTournamentSyncService> _logger;

    private const string ListCacheKey = "external:sync:upcoming";

    public ExternalTournamentSyncService(
        AppDbContext db, 
        LiquipediaService liquipedia, 
        PandaScoreService pandascore,
        IEnumerable<ITournamentProvider> providers,
        IMemoryCache cache, 
        ILogger<ExternalTournamentSyncService> logger)
    {
        _db = db;
        _liquipedia = liquipedia;
        _pandascore = pandascore;
        _providers = providers;
        _cache = cache;
        _logger = logger;
    }

    public async Task SyncUpcomingAsync(CancellationToken ct = default)
    {
        if (_cache.TryGetValue(ListCacheKey, out _))
            return;

        var addedCount = 0;

        // 1. Liquipedia
        try
        {
            var lpParsed = await _liquipedia.GetTournamentListAsync(ct);
            var lpSelected = lpParsed.Where(t => t.Status == "live")
                .Concat(lpParsed.Where(t => t.Status == "finished").OrderByDescending(t => t.EndDate).Take(10))
                .Concat(lpParsed.Where(t => t.Status == "planned").OrderBy(t => t.StartDate).Take(25))
                .GroupBy(t => t.PageName)
                .Select(g => g.First())
                .ToList();

            foreach (var t in lpSelected)
                addedCount += await SaveTournamentAsync("liquipedia", t.PageName, t.Name, "counterstrike", t.PrizePool, t.Participants, t.StartDate?.ToString("yyyy-MM-dd"), t.Status, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Liquipedia sync failed");
        }

        // 2. Pandascore
        try
        {
            if (_pandascore.Enabled)
            {
                var psLive = await _pandascore.GetRunningTournamentsAsync(10, null, ct);
                var psUpcoming = await _pandascore.GetUpcomingTournamentsAsync(25, null, ct);
                var psSelected = psLive.Concat(psUpcoming).GroupBy(t => t.Id).Select(g => g.First()).ToList();

                foreach (var t in psSelected)
                    addedCount += await SaveTournamentAsync("pandascore", t.Id, t.Name, t.VideogameName ?? "esports", t.PrizePool ?? 0, 16, t.BeginAt, t.Status ?? "planned", ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Pandascore sync failed");
        }

        // 3. ITournamentProvider (Faceit etc.)
        foreach (var provider in _providers.Where(p => p.ProviderName != "Liquipedia")) // _liquipedia handled separately for prize pool etc
        {
            try
            {
                var providerTournaments = await provider.GetTournamentsAsync(ct);
                foreach (var t in providerTournaments)
                    addedCount += await SaveTournamentAsync(provider.ProviderName.ToLowerInvariant(), t.ExternalId, t.Name, "esports", 0, 16, t.StartDate?.ToString("yyyy-MM-dd"), t.Status, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "{ProviderName} sync failed", provider.ProviderName);
            }
        }

        try
        {
            await _db.SaveChangesAsync(ct);
            _cache.Set(ListCacheKey, true, TimeSpan.FromMinutes(5));
            _logger.LogInformation("External sync: added/updated {Count} tournaments from multiple providers", addedCount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "External tournament database save failed");
            _cache.Set(ListCacheKey, true, TimeSpan.FromMinutes(2));
        }
    }

    private async Task<int> SaveTournamentAsync(string provider, string externalId, string name, string game, decimal prize, int participants, string? startDate, string status, CancellationToken ct)
    {
        var mappedStatus = MapTournamentStatus(status);
        var existing = await _db.Tournaments.FirstOrDefaultAsync(x => x.Provider == provider && x.ProviderTournamentId == externalId, ct);
        if (existing == null)
        {
            _db.Tournaments.Add(new Tournament
            {
                Name = name,
                Game = game,
                PrizePool = prize,
                MaxParticipants = participants,
                CurrentParticipants = participants,
                StartDate = startDate ?? string.Empty,
                Status = mappedStatus,
                Format = "single_elimination",
                StageType = "single",
                IsExternal = true,
                Provider = provider,
                ProviderTournamentId = externalId,
                PrizeDistributionJson = DefaultPrizeDistribution()
            });
            return 1;
        }
        else
        {
            existing.Name = name;
            existing.PrizePool = prize > 0 ? prize : existing.PrizePool;
            existing.MaxParticipants = participants > 0 ? participants : existing.MaxParticipants;
            existing.CurrentParticipants = existing.MaxParticipants;
            existing.StartDate = string.IsNullOrWhiteSpace(startDate) ? existing.StartDate : startDate;
            existing.Status = mappedStatus;
            existing.IsExternal = true;
            if (string.IsNullOrWhiteSpace(existing.PrizeDistributionJson))
                existing.PrizeDistributionJson = DefaultPrizeDistribution();
            return 0;
        }
    }

    /// <summary>
    /// Ленивая синхронизация матчей внешнего турнира в БД.
    /// Возвращает true, если в БД есть матчи (после синка или уже были).
    /// </summary>
    public async Task<bool> SyncMatchesAsync(Tournament tournament, CancellationToken ct = default)
    {
        if (!tournament.IsExternal || string.IsNullOrWhiteSpace(tournament.ProviderTournamentId))
        {
            return await _db.Matches.AnyAsync(m => m.TournamentId == tournament.Id, ct);
        }

        var markerKey = $"external:matches:synced:{tournament.Id}";
        if (_cache.TryGetValue(markerKey, out _))
            return await _db.Matches.AnyAsync(m => m.TournamentId == tournament.Id, ct);

        try
        {
            List<LpMatch> parsed = new();

            if (tournament.Provider == "liquipedia")
            {
                parsed = await _liquipedia.GetMatchesAsync(tournament.ProviderTournamentId, ct);
            }
            else if (tournament.Provider == "pandascore" && _pandascore.Enabled)
            {
                var psMatches = await _pandascore.GetMatchesForTournamentAsync(tournament.ProviderTournamentId, 50, tournament.Game, ct);
                foreach (var m in psMatches)
                {
                    var mappedStatus = MapMatchStatus(m.Status);
                    parsed.Add(new LpMatch(
                        Round: m.Name ?? "Match",
                        RoundNumber: GuessRoundNumber(m.Name),
                        TeamA: m.OpponentA,
                        TeamB: m.OpponentB,
                        ScoreA: m.ScoreA,
                        ScoreB: m.ScoreB,
                        Status: mappedStatus,
                        WinnerName: m.ScoreA > m.ScoreB ? m.OpponentA : (m.ScoreB > m.ScoreA ? m.OpponentB : null)
                    ));
                }
            }
            else
            {
                return await _db.Matches.AnyAsync(m => m.TournamentId == tournament.Id, ct);
            }

            if (parsed.Count == 0)
            {
                _cache.Set(markerKey, true, TimeSpan.FromMinutes(10));
                return await _db.Matches.AnyAsync(m => m.TournamentId == tournament.Id, ct);
            }

            var teamsByName = await ResolveExternalTeamsAsync(parsed, tournament.Game, ct);

            var oldMatches = await _db.Matches.Where(m => m.TournamentId == tournament.Id).ToListAsync(ct);
            var oldMatchesByRoundAndTeams = oldMatches
                .GroupBy(m => new { m.Round, m.RoundNumber })
                .ToDictionary(g => g.Key, g => g.ToList());

            var winningTeamIds = new HashSet<int>();

            foreach (var m in parsed)
            {
                var teamA = m.TeamA != null && teamsByName.TryGetValue(m.TeamA, out var ta) ? ta : null;
                var teamB = m.TeamB != null && teamsByName.TryGetValue(m.TeamB, out var tb) ? tb : null;
                var winner = m.WinnerName != null && teamsByName.TryGetValue(m.WinnerName, out var w) ? w : null;

                var matchKey = new { m.Round, m.RoundNumber };
                Match? existingMatch = null;
                bool newlyFinished = false;

                if (oldMatchesByRoundAndTeams.TryGetValue(matchKey, out var list) && list.Count > 0)
                {
                    existingMatch = list.FirstOrDefault(x => x.TeamAId == teamA?.Id && x.TeamBId == teamB?.Id) 
                                 ?? list.First();
                    list.Remove(existingMatch);
                }

                var mappedStatus = MapMatchStatus(m.Status);
                if (existingMatch != null)
                {
                    existingMatch.TeamA = teamA;
                    existingMatch.TeamB = teamB;
                    existingMatch.ScoreA = m.ScoreA;
                    existingMatch.ScoreB = m.ScoreB;
                    existingMatch.Winner = winner;
                    
                    if (existingMatch.Status != "finished" && mappedStatus == "finished" && winner != null)
                        newlyFinished = true;

                    if (existingMatch.Status != "finished")
                        existingMatch.Status = mappedStatus;
                }
                else
                {
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
                        Status = mappedStatus
                    });
                    
                    if (mappedStatus == "finished" && winner != null)
                        newlyFinished = true;
                }

                if (newlyFinished && winner != null)
                {
                    winningTeamIds.Add(winner.Id);
                }
            }

            // Remove any old matches that are no longer in the parsed list
            var matchesToRemove = oldMatchesByRoundAndTeams.Values.SelectMany(x => x).ToList();
            if (matchesToRemove.Any())
            {
                _db.Matches.RemoveRange(matchesToRemove);
            }
            
            // Process Fantasy Points
            if (winningTeamIds.Any())
            {
                var winnerPlayerIds = await _db.TeamPlayers
                    .Where(p => winningTeamIds.Contains(p.TeamId))
                    .Select(p => p.Id)
                    .ToListAsync(ct);

                if (winnerPlayerIds.Any())
                {
                    var winningRosters = await _db.FantasyRosters
                        .Include(r => r.FantasyTeam)
                        .Where(r => winnerPlayerIds.Contains(r.ProPlayerId))
                        .ToListAsync(ct);

                    foreach (var roster in winningRosters)
                    {
                        if (roster.FantasyTeam != null && roster.FantasyTeam.TournamentId == tournament.Id)
                        {
                            roster.FantasyTeam.TotalPoints += 10;
                        }
                    }
                }
            }

            // Bracket linkage heuristic for external tournaments
            var tournamentMatches = _db.Matches.Local.Where(m => m.TournamentId == tournament.Id).ToList();
            if (tournamentMatches.Count == 0)
                tournamentMatches = await _db.Matches.Where(m => m.TournamentId == tournament.Id).ToListAsync(ct);
            
            // Dynamic tournament status propagation based on match progress
            if (tournamentMatches.Count > 0)
            {
                if (tournamentMatches.All(m => m.Status == "finished"))
                {
                    tournament.Status = "finished";
                }
                else if (tournamentMatches.Any(m => m.Status == "live" || m.Status == "finished"))
                {
                    tournament.Status = "live";
                }
            }

            LinkExternalBracket(tournamentMatches);

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

    private void LinkExternalBracket(List<Match> matches)
    {
        var finals = matches.Where(m => m.Round != null && m.Round.Contains("Final", StringComparison.OrdinalIgnoreCase) && !m.Round.Contains("Semi", StringComparison.OrdinalIgnoreCase) && !m.Round.Contains("Quarter", StringComparison.OrdinalIgnoreCase)).ToList();
        var semis = matches.Where(m => m.Round != null && m.Round.Contains("Semi", StringComparison.OrdinalIgnoreCase)).ToList();
        var quarters = matches.Where(m => m.Round != null && m.Round.Contains("Quarter", StringComparison.OrdinalIgnoreCase)).ToList();

        if (finals.Count == 1 && semis.Count == 2)
        {
            semis[0].NextMatchId = finals[0].Id;
            semis[0].NextMatch = finals[0];
            semis[1].NextMatchId = finals[0].Id;
            semis[1].NextMatch = finals[0];

            if (quarters.Count == 4)
            {
                quarters[0].NextMatchId = semis[0].Id; quarters[0].NextMatch = semis[0];
                quarters[1].NextMatchId = semis[0].Id; quarters[1].NextMatch = semis[0];
                quarters[2].NextMatchId = semis[1].Id; quarters[2].NextMatch = semis[1];
                quarters[3].NextMatchId = semis[1].Id; quarters[3].NextMatch = semis[1];
            }
        }
    }

    /// <summary>
    /// Команды внешних турниров живут в Teams с флагом IsExternal = true —
    /// локальные команды не затрагиваются и не смешиваются с ними.
    /// </summary>
    private async Task<Dictionary<string, Team>> ResolveExternalTeamsAsync(List<LpMatch> matches, string game, CancellationToken ct)
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
            
            // Auto-generate 5 dummy pro players for Fantasy Esports Draft
            var rng = new Random();
            for (int i = 1; i <= 5; i++)
            {
                team.Players.Add(new TeamPlayer
                {
                    Nickname = $"{name} Player {i}",
                    Game = game,
                    Cost = rng.Next(8, 13) * 10, // Random cost 80, 90, 100, 110, 120
                    Rating = Math.Round(0.85m + (decimal)rng.NextDouble() * 0.50m, 2),
                    RatingStatus = "confirmed",
                    RatingSource = "liquipedia"
                });
            }
            
            _db.Teams.Add(team);
            map[name] = team;
        }

        return map;
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

    public static string MapMatchStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return "planned";
        var lower = status.ToLowerInvariant().Trim();
        if (lower == "running" || lower == "live" || lower == "ongoing")
            return "live";
        if (lower == "finished" || lower == "completed" || lower == "won" || lower == "approved")
            return "finished";
        if (lower == "canceled" || lower == "cancelled" || lower == "postponed")
            return "finished";
        return "planned";
    }

    public static string MapTournamentStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return "planned";
        var lower = status.ToLowerInvariant().Trim();
        if (lower == "running" || lower == "live" || lower == "ongoing")
            return "live";
        if (lower == "finished" || lower == "completed")
            return "finished";
        return "planned";
    }
}
