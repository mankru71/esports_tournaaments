using Data;
using Microsoft.EntityFrameworkCore;
using Models;

namespace Services
{
    public class TournamentService
    {
        private readonly AppDbContext _context;
        private readonly PandaScoreService _pandascore;

        public TournamentService(AppDbContext context, PandaScoreService pandascore)
        {
            _context = context;
            _pandascore = pandascore;
        }

        public IEnumerable<Tournament> GetAllTournaments() => _context.Tournaments.OrderBy(t => t.StartDate).ToList();

        public Tournament? GetTournamentById(int id)
        {
            return _context.Tournaments.FirstOrDefault(t => t.Id == id);
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

        public async Task<object> GetStatsAsync(CancellationToken ct = default)
        {
            var totalPlayers = await _context.TeamPlayers.CountAsync(ct);
            var localTournaments = await _context.Tournaments.CountAsync(ct);
            var localPopularGame = await _context.Tournaments
                .AsNoTracking()
                .GroupBy(t => string.IsNullOrWhiteSpace(t.Game) ? "Не указано" : t.Game)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .FirstOrDefaultAsync(ct) ?? "Не указано";

            var live = await _pandascore.GetLiveDashboardSnapshotAsync(ct);
            var mostPopular = live.ActiveTournaments > 0 ? live.MostPopularDiscipline : localPopularGame;

            return new
            {
                totalPlayers,
                activeTournaments = live.ActiveTournaments > 0 ? live.ActiveTournaments : localTournaments,
                totalViewers = live.TotalViewers,
                eventsToday = live.EventsToday,
                mostPopularDiscipline = mostPopular,
                liveStreams = live.LiveStreams,
                viewersEstimated = live.ViewersEstimated,
                liveTournaments = live.LiveTournaments
            };
        }
    }
}
