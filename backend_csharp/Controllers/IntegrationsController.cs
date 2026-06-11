using Data;
using Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Services;
using EsportsBackend.Services;
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

    [HttpGet("faceit/oauth/url")]
    public IActionResult GetFaceitOAuthUrl([FromQuery] string redirectUri)
    {
        var clientId = _faceit.ClientId;
        if (string.IsNullOrEmpty(clientId))
            return StatusCode(501, new { message = "Faceit OAuth не настроен сервером." });

        // Generate the Faceit authorization URL
        var state = Guid.NewGuid().ToString("N");
        var url = $"https://accounts.faceit.com/?response_type=code&client_id={clientId}&redirect_popup=true&redirect_uri={Uri.EscapeDataString(redirectUri)}&state={state}";

        return Ok(new { url, state });
    }

    public class FaceitOAuthVerifyRequest
    {
        [Required] public string Code { get; set; } = string.Empty;
        [Required] public string RedirectUri { get; set; } = string.Empty;
    }

    /// <summary>
    /// POST /api/integrations/faceit/oauth/verify/{userId}
    /// </summary>
    [HttpPost("faceit/oauth/verify/{userId:int}")]
    public async Task<IActionResult> VerifyFaceitOAuth(int userId, [FromBody] FaceitOAuthVerifyRequest request, [FromServices] RatingHistoryService history, CancellationToken ct)
    {
        var tokenUserId = User.GetUserId();
        if (tokenUserId is null)
            return Unauthorized(new { message = "Требуется вход" });
        if (tokenUserId.Value != userId)
            return Forbid();

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
            return NotFound(new { message = "Пользователь не найден" });

        FaceitPlayerInfo? playerInfo;
        try
        {
            playerInfo = await _faceit.VerifyOAuthCodeAsync(request.Code, request.RedirectUri, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to verify Faceit OAuth code for userId={UserId}", userId);
            return BadRequest(new { message = ex.Message });
        }

        if (playerInfo is null)
            return NotFound(new { message = "Игрок не найден на Faceit" });

        // ── Persist to DB ─────────────────────────────────────────────────
        user.FaceitNickname = playerInfo.Nickname;
        user.FaceitElo = playerInfo.Elo;
        user.FaceitLevel = playerInfo.Level;
        user.FaceitAvatar = playerInfo.Avatar;
        user.FaceitProfileUrl = playerInfo.FaceitUrl;
        user.FaceitLinkedAt = DateTime.UtcNow;

        user.RatingProvider = "faceit";
        user.RatingProfileUrl = playerInfo.FaceitUrl;
        user.Rating = playerInfo.Elo;
        user.RatingVerified = true;
        user.RatingVerifiedAtUtc = DateTime.UtcNow;

        if (playerInfo.Elo > 0)
            await history.SnapshotAsync(user.Id, playerInfo.Elo, "faceit", ct);

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Faceit linked via OAuth for userId={UserId}: nickname={Nickname}, elo={Elo}, level={Level}",
            userId, playerInfo.Nickname, playerInfo.Elo, playerInfo.Level);

        return Ok(new
        {
            message = $"Faceit-аккаунт «{playerInfo.Nickname}» успешно привязан",
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
        var tokenUserId = User.GetUserId();
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

    [HttpGet("steam/openid/url")]
    public IActionResult GetSteamOpenIdUrl([FromQuery] string redirectUri)
    {
        var steamOpenIdEndpoint = "https://steamcommunity.com/openid/login";
        
        var queryParams = new Dictionary<string, string>
        {
            { "openid.ns", "http://specs.openid.net/auth/2.0" },
            { "openid.mode", "checkid_setup" },
            { "openid.return_to", redirectUri },
            { "openid.realm", new Uri(redirectUri).GetComponents(UriComponents.SchemeAndServer, UriFormat.Unescaped) },
            { "openid.identity", "http://specs.openid.net/auth/2.0/identifier_select" },
            { "openid.claimed_id", "http://specs.openid.net/auth/2.0/identifier_select" }
        };

        var queryString = string.Join("&", queryParams.Select(kvp => $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));
        var url = $"{steamOpenIdEndpoint}?{queryString}";

        return Ok(new { url });
    }

    [HttpPost("steam/openid/verify/{userId:int}")]
    public async Task<IActionResult> VerifySteamOpenId(int userId, [FromBody] Dictionary<string, string> openIdParams, [FromServices] SteamApiService steam, CancellationToken ct)
    {
        var tokenUserId = User.GetUserId();
        if (tokenUserId is null || tokenUserId.Value != userId)
            return Unauthorized(new { message = "Требуется вход" });

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return NotFound(new { message = "Пользователь не найден" });

        var stringValuesDict = openIdParams.ToDictionary(k => k.Key, v => new Microsoft.Extensions.Primitives.StringValues(v.Value));
        var queryCollection = new QueryCollection(stringValuesDict);

        bool isValid = await steam.ValidateOpenIdAsync(queryCollection);
        if (!isValid) return BadRequest(new { message = "Невалидный ответ от Steam OpenID" });

        if (!openIdParams.TryGetValue("openid.claimed_id", out var claimedId))
            return BadRequest(new { message = "Отсутствует claimed_id" });

        var steamIdMatch = System.Text.RegularExpressions.Regex.Match(claimedId, @"https?://steamcommunity\.com/openid/id/(\d+)");
        if (!steamIdMatch.Success) return BadRequest(new { message = "Не удалось извлечь SteamID" });
        
        var steamId = steamIdMatch.Groups[1].Value;
        var (nickname, avatarUrl) = await steam.GetPlayerSummariesAsync(steamId);

        user.SteamId = steamId;
        if (!string.IsNullOrEmpty(avatarUrl)) user.AvatarUrl = avatarUrl;
        
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Steam linked via OpenID for userId={UserId}: steamId={SteamId}", userId, steamId);

        return Ok(new { message = "Steam-аккаунт успешно привязан", steamId, nickname, avatarUrl });
    }
}
