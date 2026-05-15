using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace EsportsBackend.Services;

public class EmailService
{
    private readonly IConfiguration _config;

    public EmailService(IConfiguration config)
    {
        _config = config;
    }

    public async Task SendEmailAsync(string toEmail, string subject, string body)
    {
        var host = _config["SMTP_HOST"];
        var user = _config["SMTP_USER"];
        var pass = _config["SMTP_PASS"];
        var rawPort = _config["SMTP_PORT"];
        var port = int.TryParse(rawPort, out var parsedPort) ? parsedPort : 587;

        Console.WriteLine("
=======================================================");
        Console.WriteLine($"[EMAIL LOG] Письмо для: {toEmail}");
        Console.WriteLine($"[SUBJECT] {subject}");
        var plainLink = body.Contains("href='")
            ? body.Split("href='")[1].Split("'")[0]
            : "Ссылка не найдена в теле письма";
        Console.WriteLine($"[URL] {plainLink}");
        Console.WriteLine("=======================================================
");

        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
        {
            Console.WriteLine(">>> SMTP не настроен. Отправка отменена, используйте ссылку выше.");
            return;
        }

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Esports Tournaments", user));
        message.To.Add(new MailboxAddress(string.Empty, toEmail));
        message.Subject = subject;
        message.Body = new BodyBuilder { HtmlBody = body }.ToMessageBody();

        using var client = new SmtpClient();
        try
        {
            await client.ConnectAsync(host, port, SecureSocketOptions.StartTls);
            await client.AuthenticateAsync(user, pass);
            await client.SendAsync(message);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] ОШИБКА ОТПРАВКИ ПОЧТЫ: {ex.Message}");
        }
        finally
        {
            if (client.IsConnected)
                await client.DisconnectAsync(true);
        }
    }
}
