using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Models;
using Infrastructure;
using System.Collections.Generic;

namespace Controllers;

[ApiController]
[Route("api/fantasy")]
public class FantasyController : ControllerBase
{
    private readonly AppDbContext _db;

    public FantasyController(AppDbContext db)
    {
        _db = db;
    }

    [HttpGet("{tournamentId:int}/players")]
    public async Task<IActionResult> GetPlayers(int tournamentId, CancellationToken ct)
    {
        // Get all teams participating in this tournament via matches
        // For external tournaments, we know matches have TeamA/TeamB.
        var teamIds = await _db.Matches
            .Where(m => m.TournamentId == tournamentId)
            .SelectMany(m => new[] { m.TeamAId, m.TeamBId })
            .Where(id => id != null)
            .Select(id => id!.Value)
            .Distinct()
            .ToListAsync(ct);

        var players = await _db.TeamPlayers
            .Include(p => p.Team)
            .Where(p => teamIds.Contains(p.TeamId))
            .Select(p => new
            {
                id = p.Id,
                nickname = p.Nickname,
                teamName = p.Team!.Name,
                cost = p.Cost
            })
            .ToListAsync(ct);

        return Ok(players);
    }

    [HttpPost("draft")]
    public async Task<IActionResult> DraftTeam([FromBody] DraftRequest req, CancellationToken ct)
    {
        var currentUserId = User.GetUserId();
        if (currentUserId == null) return Unauthorized(new { message = "Требуется вход" });

        if (req.PlayerIds == null || req.PlayerIds.Count != 5)
            return BadRequest(new { message = "Вам необходимо выбрать ровно 5 игроков." });

        if (string.IsNullOrWhiteSpace(req.TeamName))
            return BadRequest(new { message = "Укажите название вашей фэнтези-команды." });

        var tournament = await _db.Tournaments.FindAsync(new object[] { req.TournamentId }, ct);
        if (tournament == null)
            return NotFound(new { message = "Турнир не найден" });

        // Verify cost
        var players = await _db.TeamPlayers
            .Where(p => req.PlayerIds.Contains(p.Id))
            .ToListAsync(ct);

        if (players.Count != 5)
            return BadRequest(new { message = "Один или несколько игроков не найдены." });

        int totalCost = players.Sum(p => p.Cost);
        if (totalCost > 500)
            return BadRequest(new { message = "Превышен бюджет в 500!" });

        // Check if user already drafted
        var existing = await _db.FantasyTeams
            .FirstOrDefaultAsync(ft => ft.UserId == currentUserId.Value && ft.TournamentId == req.TournamentId, ct);
        
        if (existing != null)
        {
            // Update existing roster
            existing.TeamName = req.TeamName;
            existing.BudgetRemaining = 500 - totalCost;
            
            var oldRoster = await _db.FantasyRosters.Where(r => r.FantasyTeamId == existing.Id).ToListAsync(ct);
            _db.FantasyRosters.RemoveRange(oldRoster);

            foreach (var pid in req.PlayerIds)
            {
                _db.FantasyRosters.Add(new FantasyRoster { FantasyTeamId = existing.Id, ProPlayerId = pid });
            }
        }
        else
        {
            // Create new
            var ft = new FantasyTeam
            {
                UserId = currentUserId.Value,
                TournamentId = req.TournamentId,
                TeamName = req.TeamName,
                BudgetRemaining = 500 - totalCost,
                TotalPoints = 0
            };
            _db.FantasyTeams.Add(ft);
            await _db.SaveChangesAsync(ct); // To get the Id

            foreach (var pid in req.PlayerIds)
            {
                _db.FantasyRosters.Add(new FantasyRoster { FantasyTeamId = ft.Id, ProPlayerId = pid });
            }
        }

        await _db.SaveChangesAsync(ct);
        return Ok(new { message = "Состав успешно сохранен!" });
    }

    [HttpGet("{tournamentId:int}/leaderboard")]
    public async Task<IActionResult> GetLeaderboard(int tournamentId, CancellationToken ct)
    {
        var teams = await _db.FantasyTeams
            .Include(t => t.User)
            .Where(t => t.TournamentId == tournamentId)
            .OrderByDescending(t => t.TotalPoints)
            .Select(t => new
            {
                id = t.Id,
                teamName = t.TeamName,
                userName = t.User!.Nickname,
                totalPoints = t.TotalPoints
            })
            .Take(100)
            .ToListAsync(ct);

        return Ok(teams);
    }
}

public class DraftRequest
{
    public int TournamentId { get; set; }
    public string TeamName { get; set; } = string.Empty;
    public List<int> PlayerIds { get; set; } = new List<int>();
}
