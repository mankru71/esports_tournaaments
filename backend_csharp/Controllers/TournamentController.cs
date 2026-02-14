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
            var tournaments = _tournamentService.GetAllTournaments();
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
                return NotFound();
            return Ok(tournament);
        }
    }
}