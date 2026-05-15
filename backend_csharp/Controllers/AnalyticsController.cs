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
        var matches = await _db.Matches.ToListAsync();
        var payouts = await _db.PrizePayouts.Include(p => p.Team).Include(p => p.Tournament).ToListAsync();

        var playerStats = teams
            .SelectMany(t => t.Players.Select(p =>
            {
                var played = matches.Count(m => m.TeamAId == t.Id || m.TeamBId == t.Id);
                var wins = matches.Count(m => m.WinnerId == t.Id);
                return new
                {
                    player = p.Nickname,
                    team = t.Name,
                    rating = p.Rating,
                    ratingStatus = p.RatingStatus,
                    played,
                    wins,
                    winRate = played == 0 ? 0 : Math.Round((decimal)wins * 100m / played, 2)
                };
            }))
            .OrderByDescending(p => p.rating ?? 0m)
            .ThenByDescending(p => p.winRate)
            .Take(15)
            .ToList();

        var disciplinePopularity = tournaments
            .GroupBy(t => string.IsNullOrWhiteSpace(t.Game) ? "Не указано" : t.Game)
            .Select(g => new { discipline = g.Key, value = g.Count(), prizePool = g.Sum(t => t.PrizePool) })
            .OrderByDescending(x => x.value)
            .ToList();

        var prizePools = tournaments
            .Select(t => new { tournament = t.Name, prizePool = t.PrizePool, status = t.Status, stage = t.CurrentStage })
            .OrderByDescending(x => x.prizePool)
            .Take(10)
            .ToList();

        var liveMatches = matches.Count(m => m.Status == "live");
        var finishedMatches = matches.Count(m => m.Status == "finished");
        var linkedStreams = matches.Count(m => !string.IsNullOrWhiteSpace(m.StreamUrl));

        return Ok(new
        {
            playerStats,
            disciplinePopularity,
            prizePools,
            payouts = payouts.Select(p => new
            {
                tournament = p.Tournament?.Name,
                place = p.PlaceTitle,
                team = p.Team?.Name ?? "TBD",
                p.Amount,
                p.Status
            }),
            summary = new
            {
                totalTeams = teams.Count,
                totalPlayers = teams.SelectMany(t => t.Players).Count(),
                confirmedRatings = teams.SelectMany(t => t.Players).Count(p => p.RatingStatus == "confirmed"),
                totalTournaments = tournaments.Count,
                liveMatches,
                finishedMatches,
                linkedStreams
            }
        });
    }

    [HttpGet("export/csv")]
    public async Task<IActionResult> ExportCsv()
    {
        var teams = await _db.Teams.Include(t => t.Players).ToListAsync();
        var matches = await _db.Matches.ToListAsync();
        var sb = new StringBuilder();
        sb.AppendLine("team,player,rating,rating_status,played,wins,win_rate");
        foreach (var team in teams)
        {
            var played = matches.Count(m => m.TeamAId == team.Id || m.TeamBId == team.Id);
            var wins = matches.Count(m => m.WinnerId == team.Id);
            var winRate = played == 0 ? 0 : Math.Round((decimal)wins * 100m / played, 2);
            foreach (var player in team.Players)
            {
                sb.AppendLine($"\"{Escape(team.Name)}\",\"{Escape(player.Nickname)}\",\"{player.Rating}\",\"{player.RatingStatus}\",\"{played}\",\"{wins}\",\"{winRate}\"");
            }
        }

        return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", "analytics.csv");
    }

    private static string Escape(string? value) => (value ?? string.Empty).Replace("\"", "\"\"");
}
