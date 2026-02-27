using Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Models;
using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json;

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

    [HttpGet("my")]
    public async Task<IActionResult> My(int tournamentId)
    {
        var userId = GetUserIdFromBearerToken();
        if (userId is null) return Unauthorized(new { message = "Требуется вход" });

        var apps = await _db.TournamentApplications
            .Include(a => a.Team)
            .Where(a => a.TournamentId == tournamentId && a.ApplicantUserId == userId.Value)
            .OrderByDescending(a => a.CreatedAtUtc)
            .ToListAsync();

        return Ok(apps.Select(ToDto));
    }

    [HttpPost]
    public async Task<IActionResult> Apply(int tournamentId, [FromBody] ApplyRequest request)
    {
        var userId = GetUserIdFromBearerToken();
        if (userId is null) return Unauthorized(new { message = "Требуется вход" });

        var tournament = await _db.Tournaments.FirstOrDefaultAsync(t => t.Id == tournamentId);
        if (tournament is null) return NotFound(new { message = "Турнир не найден" });

        var team = await _db.Teams.FirstOrDefaultAsync(t => t.Id == request.TeamId);
        if (team is null) return NotFound(new { message = "Команда не найдена" });

        if (team.CaptainUserId != userId.Value)
        {
            return StatusCode(403, new { message = "Недостаточно прав" });
        }

        var exists = await _db.TournamentApplications.AnyAsync(a => a.TournamentId == tournamentId && a.TeamId == request.TeamId);
        if (exists)
        {
            return Conflict(new { message = "Заявка для этой команды уже подана" });
        }

        if (tournament.MaxParticipants > 0 && tournament.CurrentParticipants >= tournament.MaxParticipants)
        {
            return BadRequest(new { message = "Турнир уже заполнен" });
        }

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

        // Для учебного проекта: пока без модерации — просто «заявка в ожидании».
        return Created($"/api/tournament/{tournamentId}/applications/{app.Id}", ToDto(app));
    }

    private static object ToDto(TournamentApplication a) => new
    {
        id = a.Id,
        tournamentId = a.TournamentId,
        teamId = a.TeamId,
        teamName = a.Team?.Name,
        status = a.Status,
        createdAtUtc = a.CreatedAtUtc
    };

    private int? GetUserIdFromBearerToken()
    {
        var auth = Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(auth) || !auth.StartsWith("Bearer "))
        {
            return null;
        }

        var token = auth.Replace("Bearer ", "");
        var claims = ParsePayload(token);
        if (!claims.TryGetValue("sub", out var userIdRaw) || !int.TryParse(userIdRaw, out var userId))
        {
            return null;
        }
        return userId;
    }

    private static Dictionary<string, string> ParsePayload(string token)
    {
        var parts = token.Split('.');
        if (parts.Length < 2) return new();
        var padded = parts[1] + new string('=', (4 - parts[1].Length % 4) % 4);
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(padded.Replace('-', '+').Replace('_', '/')));
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ToString());
    }
}
