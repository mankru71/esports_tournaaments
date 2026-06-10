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
    public DbSet<UserFavorite> UserFavorites { get; set; }
    public DbSet<ActivityLog> ActivityLogs { get; set; }
    public DbSet<RatingHistory> RatingHistories { get; set; }
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

        // Избранные турниры: один турнир — один раз на пользователя
        modelBuilder.Entity<UserFavorite>(entity =>
        {
            entity.HasIndex(f => new { f.UserId, f.TournamentId }).IsUnique();

            entity.HasOne(f => f.User)
                  .WithMany()
                  .HasForeignKey(f => f.UserId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(f => f.Tournament)
                  .WithMany()
                  .HasForeignKey(f => f.TournamentId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // Лента событий: выборка всегда «последние N по времени»
        modelBuilder.Entity<ActivityLog>()
            .HasIndex(a => a.TimestampUtc);

        // История рейтинга: выборка всегда «по пользователю в хронологии»
        modelBuilder.Entity<RatingHistory>(entity =>
        {
            entity.HasIndex(h => new { h.UserId, h.RecordedAtUtc });

            entity.HasOne(h => h.User)
                  .WithMany()
                  .HasForeignKey(h => h.UserId)
                  .OnDelete(DeleteBehavior.Cascade);
        });
    }
}