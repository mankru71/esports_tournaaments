namespace Models;

public class Match
{
    public int Id { get; set; }
    
    public int TournamentId { get; set; }
    public Tournament? Tournament { get; set; }

    public string Round { get; set; } = string.Empty;
    public int RoundNumber { get; set; }

    public int? NextMatchId { get; set; }
    public Match? NextMatch { get; set; }

    public int? TeamAId { get; set; }
    public Team? TeamA { get; set; }

    public int? TeamBId { get; set; }
    public Team? TeamB { get; set; }

    public int ScoreA { get; set; } = 0;
    public int ScoreB { get; set; } = 0;

    public int? WinnerId { get; set; }
    public Team? Winner { get; set; }

    public string StreamUrl { get; set; } = string.Empty;

    public bool IsAutoServer { get; set; } = false;

    public string Status { get; set; } = "planned"; 

    public DateTime? StartTimeUtc { get; set; }
    public bool Is15MinNotified { get; set; } = false;
}