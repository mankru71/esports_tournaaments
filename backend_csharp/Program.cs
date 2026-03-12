using Data;
using Hubs;
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

var pandaBaseUrl = (builder.Configuration["PANDASCORE_BASE_URL"] ?? "https://api.pandascore.co").TrimEnd('/');

builder.Services.AddHttpClient("pandascore", client =>
{
    client.BaseAddress = new Uri(pandaBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(20);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("EsportsTournamentsPractice/1.0 (+edu project)");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
})
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
});

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddScoped<TournamentService>();
builder.Services.AddScoped<PandaScoreService>();
builder.Services.AddScoped<ExternalTournamentSyncService>();
builder.Services.AddScoped<TournamentPlanningService>();

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
            context.Database.EnsureCreated();

            string? TableExists(string table)
                => context.Database
                    .SqlQueryRaw<string>($"SELECT to_regclass('public.\"{table}\"')::text")
                    .AsEnumerable()
                    .FirstOrDefault();

            var usersTable = TableExists("Users");
            var teamsTable = TableExists("Teams");
            var teamPlayersTable = TableExists("TeamPlayers");
            var appsTable = TableExists("TournamentApplications");

            if (string.IsNullOrWhiteSpace(usersTable)
                || string.IsNullOrWhiteSpace(teamsTable)
                || string.IsNullOrWhiteSpace(teamPlayersTable)
                || string.IsNullOrWhiteSpace(appsTable))
            {
                Console.WriteLine(">>> ВНИМАНИЕ: Схема БД устарела. Пересоздаём БД для учебного проекта...");
                context.Database.EnsureDeleted();
                context.Database.EnsureCreated();
                Console.WriteLine(">>> УСПЕХ: БД пересоздана (EnsureDeleted+EnsureCreated).");
            }
            else
            {
                Console.WriteLine(">>> УСПЕХ: Миграции отсутствуют, схема проверена через EnsureCreated().");
            }

            EnsureDbSchema(context);
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

static void EnsureDbSchema(AppDbContext context)
{
    try
    {
        context.Database.ExecuteSqlRaw(@"
            ALTER TABLE IF EXISTS ""Tournaments"" ADD COLUMN IF NOT EXISTS ""IsExternal"" boolean NOT NULL DEFAULT FALSE;
            ALTER TABLE IF EXISTS ""Tournaments"" ADD COLUMN IF NOT EXISTS ""Provider"" text NULL;
            ALTER TABLE IF EXISTS ""Tournaments"" ADD COLUMN IF NOT EXISTS ""ProviderTournamentId"" text NULL;
            ALTER TABLE IF EXISTS ""Tournaments"" ADD COLUMN IF NOT EXISTS ""Format"" text NOT NULL DEFAULT 'single_elimination';
            ALTER TABLE IF EXISTS ""Tournaments"" ADD COLUMN IF NOT EXISTS ""StageType"" text NOT NULL DEFAULT 'single';
            ALTER TABLE IF EXISTS ""Tournaments"" ADD COLUMN IF NOT EXISTS ""PrizeDistributionJson"" text NOT NULL DEFAULT '[{""place"":""1 место"",""percent"":50},{""place"":""2 место"",""percent"":30},{""place"":""3 место"",""percent"":20}]';

            ALTER TABLE IF EXISTS ""TeamPlayers"" ADD COLUMN IF NOT EXISTS ""Rating"" numeric NULL;
            ALTER TABLE IF EXISTS ""TeamPlayers"" ADD COLUMN IF NOT EXISTS ""RatingSource"" text NOT NULL DEFAULT 'manual';
            ALTER TABLE IF EXISTS ""TeamPlayers"" ADD COLUMN IF NOT EXISTS ""RatingStatus"" text NOT NULL DEFAULT 'pending';
            ALTER TABLE IF EXISTS ""TeamPlayers"" ADD COLUMN IF NOT EXISTS ""ExternalPlayerId"" text NULL;
            ALTER TABLE IF EXISTS ""TeamPlayers"" ADD COLUMN IF NOT EXISTS ""ExternalProfileUrl"" text NULL;
            ALTER TABLE IF EXISTS ""TeamPlayers"" ADD COLUMN IF NOT EXISTS ""ConfirmedAtUtc"" timestamp with time zone NULL;

            CREATE UNIQUE INDEX IF NOT EXISTS ""IX_Tournaments_Provider_ProviderTournamentId"" ON ""Tournaments"" (""Provider"", ""ProviderTournamentId"");
        ");
    }
    catch (Exception ex)
    {
        Console.WriteLine($">>> WARNING: schema self-heal failed: {ex.Message}");
    }
}
