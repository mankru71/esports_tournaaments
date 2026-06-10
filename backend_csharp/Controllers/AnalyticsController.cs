using Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Services;
using System.Text;

namespace Controllers;

[ApiController]
[Route("api/analytics")]
public class AnalyticsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly AnalyticsService _analytics;

    public AnalyticsController(AppDbContext db, AnalyticsService analytics)
    {
        _db = db;
        _analytics = analytics;
    }

    /// <summary>
    /// Винрейты команд: общий, групповая стадия vs плей-офф и «упорные» матчи
    /// (разница в счёте ≤ 2). Только локальные турниры и завершённые матчи.
    /// </summary>
    [HttpGet("team-winrates")]
    public async Task<IActionResult> TeamWinRates(CancellationToken ct)
    {
        var winRates = await _analytics.GetTeamWinRatesAsync(ct);

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
    public async Task<IActionResult> Get()
    {
        // Глобальная аналитика — ТОЛЬКО локальные данные: внешние (Liquipedia)
        // турниры и команды не смешиваются с локальными
        var teams = await _db.Teams.Include(t => t.Players).Where(t => !t.IsExternal).ToListAsync();
        var tournaments = await _db.Tournaments.Where(t => !t.IsExternal).ToListAsync();

        var playerStats = teams
            .SelectMany(t => t.Players.Select(p => new
            {
                player = p.Nickname,
                team = t.Name,
                rating = p.Rating,
                ratingStatus = p.RatingStatus
            }))
            .OrderByDescending(p => p.rating ?? 0m)
            .Take(10)
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

        return Ok(new
        {
            playerStats,
            disciplinePopularity,
            prizePools = payouts,
            summary = new
            {
                totalTeams = teams.Count,
                totalPlayers = teams.SelectMany(t => t.Players).Count(),
                confirmedRatings = teams.SelectMany(t => t.Players).Count(p => p.RatingStatus == "confirmed")
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
            .Where(p => p.Rating != null && (p.Team == null || !p.Team.IsExternal))
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
        var players = await _db.Teams.Include(t => t.Players).Where(t => !t.IsExternal).ToListAsync();
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
