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

        var token = BuildDemoToken(user.Id, user.Email, user.Nickname, user.Role);
        return Ok(new
        {
            token,
            user = new { email = user.Email, nickname = user.Nickname, role = user.Role }
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

        return Ok(new { email = user.Email, nickname = user.Nickname, role = user.Role });
    }

    private static string Hash(string plain)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(plain));
        return Convert.ToHexString(bytes);
    }

    private static string BuildDemoToken(int userId, string email, string nickname, string role)
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
        return $"{header}.{payload}.demo";
    }

    private static string Base64UrlEncode(string plain)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(plain)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
