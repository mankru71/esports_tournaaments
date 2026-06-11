using System;

namespace Models;

public class FantasyRoster
{
    public int Id { get; set; }

    public int FantasyTeamId { get; set; }
    public FantasyTeam? FantasyTeam { get; set; }

    public int ProPlayerId { get; set; }
    public TeamPlayer? ProPlayer { get; set; }
}
