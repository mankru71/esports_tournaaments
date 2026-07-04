using Data;
using Microsoft.EntityFrameworkCore;
using Models;

namespace Services;

/// <summary>
/// Аналитика по локальным данным платформы.
/// Винрейты команд считаются по завершённым матчам НЕвнешних турниров:
///  - общий винрейт;
///  - групповая стадия (Round = "Group X");
///  - плей-офф (матч встроен в сетку: NextMatchId задан, либо Round = Final/Semifinal/Quarterfinal/R{n});
///  - «упорные» матчи (разница в счёте ≤ 2, например 16:14) — замена comeback-метрики,
///    т.к. по-халфовая статистика в БД не хранится, есть только итоговый счёт.
/// </summary>
public class AnalyticsService
{
    private readonly AppDbContext _db;

    public AnalyticsService(AppDbContext db)
    {
        _db = db;
    }

    public sealed record TeamWinRate(
        int TeamId,
        string TeamName,
        int TotalMatches,
        int Wins,
        decimal WinRate,
        int GroupMatches,
        int GroupWins,
        decimal GroupWinRate,
        int PlayoffMatches,
        int PlayoffWins,
        decimal PlayoffWinRate,
        int CloseMatches,
        int CloseWins,
        decimal CloseWinRate);

    public async Task<List<TeamWinRate>> GetTeamWinRatesAsync(string? game = null, CancellationToken ct = default)
    {
        var query = _db.Matches
            .Include(m => m.TeamA)
            .Include(m => m.TeamB)
            .Include(m => m.Tournament)
            .Where(m => m.Status == "finished"
                        && m.WinnerId != null
                        && m.TeamAId != null
                        && m.TeamBId != null);

        if (!string.IsNullOrWhiteSpace(game))
        {
            var lowerGame = game.Trim().ToLowerInvariant();
            if (lowerGame == "counterstrike" || lowerGame == "cs" || lowerGame == "cs2" || lowerGame == "cs:go" || lowerGame == "counter-strike")
            {
                query = query.Where(m => m.Tournament!.Game.ToLower() == "counterstrike" || m.Tournament!.Game.ToLower() == "counter-strike");
            }
            else if (lowerGame == "dota2" || lowerGame == "dota 2" || lowerGame == "dota")
            {
                query = query.Where(m => m.Tournament!.Game.ToLower() == "dota 2" || m.Tournament!.Game.ToLower() == "dota2");
            }
            else if (lowerGame == "valorant")
            {
                query = query.Where(m => m.Tournament!.Game.ToLower() == "valorant");
            }
            else
            {
                query = query.Where(m => m.Tournament!.Game.ToLower() == lowerGame);
            }
        }

        var finished = await query.ToListAsync(ct);

        var stats = new Dictionary<int, Accumulator>();

        foreach (var match in finished)
        {
            var isGroup = IsGroupStage(match);
            var isPlayoff = !isGroup && IsPlayoff(match);
            var isClose = Math.Abs(match.ScoreA - match.ScoreB) <= 2;

            Accumulate(stats, match.TeamAId!.Value, match.TeamA?.Name, match.WinnerId == match.TeamAId, isGroup, isPlayoff, isClose);
            Accumulate(stats, match.TeamBId!.Value, match.TeamB?.Name, match.WinnerId == match.TeamBId, isGroup, isPlayoff, isClose);
        }

        return stats.Values
            .Select(a => new TeamWinRate(
                a.TeamId,
                a.TeamName,
                a.Total, a.Wins, Rate(a.Wins, a.Total),
                a.Group, a.GroupWins, Rate(a.GroupWins, a.Group),
                a.Playoff, a.PlayoffWins, Rate(a.PlayoffWins, a.Playoff),
                a.Close, a.CloseWins, Rate(a.CloseWins, a.Close)))
            .OrderByDescending(t => t.WinRate)
            .ThenByDescending(t => t.TotalMatches)
            .ThenBy(t => t.TeamName)
            .ToList();
    }

    private static bool IsGroupStage(Match match) =>
        match.Round.StartsWith("Group", StringComparison.OrdinalIgnoreCase);

    private static bool IsPlayoff(Match match)
    {
        if (match.NextMatchId.HasValue)
            return true;

        // Финал — вершина сетки, у него NextMatchId нет; узнаём по названию раунда
        var round = match.Round.Trim();
        return round.Equals("Final", StringComparison.OrdinalIgnoreCase)
               || round.Equals("Semifinal", StringComparison.OrdinalIgnoreCase)
               || round.Equals("Quarterfinal", StringComparison.OrdinalIgnoreCase)
               || System.Text.RegularExpressions.Regex.IsMatch(round, @"^R\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static decimal Rate(int wins, int total) =>
        total == 0 ? 0m : Math.Round(wins * 100m / total, 1);

    private static void Accumulate(Dictionary<int, Accumulator> stats, int teamId, string? teamName, bool won, bool isGroup, bool isPlayoff, bool isClose)
    {
        if (!stats.TryGetValue(teamId, out var acc))
        {
            acc = new Accumulator { TeamId = teamId, TeamName = teamName ?? $"Команда #{teamId}" };
            stats[teamId] = acc;
        }

        acc.Total++;
        if (won) acc.Wins++;

        if (isGroup)
        {
            acc.Group++;
            if (won) acc.GroupWins++;
        }
        else if (isPlayoff)
        {
            acc.Playoff++;
            if (won) acc.PlayoffWins++;
        }

        if (isClose)
        {
            acc.Close++;
            if (won) acc.CloseWins++;
        }
    }

    private sealed class Accumulator
    {
        public int TeamId;
        public string TeamName = string.Empty;
        public int Total;
        public int Wins;
        public int Group;
        public int GroupWins;
        public int Playoff;
        public int PlayoffWins;
        public int Close;
        public int CloseWins;
    }
}
