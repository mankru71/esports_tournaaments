using Data;
using Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Services;
using System.ComponentModel.DataAnnotations;

namespace Controllers;

[ApiController]
[Route("api/integrations")]
public class IntegrationsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly FaceitApiService _faceit;
    private readonly ILogger<IntegrationsController> _logger;

    public IntegrationsController(AppDbContext db, FaceitApiService faceit, ILogger<IntegrationsController> logger)
    {
        _db = db;
        _faceit = faceit;
        _logger = logger;
    }

    public class FaceitVerifyRequest
    {
        [Required, MinLength(2), MaxLength(64)]
        public string FaceitNickname { get; set; } = string.Empty;
    }

    /// <summary>
    /// POST /api/integrations/faceit/verify/{userId}
    /// Verifies a Faceit nickname for the authenticated user.
    /// The caller must be the user themselves (userId must match token).
    /// </summary>
    [HttpPost("faceit/verify/{userId:int}")]
    public async Task<IActionResult> VerifyFaceit(int userId, [FromBody] FaceitVerifyRequest request, [FromServices] RatingHistoryService history, CancellationToken ct)
    {
        // Auth check — token owner must match the userId in the route
        var tokenUserId = AuthTokenHelper.GetUserId(Request);
        if (tokenUserId is null)
            return Unauthorized(new { message = "Требуется вход" });
        if (tokenUserId.Value != userId)
            return Forbid();

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
            return NotFound(new { message = "Пользователь не найден" });

        var nickname = (request.FaceitNickname ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(nickname))
            return BadRequest(new { message = "Укажите Faceit-ник" });

        // ── Fetch from Faceit API ──────────────────────────────────────────
        FaceitPlayerInfo? playerInfo;
        try
        {
            playerInfo = await _faceit.GetPlayerByNicknameAsync(nickname, ct);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Faceit API call failed for userId={UserId}", userId);
            return StatusCode(503, new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error fetching Faceit data for userId={UserId}", userId);
            return StatusCode(500, new { message = "Внутренняя ошибка при запросе к Faceit" });
        }

        if (playerInfo is null)
            return NotFound(new { message = $"Игрок с ником «{nickname}» не найден на Faceit" });

        // ── Persist to DB ─────────────────────────────────────────────────
        user.FaceitNickname = playerInfo.Nickname;
        user.FaceitElo = playerInfo.Elo;
        user.FaceitLevel = playerInfo.Level;
        user.FaceitAvatar = playerInfo.Avatar;
        user.FaceitProfileUrl = playerInfo.FaceitUrl;
        user.FaceitLinkedAt = DateTime.UtcNow;

        // Mirror to generic rating fields for backward compat
        user.RatingProvider = "faceit";
        user.RatingProfileUrl = playerInfo.FaceitUrl;
        user.Rating = playerInfo.Elo;
        user.RatingVerified = true;
        user.RatingVerifiedAtUtc = DateTime.UtcNow;

        // Снимок Elo в историю рейтинга — для графика динамики в профиле
        if (playerInfo.Elo > 0)
            await history.SnapshotAsync(user.Id, playerInfo.Elo, "faceit", ct);

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Faceit linked for userId={UserId}: nickname={Nickname}, elo={Elo}, level={Level}",
            userId, playerInfo.Nickname, playerInfo.Elo, playerInfo.Level);

        return Ok(new
        {
            message = $"Faceit-аккаунт «{playerInfo.Nickname}» привязан",
            faceit = new
            {
                nickname = playerInfo.Nickname,
                elo = playerInfo.Elo,
                level = playerInfo.Level,
                avatar = playerInfo.Avatar,
                profileUrl = playerInfo.FaceitUrl
            },
            profile = ToUserDto(user)
        });
    }

    /// <summary>
    /// DELETE /api/integrations/faceit/unlink/{userId}
    /// Unlinks the Faceit account for the user.
    /// </summary>
    [HttpDelete("faceit/unlink/{userId:int}")]
    public async Task<IActionResult> UnlinkFaceit(int userId, CancellationToken ct)
    {
        var tokenUserId = AuthTokenHelper.GetUserId(Request);
        if (tokenUserId is null) return Unauthorized(new { message = "Требуется вход" });
        if (tokenUserId.Value != userId) return Forbid();

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return NotFound(new { message = "Пользователь не найден" });

        user.FaceitNickname = null;
        user.FaceitElo = null;
        user.FaceitLevel = null;
        user.FaceitAvatar = null;
        user.FaceitProfileUrl = null;
        user.FaceitLinkedAt = null;

        if (user.RatingProvider == "faceit")
        {
            user.RatingProvider = null;
            user.RatingProfileUrl = null;
            user.Rating = null;
            user.RatingVerified = false;
            user.RatingVerifiedAtUtc = null;
        }

        await _db.SaveChangesAsync(ct);
        return Ok(new { message = "Faceit-аккаунт отвязан", profile = ToUserDto(user) });
    }

    private static object ToUserDto(Models.AppUser user) => new
    {
        id = user.Id,
        email = user.Email,
        nickname = user.Nickname,
        role = user.Role,
        bio = user.Bio,
        rating = user.Rating,
        ratingProvider = user.RatingProvider,
        ratingVerified = user.RatingVerified,
        ratingVerifiedAtUtc = user.RatingVerifiedAtUtc,
        ratingProfileUrl = user.RatingProfileUrl,
        faceitNickname = user.FaceitNickname,
        faceitElo = user.FaceitElo,
        faceitLevel = user.FaceitLevel,
        faceitAvatar = user.FaceitAvatar,
        faceitProfileUrl = user.FaceitProfileUrl
    };
}
