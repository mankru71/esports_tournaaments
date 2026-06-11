using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Models;

public class MatchComment
{
    public int Id { get; set; }
    
    public int MatchId { get; set; }
    
    public int UserId { get; set; }
    
    [Required]
    public string Message { get; set; } = string.Empty;
    
    public bool IsInternalLobby { get; set; } = false;
    
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    [ForeignKey("MatchId")]
    public Match? Match { get; set; }
    
    [ForeignKey("UserId")]
    public AppUser? User { get; set; }
}
