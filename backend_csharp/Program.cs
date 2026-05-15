using Data;
using Hubs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Services;
using EsportsBackend.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers().ConfigureApiBehaviorOptions(options =>
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

builder.Services.AddHttpClient("pandascore", client =>
{
    var baseUrl = builder.Configuration["PANDASCORE_BASE_URL"]
                  ?? builder.Configuration["PandaScore:BaseUrl"]
                  ?? "https://api.pandascore.co";
    client.BaseAddress = new Uri(baseUrl.TrimEnd('/'));
    client.Timeout = TimeSpan.FromSeconds(15);
});

builder.Services.AddHttpClient("faceit", client =>
{
    client.BaseAddress = new Uri("https://open.faceit.com/data/v4/");
    client.Timeout = TimeSpan.FromSeconds(15);
});

builder.Services.AddHttpClient<FaceitTournamentService>(client =>
{
    client.BaseAddress = new Uri("https://open.faceit.com/data/v4/");
    client.Timeout = TimeSpan.FromSeconds(15);
});

builder.Services.AddHttpClient<LiquipediaService>(client =>
{
    client.BaseAddress = new Uri("https://liquipedia.net/counterstrike/api.php");
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.UserAgent.ParseAdd(builder.Configuration["LIQUIPEDIA_USER_AGENT"] ?? "EsportsTournamentsStudyProject/1.0");
});

builder.Services.AddHttpClient<DiscordWebhookService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));

builder.Services.AddScoped<TournamentService>();
builder.Services.AddScoped<PandaScoreService>();
builder.Services.AddScoped<FaceitApiService>();
builder.Services.AddScoped<EsportsBackend.Services.EmailService>();
builder.Services.AddScoped<ExternalTournamentSyncService>();
builder.Services.AddScoped<TournamentPlanningService>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    try
    {
        var context = services.GetRequiredService<AppDbContext>();
        if (context.Database.GetMigrations().Any())
        {
            context.Database.Migrate();
            Console.WriteLine(">>> УСПЕХ: миграции применены.");
        }
        else
        {
            context.Database.EnsureCreated();
            EnsureDbSchema(context);
            Console.WriteLine(">>> УСПЕХ: база готова через EnsureCreated.");
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
            ALTER TABLE IF EXISTS ""Tournaments"" ADD COLUMN IF NOT EXISTS ""CurrentStage"" text NOT NULL DEFAULT 'registration';
            ALTER TABLE IF EXISTS ""Tournaments"" ADD COLUMN IF NOT EXISTS ""MvpVotingOpen"" boolean NOT NULL DEFAULT FALSE;
            ALTER TABLE IF EXISTS ""Matches"" ADD COLUMN IF NOT EXISTS ""GroupName"" text NOT NULL DEFAULT '';
            ALTER TABLE IF EXISTS ""Matches"" ADD COLUMN IF NOT EXISTS ""ScoreA"" integer NOT NULL DEFAULT 0;
            ALTER TABLE IF EXISTS ""Matches"" ADD COLUMN IF NOT EXISTS ""ScoreB"" integer NOT NULL DEFAULT 0;
            ALTER TABLE IF EXISTS ""Matches"" ADD COLUMN IF NOT EXISTS ""Status"" text NOT NULL DEFAULT 'planned';
            ALTER TABLE IF EXISTS ""Matches"" ADD COLUMN IF NOT EXISTS ""StreamUrl"" text NULL;
            ALTER TABLE IF EXISTS ""Matches"" ADD COLUMN IF NOT EXISTS ""StreamProvider"" text NULL;
            ALTER TABLE IF EXISTS ""Matches"" ADD COLUMN IF NOT EXISTS ""StreamStatus"" text NOT NULL DEFAULT 'offline';
            ALTER TABLE IF EXISTS ""Matches"" ADD COLUMN IF NOT EXISTS ""ScheduledAtUtc"" timestamp with time zone NULL;
            ALTER TABLE IF EXISTS ""Users"" ADD COLUMN IF NOT EXISTS ""IsEmailVerified"" boolean NOT NULL DEFAULT FALSE;
            ALTER TABLE IF EXISTS ""Users"" ADD COLUMN IF NOT EXISTS ""EmailVerificationToken"" text NULL;
            ALTER TABLE IF EXISTS ""Users"" ADD COLUMN IF NOT EXISTS ""EmailVerificationTokenExpiry"" timestamp with time zone NULL;
            CREATE TABLE IF NOT EXISTS ""MvpVotes"" (
                ""Id"" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                ""TournamentId"" integer NOT NULL,
                ""PlayerId"" integer NOT NULL,
                ""UserId"" integer NULL,
                ""VoterSession"" text NOT NULL DEFAULT '',
                ""VoterIp"" text NOT NULL DEFAULT '',
                ""CreatedAtUtc"" timestamp with time zone NOT NULL DEFAULT NOW()
            );
            CREATE TABLE IF NOT EXISTS ""PrizePayouts"" (
                ""Id"" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                ""TournamentId"" integer NOT NULL,
                ""Place"" integer NOT NULL,
                ""PlaceTitle"" text NOT NULL DEFAULT '',
                ""TeamId"" integer NULL,
                ""Percent"" numeric NOT NULL DEFAULT 0,
                ""Amount"" numeric NOT NULL DEFAULT 0,
                ""Status"" text NOT NULL DEFAULT 'pending',
                ""CreatedAtUtc"" timestamp with time zone NOT NULL DEFAULT NOW(),
                ""PaidAtUtc"" timestamp with time zone NULL
            );
        ");
    }
    catch (Exception ex)
    {
        Console.WriteLine($">>> WARNING: проверка схемы не выполнена: {ex.Message}");
    }
}
