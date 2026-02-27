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

            // В одной команде не должно быть двух игроков с одинаковым ником.
            // (DB-level защита + в контроллере есть дополнительная проверка на trim/регистр.)
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


            // External tournaments: unique key to prevent duplicates when syncing
            modelBuilder.Entity<Tournament>()
                .HasIndex(t => new { t.Provider, t.ProviderTournamentId })
                .IsUnique();

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
