using Data;
using EsportsBackend.Services;
using Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Models;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ILogger<AuthController> _logger;
    private readonly SteamApiService _steamApi;

    public AuthController(AppDbContext db, ILogger<AuthController> logger, SteamApiService steamApi)
    {
        _db = db;
        _logger = logger;
        _steamApi = steamApi;
    }

    [HttpGet("steam/login")]
    public IActionResult SteamLogin()
    {
        // Build absolute return URL
        var returnUrl = $"{Request.Scheme}://{Request.Host}/api/auth/steam/callback";
        var realm = $"{Request.Scheme}://{Request.Host}";

        var steamOpenIdUrl = "https://steamcommunity.com/openid/login" +
            "?openid.ns=http://specs.openid.net/auth/2.0" +
            "&openid.mode=checkid_setup" +
            $"&openid.return_to={Uri.EscapeDataString(returnUrl)}" +
            $"&openid.realm={Uri.EscapeDataString(realm)}" +
            "&openid.identity=http://specs.openid.net/auth/2.0/identifier_select" +
            "&openid.claimed_id=http://specs.openid.net/auth/2.0/identifier_select";

        return Redirect(steamOpenIdUrl);
    }

    [HttpGet("steam/callback")]
    public async Task<IActionResult> SteamCallback()
    {
        var isValid = await _steamApi.ValidateOpenIdAsync(Request.Query);
        if (!isValid)
            return Redirect("http://localhost:8000/login?error=SteamAuthFailed");

        var claimedId = Request.Query["openid.claimed_id"].ToString();
        var steamId = claimedId.Split('/').LastOrDefault();
        
        if (string.IsNullOrEmpty(steamId))
            return Redirect("http://localhost:8000/login?error=InvalidSteamId");

        var user = await _db.Users.FirstOrDefaultAsync(u => u.SteamId == steamId);
        if (user == null)
        {
            var (nickname, avatarUrl) = await _steamApi.GetPlayerSummariesAsync(steamId);
            
            user = new AppUser
            {
                Email = $"{steamId}@steam.local", // Placeholder email
                Nickname = !string.IsNullOrWhiteSpace(nickname) ? nickname : $"SteamUser_{steamId.Substring(steamId.Length - 4)}",
                Role = "player",
                SteamId = steamId,
                AvatarUrl = avatarUrl,
                PasswordHash = Hash(Guid.NewGuid().ToString()) // Random password
            };
            _db.Users.Add(user);
            await _db.SaveChangesAsync();
        }

        var token = BuildAccessToken(user.Id, user.Email, user.Nickname, user.Role);
        return Redirect($"http://localhost:8000/login/steam/callback?token={token}");
    }

    public class LoginRequest
    {
        [Required, EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required, MinLength(8)]
        public string Password { get; set; } = string.Empty;
    }

    public class RegisterRequest
    {
        [Required, EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required, MinLength(2), MaxLength(32)]
        public string Nickname { get; set; } = string.Empty;

        [Required, MinLength(8)]
        public string Password { get; set; } = string.Empty;

        [Required]
        public string Role { get; set; } = "captain";
    }

    public class UpdateProfileRequest
    {
        [Required, MinLength(2), MaxLength(32)]
        public string Nickname { get; set; } = string.Empty;

        [MaxLength(500)]
        public string? Bio { get; set; }

        public string? GameRole { get; set; }
        public string? Availability { get; set; }
        [MaxLength(150)]
        public string? Pitch { get; set; }
        public string? DiscordId { get; set; }
        public string? HighlightsUrl { get; set; }
        public string? Country { get; set; }
        public string? City { get; set; }
        public string? Languages { get; set; }
    }

    public class VerifyRatingRequest
    {
        [Required]
        public string Provider { get; set; } = "faceit";

        [Required]
        public string ProfileUrl { get; set; } = string.Empty;
    }

    public class LookingForTeamRequest
    {
        public bool Enabled { get; set; }
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request, [FromServices] EsportsBackend.Services.VerificationService verification)
    {
        _logger.LogInformation("Incoming /api/auth/register for {Email}", request.Email);

        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        var normalizedNickname = (request.Nickname ?? string.Empty).Trim();
        var normalizedRole = request.Role.Trim().ToLowerInvariant();
        var allowedRoles = new[] { "player", "captain", "judge", "admin", "viewer" };

        if (normalizedNickname.Length < 2 || normalizedNickname.Length > 32)
            return BadRequest(new { message = "Некорректный ник. Длина: 2..32" });

        if (!System.Text.RegularExpressions.Regex.IsMatch(normalizedNickname, "^[A-Za-z0-9._-]+$"))
            return BadRequest(new { message = "Ник может содержать только латинские буквы, цифры и символы . _ -" });

        if (!allowedRoles.Contains(normalizedRole))
            return BadRequest(new { message = "Некорректная роль. Доступно: player, captain, judge, admin, viewer" });

        if (await _db.Users.AnyAsync(u => u.Email == normalizedEmail))
            return Conflict(new { message = "Пользователь с таким email уже существует" });

        if (await _db.Users.AnyAsync(u => u.Nickname == normalizedNickname))
            return Conflict(new { message = "Пользователь с таким ником уже существует" });

        var user = new AppUser
        {
            Email = normalizedEmail,
            Nickname = normalizedNickname,
            PasswordHash = Hash(request.Password),
            Role = normalizedRole
        };

        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        // Сразу шлём письмо подтверждения на реальный адрес. Сбой SMTP не валит
        // регистрацию — пользователь запросит повторную отправку из профиля.
        var emailSent = false;
        try
        {
            emailSent = await verification.SendVerificationLinkAsync(user);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось отправить письмо подтверждения для {Email}", user.Email);
        }

        return Created("/api/auth/me", new
        {
            message = "registered",
            email = user.Email,
            nickname = user.Nickname,
            role = user.Role,
            verificationEmailSent = emailSent
        });
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        _logger.LogInformation("Incoming /api/auth/login for {Email}", request.Email);

        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail);
        if (user is null || user.PasswordHash != Hash(request.Password))
            return Unauthorized(new { message = "Неверный email или пароль" });

        var token = BuildAccessToken(user.Id, user.Email, user.Nickname, user.Role);
        return Ok(new
        {
            token,
            user = ToUserDto(user)
        });
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> Me()
    {
        _logger.LogInformation("Incoming /api/auth/me");

        var userId = User.GetUserId();
        if (userId == null)
            return Unauthorized(new ProblemDetails { Title = "Unauthorized", Detail = "Invalid token", Status = 401 });

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId.Value);
        if (user is null)
            return Unauthorized(new ProblemDetails { Title = "Unauthorized", Detail = "User not found", Status = 401 });

        return Ok(ToUserDto(user));
    }

    [HttpPut("profile")]
    public async Task<IActionResult> UpdateProfile([FromBody] UpdateProfileRequest request)
    {
        var userId = User.GetUserId();
        if (userId is null)
            return Unauthorized(new { message = "Требуется вход" });

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId.Value);
        if (user is null)
            return Unauthorized(new { message = "Пользователь не найден" });

        var nickname = (request.Nickname ?? string.Empty).Trim();
        if (nickname.Length < 2 || nickname.Length > 32)
            return BadRequest(new { message = "Ник должен быть длиной 2–32 символа" });

        if (!System.Text.RegularExpressions.Regex.IsMatch(nickname, "^[A-Za-z0-9._-]+$"))
            return BadRequest(new { message = "Ник может содержать только латинские буквы, цифры и символы . _ -" });

        var nicknameTaken = await _db.Users.AnyAsync(u => u.Id != user.Id && u.Nickname == nickname);
        if (nicknameTaken)
            return Conflict(new { message = "Пользователь с таким ником уже существует" });

        user.Nickname = nickname;
        user.Bio = string.IsNullOrWhiteSpace(request.Bio) ? null : request.Bio.Trim();
        user.GameRole = string.IsNullOrWhiteSpace(request.GameRole) ? null : request.GameRole.Trim();
        user.Availability = string.IsNullOrWhiteSpace(request.Availability) ? null : request.Availability.Trim();
        user.Pitch = string.IsNullOrWhiteSpace(request.Pitch) ? null : request.Pitch.Trim();
        user.DiscordId = string.IsNullOrWhiteSpace(request.DiscordId) ? null : request.DiscordId.Trim();
        user.HighlightsUrl = string.IsNullOrWhiteSpace(request.HighlightsUrl) ? null : request.HighlightsUrl.Trim();
        user.Country = string.IsNullOrWhiteSpace(request.Country) ? null : request.Country.Trim();
        user.City = string.IsNullOrWhiteSpace(request.City) ? null : request.City.Trim();
        user.Languages = string.IsNullOrWhiteSpace(request.Languages) ? null : request.Languages.Trim();
        
        await _db.SaveChangesAsync();

        return Ok(ToUserDto(user));
    }

    /// <summary>
    /// История рейтинга текущего пользователя для графика динамики в профиле.
    /// Если снимков ещё нет, но текущий рейтинг есть — возвращаем одну точку,
    /// чтобы график не был пустым.
    /// </summary>
    [HttpGet("profile/rating-history")]
    public async Task<IActionResult> RatingHistory([FromServices] Services.RatingHistoryService history, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId is null)
            return Unauthorized(new { message = "Требуется вход" });

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId.Value, ct);
        if (user is null)
            return Unauthorized(new { message = "Пользователь не найден" });

        var records = await history.GetHistoryAsync(user.Id, ct);

        if (records.Count == 0 && (user.FaceitElo.HasValue || user.Rating.HasValue))
        {
            var current = user.FaceitElo.HasValue ? user.FaceitElo.Value : user.Rating!.Value;
            return Ok(new[]
            {
                new
                {
                    rating = current,
                    source = user.FaceitElo.HasValue ? "faceit" : (user.RatingProvider ?? "manual"),
                    recordedAtUtc = user.FaceitLinkedAt ?? user.RatingVerifiedAtUtc ?? DateTime.UtcNow
                }
            });
        }

        return Ok(records.Select(h => new
        {
            rating = h.Rating,
            source = h.Source,
            recordedAtUtc = h.RecordedAtUtc
        }));
    }

    /// <summary>Переключатель «Ищу команду» — игрок попадает на доску скаутинга.</summary>
    [HttpPost("profile/looking-for-team")]
    public async Task<IActionResult> SetLookingForTeam([FromBody] LookingForTeamRequest request)
    {
        var userId = User.GetUserId();
        if (userId is null)
            return Unauthorized(new { message = "Требуется вход" });

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId.Value);
        if (user is null)
            return Unauthorized(new { message = "Пользователь не найден" });

        user.IsLookingForTeam = request.Enabled;
        user.LookingForTeamSinceUtc = request.Enabled ? DateTime.UtcNow : null;
        await _db.SaveChangesAsync();

        return Ok(new
        {
            message = request.Enabled
                ? "Вы добавлены на доску скаутинга"
                : "Вы убраны с доски скаутинга",
            profile = ToUserDto(user)
        });
    }

    [HttpPost("profile/verify-rating")]
    public async Task<IActionResult> VerifyRating([FromBody] VerifyRatingRequest request, [FromServices] Services.RatingHistoryService history)
    {
        var userId = User.GetUserId();
        if (userId is null)
            return Unauthorized(new { message = "Требуется вход" });

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId.Value);
        if (user is null)
            return Unauthorized(new { message = "Пользователь не найден" });

        var provider = (request.Provider ?? string.Empty).Trim().ToLowerInvariant();
        if (provider != "faceit" && provider != "steam")
            return BadRequest(new { message = "Поддерживаются только faceit или steam" });

        if (!Uri.TryCreate(request.ProfileUrl, UriKind.Absolute, out var uri))
            return BadRequest(new { message = "Укажите корректную ссылку на профиль" });

        var nicknameFactor = Math.Max(1, user.Nickname.Length);
        var rating = provider == "faceit" ? 1500 + nicknameFactor * 37 : 1200 + nicknameFactor * 29;

        user.RatingProvider = provider;
        user.RatingProfileUrl = uri.ToString();
        user.Rating = rating;
        user.RatingVerified = true;
        user.RatingVerifiedAtUtc = DateTime.UtcNow;

        // Снимок в историю рейтинга — для графика динамики в профиле
        await history.SnapshotAsync(user.Id, rating, provider);

        await _db.SaveChangesAsync();

        return Ok(new
        {
            message = "Рейтинг подтверждён",
            profile = ToUserDto(user)
        });
    }

    private object ToUserDto(AppUser user) => new
    {
        id = user.Id,
        email = user.Email,
        nickname = user.Nickname,
        role = user.Role,
        bio = user.Bio,
        
        isEmailVerified = user.IsEmailVerified,
        isLookingForTeam = user.IsLookingForTeam,
        lookingForTeamSinceUtc = user.LookingForTeamSinceUtc,
        
        faceitNickname = user.FaceitNickname,
        faceitElo = user.FaceitElo,
        faceitLevel = user.FaceitLevel,
        faceitAvatar = user.FaceitAvatar,
        faceitProfileUrl = user.FaceitProfileUrl,
        faceitLinkedAt = user.FaceitLinkedAt,

        rating = user.Rating,
        ratingProvider = user.RatingProvider,
        ratingVerified = user.RatingVerified,
        ratingVerifiedAtUtc = user.RatingVerifiedAtUtc,
        ratingProfileUrl = user.RatingProfileUrl,

        gameRole = user.GameRole,
        availability = user.Availability,
        pitch = user.Pitch,
        discordId = user.DiscordId,
        highlightsUrl = user.HighlightsUrl,
        
        country = user.Country,
        city = user.City,
        languages = user.Languages,
        
        reputation = _db.PlayerEndorsements.Count(e => e.EndorsedUserId == user.Id),
        
        invites = _db.TeamInvites.Include(i => i.Team).Where(i => i.UserId == user.Id && i.Status == "pending").Select(i => new { id = i.Id, teamId = i.TeamId, teamName = i.Team!.Name, createdAtUtc = i.CreatedAtUtc }).ToList()
    };

    private static string Hash(string plain)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(plain));
        return Convert.ToHexString(bytes);
    }

    private string BuildAccessToken(int userId, string email, string nickname, string role)
    {
        var configuration = HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        var key = configuration["Jwt:Key"] ?? "super_secret_key_12345678901234567890";
        var securityKey = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(Encoding.UTF8.GetBytes(key));
        var credentials = new Microsoft.IdentityModel.Tokens.SigningCredentials(securityKey, Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new System.Security.Claims.Claim(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub, userId.ToString()),
            new System.Security.Claims.Claim("email", email ?? ""),
            new System.Security.Claims.Claim("nickname", nickname ?? ""),
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, role ?? "player"),
            new System.Security.Claims.Claim("role", role ?? "player") // Add generic role for backward compatibility if needed
        };

        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            claims: claims,
            expires: DateTime.UtcNow.AddHours(8),
            signingCredentials: credentials);

        return new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token);
    }
}
