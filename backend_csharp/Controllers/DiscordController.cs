using Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Services;

namespace Controllers;

[ApiController]
[Route("api/discord")]
public class DiscordController : ControllerBase
{
    private readonly DiscordWebhookService _discord;

    public DiscordController(DiscordWebhookService discord)
    {
        _discord = discord;
    }

    [HttpGet("status")]
    public IActionResult Status()
    {
        return Ok(new
        {
            enabled = _discord.Enabled,
            message = _discord.Enabled
                ? "Discord Webhook настроен"
                : "Discord Webhook не настроен. Добавьте DISCORD_WEBHOOK_URL в .env"
        });
    }

    [HttpPost("test")]
    public async Task<IActionResult> Test(CancellationToken ct)
    {
        if (!AuthTokenHelper.IsInAnyRole(Request, "admin"))
            return StatusCode(403, new { message = "Тест Discord может запускать только администратор" });

        var sent = await _discord.SendTestAsync(ct);
        return Ok(new
        {
            sent,
            message = sent
                ? "Тестовое сообщение отправлено в Discord"
                : "Webhook не настроен или Discord отклонил запрос. Проверьте DISCORD_WEBHOOK_URL и логи backend."
        });
    }
}
