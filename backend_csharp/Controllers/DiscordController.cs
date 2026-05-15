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
        return Ok(new { enabled = _discord.Enabled });
    }
}
