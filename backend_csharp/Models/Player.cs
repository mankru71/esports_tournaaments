namespace Models
{
    public class TeamPlayer
    {
        public int Id { get; set; }
        public string Nickname { get; set; } = string.Empty;
        public int TeamId { get; set; }
        public Team? Team { get; set; }
    }
}
