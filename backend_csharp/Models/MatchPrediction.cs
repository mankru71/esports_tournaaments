using System;

namespace Models;

public class MatchPrediction
{
    public int Id { get; set; }
    
    public int UserId { get; set; }
    public AppUser? User { get; set; }

    public int MatchId { get; set; }
    public Match? Match { get; set; }

    public int PredictedTeamId { get; set; }
    public Team? PredictedTeam { get; set; }

    // Status: "Pending", "Won", "Lost"
    public string Status { get; set; } = "Pending";

    public int PointsEarned { get; set; } = 0;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
