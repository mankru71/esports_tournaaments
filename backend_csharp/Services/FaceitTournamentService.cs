using System.Text.Json.Serialization;

namespace EsportsBackend.Services;

public class FaceitTournamentService : ITournamentProvider
{
    private readonly HttpClient _http;
    public string ProviderName => "Faceit";

    public FaceitTournamentService(HttpClient http) => _http = http;

    public async Task<IEnumerable<UnifiedTournament>> GetTournamentsAsync(CancellationToken ct)
    {
        // limit=50 — страховка от аллокаций
        var result = await _http.GetFromJsonAsync<FaceitResponse>("championships?limit=50", ct);
        return result?.Items.Select(x => new UnifiedTournament(
            x.Id, x.Name, DateTimeOffset.FromUnixTimeSeconds(x.Start).UtcDateTime, x.Status)) 
            ?? Enumerable.Empty<UnifiedTournament>();
    }

    private record FaceitResponse([property: JsonPropertyName("items")] FaceitItem[] Items);
    private record FaceitItem(
        [property: JsonPropertyName("championship_id")] string Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("tournament_start")] long Start,
        [property: JsonPropertyName("status")] string Status);
}