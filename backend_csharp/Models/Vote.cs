namespace Models
{
    public class Vote
    {
        public int Id { get; set; }
        public int NomineeId { get; set; }
        public string VoterSession { get; set; } = string.Empty;
        public string VoterIp { get; set; } = string.Empty;
    }
}