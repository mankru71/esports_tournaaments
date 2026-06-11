using System;
using System.ComponentModel.DataAnnotations;

namespace Models;

public class AppUser
{
    public int Id { get; set; }

    [Required, EmailAddress]
    public string Email { get; set; } = string.Empty;

    public string PasswordHash { get; set; } = string.Empty;

    [Required, MinLength(2), MaxLength(32)]
    public string Nickname { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? Bio { get; set; }

    public string Role { get; set; } = "viewer";
    public bool IsEmailVerified { get; set; } = false;
    public string? EmailVerificationToken { get; set; }
    public DateTime? EmailVerificationTokenExpiry { get; set; }
    // ── Generic rating fields ──────────────────────────────────────────
    public string? RatingProvider { get; set; }
    public string? RatingProfileUrl { get; set; }
    public decimal? Rating { get; set; }
    public bool RatingVerified { get; set; }
    public DateTime? RatingVerifiedAtUtc { get; set; }

    // ── Скаутинг (доска свободных агентов) ─────────────────────────────
    public bool IsLookingForTeam { get; set; } = false;
    public DateTime? LookingForTeamSinceUtc { get; set; }

    // ── Faceit-specific fields ─────────────────────────────────────────
    public string? FaceitNickname { get; set; }
    public int? FaceitElo { get; set; }
    public int? FaceitLevel { get; set; }
    public string? FaceitAvatar { get; set; }
    public string? FaceitProfileUrl { get; set; }
    public DateTime? FaceitLinkedAt { get; set; }

    // ── Extended Scouting Fields ───────────────────────────────────────
    public string? GameRole { get; set; } // e.g., IGL, Support, Sniper
    public string? Availability { get; set; } // e.g., "19:00 - 23:00"
    [MaxLength(150)]
    public string? Pitch { get; set; } // Short bio for scouting
    public string? DiscordId { get; set; } // For contact copying
    [MaxLength(200)]
    public string? HighlightsUrl { get; set; } // Youtube or Twitch clip

    // ── Geo & Language Fields ──────────────────────────────────────────
    public string? Country { get; set; }
    public string? City { get; set; }
    public string? Languages { get; set; }

    // ── Steam SSO ──────────────────────────────────────────────────────
    public string? SteamId { get; set; }
    public string? AvatarUrl { get; set; }

    // ── Gamification ───────────────────────────────────────────────────
    public int PredictorMMR { get; set; } = 1000;
}
