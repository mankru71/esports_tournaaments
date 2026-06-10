using System;

namespace Models;

public class UserFavorite
{
    public int Id { get; set; }

    public int UserId { get; set; }
    public AppUser? User { get; set; }

    public int TournamentId { get; set; }
    public Tournament? Tournament { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
