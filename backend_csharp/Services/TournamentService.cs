using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Linq;
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
            var existing = _context.Nominees.ToList();
            if (existing.Any())
            {
                return existing;
            }

            var nomineesToSeed = new List<Nominee>();

            // 1. Local players from Users table
            var localUsers = _context.Users
                .Where(u => u.Role == "player" || u.Role == "captain")
                .ToList();

            foreach (var user in localUsers)
            {
                nomineesToSeed.Add(new Nominee
                {
                    Name = user.Nickname,
                    Team = user.IsLookingForTeam ? "Свободный агент" : "Локальный микс",
                    Role = user.GameRole ?? "Rifler",
                    Kda = "1.08",
                    Rating = user.Rating ?? 1.05m,
                    Votes = 0
                });
            }

            // 2. Synced players from TeamPlayers table
            var teamPlayers = _context.TeamPlayers
                .Include(tp => tp.Team)
                .ToList();

            foreach (var tp in teamPlayers)
            {
                nomineesToSeed.Add(new Nominee
                {
                    Name = tp.Nickname,
                    Team = tp.Team?.Name ?? "External Team",
                    Role = string.IsNullOrWhiteSpace(tp.Game) ? "Entry Fragger" : tp.Game,
                    Kda = "1.12",
                    Rating = tp.Rating ?? 1.10m,
                    Votes = 0
                });
            }

            // 3. Fallback: if database has neither local nor synced players yet, seed some default/dummy ones
            if (!nomineesToSeed.Any())
            {
                // Pro CS2 players (representing parsed/synced ones)
                nomineesToSeed.Add(new Nominee { Name = "s1mple", Team = "Natus Vincere", Role = "AWPer", Kda = "1.25", Rating = 1.28m, Votes = 0 });
                nomineesToSeed.Add(new Nominee { Name = "m0NESY", Team = "G2 Esports", Role = "AWPer", Kda = "1.30", Rating = 1.32m, Votes = 0 });
                nomineesToSeed.Add(new Nominee { Name = "ZywOo", Team = "Team Vitality", Role = "AWPer", Kda = "1.28", Rating = 1.30m, Votes = 0 });
                nomineesToSeed.Add(new Nominee { Name = "donk", Team = "Team Spirit", Role = "Entry Fragger", Kda = "1.35", Rating = 1.38m, Votes = 0 });
                nomineesToSeed.Add(new Nominee { Name = "NiKo", Team = "G2 Esports", Role = "Rifler", Kda = "1.18", Rating = 1.20m, Votes = 0 });
                nomineesToSeed.Add(new Nominee { Name = "ropz", Team = "FaZe Clan", Role = "Lurker", Kda = "1.15", Rating = 1.18m, Votes = 0 });

                // Local players (representing registered users)
                nomineesToSeed.Add(new Nominee { Name = "Zbarashevsky_Fan", Team = "Свободный агент", Role = "Rifler", Kda = "1.05", Rating = 1.02m, Votes = 0 });
                nomineesToSeed.Add(new Nominee { Name = "artem_cs", Team = "Локальный микс", Role = "IGL", Kda = "0.98", Rating = 0.95m, Votes = 0 });
            }

            _context.Nominees.AddRange(nomineesToSeed);
            _context.SaveChanges();

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
