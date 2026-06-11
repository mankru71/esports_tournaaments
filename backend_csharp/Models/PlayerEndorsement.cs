using System;

namespace Models;

public class PlayerEndorsement
{
    public int Id { get; set; }
    
    public int EndorsedUserId { get; set; }
    public AppUser? EndorsedUser { get; set; }

    public int EndorserUserId { get; set; }
    public AppUser? EndorserUser { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
