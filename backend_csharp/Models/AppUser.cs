using System;
using System.ComponentModel.DataAnnotations;

namespace Models;

public class AppUser
{
    public int Id { get; set; }
    
    [Required, EmailAddress]
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Nickname { get; set; } = string.Empty;
    public string? Bio { get; set; }
    
    public string Role { get; set; } = "viewer"; 
    
    public string? RatingProvider { get; set; }
    public string? RatingProfileUrl { get; set; }

    // Поля, которые требует AuthController:
    public decimal? Rating { get; set; }
    public bool RatingVerified { get; set; }
    public DateTime? RatingVerifiedAtUtc { get; set; }
}