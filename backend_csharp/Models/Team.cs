namespace Models
{
    public class Team
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int CaptainUserId { get; set; }
        public AppUser? CaptainUser { get; set; }
        public List<TeamPlayer> Players { get; set; } = new();
    }
}
