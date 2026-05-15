using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;

namespace Controllers;

[ApiController]
[Route("api/mvp")]
public class MvpController : ControllerBase
{
    public class MvpVoteRequest
    {
        [Required] public int TournamentId { get; set; }
        [Required] public int PlayerId { get; set; }
    }

    [HttpPost("vote")]
    public IActionResult Vote([FromBody] MvpVoteRequest request)
        => Ok(new { success = true, request.PlayerId, request.TournamentId });

    [HttpGet("results")]
    public IActionResult Results([FromQuery] int tournamentId)
        => Ok(new
        {
            isOpen = true,
            candidates = new[] { new { id = 10, name = "s1mple" }, new { id = 11, name = "m0NESY" } },
            results = new[] { new { id = 10, name = "s1mple", votes = 120 }, new { id = 11, name = "m0NESY", votes = 85 } },
            tournamentId
        });

    [HttpPost("open")]
    public IActionResult Open([FromBody] bool isOpen)
        => Ok(new { isOpen });
}
