using Data;
using Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

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
        public string? VoterSession { get; set; }
    }

    public class MvpOpenRequest
    {
        [Required] public int TournamentId { get; set; }
        public bool IsOpen { get; set; }
    }

    [HttpPost("vote")]
    public async Task<IActionResult> Vote([FromBody] MvpVoteRequest request)
    {
        var tournament = await _db.Tournaments.FirstOrDefaultAsync(t => t.Id == request.TournamentId);
        if (tournament == null)
            return NotFound(new { message = "Турнир не найден" });

        if (!tournament.MvpVotingOpen)
            return BadRequest(new { message = "Голосование за MVP ещё не открыто" });

        var player = await _db.TeamPlayers
            .Include(p => p.Team)
            .FirstOrDefaultAsync(p => p.Id == request.PlayerId);
        if (player == null)
            return NotFound(new { message = "Игрок не найден" });

        var participates = await _db.TournamentApplications
            .AnyAsync(a => a.TournamentId == request.TournamentId && a.TeamId == player.TeamId && a.Status == "approved");
        if (!participates)
            return BadRequest(new { message = "Этот игрок не участвует в турнире" });

        var userId = AuthTokenHelper.GetUserId(Request);
        var session = string.IsNullOrWhiteSpace(request.VoterSession)
            ? Request.Headers["X-Session-Id"].FirstOrDefault() ?? string.Empty
            : request.VoterSession.Trim();

        var alreadyVoted = userId.HasValue
            ? await _db.MvpVotes.AnyAsync(v => v.TournamentId == request.TournamentId && v.UserId == userId.Value)
            : !string.IsNullOrWhiteSpace(session) && await _db.MvpVotes.AnyAsync(v => v.TournamentId == request.TournamentId && v.VoterSession == session);

        if (alreadyVoted)
            return Conflict(new { message = "Вы уже голосовали за MVP этого турнира" });

        _db.MvpVotes.Add(new Models.MvpVote
        {
            TournamentId = request.TournamentId,
            PlayerId = request.PlayerId,
            UserId = userId,
            VoterSession = session,
            VoterIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
            CreatedAtUtc = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        return Ok(new { success = true, message = "Голос за MVP принят", request.PlayerId, request.TournamentId });
    }

    [HttpGet("results")]
    public async Task<IActionResult> Results([FromQuery] int tournamentId)
    {
        var tournament = await _db.Tournaments.FirstOrDefaultAsync(t => t.Id == tournamentId);
        if (tournament == null)
            return NotFound(new { message = "Турнир не найден" });

        var apps = await _db.TournamentApplications
            .Include(a => a.Team)
                .ThenInclude(t => t!.Players)
            .Where(a => a.TournamentId == tournamentId && a.Status == "approved")
            .ToListAsync();

        var candidates = apps
            .Where(a => a.Team != null)
            .SelectMany(a => a.Team!.Players.Select(p => new
            {
                id = p.Id,
                name = p.Nickname,
                playerName = p.Nickname,
                team = a.Team!.Name,
                rating = p.Rating,
                ratingStatus = p.RatingStatus
            }))
            .OrderByDescending(p => p.rating ?? 0m)
            .ThenBy(p => p.name)
            .ToList();

        var votes = await _db.MvpVotes
            .Where(v => v.TournamentId == tournamentId)
            .GroupBy(v => v.PlayerId)
            .Select(g => new { playerId = g.Key, votes = g.Count() })
            .ToListAsync();

        var results = candidates
            .GroupJoin(votes, c => c.id, v => v.playerId, (c, vs) => new
            {
                id = c.id,
                name = c.name,
                playerName = c.playerName,
                team = c.team,
                rating = c.rating,
                votes = vs.FirstOrDefault()?.votes ?? 0
            })
            .OrderByDescending(x => x.votes)
            .ThenByDescending(x => x.rating ?? 0m)
            .ToList();

        return Ok(new
        {
            isOpen = tournament.MvpVotingOpen,
            candidates,
            results,
            tournamentId,
            totalVotes = votes.Sum(v => v.votes)
        });
    }

    [HttpPost("open")]
    public async Task<IActionResult> Open([FromBody] MvpOpenRequest request)
    {
        if (!AuthTokenHelper.IsInAnyRole(Request, "admin", "judge"))
            return StatusCode(403, new { message = "Открывать MVP-голосование может только администратор или судья" });

        var tournament = await _db.Tournaments.FirstOrDefaultAsync(t => t.Id == request.TournamentId);
        if (tournament == null)
            return NotFound(new { message = "Турнир не найден" });

        tournament.MvpVotingOpen = request.IsOpen;
        await _db.SaveChangesAsync();
        return Ok(new { tournamentId = tournament.Id, isOpen = tournament.MvpVotingOpen });
    }
}
