using Hubs;
using Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddControllers()
    .ConfigureApiBehaviorOptions(options =>
    {
        options.InvalidModelStateResponseFactory = context =>
        {
            var details = new ValidationProblemDetails(context.ModelState)
            {
                Title = "Validation failed",
                Detail = "Проверьте корректность входных данных",
                Status = StatusCodes.Status400BadRequest
            };
            return new BadRequestObjectResult(details);
        };
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSignalR();
builder.Services.AddMemoryCache();

builder.Services.AddHttpClient("liquipedia", client =>
{
    client.DefaultRequestHeaders.UserAgent.ParseAdd("EsportsTournamentsPractice/1.0 (contact: demo@local)");
})
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
});

builder.Services.AddScoped<LiquipediaService>();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddScoped<TournamentService>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    try
    {
        var context = services.GetRequiredService<AppDbContext>();
        var hasMigrations = context.Database.GetMigrations().Any();
        if (hasMigrations)
        {
            context.Database.Migrate();
            Console.WriteLine(">>> УСПЕХ: Миграции применены, база готова!");
        }
        else
        {
            // Учебный проект: если миграций нет, используем EnsureCreated.
            // Важно: EnsureCreated НЕ обновляет существующую схему. Если база уже была создана ранее
            // (например, без таблицы Users), то новые таблицы не появятся и auth будет падать.
            // Поэтому делаем простой self-heal: если таблицы Users нет — пересоздаём базу (demo-safe).

            context.Database.EnsureCreated();

            // Проверяем наличие таблицы Users (именно так её ожидает текущая модель EF).
            // to_regclass вернёт NULL, если таблицы нет.
            var usersTable = context.Database
                .SqlQueryRaw<string>("SELECT to_regclass('public.\"Users\"')::text")
                .AsEnumerable()
                .FirstOrDefault();

            var appsTable = context.Database
                .SqlQueryRaw<string>("SELECT to_regclass('public.\"TournamentApplications\"')::text")
                .AsEnumerable()
                .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(usersTable) || string.IsNullOrWhiteSpace(appsTable))
            {
                Console.WriteLine(">>> ВНИМАНИЕ: В существующей БД отсутствуют необходимые таблицы (Users/TournamentApplications). Пересоздаём БД для демо...");
                context.Database.EnsureDeleted();
                context.Database.EnsureCreated();
                Console.WriteLine(">>> УСПЕХ: БД пересоздана (EnsureDeleted+EnsureCreated), auth/teams готовы.");
            }
            else
            {
                Console.WriteLine(">>> УСПЕХ: Миграции отсутствуют, схема проверена через EnsureCreated().");
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($">>> ОШИБКА БАЗЫ ДАННЫХ: {ex.Message}");
    }
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthorization();
app.MapControllers();
app.MapHub<MatchesHub>("/hubs/matches");

app.Run();
