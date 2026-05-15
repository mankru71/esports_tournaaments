using Microsoft.AspNetCore.Mvc;
using Services;
using System.Linq;

namespace Controllers;

[ApiController]
[Route("api/streams")]
public class StreamsController : ControllerBase
{
    private readonly PandaScoreService _pandascore;

    public StreamsController(PandaScoreService pandascore)
    {
        _pandascore = pandascore;
    }

    [HttpGet("status")]
    public async Task<IActionResult> Status([FromQuery] string? q = null, CancellationToken ct = default)
    {
        if (!_pandascore.Enabled)
            return StatusCode(503, new { message = "PandaScore token is not configured (PANDASCORE_TOKEN)" });

        var tournaments = string.IsNullOrWhiteSpace(q)
            ? await _pandascore.GetRunningTournamentsAsync(5, ct: ct)
            : await _pandascore.SearchTournamentsAsync(q!.Trim(), 5, ct: ct);

        foreach (var tournament in tournaments.Where(t => !string.IsNullOrWhiteSpace(t.Id)))
        {
            var matches = await _pandascore.GetMatchesForTournamentAsync(tournament.Id, 50, tournament.VideogameSlug, ct);
            var payload = matches
                .Where(m => !string.IsNullOrWhiteSpace(m.StreamUrl))
                .Select(m => new
                {
                    provider = DetectProvider(m.StreamUrl!),
                    url = m.StreamUrl!,
                    channel = ExtractChannel(m.StreamUrl!),
                    status = new { online = false, viewers = 0 },
                    meta = new { source = "pandascore", tournament = tournament.Name, match = m.Name }
                })
                .DistinctBy(x => x.url)
                .ToList();

            if (payload.Count > 0)
                return Ok(payload);
        }

        return Ok(Array.Empty<object>());
    }

    private static string DetectProvider(string url)
    {
        var u = url.ToLowerInvariant();
        if (u.Contains("twitch.tv")) return "Twitch";
        if (u.Contains("youtube.com") || u.Contains("youtu.be")) return "YouTube";
        return "Stream";
    }

    private static string ExtractChannel(string url)
    {
        try
        {
            var uri = new Uri(url);
            var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 0 ? parts[0] : "";
        }
        catch
        {
            return "";
        }
    }
}
