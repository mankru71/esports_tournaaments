using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using MimeKit;

namespace EsportsBackend.Services;

/// <summary>
/// Отправка почты через SMTP (MailKit, STARTTLS на 587-м порту).
/// Рассчитано на Gmail с App Password:
///   SMTP_HOST=smtp.gmail.com, SMTP_PORT=587,
///   SMTP_USER=адрес@gmail.com, SMTP_PASS=16-значный App Password.
/// Возвращает успех/провал — вызывающий код решает, что сказать пользователю.
/// Без настроенного SMTP письмо не уходит, ссылка печатается в лог (демо-режим).
/// </summary>
public class EmailService
{
    private readonly IConfiguration _config;

    public EmailService(IConfiguration config)
    {
        _config = config;
    }

    public async Task<bool> SendEmailAsync(string toEmail, string subject, string body)
    {
        Console.WriteLine("\n=======================================================");
        Console.WriteLine($"[EMAIL] To: {toEmail}");
        Console.WriteLine($"[EMAIL] Subject: {subject}");
        Console.WriteLine($"[EMAIL] Link: {ExtractFirstHref(body)}");
        Console.WriteLine("=======================================================\n");

        var host = _config["SMTP_HOST"];
        var port = int.TryParse(_config["SMTP_PORT"], out var parsedPort) ? parsedPort : 587;
        var user = _config["SMTP_USER"];
        var pass = _config["SMTP_PASS"];

        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
        {
            Console.WriteLine(">>> SMTP не настроен. Письмо не отправлено, ссылка выведена в лог.");
            return false;
        }

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Arena Control", user));
        message.To.Add(MailboxAddress.Parse(toEmail));
        message.Subject = subject;
        message.Body = new BodyBuilder { HtmlBody = body }.ToMessageBody();

        using var client = new SmtpClient();
        client.Timeout = 15000; // Gmail отвечает быстро; не подвешиваем запрос
        try
        {
            // 587 + STARTTLS — стандартная конфигурация Gmail (аналог EnableSsl=true)
            await client.ConnectAsync(host, port, SecureSocketOptions.StartTls);
            await client.AuthenticateAsync(user, pass);
            await client.SendAsync(message);
            Console.WriteLine($"[EMAIL] Отправлено: {toEmail}");
            return true;
        }
        catch (MailKit.Security.AuthenticationException ex)
        {
            // Типичная ошибка Gmail 535-5.7.8: неверный App Password или
            // не включена двухэтапная аутентификация
            Console.WriteLine($"[EMAIL ERROR] Ошибка аутентификации SMTP ({user}): {ex.Message}");
            Console.WriteLine(">>> Проверьте App Password (без пробелов) и что в Google включена 2FA.");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[EMAIL ERROR] {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        finally
        {
            if (client.IsConnected)
                await client.DisconnectAsync(true);
        }
    }

    private static string ExtractFirstHref(string body)
    {
        const string marker = "href='";
        var start = body.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return "—";
        start += marker.Length;
        var end = body.IndexOf("'", start, StringComparison.OrdinalIgnoreCase);
        return end > start ? body[start..end] : "—";
    }
}
