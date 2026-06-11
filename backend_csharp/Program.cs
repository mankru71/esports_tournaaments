using Data;
using Hubs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using EsportsBackend.Services;
using Services;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;

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




builder.Services.AddHttpClient("faceit", client =>
{
    client.BaseAddress = new Uri("https://open.faceit.com/data/v4/");
});

builder.Services.AddHttpClient<FaceitTournamentService>(client =>
{
    client.BaseAddress = new Uri("https://open.faceit.com/data/v4/");
    var key = builder.Configuration["FACEIT_API_KEY"];
    if (!string.IsNullOrWhiteSpace(key))
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {key}");
});

builder.Services.AddHttpClient("pandascore", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["PANDASCORE_BASE_URL"] ?? "https://api.pandascore.co");
});

builder.Services.AddHttpClient<DiscordWebhookService>();

// ML-сервис прогнозов Dota 2 (FastAPI-контейнер ml-service).
// Пустой ML_SERVICE_URL → BaseAddress = null → MatchPredictionService.Enabled = false
builder.Services.AddHttpClient<MatchPredictionService>(client =>
{
    var mlUrl = builder.Configuration["ML_SERVICE_URL"];
    if (!string.IsNullOrWhiteSpace(mlUrl))
        client.BaseAddress = new Uri(mlUrl.TrimEnd('/') + "/");
    // Прогноз — вспомогательная фича: не тормозим выдачу матчей из-за модели
    client.Timeout = TimeSpan.FromSeconds(3);
});

// Liquipedia: gzip и User-Agent с контактом ОБЯЗАТЕЛЬНЫ (иначе 406),
// темп запросов сдерживает LiquipediaRateLimitHandler (1 req / 2.1 s)
builder.Services.AddTransient<Infrastructure.LiquipediaRateLimitHandler>();
builder.Services.AddHttpClient<EsportsBackend.Services.LiquipediaService>(client =>
{
    client.BaseAddress = new Uri("https://liquipedia.net/");
    client.DefaultRequestHeaders.Add("User-Agent",
        builder.Configuration["LIQUIPEDIA_USER_AGENT"]
        ?? "EsportsTournamentsStudyProject/1.0 (study project; contact: student@example.com)");
    client.Timeout = TimeSpan.FromSeconds(40);
})
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
})
.AddHttpMessageHandler<Infrastructure.LiquipediaRateLimitHandler>();


var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));


builder.Services.AddScoped<EmailService>();
builder.Services.AddScoped<VerificationService>();
builder.Services.AddScoped<SteamApiService>();
builder.Services.AddScoped<TournamentService>();
builder.Services.AddScoped<ActivityLogService>();
builder.Services.AddScoped<AnalyticsService>();
builder.Services.AddScoped<RatingHistoryService>();
builder.Services.AddScoped<EsportsBackend.Services.EmailService>();
builder.Services.AddScoped<EsportsBackend.Services.VerificationService>();
builder.Services.AddScoped<ExternalTournamentSyncService>();
builder.Services.AddScoped<TournamentPlanningService>();
builder.Services.AddScoped<PandaScoreService>();
builder.Services.AddHostedService<Services.MatchNotificationWorker>();
builder.Services.AddHostedService<Services.PredictionResolverService>();
builder.Services.AddScoped<FaceitApiService>();


builder.Services.AddScoped<ITournamentProvider, FaceitTournamentService>();
// Переиспользуем типизированный HttpClient-сервис, а не строим второй экземпляр без BaseAddress
builder.Services.AddScoped<ITournamentProvider>(sp => sp.GetRequiredService<EsportsBackend.Services.LiquipediaService>());

// Add this before var app = builder.Build();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        var key = builder.Configuration["Jwt:Key"] ?? "super_secret_key_12345678901234567890";
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key))
        };
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs/matches"))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            }
        };
    });

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
            Console.WriteLine(">>> УСПЕХ: Миграции применены.");
        }
        else
        {
            context.Database.EnsureCreated();
            EnsureDbSchema(context);
            Console.WriteLine(">>> УСПЕХ: База готова (EnsureCreated).");
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

app.UseAuthentication();
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
            -- ВАЖНО: фигурные скобки в JSON ниже удвоены — ExecuteSqlRaw трактует
            -- одиночные скобки как плейсхолдеры параметров, и весь батч падал молча.
            ALTER TABLE IF EXISTS ""Tournaments"" ADD COLUMN IF NOT EXISTS ""PrizeDistributionJson"" text NOT NULL DEFAULT '[{{""place"":""1 место"",""percent"":50}},{{""place"":""2 место"",""percent"":30}},{{""place"":""3 место"",""percent"":20}}]';
            ALTER TABLE IF EXISTS ""Matches"" ADD COLUMN IF NOT EXISTS ""StreamUrl"" text NOT NULL DEFAULT '';
            ALTER TABLE IF EXISTS ""Teams"" ADD COLUMN IF NOT EXISTS ""IsExternal"" boolean NOT NULL DEFAULT FALSE;
            CREATE TABLE IF NOT EXISTS ""UserFavorites"" (
                ""Id"" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                ""UserId"" integer NOT NULL,
                ""TournamentId"" integer NOT NULL,
                ""CreatedAtUtc"" timestamp with time zone NOT NULL DEFAULT (now() at time zone 'utc')
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ""IX_UserFavorites_UserId_TournamentId""
                ON ""UserFavorites"" (""UserId"", ""TournamentId"");
            CREATE TABLE IF NOT EXISTS ""ActivityLogs"" (
                ""Id"" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                ""TimestampUtc"" timestamp with time zone NOT NULL DEFAULT (now() at time zone 'utc'),
                ""ActionType"" text NOT NULL DEFAULT '',
                ""Message"" text NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS ""IX_ActivityLogs_TimestampUtc""
                ON ""ActivityLogs"" (""TimestampUtc"");
            -- Epic 4: доска скаутинга (LFT) и история рейтинга
            ALTER TABLE IF EXISTS ""Users"" ADD COLUMN IF NOT EXISTS ""IsLookingForTeam"" boolean NOT NULL DEFAULT FALSE;
            ALTER TABLE IF EXISTS ""Users"" ADD COLUMN IF NOT EXISTS ""LookingForTeamSinceUtc"" timestamp with time zone NULL;
            CREATE TABLE IF NOT EXISTS ""RatingHistories"" (
                ""Id"" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                ""UserId"" integer NOT NULL,
                ""Rating"" numeric NOT NULL,
                ""Source"" text NOT NULL DEFAULT 'faceit',
                ""RecordedAtUtc"" timestamp with time zone NOT NULL DEFAULT (now() at time zone 'utc')
            );
            CREATE INDEX IF NOT EXISTS ""IX_RatingHistories_UserId_RecordedAtUtc""
                ON ""RatingHistories"" (""UserId"", ""RecordedAtUtc"");
        ");
    }
    catch (Exception ex)
    {
        // Не валим запуск, но и не глотаем ошибку молча — иначе отсутствие
        // таблицы/колонки всплывает только как 500 на первом запросе.
        Console.WriteLine($">>> ОШИБКА EnsureDbSchema: {ex.Message}");
    }
}