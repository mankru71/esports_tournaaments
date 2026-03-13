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

        var query = string.IsNullOrWhiteSpace(q) ? "Major" : q!.Trim();
        var tournaments = await _pandascore.SearchTournamentsAsync(query, 5, ct: ct);
        var tournament = tournaments.FirstOrDefault();
        if (tournament == null || string.IsNullOrWhiteSpace(tournament.Id))
            return Ok(Array.Empty<object>());

        var matches = await _pandascore.GetMatchesForTournamentAsync(tournament.Id, 50, tournament.VideogameSlug, ct);
        var statuses = await _pandascore.BuildStreamStatusesAsync(matches, ct);
        var statusByUrl = statuses.ToDictionary(x => x.Url, StringComparer.OrdinalIgnoreCase);

        var payload = matches
            .Where(m => !string.IsNullOrWhiteSpace(m.StreamUrl))
            .Select(m =>
            {
                var url = m.StreamUrl!;
                statusByUrl.TryGetValue(url, out var status);
                return new
                {
                    provider = PandaScoreService.DetectProvider(url),
                    url,
                    channel = PandaScoreService.ExtractChannelOrVideo(url),
                    status = new
                    {
                        online = status?.IsLive ?? false,
                        viewers = status?.ViewerCount
                    },
                    meta = new { source = "pandascore", tournament = tournament.Name, match = m.Name }
                };
            })
            .DistinctBy(x => x.url)
            .ToList();

        return Ok(payload);
    }
}
