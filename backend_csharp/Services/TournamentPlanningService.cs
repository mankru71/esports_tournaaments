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
        var teams = await LoadTeamsAsync(tournament.Id, ct);
        if (teams.Count == 0)
        {
            return new TournamentPlanVm(
                tournament.StageType,
                new List<PlannedGroup>(),
                new List<PlannedMatch>(),
                "Недостаточно зарегистрированных команд для построения сетки."
            );
        }

        if (IsGroupStage(tournament))
        {
            var groups = BuildGroups(teams, 4);
            var matches = BuildGroupMatches(groups);
            return new TournamentPlanVm(
                "groups",
                groups,
                matches,
                $"Сформировано {groups.Count} групп и {matches.Count} матчей группового этапа."
            );
        }

        var singleMatches = BuildSingleElimination(teams);
        return new TournamentPlanVm(
            "single",
            new List<PlannedGroup>(),
            singleMatches,
            $"Построена сетка single elimination на {teams.Count} команд."
        );
    }

    private bool IsGroupStage(Tournament tournament)
    {
        var format = (tournament.Format ?? string.Empty).ToLowerInvariant();
        var stageType = (tournament.StageType ?? string.Empty).ToLowerInvariant();
        return format.Contains("group") || stageType.Contains("group");
    }

    private async Task<List<PlannedTeam>> LoadTeamsAsync(int tournamentId, CancellationToken ct)
    {
        var approvedApps = await _db.TournamentApplications
            .Include(a => a.Team)
                .ThenInclude(t => t!.Players)
            .Where(a => a.TournamentId == tournamentId && (a.Status == "approved" || a.Status == "pending"))
            .ToListAsync(ct);

        var teams = approvedApps
            .Where(a => a.Team != null)
            .Select(a => a.Team!)
            .GroupBy(t => t.Id)
            .Select(g => g.First())
            .Select(t =>
            {
                var ratings = t.Players.Where(p => p.Rating.HasValue).Select(p => p.Rating!.Value).ToList();
                var avgRating = ratings.Count > 0 ? ratings.Average() : 0m;
                return new { Team = t, Rating = avgRating };
            })
            .OrderByDescending(x => x.Rating)
            .ThenBy(x => x.Team.Name)
            .ToList();

        var result = new List<PlannedTeam>();
        var seed = 1;
        foreach (var item in teams)
        {
            result.Add(new PlannedTeam(item.Team.Id, item.Team.Name, seed++, item.Rating));
        }

        return result;
    }

    private static List<PlannedMatch> BuildSingleElimination(List<PlannedTeam> teams)
    {
        var matches = new List<PlannedMatch>();
        var roundTeams = teams.Select(t => t.Name).ToList();
        var roundNumber = 1;

        while (roundTeams.Count > 1)
        {
            var nextRound = new List<string>();
            for (var i = 0; i < roundTeams.Count; i += 2)
            {
                var teamA = roundTeams[i];
                var teamB = i + 1 < roundTeams.Count ? roundTeams[i + 1] : "BYE";
                matches.Add(new PlannedMatch($"R{roundNumber}", teamA, teamB, 0, 0, "planned"));
                nextRound.Add(teamB == "BYE" ? teamA : "Победитель матча");
            }
            roundTeams = nextRound;
            roundNumber++;
        }

        return matches;
    }

    private static List<PlannedGroup> BuildGroups(List<PlannedTeam> teams, int groupSize)
    {
        var groups = new List<PlannedGroup>();
        var groupCount = (int)Math.Ceiling(teams.Count / (double)groupSize);
        for (var i = 0; i < groupCount; i++)
        {
            groups.Add(new PlannedGroup($"Группа {(char)('A' + i)}", new List<PlannedTeam>()));
        }

        for (var i = 0; i < teams.Count; i++)
        {
            groups[i % groupCount].Teams.Add(teams[i]);
        }

        return groups;
    }

    private static List<PlannedMatch> BuildGroupMatches(List<PlannedGroup> groups)
    {
        var matches = new List<PlannedMatch>();
        foreach (var group in groups)
        {
            for (var i = 0; i < group.Teams.Count; i++)
            {
                for (var j = i + 1; j < group.Teams.Count; j++)
                {
                    matches.Add(new PlannedMatch(group.Name, group.Teams[i].Name, group.Teams[j].Name, 0, 0, "planned"));
                }
            }
        }
        return matches;
    }
}
