using Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;
using Services;
using Hubs;

namespace Controllers;

[ApiController]
[Route("api/webhooks/gsi")]
public class GsiWebhookController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;
    private readonly IHubContext<MatchesHub> _hub;
    private readonly ActivityLogService _activity;
    private readonly MatchPredictionService _predictions;

    public GsiWebhookController(
        AppDbContext db, 
        IConfiguration config, 
        IHubContext<MatchesHub> hub, 
        ActivityLogService activity,
        MatchPredictionService predictions)
    {
        _db = db;
        _config = config;
        _hub = hub;
        _activity = activity;
        _predictions = predictions;
    }

    [HttpPost("match-result")]
    public async Task<IActionResult> HandleMatchResult([FromBody] GsiMatchResultPayload payload)
    {
        // 1. Validate Secret Key
        var expectedKey = _config["GSI_API_KEY"] ?? "dev_secret_key";
        if (!Request.Headers.TryGetValue("X-Api-Key", out var providedKey) || providedKey != expectedKey)
        {
            return Unauthorized(new { message = "Invalid API Key" });
        }

        // 2. Find Match
        var match = await _db.Matches
            .Include(m => m.NextMatch)
            .Include(m => m.TeamA)
            .Include(m => m.TeamB)
            .FirstOrDefaultAsync(m => m.Id == payload.MatchId);

        if (match == null) return NotFound(new { message = "Match not found" });

        // 3. Update Match
        match.ScoreA = payload.Team1Score;
        match.ScoreB = payload.Team2Score;

        if (payload.WinnerId.HasValue || match.ScoreA >= 16 || match.ScoreB >= 16)
        {
            match.Status = "finished";
            match.WinnerId = payload.WinnerId ?? (match.ScoreA > match.ScoreB ? match.TeamAId : match.TeamBId);

            if (match.NextMatch != null && match.WinnerId.HasValue)
            {
                if (match.NextMatch.TeamAId == null)
                    match.NextMatch.TeamAId = match.WinnerId;
                else if (match.NextMatch.TeamBId == null && match.NextMatch.TeamAId != match.WinnerId)
                    match.NextMatch.TeamBId = match.WinnerId;
            }

            await _activity.LogAsync("auto_match_finished", $"[Auto-Server] Матч #{match.Id} завершён со счётом {match.ScoreA}:{match.ScoreB}");
        }
        else
        {
            match.Status = "live";
        }

        await _db.SaveChangesAsync();
        _predictions.Invalidate(match);

        // 4. Notify via SignalR (Overall match state)
        var notifyPayload = new
        {
            id = match.Id,
            tournamentId = match.TournamentId,
            scoreA = match.ScoreA,
            scoreB = match.ScoreB,
            status = match.Status,
            updated = true
        };
        await _hub.Clients.Group($"tournament:{match.TournamentId}").SendAsync("matchUpdated", notifyPayload);

        // 5. Notify Live Round Details
        if (payload.RoundNumber.HasValue)
        {
            var roundPayload = new
            {
                matchId = match.Id,
                roundNumber = payload.RoundNumber,
                bombStatus = payload.BombStatus,
                team1Alive = payload.Team1Alive,
                team2Alive = payload.Team2Alive
            };
            await _hub.Clients.Group($"tournament:{match.TournamentId}").SendAsync("liveRoundUpdate", roundPayload);
        }

        return Ok(new { message = "Success", status = match.Status });
    }
}

public class GsiMatchResultPayload
{
    public int MatchId { get; set; }
    public int Team1Score { get; set; }
    public int Team2Score { get; set; }
    public int? WinnerId { get; set; }
    
    // Live round details
    public int? RoundNumber { get; set; }
    public string? BombStatus { get; set; } // "planted", "defused", "exploded", null
    public int? Team1Alive { get; set; }
    public int? Team2Alive { get; set; }
}
