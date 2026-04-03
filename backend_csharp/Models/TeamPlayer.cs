using System;

namespace Models;

public class TeamPlayer
{
    public int Id { get; set; }
    public int TeamId { get; set; }
    public Team? Team { get; set; }
    
    public string Nickname { get; set; } = string.Empty;
    public string Game { get; set; } = string.Empty;
    public decimal? Rating { get; set; }

    // Поля, которые требует TeamsController:
    public string? RatingSource { get; set; }
    public string? RatingStatus { get; set; }
    public string? ExternalPlayerId { get; set; }
    public string? ExternalProfileUrl { get; set; }
    public DateTime? ConfirmedAtUtc { get; set; }
}dotnet build