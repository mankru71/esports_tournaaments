using System.Text;
using System.Text.Json;
using Models;

namespace Services;

public class DiscordWebhookService
{
    private readonly HttpClient _http;
    private readonly IConfiguration _config;
    private readonly ILogger<DiscordWebhookService> _logger;

    public DiscordWebhookService(HttpClient http, IConfiguration config, ILogger<DiscordWebhookService> logger)
    {
        _http = http;
        _config = config;
        _logger = logger;
    }

    public bool Enabled => !string.IsNullOrWhiteSpace(WebhookUrl);

    private string? WebhookUrl =>
        _config["Discord:WebhookUrl"] ??
        _config["DISCORD_WEBHOOK_URL"];

    private string FrontendUrl =>
        (_config["PUBLIC_FRONTEND_URL"] ?? "http://localhost").TrimEnd('/');

    private string BotName =>
        _config["Discord:BotName"] ??
        _config["DISCORD_BOT_NAME"] ??
        "Esports Arena";

    public async Task NotifyTournamentCreatedAsync(Tournament tournament, CancellationToken ct = default)
    {
        var url = $"{FrontendUrl}/tournaments/{tournament.Id}/";
        var payload = new
        {
            username = BotName,
            content = "🏆 **Создан новый турнир!**",
            embeds = new[]
            {
                new
                {
                    title = tournament.Name,
                    description = $"Дисциплина: **{NormalizeGame(tournament.Game)}**\nФормат: **{tournament.Format}**\nУчастников: **0/{tournament.MaxParticipants}**",
                    url,
                    color = 0x5865F2,
                    fields = new[]
                    {
                        new { name = "Старт", value = SafeValue(tournament.StartDate), inline = true },
                        new { name = "Призовой фонд", value = $"{tournament.PrizePool:0.##}", inline = true },
                        new { name = "Статус", value = SafeValue(tournament.Status), inline = true }
                    },
                    thumbnail = new { url = "https://cdn-icons-png.flaticon.com/512/871/871392.png" },
                    timestamp = DateTimeOffset.UtcNow.ToString("O"),
                    footer = new { text = "Esports Tournaments · учебный проект" }
                }
            }
        };

        await SendAsync(payload, "tournament-created", ct);
    }

    public async Task NotifyMatchLiveAsync(Match match, CancellationToken ct = default)
    {
        var tournament = match.Tournament;
        var url = tournament == null ? FrontendUrl : $"{FrontendUrl}/tournaments/{match.TournamentId}/matches/";
        var teamA = match.TeamA?.Name ?? "TBD";
        var teamB = match.TeamB?.Name ?? "TBD";
        var payload = new
        {
            username = BotName,
            content = "🔴 **Матч перешёл в LIVE!**",
            embeds = new[]
            {
                new
                {
                    title = $"{teamA} vs {teamB}",
                    description = tournament == null ? "Матч начался." : $"Турнир: **{tournament.Name}**",
                    url,
                    color = 0xED4245,
                    fields = new[]
                    {
                        new { name = "Раунд", value = SafeValue(match.Round), inline = true },
                        new { name = "Счёт", value = $"{match.ScoreA} : {match.ScoreB}", inline = true },
                        new { name = "Статус", value = "LIVE", inline = true }
                    },
                    thumbnail = new { url = "https://cdn-icons-png.flaticon.com/512/5968/5968756.png" },
                    timestamp = DateTimeOffset.UtcNow.ToString("O"),
                    footer = new { text = "Live update from backend" }
                }
            }
        };

        await SendAsync(payload, "match-live", ct);
    }

    public async Task<bool> SendTestAsync(CancellationToken ct = default)
    {
        var payload = new
        {
            username = BotName,
            content = "✅ Discord Webhook подключён. Тестовое уведомление из учебного проекта работает.",
            embeds = new[]
            {
                new
                {
                    title = "Проверка интеграции",
                    description = "Если это сообщение появилось в канале, можно показывать Discord-интеграцию на защите.",
                    color = 0x57F287,
                    timestamp = DateTimeOffset.UtcNow.ToString("O")
                }
            }
        };

        return await SendAsync(payload, "test", ct);
    }

    private async Task<bool> SendAsync(object payload, string eventName, CancellationToken ct)
    {
        if (!Enabled)
        {
            _logger.LogInformation("Discord webhook is not configured. Event {EventName} was skipped.", eventName);
            return false;
        }

        try
        {
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(WebhookUrl, content, ct);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Discord webhook event {EventName} sent successfully.", eventName);
                return true;
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Discord webhook event {EventName} failed: {StatusCode} {Body}", eventName, (int)response.StatusCode, body);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Discord webhook event {EventName} failed.", eventName);
            return false;
        }
    }

    private static string SafeValue(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value.Trim();

    private static string NormalizeGame(string? game) => string.IsNullOrWhiteSpace(game) ? "Не указано" : game.Trim();
}
