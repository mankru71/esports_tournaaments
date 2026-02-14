namespace Models
{
    public class Nominee
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Team { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
        public string Kda { get; set; } = string.Empty;
        public decimal Rating { get; set; }
        public int Votes { get; set; }
    }
}