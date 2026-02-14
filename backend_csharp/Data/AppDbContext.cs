using Microsoft.EntityFrameworkCore;
using Models;

namespace Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        // Эти свойства создают таблицы в базе данных
        public DbSet<Tournament> Tournaments { get; set; }
        public DbSet<Nominee> Nominees { get; set; }
        public DbSet<Vote> Votes { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Добавляем один турнир сразу, чтобы база не была пустой
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
                    Status = "Регистрация" 
                }
            );
        }
    }
}