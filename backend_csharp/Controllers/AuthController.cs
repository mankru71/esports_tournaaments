using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json;

namespace Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
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
    public IActionResult Register([FromBody] RegisterRequest request)
    {
        return Ok(new { message = "registered", email = request.Email, role = request.Role });
    }

    [HttpPost("login")]
    public IActionResult Login([FromBody] LoginRequest request)
    {
        var role = request.Email.Contains("admin") ? "admin" : request.Email.Contains("judge") ? "judge" : "captain";
        var token = BuildDemoToken(request.Email, role);
        return Ok(new
        {
            token,
            user = new { email = request.Email, role }
        });
    }

    [HttpGet("me")]
    public IActionResult Me()
    {
        var auth = Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(auth) || !auth.StartsWith("Bearer "))
        {
            return Unauthorized(new ProblemDetails { Title = "Unauthorized", Detail = "Missing bearer token", Status = 401 });
        }

        var token = auth.Replace("Bearer ", "");
        var claims = ParsePayload(token);
        if (!claims.TryGetValue("email", out var email) || !claims.TryGetValue("role", out var role))
        {
            return Unauthorized(new ProblemDetails { Title = "Unauthorized", Detail = "Invalid token", Status = 401 });
        }

        return Ok(new { email, role });
    }

    private static string BuildDemoToken(string email, string role)
    {
        var header = Base64UrlEncode("{\"alg\":\"none\",\"typ\":\"JWT\"}");
        var payloadObj = new { email, role, exp = DateTimeOffset.UtcNow.AddHours(8).ToUnixTimeSeconds() };
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
