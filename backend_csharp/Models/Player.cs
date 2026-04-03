using System;

namespace Models;

public class Player
{
    public int Id { get; set; }
    public string Nickname { get; set; } = string.Empty;
    public string Game { get; set; } = "counterstrike";
    public decimal? Rating { get; set; }
    public bool IsVerified { get; set; } = false;

    public int? TeamId { get; set; }
    public Team? Team { get; set; }

    // Поля, которые требуют контроллеры:
    public string? RatingStatus { get; set; }
    public string? RatingSource { get; set; }
    public string? ExternalProfileUrl { get; set; }
    public DateTime? ConfirmedAtUtc { get; set; }
}