using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Data;   // Правильное пространство имен для AppDbContext
using Models; // Правильное пространство имен для AppUser
using EsportsBackend.Services;
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

    [HttpPost("send/{userId}")]
    public async Task<IActionResult> SendVerificationEmail(int userId)
    {
        var user = await _db.Users.FindAsync(userId);
        if (user == null) return NotFound(new { message = "Пользователь не найден" });
        if (user.IsEmailVerified) return BadRequest(new { message = "Почта уже подтверждена" });

        // Генерируем токен и даем ему 24 часа жизни
        user.EmailVerificationToken = Guid.NewGuid().ToString();
        user.EmailVerificationTokenExpiry = DateTime.UtcNow.AddHours(24);
        await _db.SaveChangesAsync();

        // Ссылка ведет на твой Django фронтенд
        var frontendUrl = _config["PUBLIC_FRONTEND_URL"] ?? "http://localhost:8000";
        var link = $"{frontendUrl}/verify-email?userId={user.Id}&token={user.EmailVerificationToken}";

        var emailBody = $@"
        <h3>Подтверждение почты</h3>
        <p>Привет, {user.Nickname}!</p> 
        <p>Для подтверждения почты на платформе Esports Tournaments перейди по ссылке ниже:</p>
        <a href='{link}' style='display:inline-block; padding:10px 20px; background:#ff5500; color:#fff; text-decoration:none; border-radius:5px;'>Подтвердить Email</a>
        <p>Ссылка действительна 24 часа.</p>";

        await _emailService.SendEmailAsync(user.Email, "Подтверждение регистрации", emailBody);

        return Ok(new { message = "Письмо с инструкциями отправлено на почту." });
    }

    public class ConfirmRequest { public string Token { get; set; } = string.Empty; }

    [HttpPost("confirm/{userId}")]
    public async Task<IActionResult> ConfirmEmail(int userId, [FromBody] ConfirmRequest req)
    {
        var user = await _db.Users.FindAsync(userId);
        if (user == null) return NotFound(new { message = "Пользователь не найден" });

        if (user.EmailVerificationToken != req.Token || user.EmailVerificationTokenExpiry < DateTime.UtcNow)
            return BadRequest(new { message = "Токен недействителен или устарел" });

        user.IsEmailVerified = true;
        user.EmailVerificationToken = null;
        user.EmailVerificationTokenExpiry = null;
        await _db.SaveChangesAsync();

        return Ok(new { message = "Email успешно подтвержден!" });
    }
}