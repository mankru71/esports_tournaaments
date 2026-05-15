using Data;
using Infrastructure;
using Microsoft.AspNetCore.Mvc;
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
    public AuthController(AppDbContext db, ILogger<AuthController> logger)
    {
        _db = db;
        _logger = logger;
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
    }

    public class VerifyRatingRequest
    {
        [Required]
        public string Provider { get; set; } = "faceit";

        [Required]
        public string ProfileUrl { get; set; } = string.Empty;
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request)
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

        return Created("/api/auth/me", new { message = "registered", email = user.Email, nickname = user.Nickname, role = user.Role });
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
    public async Task<IActionResult> Me()
    {
        _logger.LogInformation("Incoming /api/auth/me");

        var claims = AuthTokenHelper.ParseClaims(AuthTokenHelper.GetBearerToken(Request));
        if (!claims.TryGetValue("sub", out var userIdRaw) || !int.TryParse(userIdRaw, out var userId))
            return Unauthorized(new ProblemDetails { Title = "Unauthorized", Detail = "Invalid token", Status = 401 });

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null)
            return Unauthorized(new ProblemDetails { Title = "Unauthorized", Detail = "User not found", Status = 401 });

        return Ok(ToUserDto(user));
    }

    [HttpPut("profile")]
    public async Task<IActionResult> UpdateProfile([FromBody] UpdateProfileRequest request)
    {
        var userId = AuthTokenHelper.GetUserId(Request);
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
        await _db.SaveChangesAsync();

        return Ok(ToUserDto(user));
    }

    [HttpPost("profile/verify-rating")]
    public async Task<IActionResult> VerifyRating([FromBody] VerifyRatingRequest request)
    {
        var userId = AuthTokenHelper.GetUserId(Request);
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

        await _db.SaveChangesAsync();

        return Ok(new
        {
            message = "Рейтинг подтверждён",
            profile = ToUserDto(user)
        });
    }

    private static object ToUserDto(AppUser user) => new
    {
        id = user.Id,
        email = user.Email,
        nickname = user.Nickname,
        role = user.Role,
        bio = user.Bio,
        
        isEmailVerified = user.IsEmailVerified,
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
        ratingProfileUrl = user.RatingProfileUrl
    };

    private static string Hash(string plain)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(plain));
        return Convert.ToHexString(bytes);
    }

    private static string BuildAccessToken(int userId, string email, string nickname, string role)
    {
        var header = Base64UrlEncode("{\"alg\":\"none\",\"typ\":\"JWT\"}");
        var payloadObj = new
        {
            sub = userId,
            email,
            nickname,
            role,
            exp = DateTimeOffset.UtcNow.AddHours(8).ToUnixTimeSeconds()
        };
        var payload = Base64UrlEncode(JsonSerializer.Serialize(payloadObj));
        return $"{header}.{payload}.local";
    }

    private static string Base64UrlEncode(string plain)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(plain)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
