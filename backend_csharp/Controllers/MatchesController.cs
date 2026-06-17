using Infrastructure;
using Data;
using Hubs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Models;
using Services;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;

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

    public class MatchAutoServerRequest
    {
        public bool IsAutoServer { get; set; }
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

            var currentUserId = User.GetUserId();
            var userPredictions = currentUserId.HasValue
                ? await _db.MatchPredictions
                    .Where(p => p.UserId == currentUserId.Value)
                    .ToDictionaryAsync(p => p.MatchId, p => p.PredictedTeamId, ct)
                : new Dictionary<int, int>();

            var payload = matchesFromDb.Select(m => new
            {
                id = m.Id,
                tournamentId,
                team_a_id = m.TeamAId,
                team_b_id = m.TeamBId,
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
                    : (object?)null,
                userPredictionTeamId = userPredictions.TryGetValue(m.Id, out var teamId) ? (int?)teamId : null
            });
            return Ok(payload);
        }

        return Ok(new List<object>());
    }

    public class PredictRequest
    {
        [Required]
        public int PredictedTeamId { get; set; }
    }

    [HttpPost("{id:int}/predict")]
    [Authorize]
    public async Task<IActionResult> Predict(int id, [FromBody] PredictRequest request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null)
            return Unauthorized();

        var match = await _db.Matches
            .Include(m => m.TeamA)
            .Include(m => m.TeamB)
            .FirstOrDefaultAsync(m => m.Id == id, ct);

        if (match == null)
            return NotFound(new { message = "Матч не найден." });

        if (match.Status != "planned")
            return BadRequest(new { message = "Прогнозы принимаются только до начала матча." });

        if (match.TeamAId != request.PredictedTeamId && match.TeamBId != request.PredictedTeamId)
            return BadRequest(new { message = "Выбранная команда не участвует в этом матче." });

        var existingPrediction = await _db.MatchPredictions
            .FirstOrDefaultAsync(p => p.UserId == userId.Value && p.MatchId == id, ct);

        if (existingPrediction != null)
        {
            existingPrediction.PredictedTeamId = request.PredictedTeamId;
            existingPrediction.CreatedAtUtc = DateTime.UtcNow;
            _db.MatchPredictions.Update(existingPrediction);
        }
        else
        {
            var prediction = new MatchPrediction
            {
                UserId = userId.Value,
                MatchId = id,
                PredictedTeamId = request.PredictedTeamId,
                Status = "Pending"
            };
            _db.MatchPredictions.Add(prediction);
        }

        await _db.SaveChangesAsync(ct);

        var teamAName = match.TeamA?.Name ?? "TBD";
        var teamBName = match.TeamB?.Name ?? "TBD";
        var truncatedA = teamAName.Length > 10 ? teamAName.Substring(0, 7) + "..." : teamAName;
        var truncatedB = teamBName.Length > 10 ? teamBName.Substring(0, 7) + "..." : teamBName;

        var classA = request.PredictedTeamId == match.TeamAId ? "btn-accent" : "btn-outline-accent";
        var classB = request.PredictedTeamId == match.TeamBId ? "btn-accent" : "btn-outline-accent";

        var html = $@"
<div class=""btn-group btn-group-sm pickem-group"" role=""group"">
  <button type=""button"" class=""btn {classA}"" hx-post=""/play/tournaments/matches/{id}/predict/"" hx-vals='{{""predicted_team_id"": {match.TeamAId}}}' hx-target=""closest .pickem-group"" hx-swap=""outerHTML"">{truncatedA}</button>
  <button type=""button"" class=""btn {classB}"" hx-post=""/play/tournaments/matches/{id}/predict/"" hx-vals='{{""predicted_team_id"": {match.TeamBId}}}' hx-target=""closest .pickem-group"" hx-swap=""outerHTML"">{truncatedB}</button>
</div>";

        return Content(html, "text/html");
    }

    [HttpGet("{id}/comments")]
    public async Task<IActionResult> GetMatchComments(int id, [FromQuery] bool internalLobby = false, CancellationToken ct = default)
    {
        var query = _db.MatchComments
            .Include(c => c.User)
            .Where(c => c.MatchId == id);
            
        if (!internalLobby)
            query = query.Where(c => !c.IsInternalLobby);
        else
            query = query.Where(c => c.IsInternalLobby);

        var comments = await query.OrderBy(c => c.TimestampUtc).ToListAsync(ct);

        return Ok(comments.Select(c => new
        {
            id = c.Id,
            matchId = c.MatchId,
            userId = c.UserId,
            nickname = c.User?.Nickname ?? "Unknown",
            avatarUrl = c.User?.AvatarUrl ?? c.User?.FaceitAvatar,
            message = c.Message,
            isInternalLobby = c.IsInternalLobby,
            timestampUtc = c.TimestampUtc,
            predictorMMR = c.User?.PredictorMMR ?? 1000,
            badges = _db.UserBadges.Where(ub => ub.UserId == c.UserId).Select(ub => new { ub.Badge.Name, ub.Badge.IconUrlOrCss, ub.Badge.ColorCss }).ToList()
        }));
    }

    [HttpPut("{id:int}/result")]
    public async Task<IActionResult> SetMatchResult(int id, [FromBody] MatchResultRequest request, [FromServices] ActivityLogService activity, [FromServices] MatchPredictionService predictions)
    {
        if (!User.IsInRole("admin") || User.IsInRole("judge"))
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

    [HttpPut("{id:int}/autoserver")]
    public async Task<IActionResult> ToggleAutoServer(int id, [FromBody] MatchAutoServerRequest request)
    {
        if (!User.IsInRole("admin") || User.IsInRole("judge"))
            return StatusCode(403, new { message = "Нет доступа" });

        var match = await _db.Matches.FirstOrDefaultAsync(m => m.Id == id);
        if (match == null) return NotFound();

        match.IsAutoServer = request.IsAutoServer;
        await _db.SaveChangesAsync();

        return Ok(new { id = match.Id, isAutoServer = match.IsAutoServer });
    }


    [HttpPut("{id:int}/stream")]
    public async Task<IActionResult> AttachStream(int id, [FromBody] MatchStreamRequest request)
    {
        if (!User.IsInRole("admin") || User.IsInRole("judge"))
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