using Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Infrastructure;
using Models;
using Services;

namespace Controllers;

/// <summary>
/// Доска скаутинга: игроки, которые ищут команду (LFT — looking for team).
/// Сортировка: сначала подтверждённый Faceit Elo, затем по его величине.
/// </summary>
[ApiController]
[Route("api/scouting")]
public class ScoutingController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ActivityLogService _activity;

    public ScoutingController(AppDbContext db, ActivityLogService activity)
    {
        _db = db;
        _activity = activity;
    }

    [HttpGet("free-agents")]
    public async Task<IActionResult> FreeAgents(CancellationToken ct)
    {
        var agents = await _db.Users
            .Where(u => u.IsLookingForTeam)
            .OrderByDescending(u => u.RatingVerified)
            .ThenByDescending(u => u.FaceitElo ?? -1)
            .ThenByDescending(u => u.Rating ?? -1)
            .ThenBy(u => u.Nickname)
            .ToListAsync(ct);

        var payload = agents.Select(u => new
        {
            id = u.Id,
            nickname = u.Nickname,
            bio = u.Bio,
            role = u.Role,
            faceitNickname = u.FaceitNickname,
            faceitElo = u.FaceitElo,
            faceitLevel = u.FaceitLevel,
            faceitAvatar = u.FaceitAvatar,
            faceitProfileUrl = u.FaceitProfileUrl,
            rating = u.Rating,
            ratingVerified = u.RatingVerified,
            lookingForTeamSinceUtc = u.LookingForTeamSinceUtc,
            gameRole = u.GameRole,
            availability = u.Availability,
            pitch = u.Pitch,
            discordId = u.DiscordId
        });

        return Ok(payload);
    }

    [HttpGet("free-agents/{id:int}")]
    public async Task<IActionResult> GetFreeAgent(int id, CancellationToken ct)
    {
        var u = await _db.Users.FirstOrDefaultAsync(user => user.Id == id && user.IsLookingForTeam, ct);
        if (u == null) return NotFound(new { message = "Игрок не найден" });
        return Ok(new
        {
            id = u.Id,
            nickname = u.Nickname,
            bio = u.Bio,
            role = u.Role,
            faceitNickname = u.FaceitNickname,
            faceitElo = u.FaceitElo,
            faceitLevel = u.FaceitLevel,
            faceitAvatar = u.FaceitAvatar,
            faceitProfileUrl = u.FaceitProfileUrl,
            rating = u.Rating,
            ratingVerified = u.RatingVerified,
            lookingForTeamSinceUtc = u.LookingForTeamSinceUtc,
            gameRole = u.GameRole,
            availability = u.Availability,
            pitch = u.Pitch,
            discordId = u.DiscordId,
            country = u.Country,
            city = u.City,
            languages = u.Languages,
            highlightsUrl = u.HighlightsUrl
        });
    }

    [HttpGet("vacancies")]
    public async Task<IActionResult> Vacancies(CancellationToken ct)
    {
        var vacancies = await _db.TeamVacancies
            .Include(v => v.Team)
            .OrderByDescending(v => v.CreatedAtUtc)
            .ToListAsync(ct);

        var payload = vacancies.Select(v => new
        {
            id = v.Id,
            teamId = v.TeamId,
            teamName = v.Team?.Name ?? "Unknown Team",
            requiredRole = v.RequiredRole,
            description = v.Description,
            createdAtUtc = v.CreatedAtUtc
        });

        return Ok(payload);
    }

    [HttpPost("endorse/{userId:int}")]
    public async Task<IActionResult> EndorsePlayer(int userId)
    {
        var currentUserId = User.GetUserId();
        if (currentUserId is null) return Unauthorized(new { message = "Требуется вход" });
        if (currentUserId.Value == userId) return BadRequest(new { message = "Нельзя дать рекомендацию самому себе" });

        var targetUser = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (targetUser is null) return NotFound(new { message = "Игрок не найден" });

        var existingEndorsement = await _db.PlayerEndorsements
            .FirstOrDefaultAsync(e => e.EndorsedUserId == userId && e.EndorserUserId == currentUserId.Value);

        if (existingEndorsement != null) return BadRequest(new { message = "Вы уже дали рекомендацию этому игроку" });

        var endorsement = new PlayerEndorsement
        {
            EndorsedUserId = userId,
            EndorserUserId = currentUserId.Value
        };

        _db.PlayerEndorsements.Add(endorsement);
        await _db.SaveChangesAsync();

        await _activity.LogAsync("player_endorsed", $"Пользователь поддержал игрока {targetUser.Nickname}");

        return Ok(new { message = "Рекомендация добавлена!" });
    }

    [HttpGet("recommendations")]
    public async Task<IActionResult> GetRecommendations([FromQuery] int teamId)
    {
        var currentUserId = User.GetUserId();
        if (currentUserId is null) return Unauthorized(new { message = "Требуется вход" });

        var team = await _db.Teams.Include(t => t.Players).FirstOrDefaultAsync(t => t.Id == teamId);
        if (team is null) return NotFound(new { message = "Команда не найдена" });
        if (team.CaptainUserId != currentUserId.Value) return StatusCode(403, new { message = "Только капитан команды может видеть рекомендации" });

        // Get players already interacted with
        var interactedUserIds = await _db.TeamInvites
            .Where(ti => ti.TeamId == teamId)
            .Select(ti => ti.UserId)
            .ToListAsync();

        // Calculate average Elo of current team members
        var teamPlayers = await _db.Users
            .Where(u => team.Players!.Select(p => p.Nickname).Contains(u.Nickname))
            .ToListAsync();
            
        double avgElo = teamPlayers.Any(u => u.FaceitElo.HasValue) 
            ? teamPlayers.Where(u => u.FaceitElo.HasValue).Average(u => u.FaceitElo!.Value) 
            : 1000;

        var query = _db.Users.AsNoTracking()
            .Where(u => u.IsLookingForTeam && !interactedUserIds.Contains(u.Id));

        var recommendations = await query
            .Select(u => new
            {
                id = u.Id,
                nickname = u.Nickname,
                faceitElo = u.FaceitElo ?? 1000,
                faceitAvatar = u.FaceitAvatar,
                gameRole = u.GameRole,
                availability = u.Availability,
                pitch = u.Pitch,
                faceitProfileUrl = u.FaceitProfileUrl,
                discordId = u.DiscordId,
                distance = Math.Abs((u.FaceitElo ?? 1000) - avgElo)
            })
            .OrderBy(u => u.distance) // Closest Elo first
            .Take(10)
            .ToListAsync();

        return Ok(recommendations);
    }

    [HttpPost("swipe")]
    public async Task<IActionResult> Swipe([FromBody] SwipeRequest req)
    {
        var currentUserId = User.GetUserId();
        if (currentUserId is null) return Unauthorized(new { message = "Требуется вход" });

        var team = await _db.Teams.FirstOrDefaultAsync(t => t.Id == req.TeamId);
        if (team is null || team.CaptainUserId != currentUserId.Value) 
            return StatusCode(403, new { message = "Нет доступа" });

        var invite = new TeamInvite
        {
            TeamId = req.TeamId,
            UserId = req.PlayerId,
            CaptainId = currentUserId.Value,
            Status = req.Action == "invite" ? "pending" : "skipped",
            CreatedAtUtc = DateTime.UtcNow
        };

        _db.TeamInvites.Add(invite);
        await _db.SaveChangesAsync();

        if (req.Action == "invite")
            await _activity.LogAsync("team_invite_sent", $"Капитан команды {team.Name} отправил приглашение игроку ID={req.PlayerId}");

        return Ok(new { message = "Успешно" });
    }
}

public class SwipeRequest
{
    public int TeamId { get; set; }
    public int PlayerId { get; set; }
    public string Action { get; set; } = string.Empty; // "invite" or "skip"
}
