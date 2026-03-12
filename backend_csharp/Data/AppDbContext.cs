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
        public DbSet<TournamentApplication> TournamentApplications { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<AppUser>().HasIndex(u => u.Email).IsUnique();
            modelBuilder.Entity<AppUser>().HasIndex(u => u.Nickname).IsUnique();

            modelBuilder.Entity<TeamPlayer>()
                .HasIndex(tp => new { tp.TeamId, tp.Nickname })
                .IsUnique();

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

            modelBuilder.Entity<TournamentApplication>()
                .HasIndex(a => new { a.TournamentId, a.TeamId })
                .IsUnique();

            modelBuilder.Entity<TournamentApplication>()
                .HasOne(a => a.Team)
                .WithMany()
                .HasForeignKey(a => a.TeamId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<TournamentApplication>()
                .HasOne(a => a.Tournament)
                .WithMany()
                .HasForeignKey(a => a.TournamentId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<TournamentApplication>()
                .HasOne(a => a.ApplicantUser)
                .WithMany()
                .HasForeignKey(a => a.ApplicantUserId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<Tournament>()
                .HasIndex(t => new { t.Provider, t.ProviderTournamentId })
                .IsUnique();

            modelBuilder.Entity<Tournament>().HasData(
                new Tournament
                {
                    Id = 1,
                    Name = "Чемпионат Major по CS2",
                    Game = "CS2",
                    PrizePool = 1000000,
                    MaxParticipants = 16,
                    CurrentParticipants = 8,
                    StartDate = "2026-10-24",
                    Status = "planned",
                    Format = "single_elimination",
                    StageType = "single",
                    PrizeDistributionJson = "[{\"place\":\"1 место\",\"percent\":50},{\"place\":\"2 место\",\"percent\":30},{\"place\":\"3 место\",\"percent\":20}]"
                },
                new Tournament
                {
                    Id = 2,
                    Name = "Dota 2 University Cup",
                    Game = "Dota 2",
                    PrizePool = 300000,
                    MaxParticipants = 8,
                    CurrentParticipants = 4,
                    StartDate = "2026-11-04",
                    Status = "planned",
                    Format = "group_stage",
                    StageType = "groups",
                    PrizeDistributionJson = "[{\"place\":\"1 место\",\"percent\":60},{\"place\":\"2 место\",\"percent\":25},{\"place\":\"3 место\",\"percent\":15}]"
                }
            );
        }
    }
}
