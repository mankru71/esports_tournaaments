using Data;
using Microsoft.EntityFrameworkCore;
using Models;

namespace Services;

/// <summary>
/// Снимки рейтинга для графика динамики в профиле.
/// Снимок пишется только если значение изменилось с прошлого раза —
/// иначе повторные привязки Faceit засоряют график одинаковыми точками.
/// SaveChanges не вызывает — коммитит вызывающий код вместе со своими изменениями.
/// </summary>
public class RatingHistoryService
{
    private readonly AppDbContext _db;

    public RatingHistoryService(AppDbContext db)
    {
        _db = db;
    }

    public async Task SnapshotAsync(int userId, decimal rating, string source, CancellationToken ct = default)
    {
        var last = await _db.RatingHistories
            .Where(h => h.UserId == userId)
            .OrderByDescending(h => h.RecordedAtUtc)
            .FirstOrDefaultAsync(ct);

        if (last != null && last.Rating == rating && last.Source == source)
            return;

        _db.RatingHistories.Add(new RatingHistory
        {
            UserId = userId,
            Rating = rating,
            Source = source,
            RecordedAtUtc = DateTime.UtcNow
        });
    }

    public async Task<List<RatingHistory>> GetHistoryAsync(int userId, CancellationToken ct = default)
    {
        return await _db.RatingHistories
            .Where(h => h.UserId == userId)
            .OrderBy(h => h.RecordedAtUtc)
            .ToListAsync(ct);
    }
}
