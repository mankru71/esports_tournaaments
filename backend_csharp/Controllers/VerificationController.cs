using Data;
using EsportsBackend.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Controllers;

[ApiController]
[Route("api/verification")]
public class VerificationController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly VerificationService _verification;

    public VerificationController(AppDbContext db, VerificationService verification)
    {
        _db = db;
        _verification = verification;
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

        var sent = await _verification.SendVerificationLinkAsync(user);
        // Раньше при сбое SMTP пользователю всё равно говорили «отправлено» —
        // теперь честно сообщаем, что ссылку нужно искать в логах (демо-режим)
        return sent
            ? Ok(new { message = "Письмо отправлено" })
            : Ok(new { message = "SMTP недоступен: ссылка подтверждения выведена в лог сервера" });
    }

    /// <summary>
    /// Подтверждение по одному токену (ссылка из письма /verify-email/?token=XYZ).
    /// Пользователь находится по самому токену — userId в ссылке не нужен.
    /// </summary>
    [HttpPost("confirm")]
    public async Task<IActionResult> ConfirmByToken([FromBody] ConfirmRequest request)
    {
        var token = request.Token?.Trim();
        if (string.IsNullOrWhiteSpace(token))
            return BadRequest(new { message = "Токен недействителен или устарел" });

        var user = await _db.Users.FirstOrDefaultAsync(u => u.EmailVerificationToken == token);
        if (user is null
            || user.EmailVerificationTokenExpiry is null
            || user.EmailVerificationTokenExpiry < DateTime.UtcNow)
        {
            return BadRequest(new { message = "Токен недействителен или устарел" });
        }

        user.IsEmailVerified = true;
        user.EmailVerificationToken = null;
        user.EmailVerificationTokenExpiry = null;
        await _db.SaveChangesAsync();

        return Ok(new { message = "Email подтверждён", email = user.Email });
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
