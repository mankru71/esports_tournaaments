namespace EsportsBackend.Services;

/// <summary>
/// Abstraction over the mail transport. Swap MailKit for SendGrid without touching callers.
/// </summary>
public interface IEmailSender
{
    Task SendEmailVerificationAsync(string toEmail, string toNickname, string verifyUrl, CancellationToken ct = default);
}
