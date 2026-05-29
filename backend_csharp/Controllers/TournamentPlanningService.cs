using Microsoft.EntityFrameworkCore;
using Models;
using Data;

namespace Services
{
    public class TournamentPlanningService
    {
        private readonly AppDbContext _context;

        public TournamentPlanningService(AppDbContext context) { _context = context; }

        public async Task GenerateSingleEliminationBracketAsync(int tournamentId)
        {
            // 1. Берем турнир и только ПОДТВЕРЖДЕННЫЕ заявки ("Approved")
            var applications = await _context.TournamentApplications
                .Include(a => a.Team)
                .Where(a => a.TournamentId == tournamentId && a.Status == "Approved")
                .ToListAsync();

            var teams = applications.Select(a => a.Team).ToList();

            if (teams.Count < 2)
                throw new Exception("Недостаточно подтвержденных команд для генерации сетки.");

            // 2. Очищаем старую сетку
            var oldMatches = await _context.Matches.Where(m => m.TournamentId == tournamentId).ToListAsync();
            if (oldMatches.Any())
            {
                _context.Matches.RemoveRange(oldMatches);
                await _context.SaveChangesAsync();
            }

            // 3. Вычисляем размеры сетки (ближайшая степень двойки)
            int numTeams = teams.Count;
            int bracketSize = (int)Math.Pow(2, Math.Ceiling(Math.Log(numTeams, 2))); 
            int totalRounds = (int)Math.Log(bracketSize, 2);

            // Перемешиваем команды для случайного посева
            var rng = new Random();
            var shuffledTeams = teams.OrderBy(a => rng.Next()).ToList();
            var matches = new List<Match>();

            // 4. Генерируем "скелет" сетки (от 1 раунда до финала)
            var rounds = new Dictionary<int, List<Match>>();
            for (int r = 1; r <= totalRounds; r++)
            {
                int matchesInRound = bracketSize / (int)Math.Pow(2, r);
                rounds[r] = new List<Match>();
                
                for (int m = 0; m < matchesInRound; m++)
                {
                    var match = new Match
                    {
                        TournamentId = tournamentId, Round = r, Status = "Scheduled"
                    };
                    rounds[r].Add(match);
                    matches.Add(match);
                }
            }

            // Сохраняем в БД, чтобы получить ID для всех матчей
            await _context.Matches.AddRangeAsync(matches);
            await _context.SaveChangesAsync();

            // 5. Связываем матчи (победитель идет в NextMatchId)
            for (int r = 1; r < totalRounds; r++)
            {
                for (int m = 0; m < rounds[r].Count; m++)
                {
                    int nextMatchIndex = m / 2;
                    rounds[r][m].NextMatchId = rounds[r + 1][nextMatchIndex].Id;
                }
            }

            // 6. Распределяем команды по матчам первого раунда (сначала TeamA, потом TeamB)
            var firstRoundMatches = rounds[1];
            int teamIndex = 0;
            
            for (int i = 0; i < firstRoundMatches.Count; i++)
                if (teamIndex < shuffledTeams.Count) firstRoundMatches[i].TeamAId = shuffledTeams[teamIndex++].Id;

            for (int i = 0; i < firstRoundMatches.Count; i++)
                if (teamIndex < shuffledTeams.Count) firstRoundMatches[i].TeamBId = shuffledTeams[teamIndex++].Id;

            // 7. Отрабатываем "Byes" (команды без пары автоматически проходят в следующий раунд)
            foreach (var match in firstRoundMatches)
            {
                if (match.TeamAId != null && match.TeamBId == null)
                {
                    match.Status = "Completed";
                    match.WinnerId = match.TeamAId;
                    
                    if (match.NextMatchId != null)
                    {
                        var nextMatch = matches.First(m => m.Id == match.NextMatchId);
                        if (nextMatch.TeamAId == null) nextMatch.TeamAId = match.TeamAId;
                        else nextMatch.TeamBId = match.TeamAId;
                    }
                }
            }

            _context.Matches.UpdateRange(matches);
            await _context.SaveChangesAsync();
        }
    }
}