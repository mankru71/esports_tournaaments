using Data;
using Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Models;
using System.ComponentModel.DataAnnotations;

namespace Controllers;

[ApiController]
[Route("api/favorites")]
public class FavoritesController : ControllerBase
{
    private readonly AppDbContext _db;

    public FavoritesController(AppDbContext db)
    {
        _db = db;
    }

    public class AddFavoriteRequest
    {
        [Required]
        public int TournamentId { get; set; }
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var userId = AuthTokenHelper.GetUserId(Request);
        if (userId is null) return Unauthorized(new { message = "Требуется вход" });

        var ids = await _db.UserFavorites
            .Where(f => f.UserId == userId.Value)
            .OrderByDescending(f => f.CreatedAtUtc)
            .Select(f => f.TournamentId)
            .ToListAsync(ct);

        return Ok(new { tournamentIds = ids });
    }

    [HttpPost]
    public async Task<IActionResult> Add([FromBody] AddFavoriteRequest request, CancellationToken ct)
    {
        var userId = AuthTokenHelper.GetUserId(Request);
        if (userId is null) return Unauthorized(new { message = "Требуется вход" });

        var tournamentExists = await _db.Tournaments.AnyAsync(t => t.Id == request.TournamentId, ct);
        if (!tournamentExists)
            return NotFound(new { message = "Турнир не найден" });

        var alreadyFavorited = await _db.UserFavorites
            .AnyAsync(f => f.UserId == userId.Value && f.TournamentId == request.TournamentId, ct);

        if (!alreadyFavorited)
        {
            _db.UserFavorites.Add(new UserFavorite
            {
                UserId = userId.Value,
                TournamentId = request.TournamentId
            });

            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Гонка на уникальном индексе — запись уже есть, считаем операцию успешной
            }
        }

        return Ok(new { tournamentId = request.TournamentId, favorited = true });
    }

    [HttpDelete("{tournamentId:int}")]
    public async Task<IActionResult> Remove(int tournamentId, CancellationToken ct)
    {
        var userId = AuthTokenHelper.GetUserId(Request);
        if (userId is null) return Unauthorized(new { message = "Требуется вход" });

        var favorite = await _db.UserFavorites
            .FirstOrDefaultAsync(f => f.UserId == userId.Value && f.TournamentId == tournamentId, ct);

        if (favorite != null)
        {
            _db.UserFavorites.Remove(favorite);
            await _db.SaveChangesAsync(ct);
        }

        return Ok(new { tournamentId, favorited = false });
    }
}
