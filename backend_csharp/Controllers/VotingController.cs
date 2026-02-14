using Microsoft.AspNetCore.Mvc;

namespace Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class VotingController : ControllerBase
    {
        private readonly Services.TournamentService _tournamentService;

        public VotingController(Services.TournamentService tournamentService)
        {
            _tournamentService = tournamentService;
        }

        [HttpGet("nominees")]
        public IActionResult GetNominees()
        {
            var nominees = _tournamentService.GetNominees();
            return Ok(nominees);
        }

        public class VoteRequest
        {
            public int NomineeId { get; set; }
            public string VoterSession { get; set; } = string.Empty;
            public string VoterIp { get; set; } = string.Empty;
        }

        [HttpPost("vote")]
        public IActionResult Vote([FromBody] VoteRequest request)
        {
            var (success, message) = _tournamentService.Vote(request.NomineeId, request.VoterSession, request.VoterIp);
            return Ok(new { success, message });
        }

        [HttpGet("hasvoted/{sessionId}")]
        public IActionResult HasVoted(string sessionId)
        {
            var (hasVoted, nomineeId) = _tournamentService.HasVoted(sessionId);
            return Ok(new { hasVoted, nomineeId });
        }
    }
}