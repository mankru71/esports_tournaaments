using System;

namespace Models;

/// <summary>
/// Лёгкая запись ленты событий платформы (создание команд, заявки, анонсы турниров).
/// ActionType: tournament_created | team_created | team_deleted | player_joined |
///             player_left | application_approved | match_finished | external_sync
/// </summary>
public class ActivityLog
{
    public int Id { get; set; }
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string ActionType { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}
