using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Data;
using Models;
using System.Security.Claims;

namespace Controllers;

[ApiController]
[Route("api/mvp")]
public class MvpController : ControllerBase
{
    private readonly AppDbContext _db;

    public MvpController(AppDbContext db)
    {
        _db = db;
    }

    public class MvpVoteRequest
    {
        [Required] public int TournamentId { get; set; }
        [Required] public int PlayerId { get; set; }
    }

    [HttpPost("vote")]
    public async Task<IActionResult> Vote([FromBody] MvpVoteRequest request, CancellationToken ct)
    {
        var tournament = await _db.Tournaments.FindAsync(request.TournamentId);
        if (tournament == null) return NotFound(new { message = "Турнир не найден" });

        var player = await _db.TeamPlayers.FindAsync(request.PlayerId);
        if (player == null) return NotFound(new { message = "Игрок не найден" });

        var userIdString = User.FindFirstValue(ClaimTypes.NameIdentifier);
        int? userId = int.TryParse(userIdString, out var uid) ? uid : null;

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        bool alreadyVoted = await _db.MvpVotes.AnyAsync(v => 
            v.TournamentId == request.TournamentId && 
            (v.UserId == userId || v.VoterIp == ip), ct);

        if (alreadyVoted)
        {
            return BadRequest(new { message = "Вы уже голосовали в этом турнире" });
        }

        _db.MvpVotes.Add(new MvpVote
        {
            TournamentId = request.TournamentId,
            PlayerId = request.PlayerId,
            UserId = userId,
            VoterIp = ip
        });

        await _db.SaveChangesAsync(ct);

        return Ok(new { success = true });
    }

    [HttpGet("results")]
    public async Task<IActionResult> Results([FromQuery] int tournamentId, CancellationToken ct)
    {
        // 1. Найти команды, участвующие в турнире
        var teamIds = await _db.Matches
            .Where(m => m.TournamentId == tournamentId)
            .SelectMany(m => new[] { m.TeamAId, m.TeamBId })
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToListAsync(ct);

        // 2. Достать всех игроков этих команд
        var candidates = await _db.TeamPlayers
            .Where(p => teamIds.Contains(p.TeamId))
            .Select(p => new { id = p.Id, name = p.Nickname })
            .ToListAsync(ct);

        // 3. Достать результаты голосования
        var votes = await _db.MvpVotes
            .Where(v => v.TournamentId == tournamentId)
            .GroupBy(v => v.PlayerId)
            .Select(g => new { PlayerId = g.Key, Votes = g.Count() })
            .ToListAsync(ct);

        var results = candidates.Select(c => new
        {
            id = c.id,
            name = c.name,
            votes = votes.FirstOrDefault(v => v.PlayerId == c.id)?.Votes ?? 0
        })
        .OrderByDescending(r => r.votes)
        .ThenBy(r => r.name)
        .ToList();

        var isOpen = true; // In a real app, this could be a flag on the Tournament model

        return Ok(new
        {
            isOpen,
            candidates,
            results,
            tournamentId
        });
    }

    [HttpPost("open")]
    public IActionResult Open([FromBody] bool isOpen)
        => Ok(new { isOpen });
}
