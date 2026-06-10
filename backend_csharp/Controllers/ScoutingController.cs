using Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Controllers;

/// <summary>
/// Доска скаутинга: игроки, которые ищут команду (LFT — looking for team).
/// Сортировка: сначала подтверждённый Faceit Elo, затем по его величине.
/// </summary>
[ApiController]
[Route("api/scouting")]
public class ScoutingController : ControllerBase
{
    private readonly AppDbContext _db;

    public ScoutingController(AppDbContext db)
    {
        _db = db;
    }

    [HttpGet("free-agents")]
    public async Task<IActionResult> FreeAgents(CancellationToken ct)
    {
        var agents = await _db.Users
            .Where(u => u.IsLookingForTeam)
            .OrderByDescending(u => u.RatingVerified)
            .ThenByDescending(u => u.FaceitElo ?? -1)
            .ThenByDescending(u => u.Rating ?? -1)
            .ThenBy(u => u.Nickname)
            .ToListAsync(ct);

        var payload = agents.Select(u => new
        {
            id = u.Id,
            nickname = u.Nickname,
            bio = u.Bio,
            role = u.Role,
            faceitNickname = u.FaceitNickname,
            faceitElo = u.FaceitElo,
            faceitLevel = u.FaceitLevel,
            faceitAvatar = u.FaceitAvatar,
            faceitProfileUrl = u.FaceitProfileUrl,
            rating = u.Rating,
            ratingVerified = u.RatingVerified,
            lookingForTeamSinceUtc = u.LookingForTeamSinceUtc
        });

        return Ok(payload);
    }
}
