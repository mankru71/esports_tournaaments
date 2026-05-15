using Data;
using Hubs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using EsportsBackend.Services; // Для нашего EmailService и новых провайдеров
using Polly;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// --- 1. Настройка контроллеров и валидации ---
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

// --- 2. Конфигурация HTTP клиентов (Faceit и Liquipedia) ---

// Faceit Client
builder.Services.AddHttpClient<FaceitTournamentService>(client =>
{
    client.BaseAddress = new Uri("https://open.faceit.com/data/v4/");
    client.DefaultRequestHeaders.Add("Authorization", $"Bearer {builder.Configuration["FACEIT_API_KEY"]}");
});

// Liquipedia Client + Защита от бана (Rate Limiting)
builder.Services.AddHttpClient<LiquipediaService>(client =>
{
    client.BaseAddress = new Uri("https://liquipedia.net/counterstrike/api.php");
    // ОБЯЗАТЕЛЬНО: без User-Agent забанят сразу
    client.DefaultRequestHeaders.Add("User-Agent", builder.Configuration["LIQUIPEDIA_USER_AGENT"] ?? "EsportsApp/1.0");
})
.AddPolicyHandler(Policy.RateLimitAsync<HttpResponseMessage>(1, TimeSpan.FromSeconds(2.5))); // 1 запрос в 2.5 сек

// --- 3. База данных и Сервисы ---
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));

// Регистрация всех сервисов
builder.Services.AddScoped<TournamentService>();
builder.Services.AddScoped<EsportsBackend.Services.EmailService>(); // Тот самый фикс пути
builder.Services.AddScoped<ExternalTournamentSyncService>();
builder.Services.AddScoped<TournamentPlanningService>();

// Регистрация новых провайдеров турниров
builder.Services.AddScoped<ITournamentProvider, FaceitTournamentService>();
builder.Services.AddScoped<ITournamentProvider, LiquipediaService>();

var app = builder.Build();

// --- 4. Авто-миграции и "Лечение" базы при старте ---
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
            EnsureDbSchema(context); // Доп. проверка колонок
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

// Метод для "самолечения" схемы (если миграции не используются)
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
        ");
    }
    catch { /* Игнорируем ошибки при проверке схемы */ }
}