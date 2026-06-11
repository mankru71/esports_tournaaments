using System.Net.Http.Json;
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
    private string WebhookUrl => (_config["DISCORD_WEBHOOK_URL"] ?? string.Empty).Trim();
    private string BotName => string.IsNullOrWhiteSpace(_config["DISCORD_BOT_NAME"]) ? "Arena Control" : _config["DISCORD_BOT_NAME"]!;

    public async Task NotifyTournamentCreatedAsync(Tournament tournament, CancellationToken ct = default)
    {
        if (!Enabled) return;

        var payload = new
        {
            username = BotName,
            embeds = new[]
            {
                new
                {
                    title = "Новый турнир",
                    description = tournament.Name,
                    color = 13150570,
                    fields = new[]
                    {
                        new { name = "Дисциплина", value = tournament.Game, inline = true },
                        new { name = "Участники", value = tournament.MaxParticipants.ToString(), inline = true },
                        new { name = "Старт", value = tournament.StartDate, inline = true },
                        new { name = "Призовой фонд", value = tournament.PrizePool.ToString("0.##"), inline = true }
                    },
                    timestamp = DateTimeOffset.UtcNow
                }
            }
        };

        await SendAsync(payload, ct);
    }

    public async Task NotifyMatchLiveAsync(Match match, CancellationToken ct = default)
    {
        if (!Enabled) return;

        var teamA = match.TeamA?.Name ?? "TBD";
        var teamB = match.TeamB?.Name ?? "TBD";
        var tournamentName = match.Tournament?.Name ?? $"Турнир #{match.TournamentId}";

        var payload = new
        {
            username = BotName,
            embeds = new[]
            {
                new
                {
                    title = "Матч в эфире",
                    description = $"{teamA} — {teamB}",
                    url = $"http://localhost:8000/play/tournaments/{match.TournamentId}/matches/",
                    color = 15844367,
                    fields = new[]
                    {
                        new { name = "Турнир", value = tournamentName, inline = false },
                        new { name = "Счёт", value = $"{match.ScoreA}:{match.ScoreB}", inline = true },
                        new { name = "Этап", value = string.IsNullOrWhiteSpace(match.Round) ? "Match" : match.Round, inline = true },
                        new { name = "Стрим", value = string.IsNullOrWhiteSpace(match.StreamUrl) ? "Не привязан" : match.StreamUrl, inline = false }
                    },
                    timestamp = DateTimeOffset.UtcNow
                }
            }
        };

        await SendAsync(payload, ct);
    }

    private async Task SendAsync(object payload, CancellationToken ct)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync(WebhookUrl, payload, new JsonSerializerOptions(JsonSerializerDefaults.Web), ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Discord webhook returned {Status}: {Body}", (int)response.StatusCode, body);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Discord webhook request failed");
        }
    }
}
