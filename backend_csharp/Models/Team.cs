namespace Models;

public class Team
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;

    public int? CaptainUserId { get; set; }
    public AppUser? CaptainUser { get; set; }

    // Команда спарсена из внешнего источника (Liquipedia) — не показывается
    // на странице «Команды» и не попадает в локальную аналитику
    public bool IsExternal { get; set; } = false;

    // ИСПРАВЛЕНО: Теперь EF Core знает, что брать из таблицы TeamPlayers
    public ICollection<TeamPlayer> Players { get; set; } = new List<TeamPlayer>();
    public ICollection<TournamentApplication> Applications { get; set; } = new List<TournamentApplication>();
    public ICollection<TeamVacancy> Vacancies { get; set; } = new List<TeamVacancy>();
    public ICollection<TeamInvite> Invites { get; set; } = new List<TeamInvite>();
}