using Data;
using Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Models;
using Services;
using System.ComponentModel.DataAnnotations;

namespace Controllers;

[ApiController]
[Route("api/teams")]
public class TeamsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly PandaScoreService _pandascore;
    private readonly ActivityLogService _activity;

    public TeamsController(AppDbContext db, PandaScoreService pandascore, ActivityLogService activity)
    {
        _db = db;
        _pandascore = pandascore;
        _activity = activity;
    }

    public class CreateTeamRequest
    {
        [Required, MinLength(2)]
        public string Name { get; set; } = string.Empty;
    }

    public class AddPlayerRequest
    {
        [Required, MinLength(2)]
        public string Nickname { get; set; } = string.Empty;
        [Range(0, 99999)]
        public decimal? Rating { get; set; }
        public string? RatingSource { get; set; }
        public string? Game { get; set; }
    }

    public class CreateVacancyRequest
    {
        [Required]
        public string RequiredRole { get; set; } = string.Empty;
        public string? Description { get; set; }
    }

    public class CreateInviteRequest
    {
        [Required]
        public int UserId { get; set; }
    }

    public class RespondInviteRequest
    {
        [Required]
        public string Action { get; set; } = string.Empty; // "accept" or "decline"
    }

    [HttpGet]
    public async Task<IActionResult> List()
    {
        var teams = await _db.Teams
            .Include(t => t.CaptainUser)
            .Include(t => t.Players)
            .Include(t => t.Vacancies)
            .Where(t => !t.IsExternal) // спарсенные команды не показываем на странице «Команды»
            .OrderByDescending(t => t.Id)
            .ToListAsync();

        return Ok(teams.Select(t => new
        {
            id = t.Id,
            name = t.Name,
            captainEmail = t.CaptainUser?.Email ?? "unknown",
            players = t.Players.Select(p => new
            {
                id = p.Id,
                nickname = p.Nickname,
                rating = p.Rating,
                ratingSource = p.RatingSource,
                ratingStatus = p.RatingStatus,
                externalProfileUrl = p.ExternalProfileUrl,
                confirmedAtUtc = p.ConfirmedAtUtc
            }),
            vacancies = t.Vacancies.Select(v => new
            {
                id = v.Id,
                requiredRole = v.RequiredRole,
                description = v.Description
            })
        }));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateTeamRequest request)
    {
        var userId = User.GetUserId();
        if (userId is null) return Unauthorized(new { message = "Требуется вход" });

        var team = new Team
        {
            Name = request.Name.Trim(),
            CaptainUserId = userId.Value,
        };

        _db.Teams.Add(team);
        await _db.SaveChangesAsync();
        await _activity.LogAsync("team_created", $"Создана команда «{team.Name}»");

        return Ok(new { id = team.Id, name = team.Name, captainUserId = team.CaptainUserId });
    }

    [HttpPost("{teamId:int}/players")]
    public async Task<IActionResult> AddPlayer(int teamId, [FromBody] AddPlayerRequest request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId is null) return Unauthorized(new { message = "Требуется вход" });

        var team = await _db.Teams.Include(t => t.Players).FirstOrDefaultAsync(t => t.Id == teamId, ct);
        if (team is null) return NotFound(new { message = "Команда не найдена" });

        if (team.CaptainUserId != userId.Value)
            return StatusCode(403, new { message = "Недостаточно прав" });

        var nickname = (request.Nickname ?? string.Empty).Trim();
        if (nickname.Length < 2)
            return BadRequest(new { message = "Ник должен содержать минимум 2 символа" });

        var exists = await _db.TeamPlayers
            .AnyAsync(p => p.TeamId == teamId && p.Nickname.ToLower() == nickname.ToLower(), ct);
        if (exists)
            return Conflict(new { message = "В команде уже есть участник с таким ником" });

        var ratingSource = string.IsNullOrWhiteSpace(request.RatingSource) ? "manual" : request.RatingSource.Trim().ToLowerInvariant();
        var ratingStatus = request.Rating.HasValue ? "pending_confirmation" : "pending";
        string? externalPlayerId = null;
        string? externalProfileUrl = null;

        if (_pandascore.Enabled)
        {
            var players = await _pandascore.SearchPlayersAsync(nickname, 5, request.Game, ct);
            var matched = players.FirstOrDefault();
            if (matched != null)
            {
                externalPlayerId = matched.Id;
                externalProfileUrl = matched.ProfileUrl;
                ratingSource = string.IsNullOrWhiteSpace(request.RatingSource) ? "pandascore" : ratingSource;
                ratingStatus = request.Rating.HasValue ? "external_match" : "external_match";
            }
        }

        var player = new TeamPlayer
        {
            TeamId = teamId,
            Nickname = nickname,
            Rating = request.Rating,
            RatingSource = ratingSource,
            RatingStatus = ratingStatus,
            ExternalPlayerId = externalPlayerId,
            ExternalProfileUrl = externalProfileUrl
        };

        _db.TeamPlayers.Add(player);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return Conflict(new { message = "В команде уже есть участник с таким ником" });
        }

        await _activity.LogAsync("player_joined", $"Игрок {player.Nickname} присоединился к команде «{team.Name}»", ct);

        return Ok(new
        {
            id = player.Id,
            teamId = player.TeamId,
            nickname = player.Nickname,
            rating = player.Rating,
            ratingSource = player.RatingSource,
            ratingStatus = player.RatingStatus,
            externalProfileUrl = player.ExternalProfileUrl
        });
    }

    [HttpPost("{teamId:int}/players/{playerId:int}/confirm-rating")]
    public async Task<IActionResult> ConfirmRating(int teamId, int playerId)
    {
        if (!User.IsInRole("admin") || User.IsInRole("judge"))
            return StatusCode(403, new { message = "Подтвердить рейтинг может только администратор или судья" });

        var player = await _db.TeamPlayers.FirstOrDefaultAsync(p => p.TeamId == teamId && p.Id == playerId);
        if (player is null)
            return NotFound(new { message = "Игрок не найден" });

        if (!player.Rating.HasValue)
            return BadRequest(new { message = "У игрока не указан рейтинг для подтверждения" });

        player.RatingStatus = "confirmed";
        player.ConfirmedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(new
        {
            id = player.Id,
            rating = player.Rating,
            ratingStatus = player.RatingStatus,
            confirmedAtUtc = player.ConfirmedAtUtc
        });
    }

    [HttpDelete("{teamId:int}/players/{playerId:int}")]
    public async Task<IActionResult> DeletePlayer(int teamId, int playerId)
    {
        var userId = User.GetUserId();
        if (userId is null) return Unauthorized(new { message = "Требуется вход" });

        var team = await _db.Teams.FirstOrDefaultAsync(t => t.Id == teamId);
        if (team is null) return NotFound(new { message = "Команда не найдена" });
        if (team.CaptainUserId != userId.Value) return StatusCode(403, new { message = "Недостаточно прав" });

        var player = await _db.TeamPlayers.FirstOrDefaultAsync(p => p.Id == playerId && p.TeamId == teamId);
        if (player is null) return NotFound(new { message = "Игрок не найден" });

        _db.TeamPlayers.Remove(player);
        await _db.SaveChangesAsync();
        await _activity.LogAsync("player_left", $"Игрок {player.Nickname} покинул команду «{team.Name}»");
        return NoContent();
    }

    [HttpDelete("{teamId:int}")]
    public async Task<IActionResult> DeleteTeam(int teamId)
    {
        var userId = User.GetUserId();
        if (userId is null) return Unauthorized(new { message = "Требуется вход" });

        var team = await _db.Teams.Include(t => t.Players).FirstOrDefaultAsync(t => t.Id == teamId);
        if (team is null) return NotFound(new { message = "Команда не найдена" });
        if (team.CaptainUserId != userId.Value) return StatusCode(403, new { message = "Недостаточно прав" });

        _db.Teams.Remove(team);
        await _db.SaveChangesAsync();
        await _activity.LogAsync("team_deleted", $"Команда «{team.Name}» расформирована");
        return NoContent();
    }

    [HttpPost("{teamId:int}/vacancies")]
    public async Task<IActionResult> CreateVacancy(int teamId, [FromBody] CreateVacancyRequest request)
    {
        var userId = User.GetUserId();
        if (userId is null) return Unauthorized(new { message = "Требуется вход" });

        var team = await _db.Teams.FirstOrDefaultAsync(t => t.Id == teamId);
        if (team is null) return NotFound(new { message = "Команда не найдена" });
        if (team.CaptainUserId != userId.Value) return StatusCode(403, new { message = "Недостаточно прав" });

        var vacancy = new TeamVacancy
        {
            TeamId = teamId,
            RequiredRole = request.RequiredRole.Trim(),
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim()
        };

        _db.TeamVacancies.Add(vacancy);
        await _db.SaveChangesAsync();

        return Ok(new { id = vacancy.Id, teamId = vacancy.TeamId, requiredRole = vacancy.RequiredRole });
    }

    [HttpDelete("{teamId:int}/vacancies/{vacancyId:int}")]
    public async Task<IActionResult> DeleteVacancy(int teamId, int vacancyId)
    {
        var userId = User.GetUserId();
        if (userId is null) return Unauthorized(new { message = "Требуется вход" });

        var team = await _db.Teams.FirstOrDefaultAsync(t => t.Id == teamId);
        if (team is null) return NotFound(new { message = "Команда не найдена" });
        if (team.CaptainUserId != userId.Value) return StatusCode(403, new { message = "Недостаточно прав" });

        var vacancy = await _db.TeamVacancies.FirstOrDefaultAsync(v => v.Id == vacancyId && v.TeamId == teamId);
        if (vacancy is null) return NotFound(new { message = "Вакансия не найдена" });

        _db.TeamVacancies.Remove(vacancy);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("{teamId:int}/invites")]
    public async Task<IActionResult> SendInvite(int teamId, [FromBody] CreateInviteRequest request)
    {
        var userId = User.GetUserId();
        if (userId is null) return Unauthorized(new { message = "Требуется вход" });

        var team = await _db.Teams.FirstOrDefaultAsync(t => t.Id == teamId);
        if (team is null) return NotFound(new { message = "Команда не найдена" });
        if (team.CaptainUserId != userId.Value) return StatusCode(403, new { message = "Недостаточно прав" });

        var userToInvite = await _db.Users.FirstOrDefaultAsync(u => u.Id == request.UserId);
        if (userToInvite is null) return NotFound(new { message = "Пользователь не найден" });

        var existingInvite = await _db.TeamInvites.FirstOrDefaultAsync(i => i.TeamId == teamId && i.UserId == request.UserId && i.Status == "pending");
        if (existingInvite != null) return Conflict(new { message = "Приглашение уже отправлено" });

        var invite = new TeamInvite
        {
            TeamId = teamId,
            UserId = request.UserId,
            Status = "pending"
        };

        _db.TeamInvites.Add(invite);
        await _db.SaveChangesAsync();

        return Ok(new { id = invite.Id, status = invite.Status });
    }

    [HttpPost("invites/{inviteId:int}/respond")]
    public async Task<IActionResult> RespondInvite(int inviteId, [FromBody] RespondInviteRequest request)
    {
        var userId = User.GetUserId();
        if (userId is null) return Unauthorized(new { message = "Требуется вход" });

        var invite = await _db.TeamInvites.Include(i => i.Team).FirstOrDefaultAsync(i => i.Id == inviteId);
        if (invite is null) return NotFound(new { message = "Приглашение не найдено" });
        if (invite.UserId != userId.Value) return StatusCode(403, new { message = "Недостаточно прав" });
        if (invite.Status != "pending") return BadRequest(new { message = "На это приглашение уже был дан ответ" });

        var action = request.Action.Trim().ToLowerInvariant();
        if (action == "accept")
        {
            invite.Status = "accepted";
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId.Value);
            if (user != null)
            {
                // Add the user to the team
                var player = new TeamPlayer
                {
                    TeamId = invite.TeamId,
                    Nickname = user.Nickname,
                    Rating = user.Rating,
                    RatingSource = user.RatingProvider ?? "manual",
                    RatingStatus = user.RatingVerified ? "confirmed" : "pending",
                    ExternalProfileUrl = user.RatingProfileUrl
                };
                _db.TeamPlayers.Add(player);
                await _activity.LogAsync("player_joined", $"Игрок {player.Nickname} принял приглашение в команду «{invite.Team?.Name}»");
            }
        }
        else if (action == "decline")
        {
            invite.Status = "declined";
        }
        else
        {
            return BadRequest(new { message = "Неизвестное действие (accept/decline)" });
        }

        await _db.SaveChangesAsync();
        return Ok(new { message = action == "accept" ? "Приглашение принято" : "Приглашение отклонено" });
    }
}
