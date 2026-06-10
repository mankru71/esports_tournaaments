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
    private readonly ExternalTournamentSyncService _sync;

    public TournamentManagementController(AppDbContext db, TournamentPlanningService planning, ExternalTournamentSyncService sync)
    {
        _db = db;
        _planning = planning;
        _sync = sync;
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

        // Внешний турнир: лениво подтягиваем матчи из Liquipedia в БД перед построением плана
        if (tournament.IsExternal)
            await _sync.SyncMatchesAsync(tournament, ct);

        var plan = await _planning.BuildPlanAsync(tournament, ct);

        // ДОБАВЛЕНО: Вытаскиваем "сырые" матчи из БД, чтобы получить их ID и связи для сетки
        var rawMatches = await _db.Matches
            .Include(m => m.TeamA)
            .Include(m => m.TeamB)
            .Where(m => m.TournamentId == tournamentId)
            .OrderBy(m => m.RoundNumber)
            .ThenBy(m => m.Id)
            .ToListAsync(ct);

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
            matches = rawMatches.Select(m => new
            {
                id = m.Id,
                round = m.Round,
                roundNumber = m.RoundNumber,
                nextMatchId = m.NextMatchId,
                teamA = m.TeamA == null ? null : new { id = m.TeamAId, name = m.TeamA.Name },
                teamB = m.TeamB == null ? null : new { id = m.TeamBId, name = m.TeamB.Name },
                scoreA = m.ScoreA,
                scoreB = m.ScoreB,
                status = m.Status,
                winnerId = m.WinnerId,
                isBye = (m.TeamAId.HasValue && !m.TeamBId.HasValue) ||
                        (!m.TeamAId.HasValue && m.TeamBId.HasValue)
            }), // <--- ИСПРАВЛЕНО: Вот та самая недостающая запятая!
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

    /// <summary>
    /// Аналитика, изолированная по турниру: только его матчи и команды.
    /// Для внешних турниров — standings по спарсенным матчам, без локальных игроков.
    /// Для локальных — статистика игроков команд с одобренными заявками.
    /// </summary>
    [HttpGet("analytics")]
    public async Task<IActionResult> Analytics(int tournamentId, CancellationToken ct)
    {
        var tournament = await _db.Tournaments.FirstOrDefaultAsync(t => t.Id == tournamentId, ct);
        if (tournament == null)
            return NotFound(new { message = "Турнир не найден" });

        if (tournament.IsExternal)
            await _sync.SyncMatchesAsync(tournament, ct);

        var matches = await _db.Matches
            .Include(m => m.TeamA)
            .Include(m => m.TeamB)
            .Where(m => m.TournamentId == tournamentId)
            .ToListAsync(ct);

        // ── Standings по матчам ЭТОГО турнира ──────────────────────────
        var standings = new Dictionary<int, (string Name, int Wins, int Losses, int ScoreFor, int ScoreAgainst)>();

        void Track(int? teamId, string? name, int scoreFor, int scoreAgainst, bool won, bool lost)
        {
            if (!teamId.HasValue || string.IsNullOrWhiteSpace(name))
                return;
            var row = standings.TryGetValue(teamId.Value, out var existing)
                ? existing
                : (Name: name!, Wins: 0, Losses: 0, ScoreFor: 0, ScoreAgainst: 0);
            standings[teamId.Value] = (
                row.Name,
                row.Wins + (won ? 1 : 0),
                row.Losses + (lost ? 1 : 0),
                row.ScoreFor + scoreFor,
                row.ScoreAgainst + scoreAgainst);
        }

        foreach (var m in matches)
        {
            var finished = m.Status is "finished" or "approved" && m.WinnerId.HasValue;
            Track(m.TeamAId, m.TeamA?.Name, m.ScoreA, m.ScoreB,
                finished && m.WinnerId == m.TeamAId, finished && m.WinnerId != m.TeamAId);
            Track(m.TeamBId, m.TeamB?.Name, m.ScoreB, m.ScoreA,
                finished && m.WinnerId == m.TeamBId, finished && m.WinnerId != m.TeamBId);
        }

        var standingsPayload = standings.Values
            .OrderByDescending(s => s.Wins)
            .ThenByDescending(s => s.ScoreFor - s.ScoreAgainst)
            .ThenBy(s => s.Name)
            .Select(s => new
            {
                team = s.Name,
                wins = s.Wins,
                losses = s.Losses,
                scoreFor = s.ScoreFor,
                scoreAgainst = s.ScoreAgainst,
                diff = s.ScoreFor - s.ScoreAgainst
            })
            .ToList();

        // ── Статистика игроков: только для ЛОКАЛЬНЫХ турниров ──────────
        var playerStats = new List<object>();
        if (!tournament.IsExternal)
        {
            var approvedTeams = await _db.TournamentApplications
                .Include(a => a.Team)
                    .ThenInclude(t => t!.Players)
                .Where(a => a.TournamentId == tournamentId && a.Status == "approved" && a.Team != null)
                .Select(a => a.Team!)
                .ToListAsync(ct);

            playerStats = approvedTeams
                .GroupBy(t => t.Id)
                .Select(g => g.First())
                .SelectMany(t => t.Players.Select(p => new
                {
                    player = p.Nickname,
                    team = t.Name,
                    rating = p.Rating,
                    ratingStatus = p.RatingStatus
                }))
                .OrderByDescending(p => p.rating ?? 0m)
                .Cast<object>()
                .ToList();
        }

        return Ok(new
        {
            tournamentId,
            isExternal = tournament.IsExternal,
            provider = tournament.Provider,
            summary = new
            {
                totalTeams = standings.Count,
                totalMatches = matches.Count,
                finishedMatches = matches.Count(m => m.Status is "finished" or "approved"),
                liveMatches = matches.Count(m => m.Status == "live"),
                totalPlayers = playerStats.Count
            },
            standings = standingsPayload,
            playerStats,
            prizePools = ParsePrizeDistribution(tournament.PrizeDistributionJson, tournament.PrizePool)
        });
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
