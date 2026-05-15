using Data;
using Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Models;
using System.ComponentModel.DataAnnotations;

namespace Controllers;

[ApiController]
[Route("api/tournament/{tournamentId:int}/applications")]
public class TournamentApplicationsController : ControllerBase
{
    private readonly AppDbContext _db;

    public TournamentApplicationsController(AppDbContext db)
    {
        _db = db;
    }

    public class ApplyRequest
    {
        [Required]
        public int TeamId { get; set; }
    }

    [HttpGet]
    public async Task<IActionResult> List(int tournamentId)
    {
        if (!AuthTokenHelper.IsInAnyRole(Request, "admin", "judge"))
            return StatusCode(403, new { message = "Недостаточно прав" });

        var apps = await _db.TournamentApplications
            .Include(a => a.Team)
                .ThenInclude(t => t!.Players)
            .Include(a => a.ApplicantUser)
            .Where(a => a.TournamentId == tournamentId)
            .OrderByDescending(a => a.CreatedAtUtc)
            .ToListAsync();

        return Ok(apps.Select(ToDto));
    }

    [HttpGet("my")]
    public async Task<IActionResult> My(int tournamentId)
    {
        var userId = AuthTokenHelper.GetUserId(Request);
        if (userId is null) return Unauthorized(new { message = "Требуется вход" });

        var apps = await _db.TournamentApplications
            .Include(a => a.Team)
                .ThenInclude(t => t!.Players)
            .Where(a => a.TournamentId == tournamentId && a.ApplicantUserId == userId.Value)
            .OrderByDescending(a => a.CreatedAtUtc)
            .ToListAsync();

        return Ok(apps.Select(ToDto));
    }

    [HttpPost]
    public async Task<IActionResult> Apply(int tournamentId, [FromBody] ApplyRequest request)
    {
        var userId = AuthTokenHelper.GetUserId(Request);
        if (userId is null) return Unauthorized(new { message = "Требуется вход" });

        var tournament = await _db.Tournaments.FirstOrDefaultAsync(t => t.Id == tournamentId);
        if (tournament is null) return NotFound(new { message = "Турнир не найден" });

        if (tournament.IsExternal)
            return BadRequest(new { message = "Для внешних турниров доступен только просмотр данных. Заявки можно подавать только в локальные турниры." });

        var team = await _db.Teams.FirstOrDefaultAsync(t => t.Id == request.TeamId);
        if (team is null) return NotFound(new { message = "Команда не найдена" });
        if (team.CaptainUserId != userId.Value) return StatusCode(403, new { message = "Недостаточно прав" });

        var exists = await _db.TournamentApplications.AnyAsync(a => a.TournamentId == tournamentId && a.TeamId == request.TeamId);
        if (exists) return Conflict(new { message = "Заявка для этой команды уже подана" });

        if (tournament.MaxParticipants > 0 && tournament.CurrentParticipants >= tournament.MaxParticipants)
            return BadRequest(new { message = "Турнир уже заполнен" });

        var app = new TournamentApplication
        {
            TournamentId = tournamentId,
            TeamId = request.TeamId,
            ApplicantUserId = userId.Value,
            Status = "pending",
            CreatedAtUtc = DateTime.UtcNow
        };

        _db.TournamentApplications.Add(app);
        await _db.SaveChangesAsync();

        return Created($"/api/tournament/{tournamentId}/applications/{app.Id}", ToDto(app));
    }

    [HttpPost("{applicationId:int}/approve")]
    public async Task<IActionResult> Approve(int tournamentId, int applicationId)
    {
        if (!AuthTokenHelper.IsInAnyRole(Request, "admin", "judge"))
            return StatusCode(403, new { message = "Недостаточно прав" });

        var tournament = await _db.Tournaments.FirstOrDefaultAsync(t => t.Id == tournamentId);
        if (tournament is null) return NotFound(new { message = "Турнир не найден" });

        var app = await _db.TournamentApplications
            .Include(a => a.Team)
                .ThenInclude(t => t!.Players)
            .Include(a => a.ApplicantUser)
            .FirstOrDefaultAsync(a => a.Id == applicationId && a.TournamentId == tournamentId);
        if (app is null) return NotFound(new { message = "Заявка не найдена" });

        if (app.Status == "approved")
            return Ok(ToDto(app));

        if (tournament.MaxParticipants > 0 && tournament.CurrentParticipants >= tournament.MaxParticipants)
            return BadRequest(new { message = "Турнир уже заполнен" });

        var players = app.Team?.Players?.ToList() ?? new List<Models.TeamPlayer>();
        if (players.Count == 0)
            return BadRequest(new { message = "В команде нет игроков. Добавьте состав перед подтверждением заявки." });

        var unconfirmed = players.Where(p => p.RatingStatus != "confirmed").Select(p => p.Nickname).ToList();
        if (unconfirmed.Count > 0)
            return BadRequest(new { message = "Нельзя подтвердить заявку: сначала подтвердите рейтинг игроков: " + string.Join(", ", unconfirmed) });

        app.Status = "approved";
        tournament.CurrentParticipants += 1;
        await _db.SaveChangesAsync();

        return Ok(ToDto(app));
    }

    [HttpPost("{applicationId:int}/reject")]
    public async Task<IActionResult> Reject(int tournamentId, int applicationId)
    {
        if (!AuthTokenHelper.IsInAnyRole(Request, "admin", "judge"))
            return StatusCode(403, new { message = "Недостаточно прав" });

        var app = await _db.TournamentApplications
            .Include(a => a.Team)
                .ThenInclude(t => t!.Players)
            .Include(a => a.ApplicantUser)
            .FirstOrDefaultAsync(a => a.Id == applicationId && a.TournamentId == tournamentId);
        if (app is null) return NotFound(new { message = "Заявка не найдена" });

        if (app.Status == "approved")
        {
            var tournament = await _db.Tournaments.FirstOrDefaultAsync(t => t.Id == tournamentId);
            if (tournament != null && tournament.CurrentParticipants > 0)
                tournament.CurrentParticipants -= 1;
        }

        app.Status = "rejected";
        await _db.SaveChangesAsync();
        return Ok(ToDto(app));
    }

    private static object ToDto(TournamentApplication a) => new
    {
        id = a.Id,
        tournamentId = a.TournamentId,
        teamId = a.TeamId,
        teamName = a.Team?.Name,
        status = a.Status,
        createdAtUtc = a.CreatedAtUtc,
        applicantEmail = a.ApplicantUser?.Email,
        playersCount = a.Team?.Players?.Count ?? 0,
        unconfirmedRatings = a.Team?.Players?.Count(p => p.RatingStatus != "confirmed") ?? 0
    };
}
