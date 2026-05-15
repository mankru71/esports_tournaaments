using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace EsportsBackend.Services
{
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
            var port = int.Parse(_config["SMTP_PORT"] ?? "587");
            var user = _config["SMTP_USER"];
            var pass = _config["SMTP_PASS"];

            // Выводим ссылку в логи ПЕРЕД отправкой (как страховку)
            Console.WriteLine("\n=======================================================");
            Console.WriteLine($"[EMAIL LOG] Письмо для: {toEmail}");
            Console.WriteLine($"[SUBJECT] {subject}");
            // Очищаем body от HTML тегов для читаемости в консоли (простой regex или замена)
            var plainLink = body.Contains("href='") 
                ? body.Split("href='")[1].Split("'")[0] 
                : "Ссылка не найдена в теле письма";
            Console.WriteLine($"[URL] {plainLink}");
            Console.WriteLine("=======================================================\n");

            if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(user))
            {
                Console.WriteLine(">>> SMTP не настроен. Отправка отменена, используйте ссылку выше.");
                return;
            }

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress("Esports Tournaments", user));
            message.To.Add(new MailboxAddress("", toEmail));
            message.Subject = subject;

            var bodyBuilder = new BodyBuilder { HtmlBody = body };
            message.Body = bodyBuilder.ToMessageBody();

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
                await client.DisconnectAsync(true);
            }
        }
    }
}