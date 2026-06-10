using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Data;
using Microsoft.EntityFrameworkCore;
using Models;

namespace Services;

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

    public async Task<TournamentPlanVm> BuildPlanAsync(Tournament tournament, CancellationToken ct = default)
    {
        if (tournament.IsExternal)
            return await BuildExternalPlanAsync(tournament, ct);
        var approvedTeams = await LoadApprovedTeamsAsync(tournament.Id, ct);
        var stageType = NormalizeStageType(tournament);
        var groups = stageType == "groups" ? BuildGroupPreviews(approvedTeams) : new List<PlannedGroup>();

        var matches = await _db.Matches
            .Include(m => m.TeamA)
            .Include(m => m.TeamB)
            .Where(m => m.TournamentId == tournament.Id)
            .OrderBy(m => m.RoundNumber)
            .ThenBy(m => m.Id)
            .ToListAsync(ct);

        var plannedMatches = matches
            .Select(m => new PlannedMatch(
                m.Round,
                m.TeamA?.Name ?? "TBD",
                m.TeamB?.Name ?? "TBD",
                m.ScoreA,
                m.ScoreB,
                m.Status))
            .ToList();

        var summary = plannedMatches.Any()
            ? "Сетка успешно сгенерирована и сохранена в базу данных."
            : approvedTeams.Count < 2
                ? "Сетка пока не построена — нужны заявки команд."
                : "Сетка ещё не сгенерирована. Нажмите «Сгенерировать сетку матчей».";

        return new TournamentPlanVm(stageType, groups, plannedMatches, summary);
    }

    private async Task<TournamentPlanVm> BuildExternalPlanAsync(Tournament tournament, CancellationToken ct)
{
    var matches = await _db.Matches
        .Include(m => m.TeamA)
        .Include(m => m.TeamB)
        .Where(m => m.TournamentId == tournament.Id)
        .OrderBy(m => m.RoundNumber)
        .ThenBy(m => m.Id)
        .ToListAsync(ct);

    var stageType = NormalizeStageType(tournament);
    var distinctTeams = matches
        .SelectMany(m => new[] { m.TeamA, m.TeamB })
        .Where(t => t != null)
        .DistinctBy(t => t!.Id)
        .Select((t, idx) => new PlannedTeam(t!.Id, t.Name, idx + 1, 0m))
        .ToList();

    var groups = stageType == "groups" ? BuildGroupPreviews(distinctTeams) : new List<PlannedGroup>();

    var plannedMatches = matches
        .Select(m => new PlannedMatch(
            NormalizeExternalRoundLabel(m.Round, m.RoundNumber),
            m.TeamA?.Name ?? "TBD",
            m.TeamB?.Name ?? "TBD",
            m.ScoreA,
            m.ScoreB,
            m.Status))
        .ToList();

    var provider = tournament.Provider ?? "external";
    var summary = plannedMatches.Any()
        ? $"Данные загружены из {provider}. Редактирование недоступно."
        : $"Матчи ещё не синхронизированы из {provider}.";

    return new TournamentPlanVm(stageType, groups, plannedMatches, summary);
}
    private static string NormalizeExternalRoundLabel(string round, int roundNumber)
{
    if (string.IsNullOrWhiteSpace(round)) return $"Round {roundNumber}";
    var lower = round.Trim().ToLowerInvariant();
    if (lower.Contains("final") && !lower.Contains("semi") && !lower.Contains("quarter")) return "Final";
    if (lower.Contains("semi")) return "Semifinal";
    if (lower.Contains("quarter")) return "Quarterfinal";
    if (lower.Contains("group")) return round.Trim();
    return System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(lower);
}

    public async Task<bool> GenerateAndSaveBracketAsync(int tournamentId, CancellationToken ct = default)
    {
        var tournament = await _db.Tournaments.FirstOrDefaultAsync(t => t.Id == tournamentId, ct);
        if (tournament == null || tournament.IsExternal)
            return false;

        var teams = await LoadApprovedTeamEntitiesAsync(tournamentId, ct);
        if (teams.Count < 2)
            return false;

        var oldMatches = await _db.Matches.Where(m => m.TournamentId == tournamentId).ToListAsync(ct);
        if (oldMatches.Count > 0)
        {
            _db.Matches.RemoveRange(oldMatches);
            await _db.SaveChangesAsync(ct);
        }

        var matchesToSave = NormalizeStageType(tournament) == "groups"
            ? BuildGroupStageMatches(tournamentId, teams)
            : BuildSingleEliminationTree(tournamentId, teams);

        if (matchesToSave.Count == 0)
            return false;

        await _db.Matches.AddRangeAsync(matchesToSave, ct);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<List<Team>> LoadApprovedTeamEntitiesAsync(int tournamentId, CancellationToken ct)
    {
        var applications = await _db.TournamentApplications
            .Include(a => a.Team)
                .ThenInclude(t => t!.Players)
            .Where(a => a.TournamentId == tournamentId && a.Status == "approved" && a.Team != null)
            .AsNoTracking()
            .ToListAsync(ct);

        return applications
            .Where(a => a.Team != null)
            .Select(a => a.Team!)
            .GroupBy(t => t.Id)
            .Select(g => g.First())
            .ToList();
    }

    private async Task<List<PlannedTeam>> LoadApprovedTeamsAsync(int tournamentId, CancellationToken ct)
    {
        var teams = await LoadApprovedTeamEntitiesAsync(tournamentId, ct);
        return teams
            .Select(t => new PlannedTeam(
                t.Id,
                t.Name,
                0,
                t.Players.Any() ? Math.Round(t.Players.Average(p => p.Rating ?? 0m), 2) : 0m))
            .OrderByDescending(t => t.RatingAverage)
            .ThenBy(t => t.Name)
            .Select((t, index) => t with { Seed = index + 1 })
            .ToList();
    }

    private static string NormalizeStageType(Tournament tournament)
    {
        var value = (tournament.StageType ?? string.Empty).Trim().ToLowerInvariant();
        if (value == "groups")
            return "groups";

        var format = (tournament.Format ?? string.Empty).Trim().ToLowerInvariant();
        return format == "group_stage" ? "groups" : "single";
    }

    private static List<PlannedGroup> BuildGroupPreviews(List<PlannedTeam> teams)
    {
        var grouped = DistributeIntoGroups(teams.Select(t => new SeededTeam(t.TeamId, t.Name, t.Seed, t.RatingAverage)).ToList());
        return grouped
            .Select(g => new PlannedGroup(
                g.Name,
                g.Teams.Select(t => new PlannedTeam(t.TeamId, t.Name, t.Seed, t.RatingAverage)).ToList()))
            .ToList();
    }

    private static List<Match> BuildGroupStageMatches(int tournamentId, List<Team> teams)
    {
        var seededTeams = teams
            .Select(t => new SeededTeam(
                t.Id,
                t.Name,
                0,
                t.Players.Any() ? Math.Round(t.Players.Average(p => p.Rating ?? 0m), 2) : 0m))
            .OrderByDescending(t => t.RatingAverage)
            .ThenBy(t => t.Name)
            .Select((t, index) => t with { Seed = index + 1 })
            .ToList();

        var groups = DistributeIntoGroups(seededTeams);
        var matches = new List<Match>();
        var roundNumber = 1;

        foreach (var group in groups)
        {
            for (var i = 0; i < group.Teams.Count; i++)
            {
                for (var j = i + 1; j < group.Teams.Count; j++)
                {
                    matches.Add(new Match
                    {
                        TournamentId = tournamentId,
                        RoundNumber = roundNumber,
                        Round = group.Name,
                        TeamAId = group.Teams[i].TeamId,
                        TeamBId = group.Teams[j].TeamId,
                        Status = "planned"
                    });
                }
            }

            roundNumber += 1;
        }

        return matches;
    }

    private List<Match> BuildSingleEliminationTree(int tournamentId, List<Team> teams)
    {
        var rankedTeams = teams
            .OrderByDescending(t => t.Players.Any() ? t.Players.Average(p => p.Rating ?? 0m) : 0m)
            .ThenBy(t => t.Name)
            .ToList();

        var bracketSize = NextPowerOfTwo(rankedTeams.Count);
        var totalRounds = (int)Math.Log2(bracketSize);
        var allMatches = new List<Match>();
        var currentRoundMatches = new List<Match>
        {
            new Match
            {
                TournamentId = tournamentId,
                RoundNumber = totalRounds,
                Round = GetRoundLabel(totalRounds, totalRounds),
                Status = "planned"
            }
        };
        allMatches.AddRange(currentRoundMatches);

        for (var round = totalRounds - 1; round >= 1; round--)
        {
            var nextRoundMatches = new List<Match>();
            foreach (var parent in currentRoundMatches)
            {
                var left = new Match
                {
                    TournamentId = tournamentId,
                    RoundNumber = round,
                    Round = GetRoundLabel(totalRounds, round),
                    NextMatch = parent,
                    Status = "planned"
                };
                var right = new Match
                {
                    TournamentId = tournamentId,
                    RoundNumber = round,
                    Round = GetRoundLabel(totalRounds, round),
                    NextMatch = parent,
                    Status = "planned"
                };

                nextRoundMatches.Add(left);
                nextRoundMatches.Add(right);
                allMatches.Add(left);
                allMatches.Add(right);
            }
            currentRoundMatches = nextRoundMatches;
        }

        var seedPositions = BuildSeedPositions(bracketSize);
        var seededSlots = seedPositions
            .Select(position => position <= rankedTeams.Count ? rankedTeams[position - 1] : null)
            .ToList();

        for (var slot = 0; slot < currentRoundMatches.Count; slot++)
        {
            var match = currentRoundMatches[slot];
            var leftTeam = seededSlots[slot * 2];
            var rightTeam = seededSlots[slot * 2 + 1];

            match.TeamAId = leftTeam?.Id;
            match.TeamBId = rightTeam?.Id;

            if (leftTeam != null && rightTeam == null)
            {
                match.WinnerId = leftTeam.Id;
                match.Status = "approved";
                AdvanceWinnerToNextMatch(match, leftTeam.Id);
            }
            else if (leftTeam == null && rightTeam != null)
            {
                match.WinnerId = rightTeam.Id;
                match.Status = "approved";
                AdvanceWinnerToNextMatch(match, rightTeam.Id);
            }
        }

        return allMatches;
    }

    private static int NextPowerOfTwo(int value)
    {
        var size = 1;
        while (size < value)
            size *= 2;
        return size;
    }

    private static string GetRoundLabel(int totalRounds, int roundNumber)
    {
        if (roundNumber == totalRounds)
            return "Final";

        var matchesInRound = 1 << (totalRounds - roundNumber);
        return matchesInRound switch
        {
            2 => "Semifinal",
            4 => "Quarterfinal",
            _ => $"R{roundNumber}"
        };
    }

    private static List<int> BuildSeedPositions(int bracketSize)
    {
        if (bracketSize <= 1)
            return new List<int> { 1 };

        var previous = BuildSeedPositions(bracketSize / 2);
        var result = new List<int>(bracketSize);
        foreach (var seed in previous)
        {
            result.Add(seed);
            result.Add(bracketSize + 1 - seed);
        }
        return result;
    }

    private static void AdvanceWinnerToNextMatch(Match completedMatch, int winnerId)
    {
        if (completedMatch.NextMatch == null)
            return;

        if (!completedMatch.NextMatch.TeamAId.HasValue)
            completedMatch.NextMatch.TeamAId = winnerId;
        else if (!completedMatch.NextMatch.TeamBId.HasValue && completedMatch.NextMatch.TeamAId != winnerId)
            completedMatch.NextMatch.TeamBId = winnerId;
    }

    private static List<GroupSeedBucket> DistributeIntoGroups(List<SeededTeam> teams)
    {
        if (teams.Count == 0)
            return new List<GroupSeedBucket>();

        var groupCount = teams.Count switch
        {
            <= 4 => 1,
            <= 8 => 2,
            <= 12 => 3,
            _ => 4,
        };

        var groups = Enumerable.Range(0, groupCount)
            .Select(index => new GroupSeedBucket(((char)('A' + index)).ToString()))
            .ToList();

        var direction = 1;
        var cursor = 0;
        foreach (var team in teams)
        {
            groups[cursor].Teams.Add(team);

            if (groupCount == 1)
                continue;

            cursor += direction;
            if (cursor >= groupCount)
            {
                direction = -1;
                cursor = groupCount - 1;
            }
            else if (cursor < 0)
            {
                direction = 1;
                cursor = 0;
            }
        }

        return groups
            .Where(g => g.Teams.Count > 0)
            .Select(g =>
            {
                g.Teams = g.Teams.OrderBy(t => t.Seed).ToList();
                return g;
            })
            .ToList();
    }

    private sealed record SeededTeam(int TeamId, string Name, int Seed, decimal RatingAverage);

    private sealed class GroupSeedBucket
    {
        public GroupSeedBucket(string shortName)
        {
            Name = $"Group {shortName}";
        }

        public string Name { get; }
        public List<SeededTeam> Teams { get; set; } = new();
    }
}
