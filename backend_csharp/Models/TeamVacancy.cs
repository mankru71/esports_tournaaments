using System;
using System.ComponentModel.DataAnnotations;

namespace Models;

public class TeamVacancy
{
    public int Id { get; set; }
    
    public int TeamId { get; set; }
    public Team? Team { get; set; }

    [MaxLength(50)]
    public string RequiredRole { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? Description { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
