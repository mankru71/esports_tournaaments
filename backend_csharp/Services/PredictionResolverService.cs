using Data;
using Microsoft.EntityFrameworkCore;
using Models;

namespace Services;

public class PredictionResolverService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<PredictionResolverService> _logger;

    public PredictionResolverService(IServiceProvider services, ILogger<PredictionResolverService> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PredictionResolverService is starting.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ResolvePendingPredictionsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred while resolving predictions.");
            }

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    private async Task ResolvePendingPredictionsAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var pendingPredictions = await db.MatchPredictions
            .Include(p => p.Match)
            .Where(p => p.Status == "Pending" && p.Match != null && p.Match.Status == "finished")
            .ToListAsync(ct);

        if (!pendingPredictions.Any())
            return;

        _logger.LogInformation("Resolving {Count} pending predictions.", pendingPredictions.Count);

        var usersToUpdate = new HashSet<AppUser>();

        foreach (var prediction in pendingPredictions)
        {
            if (prediction.Match == null || prediction.Match.WinnerId == null)
            {
                // If finished but no winner, maybe draw or cancelled? Mark as refunded/lost?
                // For simplicity, let's just mark lost or ignore if winner is null.
                if (prediction.Match?.WinnerId == null)
                {
                    prediction.Status = "Lost";
                    prediction.PointsEarned = -10; // Penalty for draw/unknown? Let's say 0 for draw.
                    prediction.PointsEarned = 0;
                }
                continue;
            }

            bool won = prediction.PredictedTeamId == prediction.Match.WinnerId;
            prediction.Status = won ? "Won" : "Lost";
            prediction.PointsEarned = won ? 25 : -25;

            var user = await db.Users.FindAsync(new object[] { prediction.UserId }, ct);
            if (user != null)
            {
                user.PredictorMMR += prediction.PointsEarned;
                // Ensure MMR doesn't go below 0
                if (user.PredictorMMR < 0) user.PredictorMMR = 0;
                
                usersToUpdate.Add(user);
            }
        }

        db.MatchPredictions.UpdateRange(pendingPredictions);
        db.Users.UpdateRange(usersToUpdate);
        
        await db.SaveChangesAsync(ct);
        
        _logger.LogInformation("Resolved predictions and updated MMR for {Count} users.", usersToUpdate.Count);
    }
}
