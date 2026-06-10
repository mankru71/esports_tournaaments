using System;

namespace Models;

/// <summary>
/// Снимок рейтинга пользователя в момент обновления (Faceit Elo или базовый рейтинг).
/// Пишется при привязке/обновлении Faceit и подтверждении рейтинга — даёт
/// историю для графика динамики в профиле.
/// </summary>
public class RatingHistory
{
    public int Id { get; set; }

    public int UserId { get; set; }
    public AppUser? User { get; set; }

    public decimal Rating { get; set; }

    /// <summary>Источник снимка: faceit | manual</summary>
    public string Source { get; set; } = "faceit";

    public DateTime RecordedAtUtc { get; set; } = DateTime.UtcNow;
}
