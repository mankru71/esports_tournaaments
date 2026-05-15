using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Data;
using System.Text.Json;

namespace Controllers;

[ApiController]
[Route("api/prizes")]
public class PrizeController : ControllerBase
{
    private readonly AppDbContext _db;

    public PrizeController(AppDbContext db)
    {
        _db = db;
    }

    [HttpPost("{tournamentId}/distribute")]
    public async Task<IActionResult> DistributePrizes(int tournamentId)
    {
        if (!Infrastructure.AuthTokenHelper.IsInAnyRole(Request, "admin"))
            return StatusCode(403, new { message = "Только администратор может распределять фонд" });

        var tournament = await _db.Tournaments.FindAsync(tournamentId);
        if (tournament == null) return NotFound(new { message = "Турнир не найден" });
        
        if (tournament.PrizePool <= 0) 
            return BadRequest(new { message = "У этого турнира нет призового фонда" });

        var distribution = new List<object>
        {
            new { place = "1 Место (Чемпион)", percent = 50, amount = tournament.PrizePool * 0.5m, status = "Ожидает выплаты" },
            new { place = "2 Место (Финалист)", percent = 30, amount = tournament.PrizePool * 0.3m, status = "Ожидает выплаты" },
            new { place = "3 Место (Бронза)", percent = 20, amount = tournament.PrizePool * 0.2m, status = "Ожидает выплаты" }
        };

        tournament.PrizeDistributionJson = JsonSerializer.Serialize(distribution);
        tournament.Status = "finished"; 
        
        await _db.SaveChangesAsync();

        return Ok(new { 
            message = "Призовой фонд успешно рассчитан и распределен!", 
            payouts = distribution 
        });
    }
}