using Data;
using Models;

namespace Services;

/// <summary>
/// Запись событий в ленту активности. Любая ошибка логирования глотается:
/// лента — вторичная функция и не должна ломать основную операцию.
/// </summary>
public class ActivityLogService
{
    private readonly AppDbContext _db;
    private readonly ILogger<ActivityLogService> _logger;

    public ActivityLogService(AppDbContext db, ILogger<ActivityLogService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task LogAsync(string actionType, string message, CancellationToken ct = default)
    {
        try
        {
            _db.ActivityLogs.Add(new ActivityLog
            {
                ActionType = actionType,
                Message = message,
                TimestampUtc = DateTime.UtcNow
            });
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Activity log insert failed ({ActionType})", actionType);
        }
    }
}
