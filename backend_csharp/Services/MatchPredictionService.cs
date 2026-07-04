using Microsoft.Extensions.Caching.Memory;
using Models;
using System.Text.Json;

namespace Services;

/// <summary>
/// Клиент ML-микросервиса прогнозов (ml-service, FastAPI, Elo-модель по
/// разнице средних рейтингов команд — применима к любой дисциплине).
/// Паттерн как у остальных интеграций: Enabled-флаг из конфигурации
/// (ML_SERVICE_URL пустой → сервис выключен, ошибок наружу нет),
/// IMemoryCache, чтобы не дёргать модель на каждый GET /api/matches.
/// </summary>
public class MatchPredictionService
{
    private readonly HttpClient _http;
    private readonly IMemoryCache _cache;
    private readonly ILogger<MatchPredictionService> _logger;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public bool Enabled { get; }

    public MatchPredictionService(HttpClient http, IConfiguration config, IMemoryCache cache, ILogger<MatchPredictionService> logger)
    {
        _http = http;
        _cache = cache;
        _logger = logger;
        Enabled = http.BaseAddress != null && config.GetValue("ML_SERVICE_ENABLED", true);
    }

    public sealed record MatchPrediction(decimal TeamAWinProbability, decimal TeamBWinProbability, string Model);

    private sealed class PredictResponse
    {
        public double ProbA { get; set; }
        public double ProbB { get; set; }
        public string? Model { get; set; }
    }

    /// <summary>
    /// Вероятности побед для матча. null — сервис выключен/недоступен или матч
    /// не подходит (нет обеих команд): UI в этом случае просто не показывает
    /// бейдж прогноза.
    /// </summary>
    public async Task<MatchPrediction?> PredictAsync(Match match, CancellationToken ct = default)
    {
        if (!Enabled || match.TeamA == null || match.TeamB == null)
            return null;

        var cacheKey = $"prediction:{match.Id}:{match.TeamAId}:{match.TeamBId}";
        if (_cache.TryGetValue(cacheKey, out MatchPrediction? cached))
            return cached;

        try
        {
            var payload = new
            {
                matchId = match.Id,
                teamA = BuildTeamFeatures(match.TeamA),
                teamB = BuildTeamFeatures(match.TeamB)
            };

            using var response = await _http.PostAsJsonAsync("predict", payload, JsonOptions, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("ML-сервис вернул {Status} для матча {MatchId}", response.StatusCode, match.Id);
                return null;
            }

            var parsed = await response.Content.ReadFromJsonAsync<PredictResponse>(JsonOptions, ct);
            if (parsed is null)
                return null;

            var prediction = new MatchPrediction(
                Math.Round((decimal)parsed.ProbA * 100m, 1),
                Math.Round((decimal)parsed.ProbB * 100m, 1),
                parsed.Model ?? "unknown");

            _cache.Set(cacheKey, prediction, CacheTtl);
            return prediction;
        }
        catch (Exception ex)
        {
            // Прогноз — некритичная фича: при недоступности ml-service матчи
            // отдаются как обычно, без поля prediction
            _logger.LogWarning(ex, "Не удалось получить прогноз для матча {MatchId}", match.Id);
            return null;
        }
    }

    /// <summary>После ввода результата прогноз для матча больше не актуален.</summary>
    public void Invalidate(Match match) =>
        _cache.Remove($"prediction:{match.Id}:{match.TeamAId}:{match.TeamBId}");

    private static object BuildTeamFeatures(Team team)
    {
        decimal rating;
        if (team.Players != null && team.Players.Any(p => p.Rating.HasValue))
        {
            rating = Math.Round(team.Players.Where(p => p.Rating.HasValue).Average(p => p.Rating!.Value), 2);
            if (rating < 100m)
            {
                rating = rating * 2000m;
            }
        }
        else
        {
            // Deterministic rating between 2000 and 3200 based on team ID
            var val = (team.Id * 149) % 1200;
            rating = 2000 + val;
        }

        return new
        {
            teamId = team.Id,
            name = team.Name,
            avgRating = rating,
            playersCount = team.Players?.Count ?? 5
        };
    }
}
