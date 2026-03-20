using Microsoft.AspNetCore.Mvc;
using Services;
using System.Text.Json;

namespace Controllers
{
    [ApiController]
    [Route("api/tournament")]
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


        public class CreateTournamentRequest
        {
            public string Name { get; set; } = string.Empty;
            public string Game { get; set; } = "counterstrike";
            public decimal PrizePool { get; set; }
            public int MaxParticipants { get; set; } = 8;
            public string StartDate { get; set; } = string.Empty;
            public string Format { get; set; } = "single_elimination";
            public string StageType { get; set; } = "single";
            public string Status { get; set; } = "planned";
        }

        [HttpPost]
        public async Task<IActionResult> Create([FromBody] CreateTournamentRequest request)
        {
            if (!Infrastructure.AuthTokenHelper.IsInAnyRole(Request, "admin"))
                return StatusCode(403, new { message = "Создавать турниры может только администратор" });

            var tournament = new Models.Tournament
            {
                Name = string.IsNullOrWhiteSpace(request.Name) ? "Новый турнир" : request.Name.Trim(),
                Game = string.IsNullOrWhiteSpace(request.Game) ? "counterstrike" : request.Game.Trim(),
                PrizePool = request.PrizePool < 0 ? 0 : request.PrizePool,
                MaxParticipants = request.MaxParticipants <= 0 ? 8 : request.MaxParticipants,
                CurrentParticipants = 0,
                StartDate = string.IsNullOrWhiteSpace(request.StartDate) ? DateTime.UtcNow.ToString("yyyy-MM-dd") : request.StartDate.Trim(),
                Status = string.IsNullOrWhiteSpace(request.Status) ? "planned" : request.Status.Trim().ToLowerInvariant(),
                Format = string.IsNullOrWhiteSpace(request.Format) ? "single_elimination" : request.Format.Trim().ToLowerInvariant(),
                StageType = string.IsNullOrWhiteSpace(request.StageType) ? "single" : request.StageType.Trim().ToLowerInvariant(),
            };

            _tournamentService.CreateTournament(tournament);
            return Created($"/api/tournament/{tournament.Id}", ToDto(tournament));
        }

        [HttpGet("stats")]
        public IActionResult GetStats()
        {
            return Ok(_tournamentService.GetStats());
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
