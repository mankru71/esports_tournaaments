using Microsoft.EntityFrameworkCore;
using Models;

namespace Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
    public DbSet<Nominee> Nominees { get; set; }
    public DbSet<Vote> Votes { get; set; }
    public DbSet<TeamPlayer> TeamPlayers { get; set; }
    public DbSet<AppUser> Users { get; set; }
    public DbSet<Team> Teams { get; set; }
    public DbSet<Player> Players { get; set; }
    public DbSet<Tournament> Tournaments { get; set; }
    public DbSet<TournamentApplication> TournamentApplications { get; set; }
    public DbSet<Match> Matches { get; set; }
    // Существующие DbSets для Nominee, Vote...

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Настройка Match (самоссылки и защиты от каскадного удаления)
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
    }
}