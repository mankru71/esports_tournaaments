namespace Models;

public class Team
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;

    public int? CaptainUserId { get; set; }
    public AppUser? CaptainUser { get; set; }

    public ICollection<TeamPlayer> Players { get; set; } = new List<TeamPlayer>();
    public ICollection<TournamentApplication> Applications { get; set; } = new List<TournamentApplication>();
}