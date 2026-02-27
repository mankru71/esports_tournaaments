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

    // Player search by nickname (open API: PandaScore)
    [HttpGet("player")]
    public async Task<IActionResult> Player([FromQuery] string nickname, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(nickname))
            return BadRequest(new { message = "nickname is required" });

        if (!_pandascore.Enabled)
            return StatusCode(503, new { message = "PandaScore token is not configured (PANDASCORE_TOKEN)" });

        var players = await _pandascore.SearchPlayersAsync(nickname.Trim(), 10, ct);
        if (players.Count == 0)
            return NotFound(new { message = "Игрок не найден" });

        return Ok(new
        {
            source = "pandascore",
            query = nickname.Trim(),
            results = players.Select(p => new
            {
                id = p.Id,
                name = p.Name,
                firstName = p.FirstName,
                lastName = p.LastName,
                role = p.Role,
                nationality = p.Nationality,
                imageUrl = p.ImageUrl,
                currentTeam = p.CurrentTeam
            })
        });
    }

    // Tournament streams: search tournament by name, then collect streams from its matches (streams_list)
    [HttpGet("tournament/streams")]
    public async Task<IActionResult> TournamentStreams([FromQuery] string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return BadRequest(new { message = "query is required" });

        if (!_pandascore.Enabled)
            return StatusCode(503, new { message = "PandaScore token is not configured (PANDASCORE_TOKEN)" });

        var tournaments = await _pandascore.SearchTournamentsAsync(query.Trim(), 10, ct);
        var t = tournaments.FirstOrDefault();
        if (t == null || string.IsNullOrWhiteSpace(t.Id))
            return NotFound(new { message = "Турнир не найден" });

        var matches = await _pandascore.GetMatchesForTournamentAsync(t.Id, 50, ct);

        var streams = matches
            .Where(m => !string.IsNullOrWhiteSpace(m.StreamUrl))
            .Select(m => new
            {
                matchId = m.Id,
                matchName = m.Name,
                url = m.StreamUrl
            })
            .Distinct()
            .ToList();

        return Ok(new
        {
            source = "pandascore",
            query = query.Trim(),
            tournament = new
            {
                id = t.Id,
                name = t.Name,
                status = t.Status,
                beginAt = t.BeginAt,
                videogame = t.VideogameName,
                league = t.LeagueName
            },
            streams,
            matches = matches.Select(m => new
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
}
