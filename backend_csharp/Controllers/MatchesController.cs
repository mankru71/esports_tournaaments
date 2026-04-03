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
    private readonly PandaScoreService _pandascore;
    private readonly IHubContext<MatchesHub> _hub;

    public MatchesController(AppDbContext db, PandaScoreService pandascore, IHubContext<MatchesHub> hub)
    {
        _db = db;
        _pandascore = pandascore;
        _hub = hub;
    }

    public class MatchResultRequest
    {
        [Range(0, 99)] public int ScoreA { get; set; }
        [Range(0, 99)] public int ScoreB { get; set; }
    }

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] int tournamentId, CancellationToken ct)
    {
        var tournament = await _db.Tournaments.FindAsync(new object[] { tournamentId }, ct);
        
        // 1. Обработка внешних турниров (PandaScore)
        if (tournament != null && tournament.IsExternal && !string.IsNullOrWhiteSpace(tournament.ProviderTournamentId) && _pandascore.Enabled)
        {
            var matches = await _pandascore.GetMatchesForTournamentAsync(tournament.ProviderTournamentId!, 50, tournament.Game, ct);
            var payload = matches.Select(m => new
            {
                id = m.Id,
                tournamentId,
                teamA = m.OpponentA ?? "TBD",
                teamB = m.OpponentB ?? "TBD",
                scoreA = m.ScoreA,
                scoreB = m.ScoreB,
                status = NormalizeMatchStatus(m.Status),
                round = string.IsNullOrWhiteSpace(m.Name) ? "Match" : m.Name!,
                groupName = string.Empty,
                streamUrl = m.StreamUrl ?? string.Empty
            });
            return Ok(payload);
        }

        // 2. Обработка локальных турниров (Берем данные из таблицы Matches)
        var matchesFromDb = await _db.Matches
            .Include(m => m.TeamA)
            .Include(m => m.TeamB)
            .Where(m => m.TournamentId == tournamentId)
            .OrderBy(m => m.RoundNumber)
            .ToListAsync(ct);

        if (matchesFromDb.Any())
        {
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
                streamUrl = string.Empty
            });
            return Ok(payload);
        }

        return Ok(new List<object>()); // Возвращаем пустой список, если матчей еще нет
    }

    [HttpPut("{id:int}/result")]
    public async Task<IActionResult> SetMatchResult(int id, [FromBody] MatchResultRequest request)
    {
        // Проверка прав доступа (Админ или Судья)
        if (!Infrastructure.AuthTokenHelper.IsInAnyRole(Request, "admin", "judge"))
            return StatusCode(403, new { message = "Только администратор или судья может изменять результаты" });

        var match = await _db.Matches
            .Include(m => m.NextMatch)
            .FirstOrDefaultAsync(m => m.Id == id);

        if (match == null)
            return NotFound(new { message = "Матч не найден" });

        match.ScoreA = request.ScoreA;
        match.ScoreB = request.ScoreB;
        match.Status = "live";

        // Логика завершения матча (например, до 16 раундов в CS)
        if (match.ScoreA >= 16 || match.ScoreB >= 16)
        {
            match.Status = "finished";
            match.WinnerId = match.ScoreA > match.ScoreB ? match.TeamAId : match.TeamBId;

            // АВТОМАТИЧЕСКОЕ ПРОДВИЖЕНИЕ ПО СЕТКЕ
            if (match.NextMatch != null && match.WinnerId.HasValue)
            {
                // Записываем победителя в следующий матч как Команду A или Команду B
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

        var payload = new
        {
            id = match.Id,
            tournamentId = match.TournamentId,
            scoreA = match.ScoreA,
            scoreB = match.ScoreB,
            status = match.Status,
            updated = true
        };

        // Отправка обновления через SignalR всем подписчикам турнира
        await _hub.Clients.Group($"tournament:{match.TournamentId}").SendAsync("matchUpdated", payload);

        return Ok(payload);
    }

    private static string NormalizeMatchStatus(string? status)
    {
        var s = (status ?? string.Empty).Trim().ToLowerInvariant();
        return s switch
        {
            "running" => "live",
            "finished" => "finished",
            "canceled" => "finished",
            "postponed" => "planned",
            "not_started" => "planned",
            _ => "planned",
        };
    }
}