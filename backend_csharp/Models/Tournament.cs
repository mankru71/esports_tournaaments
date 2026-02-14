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
    }
}