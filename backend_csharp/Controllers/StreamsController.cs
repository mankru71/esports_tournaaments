using Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Services;

namespace Controllers;

[ApiController]
[Route("api/streams")]
public class StreamsController : ControllerBase
{
    private readonly PandaScoreService _pandascore;
    private readonly AppDbContext _db;

    public StreamsController(PandaScoreService pandascore, AppDbContext db)
    {
        _pandascore = pandascore;
        _db = db;
    }

    [HttpGet("tournament/{tournamentId:int}")]
    public async Task<IActionResult> TournamentStreams(int tournamentId)
    {
        var matches = await _db.Matches
            .Include(m => m.TeamA)
            .Include(m => m.TeamB)
            .Where(m => m.TournamentId == tournamentId && !string.IsNullOrWhiteSpace(m.StreamUrl))
            .OrderBy(m => m.RoundNumber)
            .ThenBy(m => m.Id)
            .ToListAsync();

        var streams = matches.Select(m => new
        {
            provider = string.IsNullOrWhiteSpace(m.StreamProvider) ? DetectProvider(m.StreamUrl!) : m.StreamProvider,
            url = m.StreamUrl!,
            channel = ExtractChannel(m.StreamUrl!),
            status = new { online = m.Status == "live", viewers = m.Status == "live" ? 1200 : 0 },
            meta = new { source = "local", matchId = m.Id, round = m.Round, match = $"{m.TeamA?.Name ?? "TBD"} vs {m.TeamB?.Name ?? "TBD"}" }
        });

        return Ok(streams);
    }

    [HttpGet("status")]
    public async Task<IActionResult> Status([FromQuery] string? q = null, CancellationToken ct = default)
    {
        var query = (q ?? string.Empty).Trim().ToLowerInvariant();
        var localMatches = await _db.Matches
            .Include(m => m.Tournament)
            .Include(m => m.TeamA)
            .Include(m => m.TeamB)
            .Where(m => !string.IsNullOrWhiteSpace(m.StreamUrl))
            .OrderByDescending(m => m.Status == "live")
            .Take(25)
            .ToListAsync(ct);

        var localStreams = localMatches
            .Where(m => string.IsNullOrWhiteSpace(query) || (m.Tournament?.Name ?? string.Empty).ToLowerInvariant().Contains(query))
            .Take(10)
            .Select(m => new
            {
                provider = string.IsNullOrWhiteSpace(m.StreamProvider) ? DetectProvider(m.StreamUrl!) : m.StreamProvider,
                url = m.StreamUrl!,
                channel = ExtractChannel(m.StreamUrl!),
                status = new { online = m.Status == "live", viewers = m.Status == "live" ? 1200 : 0 },
                meta = new { source = "local", tournament = m.Tournament?.Name ?? "Local", match = $"{m.TeamA?.Name ?? "TBD"} vs {m.TeamB?.Name ?? "TBD"}" }
            })
            .ToList();

        if (localStreams.Count > 0)
            return Ok(localStreams);

        if (!_pandascore.Enabled)
            return Ok(Array.Empty<object>());

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
