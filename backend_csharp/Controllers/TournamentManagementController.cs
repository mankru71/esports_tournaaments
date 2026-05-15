using Data;
using Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Controllers;

[ApiController]
[Route("api/tournament/{tournamentId:int}")]
public class TournamentManagementController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly Services.TournamentPlanningService _planning;

    public TournamentManagementController(AppDbContext db, Services.TournamentPlanningService planning)
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

    public class StageRequest
    {
        public string Status { get; set; } = "planned";
        public string? Stage { get; set; }
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
                id = m.Id,
                round = m.Round,
                groupName = m.GroupName,
                teamA = m.TeamA,
                teamB = m.TeamB,
                scoreA = m.ScoreA,
                scoreB = m.ScoreB,
                status = m.Status,
                streamUrl = m.StreamUrl
            }).ToList(),
            standings = plan.Standings.Select(s => new
            {
                groupName = s.GroupName,
                team = s.Team,
                played = s.Played,
                wins = s.Wins,
                losses = s.Losses,
                points = s.Points
            }).ToList(),
            summary = plan.Summary
        });
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
            tournament.Format = NormalizeFormat(request.Format);
        if (!string.IsNullOrWhiteSpace(request.StageType))
            tournament.StageType = NormalizeStageType(request.StageType);

        await _db.SaveChangesAsync();
        return Ok(new { tournament.Id, tournament.Format, tournament.StageType });
    }

    [HttpPost("stage")]
    public async Task<IActionResult> SetStage(int tournamentId, [FromBody] StageRequest request)
    {
        if (!AuthTokenHelper.IsInAnyRole(Request, "admin", "judge"))
            return StatusCode(403, new { message = "Недостаточно прав" });

        var tournament = await _db.Tournaments.FirstOrDefaultAsync(t => t.Id == tournamentId);
        if (tournament == null)
            return NotFound(new { message = "Турнир не найден" });

        var status = (request.Status ?? string.Empty).Trim().ToLowerInvariant();
        if (!new[] { "planned", "live", "paused", "finished" }.Contains(status))
            return BadRequest(new { message = "Статус должен быть planned/live/paused/finished" });

        tournament.Status = status;
        tournament.CurrentStage = string.IsNullOrWhiteSpace(request.Stage) ? GuessStage(tournament) : request.Stage.Trim().ToLowerInvariant();
        if (status == "finished")
            tournament.MvpVotingOpen = true;

        await _db.SaveChangesAsync();
        return Ok(new { tournament.Id, tournament.Status, tournament.CurrentStage, tournament.MvpVotingOpen });
    }

    [HttpGet("prize-pool")]
    public async Task<IActionResult> PrizePool(int tournamentId)
    {
        var tournament = await _db.Tournaments.FirstOrDefaultAsync(t => t.Id == tournamentId);
        if (tournament == null)
            return NotFound(new { message = "Турнир не найден" });

        var payouts = await _db.PrizePayouts
            .Include(p => p.Team)
            .Where(p => p.TournamentId == tournamentId)
            .OrderBy(p => p.Place)
            .ToListAsync();

        var configuredPayouts = ParsePrizeDistribution(tournament.PrizeDistributionJson, tournament.PrizePool)
            .Select(p => new { place = p.Place, team = "TBD", percent = p.Percent, amount = p.Amount, status = p.Status })
            .ToList();
        var savedPayouts = payouts
            .Select(p => new { place = p.PlaceTitle, team = p.Team?.Name ?? "TBD", percent = p.Percent, amount = p.Amount, status = p.Status })
            .ToList();

        return Ok(new
        {
            tournamentId,
            totalAmount = tournament.PrizePool,
            payouts = savedPayouts.Any() ? savedPayouts : configuredPayouts
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

    [HttpPost("prize-pool/distribute")]
    public async Task<IActionResult> DistributePrizes(int tournamentId)
    {
        if (!AuthTokenHelper.IsInAnyRole(Request, "admin"))
            return StatusCode(403, new { message = "Только администратор может распределять выплаты" });

        var tournament = await _db.Tournaments.FirstOrDefaultAsync(t => t.Id == tournamentId);
        if (tournament == null)
            return NotFound(new { message = "Турнир не найден" });
        if (tournament.PrizePool <= 0)
            return BadRequest(new { message = "Призовой фонд должен быть больше 0" });

        var winners = await DetectWinnersAsync(tournamentId);
        var configured = ParsePrizeDistribution(tournament.PrizeDistributionJson, tournament.PrizePool)
            .Select((x, index) => new
            {
                Place = index + 1,
                Title = x.Place,
                Percent = x.Percent,
                Amount = x.Amount
            })
            .ToList();

        var old = await _db.PrizePayouts.Where(p => p.TournamentId == tournamentId).ToListAsync();
        _db.PrizePayouts.RemoveRange(old);

        var payouts = configured.Select(item => new Models.PrizePayout
        {
            TournamentId = tournamentId,
            Place = item.Place,
            PlaceTitle = item.Title,
            TeamId = winners.ElementAtOrDefault(item.Place - 1),
            Percent = item.Percent,
            Amount = item.Amount,
            Status = "pending",
            CreatedAtUtc = DateTime.UtcNow
        }).ToList();

        await _db.PrizePayouts.AddRangeAsync(payouts);
        await _db.SaveChangesAsync();

        return Ok(new
        {
            message = "Призовой фонд рассчитан. Выплаты подготовлены к подтверждению.",
            payouts = payouts.Select(p => new { place = p.PlaceTitle, p.TeamId, p.Percent, p.Amount, p.Status })
        });
    }

    [HttpPost("prize-pool/mark-paid")]
    public async Task<IActionResult> MarkPayoutsPaid(int tournamentId)
    {
        if (!AuthTokenHelper.IsInAnyRole(Request, "admin"))
            return StatusCode(403, new { message = "Только администратор может подтверждать выплаты" });

        var payouts = await _db.PrizePayouts.Where(p => p.TournamentId == tournamentId).ToListAsync();
        if (!payouts.Any())
            return BadRequest(new { message = "Сначала распределите призовой фонд" });

        foreach (var payout in payouts)
        {
            payout.Status = "paid";
            payout.PaidAtUtc = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();
        return Ok(new { message = "Выплаты отмечены как выполненные", count = payouts.Count });
    }

    private async Task<List<int?>> DetectWinnersAsync(int tournamentId)
    {
        var final = await _db.Matches
            .Where(m => m.TournamentId == tournamentId && m.Status == "finished")
            .OrderByDescending(m => m.RoundNumber)
            .ThenByDescending(m => m.Id)
            .FirstOrDefaultAsync();

        var result = new List<int?>();
        if (final != null)
        {
            result.Add(final.WinnerId);
            var runnerUp = final.TeamAId == final.WinnerId ? final.TeamBId : final.TeamAId;
            result.Add(runnerUp);
        }

        var approvedTeams = await _db.TournamentApplications
            .Where(a => a.TournamentId == tournamentId && a.Status == "approved")
            .OrderBy(a => a.Id)
            .Select(a => (int?)a.TeamId)
            .ToListAsync();

        foreach (var teamId in approvedTeams)
        {
            if (!result.Contains(teamId)) result.Add(teamId);
        }

        return result;
    }

    private static string NormalizeFormat(string value)
    {
        var v = value.Trim().ToLowerInvariant();
        return v == "group_stage" ? "group_stage" : "single_elimination";
    }

    private static string NormalizeStageType(string value)
    {
        var v = value.Trim().ToLowerInvariant();
        return v == "groups" ? "groups" : "single";
    }

    private static string GuessStage(Models.Tournament tournament)
        => tournament.StageType == "groups" ? "group_stage" : "playoff";

    private record PrizeDistributionDto(string Place, decimal Percent, decimal Amount, string Status);

    private static List<PrizeDistributionDto> ParsePrizeDistribution(string? json, decimal prizePool)
    {
        var result = new List<PrizeDistributionDto>();
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var place = item.TryGetProperty("place", out var placeEl) ? placeEl.GetString() ?? "Место" : "Место";
                var percent = item.TryGetProperty("percent", out var percentEl) && decimal.TryParse(percentEl.ToString(), out var parsed) ? parsed : 0m;
                result.Add(new PrizeDistributionDto(place, percent, Math.Round(prizePool * percent / 100m, 2), "pending"));
            }
        }
        catch
        {
        }
        return result;
    }
}
