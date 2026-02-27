using Data;
using Microsoft.AspNetCore.Mvc;
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
    private readonly ILogger<MatchesController> _logger;

    // Demo matches for "local" tournaments.
    private static readonly List<DemoMatch> DemoMatches = new()
    {
        new() { Id = 1, TournamentId = 1, TeamA = "NaVi", TeamB = "G2", ScoreA = 1, ScoreB = 0, Status = "live", Round = "R1", GroupName = "A", StreamUrl = "https://twitch.tv/demo" },
        new() { Id = 2, TournamentId = 1, TeamA = "Spirit", TeamB = "Faze", ScoreA = 0, ScoreB = 0, Status = "planned", Round = "R1", GroupName = "A", StreamUrl = "https://youtube.com/demo" }
    };

    public MatchesController(AppDbContext db, PandaScoreService pandascore, ILogger<MatchesController> logger)
    {
        _db = db;
        _pandascore = pandascore;
        _logger = logger;
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
        var t = await _db.Tournaments.FindAsync(new object[] { tournamentId }, ct);
        if (t != null && t.IsExternal && !string.IsNullOrWhiteSpace(t.ProviderTournamentId) && _pandascore.Enabled)
        {
            var matches = await _pandascore.GetMatchesForTournamentAsync(t.ProviderTournamentId!, 50, ct);
            var payload = matches.Select(m => new
            {
                id = m.Id,
                tournamentId = tournamentId,
                teamA = m.OpponentA ?? "TBD",
                teamB = m.OpponentB ?? "TBD",
                scoreA = m.ScoreA,
                scoreB = m.ScoreB,
                status = NormalizeMatchStatus(m.Status),
                round = string.IsNullOrWhiteSpace(m.Name) ? "Match" : m.Name!,
                groupName = "",
                streamUrl = m.StreamUrl ?? ""
            });
            return Ok(payload);
        }

        // Fallback to demo matches (local tournaments).
        return Ok(DemoMatches.Where(m => m.TournamentId == tournamentId));
    }

    [HttpPut("{id}/result")]
    public IActionResult SetMatchResult(int id, [FromBody] MatchResultRequest request)
    {
        var match = DemoMatches.FirstOrDefault(m => m.Id == id);
        if (match == null)
        {
            return NotFound(new { message = "Match not found (only demo matches are editable)" });
        }

        match.ScoreA = request.ScoreA;
        match.ScoreB = request.ScoreB;
        match.Status = "finished";

        return Ok(new { id = match.Id, scoreA = match.ScoreA, scoreB = match.ScoreB, updated = true });
    }

    private static string NormalizeMatchStatus(string? status)
    {
        var s = (status ?? "").Trim().ToLowerInvariant();
        // PandaScore uses "running", "finished", "not_started" etc. We map to our UI labels.
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
