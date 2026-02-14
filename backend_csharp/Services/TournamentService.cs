using Data;
using Models;
using System.Collections.Generic;
using System.Linq;

namespace Services
{
    public class TournamentService
    {
        private readonly AppDbContext _context;

        public TournamentService(AppDbContext context)
        {
            _context = context;
        }

        // 1. Получение всех турниров
        public IEnumerable<Tournament> GetAllTournaments() => _context.Tournaments.ToList();

        // 2. Получение одного турнира по ID (нужно для TournamentController)
        public Tournament GetTournamentById(int id)
        {
            return _context.Tournaments.FirstOrDefault(t => t.Id == id);
        }

        // 3. Получение списка номинантов (нужно для VotingController)
        public IEnumerable<Nominee> GetNominees()
        {
            return _context.Nominees.ToList();
        }

        // 4. Проверка: голосовал ли уже пользователь (нужно для деконструкции в VotingController)
        public (bool hasVoted, int? nomineeId) HasVoted(string voterSession)
        {
            var vote = _context.Votes.FirstOrDefault(v => v.VoterSession == voterSession);
            return (vote != null, vote?.NomineeId);
        }

        // 5. Метод для голосования
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
                VoterIp = voterIp ?? "" 
            });
            
            nominee.Votes += 1;
            _context.SaveChanges();

            return (true, "Голос принят!");
        }

        public object GetStats()
        {
            return new
            {
                totalPlayers = 12000,
                activeTournaments = _context.Tournaments.Count(),
                totalViewers = 850000
            };
        }
    }
}