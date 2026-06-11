using Data;
using Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Models;
using System.ComponentModel.DataAnnotations;

namespace Controllers;

[ApiController]
[Route("api/predictions")]
public class PredictionsController : ControllerBase
{
    private readonly AppDbContext _db;

    public PredictionsController(AppDbContext db)
    {
        _db = db;
    }

    public class PredictionRequest
    {
        [Required]
        public int MatchId { get; set; }

        [Required]
        public int PredictedTeamId { get; set; }
    }

    [HttpPost]
    [Authorize]
    public async Task<IActionResult> MakePrediction([FromBody] PredictionRequest request)
    {
        var userId = User.GetUserId();
        if (userId == null)
            return Unauthorized();

        var match = await _db.Matches.FirstOrDefaultAsync(m => m.Id == request.MatchId);
        if (match == null)
            return NotFound(new { message = "Матч не найден." });

        if (match.Status != "planned")
            return BadRequest(new { message = "Прогнозы принимаются только до начала матча." });

        if (match.TeamAId != request.PredictedTeamId && match.TeamBId != request.PredictedTeamId)
            return BadRequest(new { message = "Выбранная команда не участвует в этом матче." });

        var existingPrediction = await _db.MatchPredictions
            .FirstOrDefaultAsync(p => p.UserId == userId.Value && p.MatchId == request.MatchId);

        if (existingPrediction != null)
        {
            existingPrediction.PredictedTeamId = request.PredictedTeamId;
            existingPrediction.CreatedAtUtc = DateTime.UtcNow;
            _db.MatchPredictions.Update(existingPrediction);
        }
        else
        {
            var prediction = new MatchPrediction
            {
                UserId = userId.Value,
                MatchId = request.MatchId,
                PredictedTeamId = request.PredictedTeamId
            };
            _db.MatchPredictions.Add(prediction);
        }

        await _db.SaveChangesAsync();

        return Ok(new { message = "Прогноз успешно сохранен." });
    }

    [HttpGet("leaderboard")]
    public async Task<IActionResult> GetLeaderboard([FromQuery] int limit = 100)
    {
        var topUsers = await _db.Users
            .OrderByDescending(u => u.PredictorMMR)
            .Take(limit)
            .Select(u => new
            {
                u.Id,
                u.Nickname,
                u.AvatarUrl,
                FaceitAvatar = u.FaceitAvatar,
                u.PredictorMMR,
                Badges = _db.UserBadges
                            .Where(ub => ub.UserId == u.Id)
                            .Select(ub => new { ub.Badge.Name, ub.Badge.IconUrlOrCss, ub.Badge.ColorCss })
                            .ToList()
            })
            .ToListAsync();

        return Ok(topUsers);
    }
}
