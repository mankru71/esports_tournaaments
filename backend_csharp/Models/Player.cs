namespace Models
{
    public class TeamPlayer
    {
        public int Id { get; set; }
        public string Nickname { get; set; } = string.Empty;
        public int TeamId { get; set; }
        public Team? Team { get; set; }

        public decimal? Rating { get; set; }
        public string RatingSource { get; set; } = "manual";
        public string RatingStatus { get; set; } = "pending";
        public string? ExternalPlayerId { get; set; }
        public string? ExternalProfileUrl { get; set; }
        public DateTime? ConfirmedAtUtc { get; set; }
    }
}
