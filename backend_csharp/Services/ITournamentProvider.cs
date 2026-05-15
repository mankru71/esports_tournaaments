namespace EsportsBackend.Services;

public record UnifiedTournament(string ExternalId, string Name, DateTime? StartDate, string Status);

public interface ITournamentProvider
{
    string ProviderName { get; }
    Task<IEnumerable<UnifiedTournament>> GetTournamentsAsync(CancellationToken ct);
}