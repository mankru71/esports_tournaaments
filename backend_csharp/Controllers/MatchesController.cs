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
        [Required, Url] public string StreamUrl { get; set; } = string.Empty;
        public string? StreamStatus { get; set; }
    }

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] int tournamentId, CancellationToken ct)
    {
        var tournament = await _db.Tournaments.FindAsync(new object[] { tournamentId }, ct);

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
                streamUrl = m.StreamUrl ?? string.Empty,
                streamProvider = DetectProvider(m.StreamUrl),
                streamStatus = string.IsNullOrWhiteSpace(m.StreamUrl) ? "offline" : "linked"
            });
            return Ok(payload);
        }

        var matchesFromDb = await _db.Matches
            .Include(m => m.TeamA)
            .Include(m => m.TeamB)
            .Where(m => m.TournamentId == tournamentId)
            .OrderBy(m => m.RoundNumber)
            .ThenBy(m => m.Id)
            .ToListAsync(ct);

        return Ok(matchesFromDb.Select(ToDto));
    }

    [HttpPut("{id:int}/result")]
    public async Task<IActionResult> SetMatchResult(int id, [FromBody] MatchResultRequest request, CancellationToken ct)
    {
        if (!Infrastructure.AuthTokenHelper.IsInAnyRole(Request, "admin", "judge"))
            return StatusCode(403, new { message = "Только администратор или судья может изменять результаты" });

        var match = await _db.Matches
            .Include(m => m.NextMatch)
            .Include(m => m.TeamA)
            .Include(m => m.TeamB)
            .Include(m => m.Tournament)
            .FirstOrDefaultAsync(m => m.Id == id, ct);

        if (match == null)
            return NotFound(new { message = "Матч не найден" });

        if (match.Tournament?.Status == "paused")
            return BadRequest(new { message = "Турнир приостановлен. Внесение результатов заблокировано." });

        var previousStatus = match.Status;
        match.ScoreA = request.ScoreA;
        match.ScoreB = request.ScoreB;
        match.Status = "live";

        if (match.ScoreA == match.ScoreB && match.NextMatchId.HasValue)
            return BadRequest(new { message = "Для матчей плей-офф ничья недопустима. Укажите победителя." });

        // Учебное правило: при счёте 16+ матч считается завершённым.
        if (match.ScoreA >= 16 || match.ScoreB >= 16)
        {
            match.Status = "finished";
            match.WinnerId = match.ScoreA > match.ScoreB ? match.TeamAId : match.TeamBId;
            if (match.Tournament != null && match.Tournament.Status != "finished")
                match.Tournament.Status = "live";

            if (match.NextMatch != null && match.WinnerId.HasValue)
            {
                if (match.NextMatch.TeamAId == null)
                    match.NextMatch.TeamAId = match.WinnerId;
                else if (match.NextMatch.TeamBId == null && match.NextMatch.TeamAId != match.WinnerId)
                    match.NextMatch.TeamBId = match.WinnerId;
            }

            if (string.Equals(match.Round, "Final", StringComparison.OrdinalIgnoreCase) && match.Tournament != null)
            {
                match.Tournament.Status = "finished";
                match.Tournament.CurrentStage = "finished";
                match.Tournament.MvpVotingOpen = true;
            }
        }
        else if (match.Tournament != null)
        {
            match.Tournament.Status = "live";
        }

        await _db.SaveChangesAsync(ct);

        var payload = new
        {
            id = match.Id,
            tournamentId = match.TournamentId,
            scoreA = match.ScoreA,
            scoreB = match.ScoreB,
            status = match.Status,
            updated = true
        };

        await _hub.Clients.Group($"tournament:{match.TournamentId}").SendAsync("matchUpdated", payload, ct);

        if (!string.Equals(previousStatus, "live", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(match.Status, "live", StringComparison.OrdinalIgnoreCase))
        {
            await _discord.NotifyMatchLiveAsync(match, ct);
        }

        return Ok(payload);
    }

    [HttpPut("{id:int}/stream")]
    public async Task<IActionResult> SetMatchStream(int id, [FromBody] MatchStreamRequest request, CancellationToken ct)
    {
        if (!Infrastructure.AuthTokenHelper.IsInAnyRole(Request, "admin", "judge"))
            return StatusCode(403, new { message = "Только администратор или судья может привязывать стримы" });

        var match = await _db.Matches
            .Include(m => m.TeamA)
            .Include(m => m.TeamB)
            .FirstOrDefaultAsync(m => m.Id == id, ct);
        if (match == null)
            return NotFound(new { message = "Матч не найден" });

        var streamUrl = request.StreamUrl.Trim();
        match.StreamUrl = streamUrl;
        match.StreamProvider = DetectProvider(streamUrl);
        match.StreamStatus = string.IsNullOrWhiteSpace(request.StreamStatus) ? "linked" : request.StreamStatus.Trim().ToLowerInvariant();
        await _db.SaveChangesAsync(ct);

        return Ok(ToDto(match));
    }

    private static object ToDto(Match m) => new
    {
        id = m.Id,
        tournamentId = m.TournamentId,
        teamA = m.TeamA?.Name ?? "TBD",
        teamB = m.TeamB?.Name ?? "TBD",
        scoreA = m.ScoreA,
        scoreB = m.ScoreB,
        status = m.Status,
        round = m.Round,
        groupName = m.GroupName,
        streamUrl = m.StreamUrl ?? string.Empty,
        streamProvider = m.StreamProvider ?? DetectProvider(m.StreamUrl),
        streamStatus = m.StreamStatus
    };

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

    private static string DetectProvider(string? url)
    {
        var u = (url ?? string.Empty).ToLowerInvariant();
        if (u.Contains("twitch.tv")) return "Twitch";
        if (u.Contains("youtube.com") || u.Contains("youtu.be")) return "YouTube";
        return string.IsNullOrWhiteSpace(u) ? "" : "Stream";
    }
}
