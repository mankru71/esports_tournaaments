using Microsoft.AspNetCore.Mvc;
using Services;
using System.Text.Json;

namespace Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class TournamentController : ControllerBase
    {
        private readonly TournamentService _tournamentService;
        private readonly ExternalTournamentSyncService _sync;

        public TournamentController(TournamentService tournamentService, ExternalTournamentSyncService sync)
        {
            _tournamentService = tournamentService;
            _sync = sync;
        }

        [HttpGet]
        public async Task<IActionResult> Get(CancellationToken ct)
        {
            await _sync.SyncUpcomingAsync(ct);
            var tournaments = _tournamentService.GetAllTournaments().Select(ToDto);
            return Ok(tournaments);
        }

        [HttpGet("stats")]
        public async Task<IActionResult> GetStats(CancellationToken ct)
        {
            return Ok(await _tournamentService.GetStatsAsync(ct));
        }

        [HttpGet("{id}")]
        public IActionResult GetById(int id)
        {
            var tournament = _tournamentService.GetTournamentById(id);
            if (tournament == null)
                return NotFound(new ProblemDetails { Title = "Not Found", Detail = "Tournament not found", Status = 404 });
            return Ok(ToDto(tournament));
        }

        private static object ToDto(Models.Tournament t)
        {
            var payouts = ParsePrizeDistribution(t.PrizeDistributionJson, t.PrizePool);
            return new
            {
                id = t.Id,
                name = t.Name,
                discipline = t.Game,
                format = t.Format,
                stageType = t.StageType,
                status = t.Status,
                startDate = t.StartDate,
                prizePool = t.PrizePool,
                totalAmount = t.PrizePool,
                currentParticipants = t.CurrentParticipants,
                maxParticipants = t.MaxParticipants,
                isExternal = t.IsExternal,
                provider = t.Provider,
                providerTournamentId = t.ProviderTournamentId,
                prizePayouts = payouts,
                stagesSummary = t.StageType == "groups" ? "Group stage → Playoffs" : "Single elimination"
            };
        }

        private static List<object> ParsePrizeDistribution(string? json, decimal prizePool)
        {
            var result = new List<object>();
            if (string.IsNullOrWhiteSpace(json)) return result;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var place = item.TryGetProperty("place", out var placeEl) ? placeEl.GetString() ?? "Место" : "Место";
                    var percent = item.TryGetProperty("percent", out var percentEl) && decimal.TryParse(percentEl.ToString(), out var p) ? p : 0m;
                    result.Add(new { place, percent, amount = Math.Round(prizePool * percent / 100m, 2) });
                }
            }
            catch
            {
            }
            return result;
        }
    }
}
