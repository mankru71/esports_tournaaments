namespace Models;

public class Match
{
    public int Id { get; set; }
    
    public int TournamentId { get; set; }
    public Tournament? Tournament { get; set; }

    public string Round { get; set; } = string.Empty; // "R1", "1/4", "1/2", "Final"
    public string GroupName { get; set; } = string.Empty;
    public int RoundNumber { get; set; }

    // Самоссылающаяся связь для дерева сетки
    public int? NextMatchId { get; set; }
    public Match? NextMatch { get; set; }

    public int? TeamAId { get; set; }
    public Team? TeamA { get; set; }

    public int? TeamBId { get; set; }
    public Team? TeamB { get; set; }

    // Интегрированный MatchScore (для идеального маппинга в Django _normalize_match)
    public int ScoreA { get; set; } = 0;
    public int ScoreB { get; set; } = 0;

    public int? WinnerId { get; set; }
    public Team? Winner { get; set; }

    // Статусы: "planned", "live", "finished", "approved"
    public string Status { get; set; } = "planned";

    // Twitch/YouTube/custom stream integration fields
    public string? StreamUrl { get; set; }
    public string? StreamProvider { get; set; }
    public string StreamStatus { get; set; } = "offline";
    public DateTime? ScheduledAtUtc { get; set; }
}