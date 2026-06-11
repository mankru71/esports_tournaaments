using Data;
using Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Services;

public class MatchNotificationWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<MatchNotificationWorker> _logger;
    private readonly IHubContext<MatchesHub> _hubContext;

    public MatchNotificationWorker(
        IServiceProvider serviceProvider,
        ILogger<MatchNotificationWorker> logger,
        IHubContext<MatchesHub> hubContext)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _hubContext = hubContext;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MatchNotificationWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAndNotifyMatchesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred in MatchNotificationWorker.");
            }

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    private async Task CheckAndNotifyMatchesAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var emailService = scope.ServiceProvider.GetRequiredService<EsportsBackend.Services.EmailService>();

        var targetTimeUtc = DateTime.UtcNow.AddMinutes(15);
        var lowerBoundUtc = DateTime.UtcNow.AddMinutes(14); // Provide a 1-minute window

        var upcomingMatches = await db.Matches
            .Include(m => m.TeamA).ThenInclude(t => t.Players)
            .Include(m => m.TeamB).ThenInclude(t => t.Players)
            .Where(m => m.Status == "planned" && 
                        !m.Is15MinNotified &&
                        m.StartTimeUtc != null &&
                        m.StartTimeUtc <= targetTimeUtc &&
                        m.StartTimeUtc >= lowerBoundUtc)
            .ToListAsync(ct);

        foreach (var match in upcomingMatches)
        {
            match.Is15MinNotified = true;

            // Gather all user Nicknames
            var playerNicknames = new HashSet<string>();

            if (match.TeamA != null)
            {
                var teamAPlayers = await db.TeamPlayers.Where(tp => tp.TeamId == match.TeamAId).ToListAsync(ct);
                foreach (var tp in teamAPlayers) playerNicknames.Add(tp.Nickname);
            }
            if (match.TeamB != null)
            {
                var teamBPlayers = await db.TeamPlayers.Where(tp => tp.TeamId == match.TeamBId).ToListAsync(ct);
                foreach (var tp in teamBPlayers) playerNicknames.Add(tp.Nickname);
            }

            var usersToNotify = await db.Users.Where(u => playerNicknames.Contains(u.Nickname)).ToListAsync(ct);

            foreach (var user in usersToNotify)
            {
                var title = $"Матч через 15 минут!";
                var body = $"Ваш матч #{match.Id} начнется через 15 минут.";

                // Send Email
                if (!string.IsNullOrWhiteSpace(user.Email))
                {
                    await emailService.SendEmailAsync(user.Email, title, body);
                }

                // Send SignalR Notification
                await _hubContext.Clients.Group($"user:{user.Id}").SendAsync("ReceiveNotification", body, ct);
            }
        }

        if (upcomingMatches.Any())
        {
            await db.SaveChangesAsync(ct);
        }
    }
}
