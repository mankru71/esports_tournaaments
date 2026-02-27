using Microsoft.AspNetCore.Mvc;
using Services;

namespace Controllers;

[ApiController]
[Route("api/esports")]
public class EsportsDataController : ControllerBase
{
    private readonly LiquipediaService _liquipedia;

    public EsportsDataController(LiquipediaService liquipedia)
    {
        _liquipedia = liquipedia;
    }

    [HttpGet("player")]
    public async Task<IActionResult> Player([FromQuery] string nickname, [FromQuery] string? game = null, CancellationToken ct = default)
    {
        var (title, info) = await _liquipedia.GetPlayerInfoAsync(game ?? "counterstrike", nickname, ct);
        if (string.IsNullOrWhiteSpace(title))
        {
            return NotFound(new { message = "Игрок не найден на Liquipedia" });
        }

        return Ok(new
        {
            source = "liquipedia",
            game = game ?? "counterstrike",
            title,
            nickname = nickname,
            info,
            pageUrl = BuildPageUrl(game ?? "counterstrike", title!)
        });
    }

    [HttpGet("tournament/streams")]
    public async Task<IActionResult> TournamentStreams([FromQuery] string query, [FromQuery] string? game = null, CancellationToken ct = default)
    {
        var (title, streams) = await _liquipedia.GetTournamentStreamsAsync(game ?? "counterstrike", query, ct);
        if (string.IsNullOrWhiteSpace(title))
        {
            return NotFound(new { message = "Турнир не найден на Liquipedia" });
        }

        return Ok(new
        {
            source = "liquipedia",
            game = game ?? "counterstrike",
            title,
            pageUrl = BuildPageUrl(game ?? "counterstrike", title!),
            streams
        });
    }

    private static string BuildPageUrl(string game, string title)
    {
        var g = (game ?? "counterstrike").Trim().ToLowerInvariant();
        var baseUrl = g switch
        {
            "dota2" => "https://liquipedia.net/dota2/",
            "leagueoflegends" => "https://liquipedia.net/leagueoflegends/",
            _ => "https://liquipedia.net/counterstrike/"
        };

        return baseUrl + Uri.EscapeDataString(title.Replace(' ', '_'));
    }
}
