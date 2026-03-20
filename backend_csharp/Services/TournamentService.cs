using Data;
using Models;

namespace Services
{
    public class TournamentService
    {
        private readonly AppDbContext _context;

        public TournamentService(AppDbContext context)
        {
            _context = context;
        }

        public IEnumerable<Tournament> GetAllTournaments() => _context.Tournaments.OrderBy(t => t.StartDate).ToList();

        public Tournament? GetTournamentById(int id)
        {
            return _context.Tournaments.FirstOrDefault(t => t.Id == id);
        }

        public Tournament CreateTournament(Tournament tournament)
        {
            _context.Tournaments.Add(tournament);
            _context.SaveChanges();
            return tournament;
        }

        public IEnumerable<Nominee> GetNominees()
        {
            return _context.Nominees.ToList();
        }

        public (bool hasVoted, int? nomineeId) HasVoted(string voterSession)
        {
            var vote = _context.Votes.FirstOrDefault(v => v.VoterSession == voterSession);
            return (vote != null, vote?.NomineeId);
        }

        public (bool success, string message) Vote(int nomineeId, string voterSession, string voterIp)
        {
            if (string.IsNullOrEmpty(voterSession))
                return (false, "Сессия не определена.");

            var (alreadyVoted, _) = HasVoted(voterSession);
            if (alreadyVoted)
                return (false, "Вы уже голосовали.");

            var nominee = _context.Nominees.Find(nomineeId);
            if (nominee == null)
                return (false, "Номинант не найден.");

            _context.Votes.Add(new Vote
            {
                NomineeId = nomineeId,
                VoterSession = voterSession,
                VoterIp = voterIp ?? string.Empty
            });

            nominee.Votes += 1;
            _context.SaveChanges();

            return (true, "Голос принят!");
        }

        public object GetStats()
        {
            var totalPlayers = _context.TeamPlayers.Count();
            var activeTournaments = _context.Tournaments.Count();
            var popularGame = _context.Tournaments
                .AsEnumerable()
                .GroupBy(t => string.IsNullOrWhiteSpace(t.Game) ? "Не указано" : t.Game)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .FirstOrDefault() ?? "Не указано";

            return new
            {
                totalPlayers,
                activeTournaments,
                totalViewers = 850000,
                eventsToday = _context.Tournaments.Count(t => t.Status == "live"),
                mostPopularDiscipline = popularGame
            };
        }
    }
}
