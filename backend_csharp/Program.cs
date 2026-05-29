using Data;
using Hubs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using EsportsBackend.Services;
using Services;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;

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

builder.Services.AddHttpClient<LiquipediaService>(client =>
{
    client.BaseAddress = new Uri("https://liquipedia.net/counterstrike/api.php");
    client.DefaultRequestHeaders.Add("User-Agent", builder.Configuration["LIQUIPEDIA_USER_AGENT"] ?? "EsportsApp/1.0");
});


var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));


builder.Services.AddScoped<TournamentService>();
builder.Services.AddScoped<EsportsBackend.Services.EmailService>();
builder.Services.AddScoped<ExternalTournamentSyncService>();
builder.Services.AddScoped<TournamentPlanningService>();
builder.Services.AddScoped<PandaScoreService>();
builder.Services.AddScoped<FaceitApiService>();


builder.Services.AddScoped<ITournamentProvider, FaceitTournamentService>();
builder.Services.AddScoped<ITournamentProvider, LiquipediaService>();

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
            ALTER TABLE IF EXISTS ""Matches"" ADD COLUMN IF NOT EXISTS ""StreamUrl"" text NOT NULL DEFAULT '';
        ");
    }
    catch { }
}