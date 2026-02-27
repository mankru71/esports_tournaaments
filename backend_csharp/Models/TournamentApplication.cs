namespace Models
{
    public class TournamentApplication
    {
        public int Id { get; set; }

        public int TournamentId { get; set; }
        public Tournament? Tournament { get; set; }

        public int TeamId { get; set; }
        public Team? Team { get; set; }

        public int ApplicantUserId { get; set; }
        public AppUser? ApplicantUser { get; set; }

        public string Status { get; set; } = "pending"; // pending/approved/rejected
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
