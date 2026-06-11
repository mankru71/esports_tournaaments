using System.ComponentModel.DataAnnotations;

namespace Models;

public class Badge
{
    public int Id { get; set; }
    
    [Required, MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? IconUrlOrCss { get; set; }

    [MaxLength(50)]
    public string? ColorCss { get; set; }
}
