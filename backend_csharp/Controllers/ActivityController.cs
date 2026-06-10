using Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Controllers;

[ApiController]
[Route("api/activity")]
public class ActivityController : ControllerBase
{
    private readonly AppDbContext _db;

    public ActivityController(AppDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct, [FromQuery] int limit = 10)
    {
        limit = Math.Clamp(limit, 1, 50);
        var items = await _db.ActivityLogs
            .OrderByDescending(a => a.TimestampUtc)
            .ThenByDescending(a => a.Id)
            .Take(limit)
            .Select(a => new
            {
                id = a.Id,
                timestampUtc = a.TimestampUtc,
                actionType = a.ActionType,
                message = a.Message
            })
            .ToListAsync(ct);

        return Ok(items);
    }
}
