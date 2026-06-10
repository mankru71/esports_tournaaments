using Data;
using Microsoft.Extensions.Configuration;
using Models;
using System.Net;

namespace EsportsBackend.Services;

/// <summary>
/// Подтверждение почты: генерация токена и отправка письма со ссылкой
/// /verify-email/?token=XYZ. Общая точка для регистрации (авто-отправка)
/// и повторной отправки из профиля (VerificationController).
/// </summary>
public class VerificationService
{
    private readonly AppDbContext _db;
    private readonly EmailService _email;
    private readonly IConfiguration _config;

    public VerificationService(AppDbContext db, EmailService email, IConfiguration config)
    {
        _db = db;
        _email = email;
        _config = config;
    }

    /// <summary>
    /// Выписывает пользователю новый токен (24 часа), сохраняет и шлёт письмо.
    /// false — SMTP не настроен/недоступен (ссылка остаётся в логе бэкенда).
    /// </summary>
    public async Task<bool> SendVerificationLinkAsync(AppUser user, CancellationToken ct = default)
    {
        user.EmailVerificationToken = Guid.NewGuid().ToString("N");
        user.EmailVerificationTokenExpiry = DateTime.UtcNow.AddHours(24);
        await _db.SaveChangesAsync(ct);

        var frontendUrl = (_config["PUBLIC_FRONTEND_URL"] ?? "http://localhost").TrimEnd('/');
        var link = $"{frontendUrl}/verify-email/?token={Uri.EscapeDataString(user.EmailVerificationToken)}";
        var nickname = WebUtility.HtmlEncode(user.Nickname ?? user.Email);

        var emailBody = $@"
<h3>Подтверждение почты</h3>
<p>Привет, {nickname}!</p>
<p>Для подтверждения почты перейдите по ссылке ниже:</p>
<p><a href='{link}' style='display:inline-block;padding:10px 18px;background:#15181d;color:#ffffff;text-decoration:none;border-radius:8px;'>Подтвердить email</a></p>
<p>Ссылка действительна 24 часа. Если вы не регистрировались на Arena Control — просто проигнорируйте это письмо.</p>";

        return await _email.SendEmailAsync(user.Email, "Подтверждение регистрации — Arena Control", emailBody);
    }
}
