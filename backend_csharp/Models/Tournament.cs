namespace Models
{
    public class Tournament
    {
        public int Id { get; set; }

        // Display
        public string Name { get; set; } = string.Empty;
        public string Game { get; set; } = string.Empty;
        public decimal PrizePool { get; set; }
        public int MaxParticipants { get; set; }
        public int CurrentParticipants { get; set; }
        public string StartDate { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;

        // External provider (optional). If set, tournament data is sourced from an open API (PandaScore).
        public bool IsExternal { get; set; } = false;
        public string? Provider { get; set; }
        public string? ProviderTournamentId { get; set; }
    }
}
