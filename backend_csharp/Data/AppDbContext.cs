using Microsoft.EntityFrameworkCore;
using Models;

namespace Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<Tournament> Tournaments { get; set; }
        public DbSet<Nominee> Nominees { get; set; }
        public DbSet<Vote> Votes { get; set; }
        public DbSet<AppUser> Users { get; set; }
        public DbSet<Team> Teams { get; set; }
        public DbSet<TeamPlayer> TeamPlayers { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<AppUser>().HasIndex(u => u.Email).IsUnique();

            modelBuilder.Entity<Team>()
                .HasOne(t => t.CaptainUser)
                .WithMany()
                .HasForeignKey(t => t.CaptainUserId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<TeamPlayer>()
                .HasOne(tp => tp.Team)
                .WithMany(t => t.Players)
                .HasForeignKey(tp => tp.TeamId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<Tournament>().HasData(
                new Tournament
                {
                    Id = 1,
                    Name = "Чемпионат Major по CS:GO",
                    Game = "CS:GO",
                    PrizePool = 1000000,
                    MaxParticipants = 32,
                    CurrentParticipants = 24,
                    StartDate = "2026-10-24",
                    Status = "planned"
                }
            );
        }
    }
}
