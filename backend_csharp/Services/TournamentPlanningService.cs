using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Data;
using Microsoft.EntityFrameworkCore;
using Models;

namespace Services;

// Рекорды должны идти строго ПОСЛЕ namespace
public record PlannedTeam(int TeamId, string Name, int Seed, decimal RatingAverage);
public record PlannedMatch(string Round, string TeamA, string TeamB, int ScoreA, int ScoreB, string Status);
public record PlannedGroup(string Name, List<PlannedTeam> Teams);
public record TournamentPlanVm(string StageType, List<PlannedGroup> Groups, List<PlannedMatch> Matches, string Summary);

public class TournamentPlanningService
{
    private readonly AppDbContext _db;

    public TournamentPlanningService(AppDbContext db)
    {
        _db = db;
    }

    // Метод для обратной совместимости со старыми контроллерами
    public async Task<TournamentPlanVm> BuildPlanAsync(Tournament tournament, CancellationToken ct = default)
    {
        // Вызываем наш новый метод генерации и сохранения в базу
        await GenerateAndSaveBracketAsync(tournament.Id, ct);
        
        return new TournamentPlanVm(
            tournament.StageType ?? "single",
            new List<PlannedGroup>(),
            new List<PlannedMatch>(),
            "Сетка успешно сгенерирована и сохранена в базу данных."
        );
    }

    // Наш новый метод, который реально сохраняет матчи в PostgreSQL
    public async Task<bool> GenerateAndSaveBracketAsync(int tournamentId, CancellationToken ct = default)
    {
        var tournament = await _db.Tournaments.FindAsync(new object[] { tournamentId }, ct);
        if (tournament == null) return false;

        // Удаляем старую сетку, если она была (для перегенерации)
        var oldMatches = await _db.Matches.Where(m => m.TournamentId == tournamentId).ToListAsync(ct);
        if (oldMatches.Any())
        {
            _db.Matches.RemoveRange(oldMatches);
            await _db.SaveChangesAsync(ct);
        }

        // Получаем подтвержденные команды, сортируем по среднему рейтингу
        var approvedApps = await _db.TournamentApplications
            .Include(a => a.Team)
                .ThenInclude(t => t!.Players)
            .Where(a => a.TournamentId == tournamentId && a.Status == "approved")
            .ToListAsync(ct);

        var teams = approvedApps
            .Where(a => a.Team != null)
            .Select(a => a.Team!)
            .OrderByDescending(t => t.Players.Any() ? t.Players.Average(p => p.Rating ?? 0) : 0)
            .ToList();

        if (teams.Count < 2) return false;

        // Генерируем объекты матчей
        var matchesToSave = BuildSingleEliminationTree(tournamentId, teams);

        await _db.Matches.AddRangeAsync(matchesToSave);
        await _db.SaveChangesAsync(ct);

        return true;
    }

    private List<Match> BuildSingleEliminationTree(int tournamentId, List<Team> teams)
    {
        var allMatches = new List<Match>();
        
        int bracketSize = (int)Math.Pow(2, Math.Ceiling(Math.Log(teams.Count, 2)));
        int byesCount = bracketSize - teams.Count;
        int totalRounds = (int)Math.Log(bracketSize, 2);

        var currentRoundMatches = new List<Match>();

        var finalMatch = new Match { TournamentId = tournamentId, RoundNumber = totalRounds, Round = "Final" };
        currentRoundMatches.Add(finalMatch);
        allMatches.Add(finalMatch);

        for (int r = totalRounds - 1; r >= 1; r--)
        {
            var nextRoundMatches = new List<Match>();
            foreach (var match in currentRoundMatches)
            {
                var prev1 = new Match { TournamentId = tournamentId, RoundNumber = r, Round = $"R{r}", NextMatch = match };
                var prev2 = new Match { TournamentId = tournamentId, RoundNumber = r, Round = $"R{r}", NextMatch = match };
                
                nextRoundMatches.Add(prev1);
                nextRoundMatches.Add(prev2);
                allMatches.AddRange(new[] { prev1, prev2 });
            }
            currentRoundMatches = nextRoundMatches; 
        }

        var teamQueue = new Queue<Team>(teams);

        foreach (var match in currentRoundMatches)
        {
            if (teamQueue.Count > 0)
                match.TeamAId = teamQueue.Dequeue().Id;

            if (byesCount > 0)
            {
                byesCount--;
                match.TeamBId = null; 
                match.WinnerId = match.TeamAId; 
                match.Status = "finished";
                
                AdvanceWinnerToNextMatch(match, match.TeamAId!.Value);
            }
            else if (teamQueue.Count > 0)
            {
                match.TeamBId = teamQueue.Dequeue().Id;
            }
        }

        return allMatches;
    }

    private void AdvanceWinnerToNextMatch(Match completedMatch, int winnerId)
    {
        if (completedMatch.NextMatch == null) return;

        if (completedMatch.NextMatch.TeamAId == null)
            completedMatch.NextMatch.TeamAId = winnerId;
        else
            completedMatch.NextMatch.TeamBId = winnerId;
    }
}