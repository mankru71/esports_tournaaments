namespace Models
{
    public class Tournament
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;
        public string Game { get; set; } = string.Empty;
        public decimal PrizePool { get; set; }
        public int MaxParticipants { get; set; }
        public int CurrentParticipants { get; set; }
        public string StartDate { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string Format { get; set; } = "single_elimination";
        public string StageType { get; set; } = "single";
        public string PrizeDistributionJson { get; set; } = "[{\"place\":\"1 место\",\"percent\":50},{\"place\":\"2 место\",\"percent\":30},{\"place\":\"3 место\",\"percent\":20}]";

        public bool IsExternal { get; set; } = false;
        public string? Provider { get; set; }
        public string? ProviderTournamentId { get; set; }
    }
}
