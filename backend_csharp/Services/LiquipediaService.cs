using HtmlAgilityPack;
using Microsoft.Extensions.Caching.Memory;

namespace EsportsBackend.Services;

public class LiquipediaService : ITournamentProvider
{
    private readonly HttpClient _http;
    private readonly IMemoryCache _cache;
    public string ProviderName => "Liquipedia";

    public LiquipediaService(HttpClient http, IMemoryCache cache) 
    {
        _http = http;
        _cache = cache;
    }

    public async Task<IEnumerable<UnifiedTournament>> GetTournamentsAsync(CancellationToken ct)
    {
        if (_cache.TryGetValue("lp_tournaments", out IEnumerable<UnifiedTournament>? cached)) return cached!;

        // Запрос на парсинг главной страницы CS секции
        var response = await _http.GetStringAsync("?action=parse&page=Main_Page&format=json", ct);
        
        // Тут должен быть парсинг JSON-поля ["parse"]["text"]["*"] через HtmlAgilityPack
        // Оставляю заглушку логики парсинга — это Bottleneck #1 (CPU-bound задача)
        var list = new List<UnifiedTournament>(); 
        
        _cache.Set("lp_tournaments", list, TimeSpan.FromMinutes(30));
        return list;
    }
}