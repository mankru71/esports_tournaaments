using System;

namespace Models;

public class UserBadge
{
    public int Id { get; set; }

    public int UserId { get; set; }
    public AppUser? User { get; set; }

    public int BadgeId { get; set; }
    public Badge? Badge { get; set; }

    public DateTime AwardedAtUtc { get; set; } = DateTime.UtcNow;
}
