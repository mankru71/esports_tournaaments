namespace Models
{
    public class AppUser
    {
        public int Id { get; set; }
        public string Email { get; set; } = string.Empty;
        public string Nickname { get; set; } = string.Empty;
        public string PasswordHash { get; set; } = string.Empty;
        public string Role { get; set; } = "captain";

        public string? Bio { get; set; }
        public decimal? Rating { get; set; }
        public string? RatingProvider { get; set; }
        public bool RatingVerified { get; set; }
        public DateTime? RatingVerifiedAtUtc { get; set; }
        public string? RatingProfileUrl { get; set; }
    }
}
