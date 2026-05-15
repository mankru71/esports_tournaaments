using Data;
using Hubs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Models;
using Services;
using System.ComponentModel.DataAnnotations;

namespace Controllers;

[ApiController]
[Route("api/matches")]
public class MatchesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly PandaScoreService _pandascore;
    private readonly IHubContext<MatchesHub> _hub;
    private readonly DiscordWebhookService _discord;

    public MatchesController(AppDbContext db, PandaScoreService pandascore, IHubContext<MatchesHub> hub, DiscordWebhookService discord)
    {
        _db = db;
        _pandascore = pandascore;
        _hub = hub;
        _discord = discord;
    }

    public class MatchResultRequest
    {
        [Range(0, 99)] public int ScoreA { get; set; }
        [Range(0, 99)] public int ScoreB { get; set; }
    }

    public class MatchStreamRequest
    {
        [Required] public string StreamUrl { get; set; } = string.Empty;
    }

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] int tournamentId, CancellationToken ct)
    {
        var tournament = await _db.Tournaments.FindAsync(new object[] { tournamentId }, ct);
        
        // Внешние турниры
        if (tournament != null && tournament.IsExternal && !string.IsNullOrWhiteSpace(tournament.ProviderTournamentId) && _pandascore.Enabled)
        {
            var matches = await _pandascore.GetMatchesForTournamentAsync(tournament.ProviderTournamentId!, 50, tournament.Game, ct);
            var payload = matches.Select(m => new
            {
                id = m.Id,
                tournamentId,
                teamA = m.OpponentA ?? "TBD",
                teamB = m.OpponentB ?? "TBD",
                scoreA = m.ScoreA,
                scoreB = m.ScoreB,
                status = NormalizeMatchStatus(m.Status),
                round = string.IsNullOrWhiteSpace(m.Name) ? "Match" : m.Name!,
                groupName = string.Empty,
                streamUrl = m.StreamUrl ?? string.Empty
            });
            return Ok(payload);
        }

        // Локальные турниры
        var matchesFromDb = await _db.Matches
            .Include(m => m.TeamA)
            .Include(m => m.TeamB)
            .Where(m => m.TournamentId == tournamentId)
            .OrderBy(m => m.RoundNumber)
            .ToListAsync(ct);

        if (matchesFromDb.Any())
        {
            var payload = matchesFromDb.Select(m => new
            {
                id = m.Id,
                tournamentId,
                teamA = m.TeamA?.Name ?? "TBD",
                teamB = m.TeamB?.Name ?? "TBD",
                scoreA = m.ScoreA,
                scoreB = m.ScoreB,
                status = m.Status,
                round = m.Round,
                groupName = string.Empty,
                streamUrl = m.StreamUrl
            });
            return Ok(payload);
        }

        return Ok(new List<object>());
    }

    [HttpPut("{id:int}/result")]
    public async Task<IActionResult> SetMatchResult(int id, [FromBody] MatchResultRequest request)
    {
        if (!Infrastructure.AuthTokenHelper.IsInAnyRole(Request, "admin", "judge"))
            return StatusCode(403, new { message = "Только администратор или судья может изменять результаты" });

        var match = await _db.Matches
            .Include(m => m.NextMatch)
            .Include(m => m.TeamA)
            .Include(m => m.TeamB)
            .Include(m => m.Tournament)
            .FirstOrDefaultAsync(m => m.Id == id);

        if (match == null)
            return NotFound(new { message = "Матч не найден" });

        match.ScoreA = request.ScoreA;
        match.ScoreB = request.ScoreB;
        match.Status = "live";

        if (match.ScoreA == match.ScoreB && match.NextMatchId.HasValue)
            return BadRequest(new { message = "Для матчей плей-офф ничья недопустима. Укажите победителя." });

        if (match.ScoreA >= 16 || match.ScoreB >= 16)
        {
            match.Status = "finished";
            match.WinnerId = match.ScoreA > match.ScoreB ? match.TeamAId : match.TeamBId;

            if (match.NextMatch != null && match.WinnerId.HasValue)
            {
                if (match.NextMatch.TeamAId == null)
                {
                    match.NextMatch.TeamAId = match.WinnerId;
                }
                else if (match.NextMatch.TeamBId == null && match.NextMatch.TeamAId != match.WinnerId)
                {
                    match.NextMatch.TeamBId = match.WinnerId;
                }
            }
        }

        await _db.SaveChangesAsync();

        var payload = new
        {
            id = match.Id,
            tournamentId = match.TournamentId,
            scoreA = match.ScoreA,
            scoreB = match.ScoreB,
            status = match.Status,
            updated = true
        };

        await _hub.Clients.Group($"tournament:{match.TournamentId}").SendAsync("matchUpdated", payload);
        if (match.Status == "live")
            await _discord.NotifyMatchLiveAsync(match);

        return Ok(payload);
    }


    [HttpPut("{id:int}/stream")]
    public async Task<IActionResult> AttachStream(int id, [FromBody] MatchStreamRequest request)
    {
        if (!Infrastructure.AuthTokenHelper.IsInAnyRole(Request, "admin", "judge"))
            return StatusCode(403, new { message = "Только администратор или судья может привязывать трансляции" });

        var match = await _db.Matches.FirstOrDefaultAsync(m => m.Id == id);
        if (match == null)
            return NotFound(new { message = "Матч не найден" });

        var url = request.StreamUrl.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https"))
            return BadRequest(new { message = "Укажите корректную ссылку на трансляцию" });

        match.StreamUrl = url;
        await _db.SaveChangesAsync();

        return Ok(new { match.Id, match.TournamentId, match.StreamUrl });
    }

    private static string NormalizeMatchStatus(string? status)
    {
        var s = (status ?? string.Empty).Trim().ToLowerInvariant();
        return s switch
        {
            "running" => "live",
            "finished" => "finished",
            "canceled" => "finished",
            "postponed" => "planned",
            "not_started" => "planned",
            _ => "planned",
        };
    }
}