using Data;
using EsportsBackend.Services;
using Microsoft.AspNetCore.Mvc;
using System.Net;

namespace Controllers;

[ApiController]
[Route("api/verification")]
public class VerificationController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly EmailService _emailService;
    private readonly IConfiguration _config;

    public VerificationController(AppDbContext db, EmailService emailService, IConfiguration config)
    {
        _db = db;
        _emailService = emailService;
        _config = config;
    }

    public class ConfirmRequest
    {
        public string Token { get; set; } = string.Empty;
    }

    [HttpPost("send/{userId:int}")]
    public async Task<IActionResult> SendVerificationEmail(int userId)
    {
        var user = await _db.Users.FindAsync(userId);
        if (user is null)
            return NotFound(new { message = "Пользователь не найден" });
        if (user.IsEmailVerified)
            return BadRequest(new { message = "Почта уже подтверждена" });

        user.EmailVerificationToken = Guid.NewGuid().ToString("N");
        user.EmailVerificationTokenExpiry = DateTime.UtcNow.AddHours(24);
        await _db.SaveChangesAsync();

        var frontendUrl = (_config["PUBLIC_FRONTEND_URL"] ?? "http://localhost").TrimEnd('/');
        var link = $"{frontendUrl}/verify-email?userId={user.Id}&token={Uri.EscapeDataString(user.EmailVerificationToken)}";
        var nickname = WebUtility.HtmlEncode(user.Nickname ?? user.Email);

        var emailBody = $@"
<h3>Подтверждение почты</h3>
<p>Привет, {nickname}!</p>
<p>Для подтверждения почты перейдите по ссылке ниже:</p>
<p><a href='{link}' style='display:inline-block;padding:10px 18px;background:#15181d;color:#ffffff;text-decoration:none;border-radius:8px;'>Подтвердить email</a></p>
<p>Ссылка действительна 24 часа.</p>";

        await _emailService.SendEmailAsync(user.Email, "Подтверждение регистрации", emailBody);
        return Ok(new { message = "Письмо отправлено" });
    }

    [HttpPost("confirm/{userId:int}")]
    public async Task<IActionResult> ConfirmEmail(int userId, [FromBody] ConfirmRequest request)
    {
        var user = await _db.Users.FindAsync(userId);
        if (user is null)
            return NotFound(new { message = "Пользователь не найден" });

        var token = request.Token?.Trim();
        if (string.IsNullOrWhiteSpace(token)
            || user.EmailVerificationToken != token
            || user.EmailVerificationTokenExpiry is null
            || user.EmailVerificationTokenExpiry < DateTime.UtcNow)
        {
            return BadRequest(new { message = "Токен недействителен или устарел" });
        }

        user.IsEmailVerified = true;
        user.EmailVerificationToken = null;
        user.EmailVerificationTokenExpiry = null;
        await _db.SaveChangesAsync();

        return Ok(new { message = "Email подтверждён" });
    }
}
