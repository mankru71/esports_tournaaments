using System;
using System.Collections.Generic;

namespace Models;

public class FantasyTeam
{
    public int Id { get; set; }

    public int UserId { get; set; }
    public AppUser? User { get; set; }

    public int TournamentId { get; set; }
    public Tournament? Tournament { get; set; }

    public string TeamName { get; set; } = string.Empty;
    public int TotalPoints { get; set; } = 0;
    public int BudgetRemaining { get; set; } = 500;

    public ICollection<FantasyRoster> Roster { get; set; } = new List<FantasyRoster>();
}
