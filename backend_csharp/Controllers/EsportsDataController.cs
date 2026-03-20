using Microsoft.AspNetCore.Mvc;
using Services;

namespace Controllers;

[ApiController]
[Route("api/esports")]
public class EsportsDataController : ControllerBase
{
    private readonly PandaScoreService _pandascore;

    public EsportsDataController(PandaScoreService pandascore)
    {
        _pandascore = pandascore;
    }

    [HttpGet("diagnostics")]
    public async Task<IActionResult> Diagnostics(CancellationToken ct = default)
    {
        var probe = await _pandascore.ProbeAsync(ct);
        return StatusCode(probe.StatusCode ?? 200, new
        {
            success = probe.Success,
            message = probe.Message
        });
    }

    [HttpGet("player")]
    public async Task<IActionResult> Player([FromQuery] string nickname, [FromQuery] string? game = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(nickname))
            return BadRequest(new { message = "nickname is required" });

        var probe = await _pandascore.ProbeAsync(ct);
        if (!probe.Success)
            return StatusCode(probe.StatusCode ?? 503, new { message = probe.Message });

        var players = await _pandascore.SearchPlayersAsync(nickname.Trim(), 10, game, ct);
        if (players.Count == 0)
            return NotFound(new { message = "Игрок не найден" });

        return Ok(new
        {
            source = "pandascore",
            query = nickname.Trim(),
            game = game,
            results = players.Select(p => new
            {
                id = p.Id,
                name = p.Name,
                firstName = p.FirstName,
                lastName = p.LastName,
                role = p.Role,
                nationality = p.Nationality,
                imageUrl = p.ImageUrl,
                currentTeam = p.CurrentTeam,
                profileUrl = p.ProfileUrl
            })
        });
    }

    [HttpGet("tournament/streams")]
    public async Task<IActionResult> TournamentStreams([FromQuery] string query, [FromQuery] string? game = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return BadRequest(new { message = "query is required" });

        var probe = await _pandascore.ProbeAsync(ct);
        if (!probe.Success)
            return StatusCode(probe.StatusCode ?? 503, new { message = probe.Message });

        var tournaments = await _pandascore.SearchTournamentsAsync(query.Trim(), 10, game, ct);
        if (tournaments.Count == 0)
            return NotFound(new { message = "Турнир не найден" });

        PandaTournament? selectedTournament = null;
        List<PandaMatch> selectedMatches = new();

        foreach (var tournament in tournaments)
        {
            if (string.IsNullOrWhiteSpace(tournament.Id))
                continue;

            var matches = await _pandascore.GetMatchesForTournamentAsync(tournament.Id, 50, game ?? tournament.VideogameSlug, ct);
            if (matches.Count == 0)
                continue;

            selectedTournament = tournament;
            selectedMatches = matches;
            if (matches.Any(m => !string.IsNullOrWhiteSpace(m.StreamUrl)))
                break;
        }

        if (selectedTournament == null)
            return NotFound(new { message = "Не удалось получить матчи турнира" });

        var streams = selectedMatches
            .Where(m => !string.IsNullOrWhiteSpace(m.StreamUrl))
            .Select(m => new
            {
                matchId = m.Id,
                matchName = m.Name,
                url = m.StreamUrl,
                provider = DetectProvider(m.StreamUrl),
                channel = ExtractChannel(m.StreamUrl)
            })
            .DistinctBy(x => x.url)
            .ToList();

        return Ok(new
        {
            source = "pandascore",
            query = query.Trim(),
            tournament = new
            {
                id = selectedTournament.Id,
                name = selectedTournament.Name,
                status = selectedTournament.Status,
                beginAt = selectedTournament.BeginAt,
                videogame = selectedTournament.VideogameName,
                league = selectedTournament.LeagueName,
                prizePool = selectedTournament.PrizePool
            },
            streams,
            matches = selectedMatches.Select(m => new
            {
                id = m.Id,
                name = m.Name,
                status = m.Status,
                beginAt = m.BeginAt,
                teamA = m.OpponentA,
                teamB = m.OpponentB,
                scoreA = m.ScoreA,
                scoreB = m.ScoreB,
                streamUrl = m.StreamUrl
            })
        });
    }

    private static string DetectProvider(string? url)
    {
        var u = (url ?? string.Empty).ToLowerInvariant();
        if (u.Contains("twitch.tv")) return "twitch";
        if (u.Contains("youtube.com") || u.Contains("youtu.be")) return "youtube";
        return "stream";
    }

    private static string ExtractChannel(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        try
        {
            var uri = new Uri(url);
            var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (uri.Host.Contains("youtu"))
                return parts.LastOrDefault() ?? string.Empty;
            return parts.FirstOrDefault() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
