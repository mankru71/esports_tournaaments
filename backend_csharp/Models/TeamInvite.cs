using System;
using System.ComponentModel.DataAnnotations;

namespace Models;

public class TeamInvite
{
    public int Id { get; set; }
    
    public int TeamId { get; set; }
    public Team? Team { get; set; }

    public int UserId { get; set; }
    public AppUser? User { get; set; }

    public int? CaptainId { get; set; }
    public AppUser? Captain { get; set; }

    [MaxLength(20)]
    public string Status { get; set; } = "pending"; // pending, accepted, declined

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
