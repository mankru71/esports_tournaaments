using Data;
using Microsoft.EntityFrameworkCore;
using Models;

namespace Services;

public record PlannedTeam(int TeamId, string Name, int Seed, decimal RatingAverage);
public record PlannedMatch(int Id, string Round, string GroupName, string TeamA, string TeamB, int ScoreA, int ScoreB, string Status, string? StreamUrl);
public record PlannedGroup(string Name, List<PlannedTeam> Teams);
public record GroupStanding(string GroupName, string Team, int Played, int Wins, int Losses, int Points);
public record TournamentPlanVm(string StageType, List<PlannedGroup> Groups, List<PlannedMatch> Matches, List<GroupStanding> Standings, string Summary);

public class TournamentPlanningService
{
    private readonly AppDbContext _db;

    public TournamentPlanningService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<TournamentPlanVm> BuildPlanAsync(Tournament tournament, CancellationToken ct = default)
    {
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
                m.Id,
                m.Round,
                m.GroupName,
                m.TeamA?.Name ?? "TBD",
                m.TeamB?.Name ?? "TBD",
                m.ScoreA,
                m.ScoreB,
                m.Status,
                m.StreamUrl))
            .ToList();

        var standings = stageType == "groups" ? BuildStandings(matches) : new List<GroupStanding>();

        var summary = plannedMatches.Any()
            ? $"Сетка сохранена в базе: {plannedMatches.Count} матчей. Посев выполнен по среднему рейтингу игроков."
            : approvedTeams.Count < 2
                ? "Сетка пока не построена — нужны минимум 2 подтверждённые команды."
                : "Сетка ещё не сгенерирована. Нажмите «Сгенерировать сетку матчей».";

        return new TournamentPlanVm(stageType, groups, plannedMatches, standings, summary);
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
            _db.Matches.RemoveRange(oldMatches);

        var matchesToSave = NormalizeStageType(tournament) == "groups"
            ? BuildGroupStageMatches(tournamentId, teams)
            : BuildSingleEliminationTree(tournamentId, teams);

        if (matchesToSave.Count == 0)
            return false;

        tournament.Status = "planned";
        tournament.CurrentStage = NormalizeStageType(tournament) == "groups" ? "group_stage" : "playoff";
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
        return SeedTeams(teams)
            .Select(t => new PlannedTeam(t.TeamId, t.Name, t.Seed, t.RatingAverage))
            .ToList();
    }

    private static string NormalizeStageType(Tournament tournament)
    {
        var value = (tournament.StageType ?? string.Empty).Trim().ToLowerInvariant();
        if (value == "groups") return "groups";
        var format = (tournament.Format ?? string.Empty).Trim().ToLowerInvariant();
        return format == "group_stage" ? "groups" : "single";
    }

    private static List<SeededTeam> SeedTeams(IEnumerable<Team> teams)
    {
        return teams
            .Select(t => new SeededTeam(
                t.Id,
                t.Name,
                0,
                t.Players.Any() ? Math.Round(t.Players.Average(p => p.Rating ?? 0m), 2) : 0m))
            .OrderByDescending(t => t.RatingAverage)
            .ThenBy(t => t.Name)
            .Select((t, index) => t with { Seed = index + 1 })
            .ToList();
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
        var groups = DistributeIntoGroups(SeedTeams(teams));
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
                        GroupName = group.Name,
                        TeamAId = group.Teams[i].TeamId,
                        TeamBId = group.Teams[j].TeamId,
                        Status = "planned"
                    });
                    roundNumber++;
                }
            }
        }

        return matches;
    }

    private static List<Match> BuildSingleEliminationTree(int tournamentId, List<Team> teams)
    {
        var rankedTeams = SeedTeams(teams);
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

            match.TeamAId = leftTeam?.TeamId;
            match.TeamBId = rightTeam?.TeamId;

            if (leftTeam != null && rightTeam == null)
            {
                match.WinnerId = leftTeam.TeamId;
                match.Status = "approved";
                AdvanceWinnerToNextMatch(match, leftTeam.TeamId);
            }
            else if (leftTeam == null && rightTeam != null)
            {
                match.WinnerId = rightTeam.TeamId;
                match.Status = "approved";
                AdvanceWinnerToNextMatch(match, rightTeam.TeamId);
            }
        }

        return allMatches;
    }

    private static List<GroupStanding> BuildStandings(IEnumerable<Match> matches)
    {
        var table = new Dictionary<(string group, int teamId), (string name, int played, int wins, int losses, int points)>();

        foreach (var match in matches.Where(m => !string.IsNullOrWhiteSpace(m.GroupName)))
        {
            AddTeam(match.GroupName, match.TeamAId, match.TeamA?.Name);
            AddTeam(match.GroupName, match.TeamBId, match.TeamB?.Name);

            if (match.Status == "finished" && match.TeamAId.HasValue && match.TeamBId.HasValue)
            {
                var aWon = match.ScoreA > match.ScoreB;
                Update(match.GroupName, match.TeamAId.Value, aWon);
                Update(match.GroupName, match.TeamBId.Value, !aWon);
            }
        }

        return table
            .Select(x => new GroupStanding(x.Key.group, x.Value.name, x.Value.played, x.Value.wins, x.Value.losses, x.Value.points))
            .OrderBy(x => x.GroupName)
            .ThenByDescending(x => x.Points)
            .ThenBy(x => x.Team)
            .ToList();

        void AddTeam(string group, int? teamId, string? name)
        {
            if (!teamId.HasValue) return;
            var key = (group, teamId.Value);
            if (!table.ContainsKey(key))
                table[key] = (string.IsNullOrWhiteSpace(name) ? "TBD" : name!, 0, 0, 0, 0);
        }

        void Update(string group, int teamId, bool won)
        {
            var key = (group, teamId);
            if (!table.TryGetValue(key, out var row)) return;
            table[key] = (row.name, row.played + 1, row.wins + (won ? 1 : 0), row.losses + (won ? 0 : 1), row.points + (won ? 3 : 0));
        }
    }

    private static int NextPowerOfTwo(int value)
    {
        var size = 1;
        while (size < value) size *= 2;
        return size;
    }

    private static string GetRoundLabel(int totalRounds, int roundNumber)
    {
        if (roundNumber == totalRounds) return "Final";
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
        var positions = new List<int> { 1, 2 };
        while (positions.Count < bracketSize)
        {
            var next = new List<int>();
            var size = positions.Count * 2 + 1;
            foreach (var seed in positions)
            {
                next.Add(seed);
                next.Add(size - seed);
            }
            positions = next;
        }
        return positions;
    }

    private static void AdvanceWinnerToNextMatch(Match match, int winnerId)
    {
        if (match.NextMatch == null) return;
        if (match.NextMatch.TeamAId == null)
        {
            match.NextMatch.TeamAId = winnerId;
        }
        else if (match.NextMatch.TeamBId == null && match.NextMatch.TeamAId != winnerId)
        {
            match.NextMatch.TeamBId = winnerId;
        }
    }

    private record SeededTeam(int TeamId, string Name, int Seed, decimal RatingAverage);
    private record SeededGroup(string Name, List<SeededTeam> Teams);

    private static List<SeededGroup> DistributeIntoGroups(List<SeededTeam> teams)
    {
        var groupCount = Math.Max(1, (int)Math.Ceiling(teams.Count / 4.0));
        groupCount = Math.Min(groupCount, 4);
        var groups = Enumerable.Range(0, groupCount)
            .Select(i => new SeededGroup($"Группа {(char)('A' + i)}", new List<SeededTeam>()))
            .ToList();

        for (var i = 0; i < teams.Count; i++)
        {
            var groupIndex = i % (groupCount * 2) < groupCount
                ? i % groupCount
                : groupCount - 1 - (i % groupCount);
            groups[groupIndex].Teams.Add(teams[i]);
        }

        return groups;
    }
}
