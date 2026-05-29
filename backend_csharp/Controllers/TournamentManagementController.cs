using Data;
using Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Services;
using System.Linq;
using System.Text.Json;

namespace Controllers;

[ApiController]
[Route("api/tournament/{tournamentId:int}")]
public class TournamentManagementController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly TournamentPlanningService _planning;

    public TournamentManagementController(AppDbContext db, TournamentPlanningService planning)
    {
        _db = db;
        _planning = planning;
    }

    public class PrizePayoutItem
    {
        public string Place { get; set; } = string.Empty;
        public decimal Percent { get; set; }
    }

    public class PrizePayoutRequest
    {
        public List<PrizePayoutItem> Payouts { get; set; } = new();
    }

    public class PlanningRequest
    {
        public string? Format { get; set; }
        public string? StageType { get; set; }
    }

    public class StatusRequest
    {
        public string Status { get; set; } = "planned";
    }

    [HttpGet("bracket")]
    public async Task<IActionResult> Bracket(int tournamentId, CancellationToken ct)
    {
        var tournament = await _db.Tournaments.FirstOrDefaultAsync(t => t.Id == tournamentId, ct);
        if (tournament == null)
            return NotFound(new { message = "Турнир не найден" });

        var plan = await _planning.BuildPlanAsync(tournament, ct);
        return Ok(new
        {
            stageType = plan.StageType,
            groups = plan.Groups.Select(g => new
            {
                name = g.Name,
                teams = g.Teams.Select(t => new
                {
                    teamId = t.TeamId,
                    name = t.Name,
                    seed = t.Seed,
                    ratingAverage = t.RatingAverage
                }).ToList()
            }).ToList(),
            matches = plan.Matches.Select(m => new
            {
                round = m.Round,
                teamA = m.TeamA,
                teamB = m.TeamB,
                scoreA = m.ScoreA,
                scoreB = m.ScoreB,
                status = m.Status
            }).ToList(),
            summary = plan.Summary
        });
    }
[HttpPost("{id}/generate-bracket")]
public async Task<IActionResult> GenerateBracket(int id)
{
    try
    {
        await _tournamentPlanningService.GenerateSingleEliminationBracketAsync(id);
        return Ok(new { Message = "Сетка сгенерирована из подтвержденных заявок" });
    }
    catch (Exception ex) { return BadRequest(new { Error = ex.Message }); }
}
    [HttpPost("planning")]
    public async Task<IActionResult> SavePlanning(int tournamentId, [FromBody] PlanningRequest request)
    {
        if (!AuthTokenHelper.IsInAnyRole(Request, "admin", "judge"))
            return StatusCode(403, new { message = "Недостаточно прав" });

        var tournament = await _db.Tournaments.FirstOrDefaultAsync(t => t.Id == tournamentId);
        if (tournament == null)
            return NotFound(new { message = "Турнир не найден" });

        if (!string.IsNullOrWhiteSpace(request.Format))
            tournament.Format = request.Format.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(request.StageType))
            tournament.StageType = request.StageType.Trim().ToLowerInvariant();

        await _db.SaveChangesAsync();
        return Ok(new { tournament.Id, tournament.Format, tournament.StageType });
    }


    [HttpPost("status")]
    public async Task<IActionResult> SetStatus(int tournamentId, [FromBody] StatusRequest request)
    {
        if (!AuthTokenHelper.IsInAnyRole(Request, "admin", "judge"))
            return StatusCode(403, new { message = "Недостаточно прав" });

        var tournament = await _db.Tournaments.FirstOrDefaultAsync(t => t.Id == tournamentId);
        if (tournament == null)
            return NotFound(new { message = "Турнир не найден" });

        var status = (request.Status ?? "planned").Trim().ToLowerInvariant();
        var allowed = new HashSet<string> { "planned", "live", "paused", "finished" };
        if (!allowed.Contains(status))
            return BadRequest(new { message = "Некорректный статус турнира" });

        tournament.Status = status;
        await _db.SaveChangesAsync();
        return Ok(new { tournament.Id, tournament.Status });
    }

    [HttpGet("prize-pool")]
    public async Task<IActionResult> PrizePool(int tournamentId)
    {
        var tournament = await _db.Tournaments.FirstOrDefaultAsync(t => t.Id == tournamentId);
        if (tournament == null)
            return NotFound(new { message = "Турнир не найден" });

        return Ok(new
        {
            tournamentId,
            totalAmount = tournament.PrizePool,
            payouts = ParsePrizeDistribution(tournament.PrizeDistributionJson, tournament.PrizePool)
        });
    }

    [HttpPost("prize-pool/payouts")]
    public async Task<IActionResult> SetPayouts(int tournamentId, [FromBody] PrizePayoutRequest request)
    {
        if (!AuthTokenHelper.IsInAnyRole(Request, "admin", "judge"))
            return StatusCode(403, new { message = "Недостаточно прав" });

        var tournament = await _db.Tournaments.FirstOrDefaultAsync(t => t.Id == tournamentId);
        if (tournament == null)
            return NotFound(new { message = "Турнир не найден" });

        if (request.Payouts == null || request.Payouts.Count == 0)
            return BadRequest(new { message = "Укажите хотя бы одно распределение призовых" });

        var totalPercent = request.Payouts.Sum(x => x.Percent);
        if (totalPercent > 100m)
            return BadRequest(new { message = "Сумма процентов не может превышать 100" });

        tournament.PrizeDistributionJson = JsonSerializer.Serialize(request.Payouts.Select(p => new { place = p.Place, percent = p.Percent }));
        await _db.SaveChangesAsync();

        return Ok(new
        {
            tournamentId,
            totalAmount = tournament.PrizePool,
            payouts = ParsePrizeDistribution(tournament.PrizeDistributionJson, tournament.PrizePool)
        });
    }

    private static List<object> ParsePrizeDistribution(string? json, decimal prizePool)
    {
        var result = new List<object>();
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var place = item.TryGetProperty("place", out var placeEl) ? placeEl.GetString() ?? "Место" : "Место";
                var percent = item.TryGetProperty("percent", out var percentEl) && decimal.TryParse(percentEl.ToString(), out var parsed) ? parsed : 0m;
                result.Add(new { place, percent, amount = Math.Round(prizePool * percent / 100m, 2) });
            }
        }
        catch
        {
        }
        return result;
    }
}
