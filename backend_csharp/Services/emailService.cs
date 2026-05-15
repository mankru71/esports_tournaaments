using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
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
            return;
        }

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Esports Tournaments", user));
        message.To.Add(MailboxAddress.Parse(toEmail));
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
            Console.WriteLine($"[EMAIL ERROR] {ex.Message}");
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
