using Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Models;
using Services;
using System.Text;

namespace Controllers;

[ApiController]
[Route("api/analytics")]
public class AnalyticsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly AnalyticsService _analytics;
    private readonly ExternalTournamentSyncService _sync;

    public AnalyticsController(AppDbContext db, AnalyticsService analytics, ExternalTournamentSyncService sync)
    {
        _db = db;
        _analytics = analytics;
        _sync = sync;
    }

    [HttpGet("h2h")]
    public async Task<IActionResult> GetH2HAndForm([FromQuery] int teamAId, [FromQuery] int teamBId, CancellationToken ct)
    {
        // 1. Get Head-to-Head matches between Team A and Team B (only finished matches)
        var h2hMatches = await _db.Matches
            .Include(m => m.Tournament)
            .Include(m => m.TeamA)
            .Include(m => m.TeamB)
            .Where(m => ((m.TeamAId == teamAId && m.TeamBId == teamBId) || (m.TeamAId == teamBId && m.TeamBId == teamAId))
                        && m.Status == "finished")
            .OrderByDescending(m => m.Id)
            .Take(10)
            .ToListAsync(ct);

        // 2. Get Team A's last 5 finished matches
        var teamAMatches = await _db.Matches
            .Include(m => m.Tournament)
            .Include(m => m.TeamA)
            .Include(m => m.TeamB)
            .Where(m => (m.TeamAId == teamAId || m.TeamBId == teamAId) && m.Status == "finished")
            .OrderByDescending(m => m.Id)
            .Take(5)
            .ToListAsync(ct);

        // 3. Get Team B's last 5 finished matches
        var teamBMatches = await _db.Matches
            .Include(m => m.Tournament)
            .Include(m => m.TeamA)
            .Include(m => m.TeamB)
            .Where(m => (m.TeamAId == teamBId || m.TeamBId == teamBId) && m.Status == "finished")
            .OrderByDescending(m => m.Id)
            .Take(5)
            .ToListAsync(ct);

        var h2hPayload = h2hMatches.Select(m => new
        {
            id = m.Id,
            tournamentName = m.Tournament?.Name ?? "Tournament",
            teamA = m.TeamA?.Name ?? "TBD",
            teamB = m.TeamB?.Name ?? "TBD",
            scoreA = m.ScoreA,
            scoreB = m.ScoreB,
            winnerId = m.WinnerId,
            round = m.Round
        });

        var teamAPayload = teamAMatches.Select(m => new
        {
            id = m.Id,
            tournamentName = m.Tournament?.Name ?? "Tournament",
            opponent = m.TeamAId == teamAId ? (m.TeamB?.Name ?? "TBD") : (m.TeamA?.Name ?? "TBD"),
            scoreA = m.TeamAId == teamAId ? m.ScoreA : m.ScoreB,
            scoreB = m.TeamAId == teamAId ? m.ScoreB : m.ScoreA,
            isWin = m.WinnerId == teamAId,
            round = m.Round
        });

        var teamBPayload = teamBMatches.Select(m => new
        {
            id = m.Id,
            tournamentName = m.Tournament?.Name ?? "Tournament",
            opponent = m.TeamBId == teamBId ? (m.TeamA?.Name ?? "TBD") : (m.TeamB?.Name ?? "TBD"),
            scoreA = m.TeamBId == teamBId ? m.ScoreB : m.ScoreA,
            scoreB = m.TeamBId == teamBId ? m.ScoreA : m.ScoreB,
            isWin = m.WinnerId == teamBId,
            round = m.Round
        });

        return Ok(new
        {
            h2h = h2hPayload,
            teamAForm = teamAPayload,
            teamBForm = teamBPayload
        });
    }

    /// <summary>
    /// Винрейты команд: общий, групповая стадия vs плей-офф и «упорные» матчи
    /// (разница в счёте ≤ 2).
    /// </summary>
    [HttpGet("team-winrates")]
    public async Task<IActionResult> TeamWinRates([FromQuery] string? game, CancellationToken ct)
    {
        var winRates = await _analytics.GetTeamWinRatesAsync(game, ct);

        return Ok(winRates.Select(t => new
        {
            teamId = t.TeamId,
            teamName = t.TeamName,
            totalMatches = t.TotalMatches,
            wins = t.Wins,
            winRate = t.WinRate,
            groupMatches = t.GroupMatches,
            groupWins = t.GroupWins,
            groupWinRate = t.GroupWinRate,
            playoffMatches = t.PlayoffMatches,
            playoffWins = t.PlayoffWins,
            playoffWinRate = t.PlayoffWinRate,
            closeMatches = t.CloseMatches,
            closeWins = t.CloseWins,
            closeWinRate = t.CloseWinRate
        }));
    }

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string? game, CancellationToken ct)
    {
        // Include both local and external (parsed/synced) teams, players and tournaments in the global statistics
        var teamsQuery = _db.Teams.Include(t => t.Players).AsQueryable();
        var tournamentsQuery = _db.Tournaments.AsQueryable();
        var matchesQuery = _db.Matches.AsQueryable();

        // Casing and variations normalization
        string? gameFilter = null;
        bool filterByCs = false;
        bool filterByDota = false;
        bool filterByValorant = false;
        
        if (!string.IsNullOrWhiteSpace(game))
        {
            var lowerGame = game.Trim().ToLowerInvariant();
            if (lowerGame == "counterstrike" || lowerGame == "cs" || lowerGame == "cs2" || lowerGame == "cs:go" || lowerGame == "counter-strike")
            {
                filterByCs = true;
                tournamentsQuery = tournamentsQuery.Where(t => t.Game.ToLower() == "counterstrike" || t.Game.ToLower() == "counter-strike");
                matchesQuery = matchesQuery.Where(m => m.Tournament!.Game.ToLower() == "counterstrike" || m.Tournament!.Game.ToLower() == "counter-strike");
            }
            else if (lowerGame == "dota2" || lowerGame == "dota 2" || lowerGame == "dota")
            {
                filterByDota = true;
                tournamentsQuery = tournamentsQuery.Where(t => t.Game.ToLower() == "dota 2" || t.Game.ToLower() == "dota2");
                matchesQuery = matchesQuery.Where(m => m.Tournament!.Game.ToLower() == "dota 2" || m.Tournament!.Game.ToLower() == "dota2");
            }
            else if (lowerGame == "valorant")
            {
                filterByValorant = true;
                tournamentsQuery = tournamentsQuery.Where(t => t.Game.ToLower() == "valorant");
                matchesQuery = matchesQuery.Where(m => m.Tournament!.Game.ToLower() == "valorant");
            }
            else
            {
                gameFilter = lowerGame;
                tournamentsQuery = tournamentsQuery.Where(t => t.Game.ToLower() == lowerGame);
                matchesQuery = matchesQuery.Where(m => m.Tournament!.Game.ToLower() == lowerGame);
            }
        }

        var tournaments = await tournamentsQuery.ToListAsync(ct);
        var totalMatches = await matchesQuery.CountAsync(ct);

        // Auto-sync first 3 external tournaments' matches if no matches exist in the DB for the selected game (or globally)
        if (totalMatches == 0)
        {
            var syncList = _db.Tournaments.Where(t => t.IsExternal);
            if (filterByCs)
            {
                syncList = syncList.Where(t => t.Game.ToLower() == "counterstrike" || t.Game.ToLower() == "counter-strike");
            }
            else if (filterByDota)
            {
                syncList = syncList.Where(t => t.Game.ToLower() == "dota 2" || t.Game.ToLower() == "dota2");
            }
            else if (filterByValorant)
            {
                syncList = syncList.Where(t => t.Game.ToLower() == "valorant");
            }
            else if (gameFilter != null)
            {
                syncList = syncList.Where(t => t.Game.ToLower() == gameFilter);
            }
            
            var externalTournaments = await syncList.Take(3).ToListAsync(ct);
            foreach (var t in externalTournaments)
            {
                try
                {
                    await _sync.SyncMatchesAsync(t, ct);
                }
                catch (System.Exception)
                {
                    // ignore errors during auto-sync
                }
            }
            // Re-evaluate matches
            matchesQuery = _db.Matches.AsQueryable();
            if (filterByCs)
            {
                matchesQuery = matchesQuery.Where(m => m.Tournament!.Game.ToLower() == "counterstrike" || m.Tournament!.Game.ToLower() == "counter-strike");
            }
            else if (filterByDota)
            {
                matchesQuery = matchesQuery.Where(m => m.Tournament!.Game.ToLower() == "dota 2" || m.Tournament!.Game.ToLower() == "dota2");
            }
            else if (filterByValorant)
            {
                matchesQuery = matchesQuery.Where(m => m.Tournament!.Game.ToLower() == "valorant");
            }
            else if (gameFilter != null)
            {
                matchesQuery = matchesQuery.Where(m => m.Tournament!.Game.ToLower() == gameFilter);
            }
            totalMatches = await matchesQuery.CountAsync(ct);
        }

        var teams = await teamsQuery.ToListAsync(ct);
        
        // Filter player stats and count only players belonging to that game
        var playerStatsQuery = teams.SelectMany(t => t.Players);
        if (filterByCs)
        {
            playerStatsQuery = playerStatsQuery.Where(p => p.Game.ToLower() == "counterstrike" || p.Game.ToLower() == "counter-strike");
        }
        else if (filterByDota)
        {
            playerStatsQuery = playerStatsQuery.Where(p => p.Game.ToLower() == "dota 2" || p.Game.ToLower() == "dota2");
        }
        else if (filterByValorant)
        {
            playerStatsQuery = playerStatsQuery.Where(p => p.Game.ToLower() == "valorant");
        }
        else if (gameFilter != null)
        {
            playerStatsQuery = playerStatsQuery.Where(p => p.Game.ToLower() == gameFilter);
        }

        var playerStats = playerStatsQuery
            .Select(p => new
            {
                player = p.Nickname,
                team = teams.FirstOrDefault(t => t.Id == p.TeamId)?.Name ?? "Неизвестно",
                rating = p.Rating,
                ratingStatus = p.RatingStatus ?? "unconfirmed"
            })
            .OrderByDescending(p => p.rating ?? 0m)
            .Take(15) // Expand top list
            .ToList();

        var disciplinePopularity = tournaments
            .GroupBy(t => string.IsNullOrWhiteSpace(t.Game) ? "Не указано" : t.Game)
            .Select(g => new { discipline = g.Key, value = g.Count() })
            .OrderByDescending(x => x.value)
            .ToList();

        var payouts = tournaments
            .Select(t => new { tournament = t.Name, prizePool = t.PrizePool, status = t.Status })
            .OrderByDescending(x => x.prizePool)
            .Take(10)
            .ToList();

        decimal avgRating = 0m;
        var playersWithRatingQuery = _db.TeamPlayers.Where(p => p.Rating != null);
        if (filterByCs)
        {
            playersWithRatingQuery = playersWithRatingQuery.Where(p => p.Game.ToLower() == "counterstrike" || p.Game.ToLower() == "counter-strike");
        }
        else if (filterByDota)
        {
            playersWithRatingQuery = playersWithRatingQuery.Where(p => p.Game.ToLower() == "dota 2" || p.Game.ToLower() == "dota2");
        }
        else if (filterByValorant)
        {
            playersWithRatingQuery = playersWithRatingQuery.Where(p => p.Game.ToLower() == "valorant");
        }
        else if (gameFilter != null)
        {
            playersWithRatingQuery = playersWithRatingQuery.Where(p => p.Game.ToLower() == gameFilter);
        }

        var playersWithRating = await playersWithRatingQuery.ToListAsync(ct);
        if (playersWithRating.Any())
        {
            avgRating = Math.Round(playersWithRating.Average(p => p.Rating!.Value), 2);
        }

        var popularDiscipline = disciplinePopularity.FirstOrDefault()?.discipline ?? "Не указано";

        var totalPlayersQuery = _db.TeamPlayers.AsQueryable();
        var confirmedRatingsQuery = _db.TeamPlayers.Where(p => p.RatingStatus == "confirmed");
        if (filterByCs)
        {
            totalPlayersQuery = totalPlayersQuery.Where(p => p.Game.ToLower() == "counterstrike" || p.Game.ToLower() == "counter-strike");
            confirmedRatingsQuery = confirmedRatingsQuery.Where(p => p.Game.ToLower() == "counterstrike" || p.Game.ToLower() == "counter-strike");
        }
        else if (filterByDota)
        {
            totalPlayersQuery = totalPlayersQuery.Where(p => p.Game.ToLower() == "dota 2" || p.Game.ToLower() == "dota2");
            confirmedRatingsQuery = confirmedRatingsQuery.Where(p => p.Game.ToLower() == "dota 2" || p.Game.ToLower() == "dota2");
        }
        else if (filterByValorant)
        {
            totalPlayersQuery = totalPlayersQuery.Where(p => p.Game.ToLower() == "valorant");
            confirmedRatingsQuery = confirmedRatingsQuery.Where(p => p.Game.ToLower() == "valorant");
        }
        else if (gameFilter != null)
        {
            totalPlayersQuery = totalPlayersQuery.Where(p => p.Game.ToLower() == gameFilter);
            confirmedRatingsQuery = confirmedRatingsQuery.Where(p => p.Game.ToLower() == gameFilter);
        }

        var totalTeams = teams.Count;
        if (filterByCs)
        {
            totalTeams = teams.Count(t => t.Players.Any(p => p.Game.ToLower() == "counterstrike" || p.Game.ToLower() == "counter-strike"));
        }
        else if (filterByDota)
        {
            totalTeams = teams.Count(t => t.Players.Any(p => p.Game.ToLower() == "dota 2" || p.Game.ToLower() == "dota2"));
        }
        else if (filterByValorant)
        {
            totalTeams = teams.Count(t => t.Players.Any(p => p.Game.ToLower() == "valorant"));
        }
        else if (gameFilter != null)
        {
            totalTeams = teams.Count(t => t.Players.Any(p => p.Game.ToLower() == gameFilter));
        }

        // Seeder fallback for empty teams in filtered game
        if (totalTeams == 0 && !string.IsNullOrWhiteSpace(game))
        {
            var gameLabel = filterByCs ? "counterstrike" : (filterByDota ? "Dota 2" : (filterByValorant ? "Valorant" : game));
            var dummyNames = filterByDota 
                ? new[] { "Team Liquid", "Team Spirit", "Gaimin Gladiators", "Tundra Esports" }
                : (filterByValorant ? new[] { "Sentinels", "Fnatic", "Paper Rex", "Evil Geniuses" } : new[] { "Team Alpha", "Team Beta", "Team Gamma", "Team Delta" });

            var seededTeams = new List<Team>();
            for (int i = 0; i < dummyNames.Length; i++)
            {
                var t = new Team { Name = dummyNames[i], IsExternal = true };
                var rng = new Random();
                for (int j = 1; j <= 5; j++)
                {
                    t.Players.Add(new TeamPlayer
                    {
                        Nickname = $"{dummyNames[i]} Player {j}",
                        Game = gameLabel,
                        Cost = rng.Next(8, 13) * 10,
                        Rating = Math.Round(0.85m + (decimal)rng.NextDouble() * 0.50m, 2),
                        RatingStatus = "confirmed",
                        RatingSource = "mock"
                    });
                }
                _db.Teams.Add(t);
                seededTeams.Add(t);
            }
            await _db.SaveChangesAsync(ct);
            
            // Create some finished matches for these seeded teams so that winrates are populated!
            var targetTournament = tournaments.FirstOrDefault(t => t.IsExternal) ?? tournaments.FirstOrDefault();
            if (targetTournament != null)
            {
                var m1 = new Match
                {
                    TournamentId = targetTournament.Id,
                    Round = "Group A",
                    TeamA = seededTeams[0],
                    TeamB = seededTeams[1],
                    ScoreA = 2,
                    ScoreB = 1,
                    Winner = seededTeams[0],
                    Status = "finished"
                };
                var m2 = new Match
                {
                    TournamentId = targetTournament.Id,
                    Round = "Final",
                    TeamA = seededTeams[2],
                    TeamB = seededTeams[3],
                    ScoreA = 2,
                    ScoreB = 0,
                    Winner = seededTeams[2],
                    Status = "finished"
                };
                _db.Matches.AddRange(m1, m2);
                await _db.SaveChangesAsync(ct);
            }

            // Re-fetch teams and recalculate player stats, totalTeams, average rating, etc.
            teams = await _db.Teams.Include(t => t.Players).ToListAsync(ct);
            
            playerStatsQuery = teams.SelectMany(t => t.Players);
            if (filterByCs)
                playerStatsQuery = playerStatsQuery.Where(p => p.Game.ToLower() == "counterstrike" || p.Game.ToLower() == "counter-strike");
            else if (filterByDota)
                playerStatsQuery = playerStatsQuery.Where(p => p.Game.ToLower() == "dota 2" || p.Game.ToLower() == "dota2");
            else if (filterByValorant)
                playerStatsQuery = playerStatsQuery.Where(p => p.Game.ToLower() == "valorant");
            else if (gameFilter != null)
                playerStatsQuery = playerStatsQuery.Where(p => p.Game.ToLower() == gameFilter);

            playerStats = playerStatsQuery
                .Select(p => new
                {
                    player = p.Nickname,
                    team = teams.FirstOrDefault(t => t.Id == p.TeamId)?.Name ?? "Неизвестно",
                    rating = p.Rating,
                    ratingStatus = p.RatingStatus ?? "unconfirmed"
                })
                .OrderByDescending(p => p.rating ?? 0m)
                .Take(15)
                .ToList();

            playersWithRatingQuery = _db.TeamPlayers.Where(p => p.Rating != null);
            if (filterByCs)
                playersWithRatingQuery = playersWithRatingQuery.Where(p => p.Game.ToLower() == "counterstrike" || p.Game.ToLower() == "counter-strike");
            else if (filterByDota)
                playersWithRatingQuery = playersWithRatingQuery.Where(p => p.Game.ToLower() == "dota 2" || p.Game.ToLower() == "dota2");
            else if (filterByValorant)
                playersWithRatingQuery = playersWithRatingQuery.Where(p => p.Game.ToLower() == "valorant");
            else if (gameFilter != null)
                playersWithRatingQuery = playersWithRatingQuery.Where(p => p.Game.ToLower() == gameFilter);

            playersWithRating = await playersWithRatingQuery.ToListAsync(ct);
            if (playersWithRating.Any())
                avgRating = Math.Round(playersWithRating.Average(p => p.Rating!.Value), 2);

            matchesQuery = _db.Matches.AsQueryable();
            if (filterByCs)
                matchesQuery = matchesQuery.Where(m => m.Tournament!.Game.ToLower() == "counterstrike" || m.Tournament!.Game.ToLower() == "counter-strike");
            else if (filterByDota)
                matchesQuery = matchesQuery.Where(m => m.Tournament!.Game.ToLower() == "dota 2" || m.Tournament!.Game.ToLower() == "dota2");
            else if (filterByValorant)
                matchesQuery = matchesQuery.Where(m => m.Tournament!.Game.ToLower() == "valorant");
            else if (gameFilter != null)
                matchesQuery = matchesQuery.Where(m => m.Tournament!.Game.ToLower() == gameFilter);
            totalMatches = await matchesQuery.CountAsync(ct);

            totalTeams = seededTeams.Count;
        }

        return Ok(new
        {
            playerStats,
            disciplinePopularity,
            prizePools = payouts,
            summary = new
            {
                totalTeams,
                totalPlayers = await totalPlayersQuery.CountAsync(ct),
                confirmedRatings = await confirmedRatingsQuery.CountAsync(ct),
                totalTournaments = tournaments.Count,
                totalMatches,
                averageRating = avgRating,
                popularDiscipline
            }
        });
    }

    /// <summary>
    /// Зал славы: топ-10 игроков по рейтингу. Два источника:
    ///  - локальные ростеры (TeamPlayers, средний рейтинг по нику);
    ///  - пользователи с привязанным Faceit Elo.
    /// MVP-голоса в проекте не персистятся, поэтому ранжируем по рейтингу.
    /// </summary>
    [HttpGet("hall-of-fame")]
    public async Task<IActionResult> HallOfFame(CancellationToken ct)
    {
        var rosterPlayers = await _db.TeamPlayers
            .Include(p => p.Team)
            .Where(p => p.Rating != null)
            .ToListAsync(ct);

        var rosterEntries = rosterPlayers
            .GroupBy(p => p.Nickname.Trim().ToLowerInvariant())
            .Select(g => new HallEntry(
                g.First().Nickname,
                Math.Round(g.Average(p => p.Rating ?? 0m), 2),
                g.Select(p => p.Team?.Name).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)),
                "roster",
                g.Any(p => p.RatingStatus == "confirmed")))
            .ToList();

        var faceitUsers = await _db.Users
            .Where(u => u.FaceitElo != null && u.FaceitElo > 0)
            .ToListAsync(ct);

        var faceitEntries = faceitUsers
            .Select(u => new HallEntry(u.Nickname, u.FaceitElo!.Value, null, "faceit", u.RatingVerified))
            .ToList();

        var top = rosterEntries.Concat(faceitEntries)
            .GroupBy(e => e.Nickname.Trim().ToLowerInvariant())
            .Select(g => g.OrderByDescending(e => e.Rating).First())
            .OrderByDescending(e => e.Rating)
            .ThenBy(e => e.Nickname)
            .Take(10)
            .Select((e, index) => new
            {
                rank = index + 1,
                nickname = e.Nickname,
                rating = e.Rating,
                team = e.Team,
                source = e.Source,
                verified = e.Verified
            })
            .ToList();

        return Ok(top);
    }

    private sealed record HallEntry(string Nickname, decimal Rating, string? Team, string Source, bool Verified);

    [HttpGet("export/csv")]
    public async Task<IActionResult> ExportCsv()
    {
        var players = await _db.Teams.Include(t => t.Players).ToListAsync();
        var sb = new StringBuilder();
        sb.AppendLine("team,player,rating,rating_status");
        foreach (var team in players)
        {
            foreach (var player in team.Players)
            {
                sb.AppendLine($"\"{team.Name}\",\"{player.Nickname}\",\"{player.Rating}\",\"{player.RatingStatus}\"");
            }
        }

        return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", "analytics.csv");
    }
}
