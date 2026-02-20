using Data;
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

        [Required, MinLength(6)]
        public string Password { get; set; } = string.Empty;
    }

    public class RegisterRequest
    {
        [Required, EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required, MinLength(6)]
        public string Password { get; set; } = string.Empty;

        [Required]
        public string Role { get; set; } = "captain";
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request)
    {
        _logger.LogInformation("Incoming /api/auth/register for {Email}", request.Email);

        var exists = await _db.Users.AnyAsync(u => u.Email == request.Email);
        if (exists)
        {
            return Conflict(new { message = "Пользователь с таким email уже существует" });
        }

        var user = new AppUser
        {
            Email = request.Email.ToLowerInvariant(),
            PasswordHash = Hash(request.Password),
            Role = request.Role
        };

        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        return Ok(new { message = "registered", email = user.Email, role = user.Role });
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        _logger.LogInformation("Incoming /api/auth/login for {Email}", request.Email);

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == request.Email.ToLower());
        if (user is null || user.PasswordHash != Hash(request.Password))
        {
            return Unauthorized(new { message = "Неверный email или пароль" });
        }

        var token = BuildDemoToken(user.Id, user.Email, user.Role);
        return Ok(new
        {
            token,
            user = new { email = user.Email, role = user.Role }
        });
    }

    [HttpGet("me")]
    public async Task<IActionResult> Me()
    {
        _logger.LogInformation("Incoming /api/auth/me");
        var auth = Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(auth) || !auth.StartsWith("Bearer "))
        {
            return Unauthorized(new ProblemDetails { Title = "Unauthorized", Detail = "Missing bearer token", Status = 401 });
        }

        var token = auth.Replace("Bearer ", "");
        var claims = ParsePayload(token);
        if (!claims.TryGetValue("sub", out var userIdRaw) || !int.TryParse(userIdRaw, out var userId))
        {
            return Unauthorized(new ProblemDetails { Title = "Unauthorized", Detail = "Invalid token", Status = 401 });
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null)
        {
            return Unauthorized(new ProblemDetails { Title = "Unauthorized", Detail = "User not found", Status = 401 });
        }

        return Ok(new { email = user.Email, role = user.Role });
    }

    private static string Hash(string plain)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(plain));
        return Convert.ToHexString(bytes);
    }

    private static string BuildDemoToken(int userId, string email, string role)
    {
        var header = Base64UrlEncode("{\"alg\":\"none\",\"typ\":\"JWT\"}");
        var payloadObj = new { sub = userId, email, role, exp = DateTimeOffset.UtcNow.AddHours(8).ToUnixTimeSeconds() };
        var payload = Base64UrlEncode(JsonSerializer.Serialize(payloadObj));
        return $"{header}.{payload}.demo";
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

    private static string Base64UrlEncode(string plain)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(plain)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
