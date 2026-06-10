using Data;
using Hubs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Models;
using Services;
using System.ComponentModel.DataAnnotations;

namespace Controllers;

[ApiController]
[Route("api/matches")]
public class MatchesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ExternalTournamentSyncService _sync;
    private readonly IHubContext<MatchesHub> _hub;
    private readonly DiscordWebhookService _discord;

    public MatchesController(AppDbContext db, ExternalTournamentSyncService sync, IHubContext<MatchesHub> hub, DiscordWebhookService discord)
    {
        _db = db;
        _sync = sync;
        _hub = hub;
        _discord = discord;
    }

    public class MatchResultRequest
    {
        [Range(0, 99)] public int ScoreA { get; set; }
        [Range(0, 99)] public int ScoreB { get; set; }
    }

    public class MatchStreamRequest
    {
        [Required] public string StreamUrl { get; set; } = string.Empty;
    }

    /// <summary>Live-матчи всех турниров для главной страницы (сначала — со стримом).</summary>
    [HttpGet("live")]
    public async Task<IActionResult> Live([FromServices] MatchPredictionService predictions, CancellationToken ct)
    {
        var matches = await _db.Matches
            .Include(m => m.TeamA)!.ThenInclude(t => t!.Players)
            .Include(m => m.TeamB)!.ThenInclude(t => t!.Players)
            .Include(m => m.Tournament)
            .Where(m => m.Status == "live")
            .OrderByDescending(m => m.StreamUrl != "")
            .ThenByDescending(m => m.Id)
            .Take(10)
            .ToListAsync(ct);

        // Прогнозы Elo-модели для live-матчей (результаты кэшируются)
        var predicted = new Dictionary<int, MatchPredictionService.MatchPrediction?>();
        if (predictions.Enabled)
        {
            var candidates = matches
                .Where(m => m.TeamA != null && m.TeamB != null)
                .ToList();
            var results = await Task.WhenAll(candidates.Select(m => predictions.PredictAsync(m, ct)));
            for (var i = 0; i < candidates.Count; i++)
                predicted[candidates[i].Id] = results[i];
        }

        var payload = matches.Select(m => new
        {
            id = m.Id,
            tournamentId = m.TournamentId,
            tournamentName = m.Tournament?.Name ?? $"Турнир #{m.TournamentId}",
            isExternal = m.Tournament?.IsExternal ?? false,
            round = m.Round,
            teamA = m.TeamA?.Name ?? "TBD",
            teamB = m.TeamB?.Name ?? "TBD",
            scoreA = m.ScoreA,
            scoreB = m.ScoreB,
            status = m.Status,
            streamUrl = m.StreamUrl,
            prediction = predicted.TryGetValue(m.Id, out var p) && p != null
                ? new { teamAWinProbability = p.TeamAWinProbability, teamBWinProbability = p.TeamBWinProbability }
                : (object?)null
        });

        return Ok(payload);
    }

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] int tournamentId, [FromServices] MatchPredictionService predictions, CancellationToken ct)
    {
        var tournament = await _db.Tournaments.FindAsync(new object[] { tournamentId }, ct);

        // Внешние турниры: лениво синхронизируем матчи из Liquipedia в БД,
        // дальше читаем их так же, как локальные
        if (tournament != null && tournament.IsExternal)
            await _sync.SyncMatchesAsync(tournament, ct);

        var matchesFromDb = await _db.Matches
            .Include(m => m.TeamA)!.ThenInclude(t => t!.Players)
            .Include(m => m.TeamB)!.ThenInclude(t => t!.Players)
            .Include(m => m.Tournament)
            .Where(m => m.TournamentId == tournamentId)
            .OrderBy(m => m.RoundNumber)
            .ToListAsync(ct);

        if (matchesFromDb.Any())
        {
            // Прогнозы Elo-модели (незавершённые матчи с обеими командами);
            // сервис недоступен/выключен → prediction = null
            var predicted = new Dictionary<int, MatchPredictionService.MatchPrediction?>();
            if (predictions.Enabled)
            {
                var candidates = matchesFromDb
                    .Where(m => m.Status != "finished" && m.TeamA != null && m.TeamB != null)
                    .ToList();
                var results = await Task.WhenAll(candidates.Select(m => predictions.PredictAsync(m, ct)));
                for (var i = 0; i < candidates.Count; i++)
                    predicted[candidates[i].Id] = results[i];
            }

            var payload = matchesFromDb.Select(m => new
            {
                id = m.Id,
                tournamentId,
                teamA = m.TeamA?.Name ?? "TBD",
                teamB = m.TeamB?.Name ?? "TBD",
                scoreA = m.ScoreA,
                scoreB = m.ScoreB,
                status = m.Status,
                round = m.Round,
                groupName = string.Empty,
                streamUrl = m.StreamUrl,
                prediction = predicted.TryGetValue(m.Id, out var p) && p != null
                    ? new { teamAWinProbability = p.TeamAWinProbability, teamBWinProbability = p.TeamBWinProbability }
                    : (object?)null
            });
            return Ok(payload);
        }

        return Ok(new List<object>());
    }

    [HttpPut("{id:int}/result")]
    public async Task<IActionResult> SetMatchResult(int id, [FromBody] MatchResultRequest request, [FromServices] ActivityLogService activity, [FromServices] MatchPredictionService predictions)
    {
        if (!Infrastructure.AuthTokenHelper.IsInAnyRole(Request, "admin", "judge"))
            return StatusCode(403, new { message = "Только администратор или судья может изменять результаты" });

        var match = await _db.Matches
            .Include(m => m.NextMatch)
            .Include(m => m.TeamA)
            .Include(m => m.TeamB)
            .Include(m => m.Tournament)
            .FirstOrDefaultAsync(m => m.Id == id);

        if (match == null)
            return NotFound(new { message = "Матч не найден" });

        if (match.Tournament?.IsExternal == true)
            return BadRequest(new { message = "Матчи внешнего турнира доступны только для просмотра" });

        match.ScoreA = request.ScoreA;
        match.ScoreB = request.ScoreB;
        match.Status = "live";

        if (match.ScoreA == match.ScoreB && match.NextMatchId.HasValue)
            return BadRequest(new { message = "Для матчей плей-офф ничья недопустима. Укажите победителя." });

        if (match.ScoreA >= 16 || match.ScoreB >= 16)
        {
            match.Status = "finished";
            match.WinnerId = match.ScoreA > match.ScoreB ? match.TeamAId : match.TeamBId;

            if (match.NextMatch != null && match.WinnerId.HasValue)
            {
                if (match.NextMatch.TeamAId == null)
                {
                    match.NextMatch.TeamAId = match.WinnerId;
                }
                else if (match.NextMatch.TeamBId == null && match.NextMatch.TeamAId != match.WinnerId)
                {
                    match.NextMatch.TeamBId = match.WinnerId;
                }
            }
        }

        await _db.SaveChangesAsync();

        // Счёт изменился — закэшированный прогноз нейросети устарел
        predictions.Invalidate(match);

        var payload = new
        {
            id = match.Id,
            tournamentId = match.TournamentId,
            scoreA = match.ScoreA,
            scoreB = match.ScoreB,
            status = match.Status,
            updated = true
        };

        await _hub.Clients.Group($"tournament:{match.TournamentId}").SendAsync("matchUpdated", payload);
        if (match.Status == "live")
            await _discord.NotifyMatchLiveAsync(match);
        if (match.Status == "finished")
            await activity.LogAsync("match_finished",
                $"Матч {match.TeamA?.Name ?? "TBD"} — {match.TeamB?.Name ?? "TBD"} завершён со счётом {match.ScoreA}:{match.ScoreB}");

        return Ok(payload);
    }


    [HttpPut("{id:int}/stream")]
    public async Task<IActionResult> AttachStream(int id, [FromBody] MatchStreamRequest request)
    {
        if (!Infrastructure.AuthTokenHelper.IsInAnyRole(Request, "admin", "judge"))
            return StatusCode(403, new { message = "Только администратор или судья может привязывать трансляции" });

        var match = await _db.Matches.FirstOrDefaultAsync(m => m.Id == id);
        if (match == null)
            return NotFound(new { message = "Матч не найден" });

        var url = request.StreamUrl.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https"))
            return BadRequest(new { message = "Укажите корректную ссылку на трансляцию" });

        match.StreamUrl = url;
        await _db.SaveChangesAsync();

        return Ok(new { match.Id, match.TournamentId, match.StreamUrl });
    }

}