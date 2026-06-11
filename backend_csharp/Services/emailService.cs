using Microsoft.Extensions.Configuration;
using System.Net;
using System.Net.Mail;

namespace EsportsBackend.Services;

/// <summary>
/// Отправка почты через стандартный .NET SmtpClient (System.Net.Mail).
/// Рассчитано на Gmail с App Password:
///   SMTP_HOST=smtp.gmail.com, SMTP_PORT=587,
///   SMTP_USER=адрес@gmail.com, SMTP_PASS=16-значный App Password.
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

        try
        {
            using var client = new SmtpClient(host, port)
            {
                Credentials = new NetworkCredential(user, pass),
                EnableSsl = true,
                Timeout = 15000 // 15 seconds
            };

            var mailMessage = new MailMessage
            {
                From = new MailAddress(user, "Arena Control"),
                Subject = subject,
                Body = body,
                IsBodyHtml = true
            };
            mailMessage.To.Add(toEmail);

            await client.SendMailAsync(mailMessage);
            Console.WriteLine($"[EMAIL] Отправлено: {toEmail}");
            return true;
        }
        catch (SmtpException ex)
        {
            Console.WriteLine($"[EMAIL ERROR] Ошибка SMTP: {ex.Message}");
            Console.WriteLine(">>> Проверьте App Password (без пробелов) и что в Google включена 2FA.");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[EMAIL ERROR] {ex.GetType().Name}: {ex.Message}");
            return false;
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
