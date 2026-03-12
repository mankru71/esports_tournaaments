using Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text;

namespace Controllers;

[ApiController]
[Route("api/analytics")]
public class AnalyticsController : ControllerBase
{
    private readonly AppDbContext _db;

    public AnalyticsController(AppDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var teams = await _db.Teams.Include(t => t.Players).ToListAsync();
        var tournaments = await _db.Tournaments.ToListAsync();

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
