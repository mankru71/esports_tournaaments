using Microsoft.AspNetCore.Mvc;
using Services;

namespace Controllers;

[ApiController]
[Route("api/streams")]
public class StreamsController : ControllerBase
{
    private readonly LiquipediaService _liquipedia;

    public StreamsController(LiquipediaService liquipedia)
    {
        _liquipedia = liquipedia;
    }

    [HttpGet("status")]
    public async Task<IActionResult> Status([FromQuery] string? q = null, [FromQuery] string? game = null, CancellationToken ct = default)
    {
        // Учебный endpoint: возвращает стримы из Liquipedia по запросу (по умолчанию "Major").
        var query = string.IsNullOrWhiteSpace(q) ? "Major" : q!;
        var (title, streams) = await _liquipedia.GetTournamentStreamsAsync(game ?? "counterstrike", query, ct);

        var payload = streams.Select(s => new
        {
            provider = s.TryGetValue("provider", out var p) ? p : "",
            url = s.TryGetValue("url", out var u) ? u : "",
            channel = s.TryGetValue("channel", out var c) ? c : "",
            status = new { online = false, viewers = 0 },
            meta = new { source = "liquipedia", title }
        });

        return Ok(payload);
    }
}
