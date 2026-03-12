using Data;
using Hubs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
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
    private readonly TournamentPlanningService _planning;
    private readonly IHubContext<MatchesHub> _hub;

    private static readonly List<DemoMatch> DemoMatches = new()
    {
        new() { Id = 1, TournamentId = 1, TeamA = "NaVi", TeamB = "G2", ScoreA = 1, ScoreB = 0, Status = "live", Round = "R1", GroupName = "A", StreamUrl = "https://twitch.tv/esl_csgo" },
        new() { Id = 2, TournamentId = 1, TeamA = "Spirit", TeamB = "Faze", ScoreA = 0, ScoreB = 0, Status = "planned", Round = "R1", GroupName = "A", StreamUrl = "https://www.youtube.com/watch?v=jfKfPfyJRdk" },
        new() { Id = 3, TournamentId = 2, TeamA = "Team Alpha", TeamB = "Team Beta", ScoreA = 0, ScoreB = 0, Status = "planned", Round = "Группа A", GroupName = "A", StreamUrl = "" }
    };

    public MatchesController(AppDbContext db, PandaScoreService pandascore, TournamentPlanningService planning, IHubContext<MatchesHub> hub)
    {
        _db = db;
        _pandascore = pandascore;
        _planning = planning;
        _hub = hub;
    }

    public class DemoMatch
    {
        public int Id { get; set; }
        public int TournamentId { get; set; }
        public string TeamA { get; set; } = string.Empty;
        public string TeamB { get; set; } = string.Empty;
        public int ScoreA { get; set; }
        public int ScoreB { get; set; }
        public string Status { get; set; } = "planned";
        public string Round { get; set; } = "R1";
        public string GroupName { get; set; } = string.Empty;
        public string StreamUrl { get; set; } = string.Empty;
    }

    public class MatchResultRequest
    {
        [Range(0, 99)] public int ScoreA { get; set; }
        [Range(0, 99)] public int ScoreB { get; set; }
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
                streamUrl = m.StreamUrl ?? string.Empty
            });
            return Ok(payload);
        }

        if (tournament != null)
        {
            var dynamicPlan = await _planning.BuildPlanAsync(tournament, ct);
            if (dynamicPlan.Matches.Count > 0)
            {
                var payload = dynamicPlan.Matches.Select((m, index) => new
                {
                    id = $"local-{tournamentId}-{index + 1}",
                    tournamentId,
                    teamA = m.TeamA,
                    teamB = m.TeamB,
                    scoreA = m.ScoreA,
                    scoreB = m.ScoreB,
                    status = m.Status,
                    round = m.Round,
                    groupName = dynamicPlan.StageType == "groups" ? m.Round : string.Empty,
                    streamUrl = string.Empty
                });
                return Ok(payload);
            }
        }

        return Ok(DemoMatches.Where(m => m.TournamentId == tournamentId));
    }

    [HttpPut("{id}/result")]
    public async Task<IActionResult> SetMatchResult(string id, [FromBody] MatchResultRequest request)
    {
        var match = DemoMatches.FirstOrDefault(m => string.Equals(m.Id.ToString(), id, StringComparison.OrdinalIgnoreCase));
        if (match == null)
            return NotFound(new { message = "Match not found (editable only for local demo matches)" });

        match.ScoreA = request.ScoreA;
        match.ScoreB = request.ScoreB;
        match.Status = "finished";

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
        return Ok(payload);
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
