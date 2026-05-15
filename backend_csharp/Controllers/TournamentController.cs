using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Services;
using System.Text.Json;

namespace Controllers;

[ApiController]
[Route("api/tournament")]
public class TournamentController : ControllerBase
{
    private readonly TournamentService _tournamentService;
    private readonly ExternalTournamentSyncService _sync;
    private readonly DiscordWebhookService _discord;

    public TournamentController(TournamentService tournamentService, ExternalTournamentSyncService sync, DiscordWebhookService discord)
    {
        _tournamentService = tournamentService;
        _sync = sync;
        _discord = discord;
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        await _sync.SyncUpcomingAsync(ct);
        var tournaments = _tournamentService.GetAllTournaments().Select(ToDto);
        return Ok(tournaments);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateTournamentRequest request, CancellationToken ct)
    {
        if (!Infrastructure.AuthTokenHelper.IsInAnyRole(Request, "admin"))
            return StatusCode(403, new { message = "Создавать турниры может только администратор" });

        var tournament = new Models.Tournament
        {
            Name = string.IsNullOrWhiteSpace(request.Name) ? "Новый турнир" : request.Name.Trim(),
            Game = string.IsNullOrWhiteSpace(request.Game) ? "counterstrike" : request.Game.Trim(),
            PrizePool = request.PrizePool < 0 ? 0 : request.PrizePool,
            MaxParticipants = request.MaxParticipants <= 0 ? 8 : request.MaxParticipants,
            CurrentParticipants = 0,
            StartDate = string.IsNullOrWhiteSpace(request.StartDate) ? DateTime.UtcNow.ToString("yyyy-MM-dd") : request.StartDate.Trim(),
            Status = string.IsNullOrWhiteSpace(request.Status) ? "planned" : request.Status.Trim().ToLowerInvariant(),
            Format = string.IsNullOrWhiteSpace(request.Format) ? "single_elimination" : request.Format.Trim().ToLowerInvariant(),
            StageType = string.IsNullOrWhiteSpace(request.StageType) ? "single" : request.StageType.Trim().ToLowerInvariant(),
        };

        _tournamentService.CreateTournament(tournament);
        await _discord.NotifyTournamentCreatedAsync(tournament, ct);
        return Created($"/api/tournament/{tournament.Id}", ToDto(tournament));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, [FromServices] Data.AppDbContext db)
    {
        if (!Infrastructure.AuthTokenHelper.IsInAnyRole(Request, "admin"))
            return StatusCode(403, new { message = "Удалять турниры может только администратор" });

        var tournament = await db.Tournaments.FindAsync(id);
        if (tournament == null)
            return NotFound(new { message = "Турнир не найден" });

        var matches = await db.Matches.Where(m => m.TournamentId == id).ToListAsync();
        var applications = await db.TournamentApplications.Where(a => a.TournamentId == id).ToListAsync();
        var mvpVotes = await db.MvpVotes.Where(v => v.TournamentId == id).ToListAsync();
        var payouts = await db.PrizePayouts.Where(p => p.TournamentId == id).ToListAsync();
        db.Matches.RemoveRange(matches);
        db.TournamentApplications.RemoveRange(applications);
        db.MvpVotes.RemoveRange(mvpVotes);
        db.PrizePayouts.RemoveRange(payouts);
        db.Tournaments.Remove(tournament);
        await db.SaveChangesAsync();
        return Ok(new { message = "Турнир успешно удален" });
    }

    [HttpPost("{id:int}/generate-bracket")]
    public async Task<IActionResult> GenerateBracket(int id, [FromServices] TournamentPlanningService planningService, [FromServices] Data.AppDbContext db, CancellationToken ct)
    {
        if (!Infrastructure.AuthTokenHelper.IsInAnyRole(Request, "admin", "judge"))
            return StatusCode(403, new { message = "Только администратор или судья может генерировать сетку" });

        var tournament = await db.Tournaments.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tournament == null)
            return NotFound(new { message = "Турнир не найден" });
        if (tournament.IsExternal)
            return BadRequest(new { message = "Для внешних турниров генерация локальной сетки недоступна" });

        var success = await planningService.GenerateAndSaveBracketAsync(id, ct);
        if (!success)
            return BadRequest(new { message = "Не удалось сгенерировать сетку. Нужны минимум 2 подтверждённые команды." });

        return Ok(new { message = "Сетка успешно сгенерирована" });
    }

    [HttpGet("stats")]
    public IActionResult GetStats()
    {
        return Ok(_tournamentService.GetStats());
    }

    [HttpGet("{id:int}")]
    public IActionResult GetById(int id)
    {
        var tournament = _tournamentService.GetTournamentById(id);
        if (tournament == null)
            return NotFound(new ProblemDetails { Title = "Not Found", Detail = "Tournament not found", Status = 404 });

        return Ok(ToDto(tournament));
    }

    public class CreateTournamentRequest
    {
        public string Name { get; set; } = string.Empty;
        public string Game { get; set; } = "counterstrike";
        public decimal PrizePool { get; set; }
        public int MaxParticipants { get; set; } = 8;
        public string StartDate { get; set; } = string.Empty;
        public string Format { get; set; } = "single_elimination";
        public string StageType { get; set; } = "single";
        public string Status { get; set; } = "planned";
    }

    private static object ToDto(Models.Tournament t)
    {
        var payouts = ParsePrizeDistribution(t.PrizeDistributionJson, t.PrizePool);
        return new
        {
            id = t.Id,
            name = t.Name,
            discipline = t.Game,
            format = t.Format,
            stageType = t.StageType,
            status = t.Status,
            currentStage = t.CurrentStage,
            mvpVotingOpen = t.MvpVotingOpen,
            startDate = t.StartDate,
            prizePool = t.PrizePool,
            totalAmount = t.PrizePool,
            currentParticipants = t.CurrentParticipants,
            maxParticipants = t.MaxParticipants,
            isExternal = t.IsExternal,
            provider = t.Provider,
            providerTournamentId = t.ProviderTournamentId,
            prizePayouts = payouts,
            stagesSummary = t.StageType == "groups" ? "Group stage" : "Single elimination"
        };
    }

    private static List<object> ParsePrizeDistribution(string? json, decimal prizePool)
    {
        var result = new List<object>();
        if (string.IsNullOrWhiteSpace(json))
            return result;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var place = item.TryGetProperty("place", out var placeEl) ? placeEl.GetString() ?? "Место" : "Место";
                var percent = item.TryGetProperty("percent", out var percentEl) && decimal.TryParse(percentEl.ToString(), out var p) ? p : 0m;
                result.Add(new { place, percent, amount = Math.Round(prizePool * percent / 100m, 2) });
            }
        }
        catch
        {
        }

        return result;
    }
}
