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

    public TeamsController(AppDbContext db, PandaScoreService pandascore)
    {
        _db = db;
        _pandascore = pandascore;
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

    [HttpGet]
    public async Task<IActionResult> List()
    {
        var teams = await _db.Teams
            .Include(t => t.CaptainUser)
            .Include(t => t.Players)
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
            })
        }));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateTeamRequest request)
    {
        var userId = AuthTokenHelper.GetUserId(Request);
        if (userId is null) return Unauthorized(new { message = "Требуется вход" });

        var team = new Team
        {
            Name = request.Name.Trim(),
            CaptainUserId = userId.Value,
        };

        _db.Teams.Add(team);
        await _db.SaveChangesAsync();

        return Ok(new { id = team.Id, name = team.Name, captainUserId = team.CaptainUserId });
    }

    [HttpPost("{teamId:int}/players")]
    public async Task<IActionResult> AddPlayer(int teamId, [FromBody] AddPlayerRequest request, CancellationToken ct)
    {
        var userId = AuthTokenHelper.GetUserId(Request);
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
        if (!AuthTokenHelper.IsInAnyRole(Request, "admin", "judge"))
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
        var userId = AuthTokenHelper.GetUserId(Request);
        if (userId is null) return Unauthorized(new { message = "Требуется вход" });

        var team = await _db.Teams.FirstOrDefaultAsync(t => t.Id == teamId);
        if (team is null) return NotFound(new { message = "Команда не найдена" });
        if (team.CaptainUserId != userId.Value) return StatusCode(403, new { message = "Недостаточно прав" });

        var player = await _db.TeamPlayers.FirstOrDefaultAsync(p => p.Id == playerId && p.TeamId == teamId);
        if (player is null) return NotFound(new { message = "Игрок не найден" });

        _db.TeamPlayers.Remove(player);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{teamId:int}")]
    public async Task<IActionResult> DeleteTeam(int teamId)
    {
        var userId = AuthTokenHelper.GetUserId(Request);
        if (userId is null) return Unauthorized(new { message = "Требуется вход" });

        var team = await _db.Teams.Include(t => t.Players).FirstOrDefaultAsync(t => t.Id == teamId);
        if (team is null) return NotFound(new { message = "Команда не найдена" });
        if (team.CaptainUserId != userId.Value) return StatusCode(403, new { message = "Недостаточно прав" });

        _db.Teams.Remove(team);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
