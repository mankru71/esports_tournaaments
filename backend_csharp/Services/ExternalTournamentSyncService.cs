using Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Models;

namespace Services;

public class ExternalTournamentSyncService
{
    private readonly AppDbContext _db;
    private readonly PandaScoreService _pandascore;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ExternalTournamentSyncService> _logger;

    private const string CacheKey = "pandascore:sync:upcoming";

    public ExternalTournamentSyncService(AppDbContext db, PandaScoreService pandascore, IMemoryCache cache, ILogger<ExternalTournamentSyncService> logger)
    {
        _db = db;
        _pandascore = pandascore;
        _cache = cache;
        _logger = logger;
    }

    public async Task SyncUpcomingAsync(CancellationToken ct = default)
    {
        if (!_pandascore.Enabled)
            return;

        if (_cache.TryGetValue(CacheKey, out _))
            return;

        try
        {
            var synced = new List<PandaTournament>();
            synced.AddRange(await _pandascore.GetRunningTournamentsAsync(15, ct: ct));
            synced.AddRange(await _pandascore.GetUpcomingTournamentsAsync(25, ct: ct));
            var upcoming = synced
                .Where(t => !string.IsNullOrWhiteSpace(t.Id))
                .GroupBy(t => t.Id)
                .Select(g => g.First())
                .ToList();

            foreach (var t in upcoming)
            {
                if (string.IsNullOrWhiteSpace(t.Id) || string.IsNullOrWhiteSpace(t.Name))
                    continue;

                var existing = await _db.Tournaments
                    .FirstOrDefaultAsync(x => x.Provider == "pandascore" && x.ProviderTournamentId == t.Id, ct);

                var startDate = string.Empty;
                if (!string.IsNullOrWhiteSpace(t.BeginAt))
                {
                    startDate = t.BeginAt.Length >= 10 ? t.BeginAt.Substring(0, 10) : t.BeginAt;
                }

                var stageType = GuessStageType(t.Name, t.Status);
                var format = stageType == "groups" ? "group_stage" : "single_elimination";

                if (existing == null)
                {
                    _db.Tournaments.Add(new Tournament
                    {
                        Name = t.Name,
                        Game = t.VideogameName ?? "n/a",
                        PrizePool = t.PrizePool ?? 0,
                        MaxParticipants = 0,
                        CurrentParticipants = 0,
                        StartDate = startDate,
                        Status = NormalizeStatus(t.Status),
                        Format = format,
                        StageType = stageType,
                        IsExternal = true,
                        Provider = "pandascore",
                        ProviderTournamentId = t.Id,
                        PrizeDistributionJson = DefaultPrizeDistribution()
                    });
                }
                else
                {
                    existing.Name = t.Name;
                    existing.Game = t.VideogameName ?? existing.Game;
                    existing.PrizePool = t.PrizePool ?? existing.PrizePool;
                    existing.StartDate = string.IsNullOrWhiteSpace(startDate) ? existing.StartDate : startDate;
                    existing.Status = NormalizeStatus(t.Status);
                    existing.Format = string.IsNullOrWhiteSpace(existing.Format) ? format : existing.Format;
                    existing.StageType = string.IsNullOrWhiteSpace(existing.StageType) ? stageType : existing.StageType;
                    existing.IsExternal = true;
                    existing.Provider = "pandascore";
                    existing.ProviderTournamentId = t.Id;
                    if (string.IsNullOrWhiteSpace(existing.PrizeDistributionJson))
                        existing.PrizeDistributionJson = DefaultPrizeDistribution();
                }
            }

            await _db.SaveChangesAsync(ct);
            _cache.Set(CacheKey, true, TimeSpan.FromMinutes(10));
            _logger.LogInformation("PandaScore tournaments synced: {Count}", upcoming.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PandaScore sync failed (will continue with local tournaments).");
            _cache.Set(CacheKey, true, TimeSpan.FromMinutes(2));
        }
    }

    private static string GuessStageType(string? name, string? status)
    {
        var haystack = $"{name} {status}".ToLowerInvariant();
        if (haystack.Contains("group") || haystack.Contains("swiss"))
            return "groups";
        return "single";
    }

    private static string NormalizeStatus(string? status)
    {
        var s = (status ?? string.Empty).Trim().ToLowerInvariant();
        return s switch
        {
            "running" => "live",
            "finished" => "finished",
            "canceled" => "finished",
            "postponed" => "planned",
            _ => "planned"
        };
    }

    private static string DefaultPrizeDistribution()
        => "[{\"place\":\"1 место\",\"percent\":50},{\"place\":\"2 место\",\"percent\":30},{\"place\":\"3 место\",\"percent\":20}]";
}
