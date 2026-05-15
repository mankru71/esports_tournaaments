namespace Models;

public class MvpVote
{
    public int Id { get; set; }

    public int TournamentId { get; set; }
    public Tournament? Tournament { get; set; }

    public int PlayerId { get; set; }
    public TeamPlayer? Player { get; set; }

    public int? UserId { get; set; }
    public AppUser? User { get; set; }

    public string VoterSession { get; set; } = string.Empty;
    public string VoterIp { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
