using Microsoft.AspNetCore.Mvc;

namespace Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class TournamentController : ControllerBase
    {
        private readonly Services.TournamentService _tournamentService;

        public TournamentController(Services.TournamentService tournamentService)
        {
            _tournamentService = tournamentService;
        }

        [HttpGet]
        public IActionResult Get()
        {
            var tournaments = _tournamentService.GetAllTournaments().Select(ToDto);
            return Ok(tournaments);
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

        private static object ToDto(Models.Tournament t) => new
        {
            id = t.Id,
            name = t.Name,
            discipline = t.Game,
            format = "single_elimination",
            status = t.Status,
            startDate = t.StartDate,
            prizePool = t.PrizePool,
            totalAmount = t.PrizePool,
            stagesSummary = "R1 -> Final",
            currentParticipants = t.CurrentParticipants,
            maxParticipants = t.MaxParticipants
        };
    }
}
