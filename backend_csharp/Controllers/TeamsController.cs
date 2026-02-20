using Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Models;
using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json;

namespace Controllers;

[ApiController]
[Route("api/teams")]
public class TeamsController : ControllerBase
{
    private readonly AppDbContext _db;

    public TeamsController(AppDbContext db)
    {
        _db = db;
    }

    public class CreateTeamRequest
    {
        [Required, MinLength(2)]
        public string Name { get; set; } = string.Empty;
    }

    public class AddPlayerRequest
    {
        [Required, MinLength(2)]
        public string Nickname { get; set; } = string.Empty;
    }

    [HttpGet]
    public async Task<IActionResult> List()
    {
        var teams = await _db.Teams
            .Include(t => t.CaptainUser)
            .Include(t => t.Players)
            .OrderByDescending(t => t.Id)
            .ToListAsync();

        return Ok(teams.Select(t => new
        {
            id = t.Id,
            name = t.Name,
            captainEmail = t.CaptainUser?.Email ?? "unknown",
            players = t.Players.Select(p => new { id = p.Id, nickname = p.Nickname })
        }));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateTeamRequest request)
    {
        var userId = GetUserIdFromBearerToken();
        if (userId is null) return Unauthorized(new { message = "Требуется вход" });

        var team = new Team
        {
            Name = request.Name,
            CaptainUserId = userId.Value,
        };

        _db.Teams.Add(team);
        await _db.SaveChangesAsync();

        return Ok(new { id = team.Id, name = team.Name, captainUserId = team.CaptainUserId });
    }

    [HttpPost("{teamId:int}/players")]
    public async Task<IActionResult> AddPlayer(int teamId, [FromBody] AddPlayerRequest request)
    {
        var userId = GetUserIdFromBearerToken();
        if (userId is null) return Unauthorized(new { message = "Требуется вход" });

        var team = await _db.Teams.FirstOrDefaultAsync(t => t.Id == teamId);
        if (team is null) return NotFound(new { message = "Команда не найдена" });

        if (team.CaptainUserId != userId.Value)
        {
            return StatusCode(403, new { message = "Недостаточно прав" });
        }

        var player = new TeamPlayer
        {
            TeamId = teamId,
            Nickname = request.Nickname,
        };

        _db.TeamPlayers.Add(player);
        await _db.SaveChangesAsync();

        return Ok(new { id = player.Id, teamId = player.TeamId, nickname = player.Nickname });
    }

    private int? GetUserIdFromBearerToken()
    {
        var auth = Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(auth) || !auth.StartsWith("Bearer ")) return null;

        var token = auth.Replace("Bearer ", "");
        var parts = token.Split('.');
        if (parts.Length < 2) return null;
        var padded = parts[1] + new string('=', (4 - parts[1].Length % 4) % 4);
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(padded.Replace('-', '+').Replace('_', '/')));
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("sub", out var userId)) return null;
        return userId.GetInt32();
    }
}
