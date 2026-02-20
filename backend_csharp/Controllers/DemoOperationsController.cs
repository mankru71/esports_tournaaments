using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;
using System.Text;

namespace Controllers;

[ApiController]
[Route("api")]
public class DemoOperationsController : ControllerBase
{
    public class MatchDto
    {
        public int Id { get; set; }
        public int TournamentId { get; set; }
        public string TeamA { get; set; } = string.Empty;
        public string TeamB { get; set; } = string.Empty;
        public int ScoreA { get; set; }
        public int ScoreB { get; set; }
        public string Status { get; set; } = string.Empty;
        public string Round { get; set; } = string.Empty;
        public string GroupName { get; set; } = string.Empty;
        public string StreamUrl { get; set; } = string.Empty;
    }

    private static readonly List<MatchDto> Matches = new()
    {
        new() { Id = 1, TournamentId = 1, TeamA = "NaVi", TeamB = "G2", ScoreA = 1, ScoreB = 0, Status = "live", Round = "R1", GroupName = "A", StreamUrl = "https://twitch.tv/demo" },
        new() { Id = 2, TournamentId = 1, TeamA = "Spirit", TeamB = "Faze", ScoreA = 0, ScoreB = 0, Status = "scheduled", Round = "R1", GroupName = "A", StreamUrl = "https://youtube.com/demo" }
    };

    public class MatchResultRequest
    {
        [Range(0, 99)] public int ScoreA { get; set; }
        [Range(0, 99)] public int ScoreB { get; set; }
    }

    public class PrizePayoutRequest
    {
        [Required] public List<PayoutItem> Payouts { get; set; } = new();
    }

    public class PayoutItem
    {
        [Required] public string Team { get; set; } = string.Empty;
        [Range(0, 100)] public int Percent { get; set; }
    }

    public class MvpVoteRequest
    {
        [Required] public int TournamentId { get; set; }
        [Required] public int PlayerId { get; set; }
    }

    [HttpGet("teams")]
    public IActionResult Teams() => Ok(new[] { new { id = 1, name = "NaVi" }, new { id = 2, name = "G2" } });
    [HttpGet("players")]
    public IActionResult Players() => Ok(new[] { new { id = 10, nickname = "s1mple", teamId = 1 }, new { id = 11, nickname = "m0NESY", teamId = 2 } });
    [HttpGet("ratings/mock")]
    [HttpGet("demo/seed")]
    public IActionResult RatingsMock() => Ok(new[] { new { playerId = 10, rating = 1.32 }, new { playerId = 11, rating = 1.28 } });
    [HttpPost("registrations/{id}/approve")]
    public IActionResult ApproveRegistration(int id) => Ok(new { registrationId = id, status = "approved" });
    [HttpPost("stages/generate/single")]
    public IActionResult GenerateSingle() => Ok(new { stageType = "single", generated = true });
    [HttpPost("stages/generate/groups")]
    public IActionResult GenerateGroups() => Ok(new { stageType = "groups", generated = true });

    [HttpGet("matches")]
    public IActionResult GetMatches([FromQuery] int tournamentId) => Ok(Matches.Where(m => m.TournamentId == tournamentId));

    [HttpPut("matches/{id}/status")]
    public IActionResult SetMatchStatus(int id, [FromBody] string status) => Ok(new { id, status });

    [HttpPut("matches/{id}/result")]
    public IActionResult SetMatchResult(int id, [FromBody] MatchResultRequest request) => Ok(new { id, scoreA = request.ScoreA, scoreB = request.ScoreB, updated = true });

    [HttpPost("mvp/open")]
    public IActionResult OpenMvp([FromBody] bool isOpen) => Ok(new { isOpen });

    [HttpPost("mvp/vote")]
    public IActionResult VoteMvp([FromBody] MvpVoteRequest request) => Ok(new { success = true, request.PlayerId, request.TournamentId });

    [HttpGet("mvp/results")]
    public IActionResult MvpResults([FromQuery] int tournamentId) => Ok(new { isOpen = true, candidates = new[] { new { id = 10, name = "s1mple" }, new { id = 11, name = "m0NESY" } }, results = new[] { new { id = 10, name = "s1mple", votes = 120 }, new { id = 11, name = "m0NESY", votes = 85 } }, tournamentId });

    [HttpGet("prize-pool/{tournamentId}")]
    public IActionResult PrizePool(int tournamentId) => Ok(new { tournamentId, totalAmount = 1000000 });

    [HttpPost("prize-pool/{tournamentId}/payouts")]
    public IActionResult SetPayouts(int tournamentId, [FromBody] PrizePayoutRequest request)
    {
        var totalPercent = request.Payouts.Sum(x => x.Percent);
        if (totalPercent > 100)
        {
            ModelState.AddModelError("payouts", "Сумма процентов не может превышать 100");
            return ValidationProblem(ModelState);
        }
        return Ok(new { tournamentId, payouts = request.Payouts, totalPercent });
    }

    [HttpGet("streams/status")]
    public IActionResult StreamsStatus() => Ok(new[] { new { provider = "Twitch", url = "https://twitch.tv/demo", status = new { online = true, viewers = 1000 } }, new { provider = "YouTube", url = "https://youtube.com/demo", status = new { online = false, viewers = 0 } } });

    [HttpGet("analytics")]
    public IActionResult Analytics() => Ok(new { playerStats = new[] { new { player = "s1mple", kills = 87 }, new { player = "m0NESY", kills = 80 } }, disciplinePopularity = new[] { new { discipline = "CS2", value = 70 }, new { discipline = "Dota 2", value = 30 } } });

    [HttpGet("analytics/export/csv")]
    public IActionResult AnalyticsCsv()
    {
        var csv = "player,kills\ns1mple,87\nm0NESY,80\n";
        return File(Encoding.UTF8.GetBytes(csv), "text/csv", "analytics.csv");
    }
}
