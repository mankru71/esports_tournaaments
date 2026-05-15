using Microsoft.EntityFrameworkCore;
using Models;

namespace Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Nominee> Nominees { get; set; }
    public DbSet<Vote> Votes { get; set; }
    public DbSet<MvpVote> MvpVotes { get; set; }
    public DbSet<PrizePayout> PrizePayouts { get; set; }
    public DbSet<TeamPlayer> TeamPlayers { get; set; }
    public DbSet<AppUser> Users { get; set; }
    public DbSet<Team> Teams { get; set; }
    public DbSet<Player> Players { get; set; }
    public DbSet<Tournament> Tournaments { get; set; }
    public DbSet<TournamentApplication> TournamentApplications { get; set; }
    public DbSet<Match> Matches { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<AppUser>()
            .HasIndex(u => u.Email)
            .IsUnique();

        modelBuilder.Entity<AppUser>()
            .HasIndex(u => u.Nickname)
            .IsUnique();

        modelBuilder.Entity<TeamPlayer>()
            .HasIndex(p => new { p.TeamId, p.Nickname })
            .IsUnique();

        modelBuilder.Entity<TournamentApplication>()
            .HasIndex(a => new { a.TournamentId, a.TeamId })
            .IsUnique();

        modelBuilder.Entity<Match>(entity =>
        {
            entity.HasOne(m => m.TeamA)
                  .WithMany()
                  .HasForeignKey(m => m.TeamAId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(m => m.TeamB)
                  .WithMany()
                  .HasForeignKey(m => m.TeamBId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(m => m.Winner)
                  .WithMany()
                  .HasForeignKey(m => m.WinnerId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(m => m.NextMatch)
                  .WithMany()
                  .HasForeignKey(m => m.NextMatchId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Team>()
            .HasOne(t => t.CaptainUser)
            .WithMany()
            .HasForeignKey(t => t.CaptainUserId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<MvpVote>()
            .HasOne(v => v.Tournament)
            .WithMany()
            .HasForeignKey(v => v.TournamentId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<MvpVote>()
            .HasOne(v => v.Player)
            .WithMany()
            .HasForeignKey(v => v.PlayerId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<MvpVote>()
            .HasOne(v => v.User)
            .WithMany()
            .HasForeignKey(v => v.UserId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<PrizePayout>()
            .HasOne(p => p.Tournament)
            .WithMany()
            .HasForeignKey(p => p.TournamentId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<PrizePayout>()
            .HasOne(p => p.Team)
            .WithMany()
            .HasForeignKey(p => p.TeamId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
