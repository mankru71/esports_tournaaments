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
    public DbSet<TeamVacancy> TeamVacancies { get; set; }
    public DbSet<TeamInvite> TeamInvites { get; set; }
    public DbSet<PlayerEndorsement> PlayerEndorsements { get; set; }
    public DbSet<MatchComment> MatchComments { get; set; }
    
    // Gamification
    public DbSet<MatchPrediction> MatchPredictions { get; set; } = null!;
    public DbSet<Badge> Badges { get; set; } = null!;
    public DbSet<UserBadge> UserBadges { get; set; } = null!;
    public DbSet<FantasyTeam> FantasyTeams { get; set; } = null!;
    public DbSet<FantasyRoster> FantasyRosters { get; set; } = null!;
    public DbSet<MvpVote> MvpVotes { get; set; } = null!;

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

        modelBuilder.Entity<PlayerEndorsement>(entity =>
        {
            entity.HasOne(e => e.EndorsedUser)
                  .WithMany()
                  .HasForeignKey(e => e.EndorsedUserId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.EndorserUser)
                  .WithMany()
                  .HasForeignKey(e => e.EndorserUserId)
                  .OnDelete(DeleteBehavior.Restrict); // Prevent multiple cascade paths
        });

        // Gamification / Pick'Em
        modelBuilder.Entity<MatchPrediction>(entity =>
        {
            entity.HasOne(p => p.User)
                  .WithMany()
                  .HasForeignKey(p => p.UserId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(p => p.Match)
                  .WithMany()
                  .HasForeignKey(p => p.MatchId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(p => p.PredictedTeam)
                  .WithMany()
                  .HasForeignKey(p => p.PredictedTeamId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(p => new { p.UserId, p.MatchId }).IsUnique();
        });

        modelBuilder.Entity<UserBadge>(entity =>
        {
            entity.HasOne(ub => ub.User)
                  .WithMany()
                  .HasForeignKey(ub => ub.UserId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(ub => ub.Badge)
                  .WithMany()
                  .HasForeignKey(ub => ub.BadgeId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(ub => new { ub.UserId, ub.BadgeId }).IsUnique();
        });

        modelBuilder.Entity<TeamInvite>(entity =>
        {
            entity.HasOne(ti => ti.Captain)
                  .WithMany()
                  .HasForeignKey(ti => ti.CaptainId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(ti => ti.User)
                  .WithMany()
                  .HasForeignKey(ti => ti.UserId)
                  .OnDelete(DeleteBehavior.Cascade);
                  
            entity.HasOne(ti => ti.Team)
                  .WithMany(t => t.Invites)
                  .HasForeignKey(ti => ti.TeamId)
                  .OnDelete(DeleteBehavior.Cascade);
        });
    }
}