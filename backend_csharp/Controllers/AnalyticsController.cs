using Microsoft.AspNetCore.Mvc;
using System.Text;

namespace Controllers;

[ApiController]
[Route("api/analytics")]
public class AnalyticsController : ControllerBase
{
    [HttpGet]
    public IActionResult Get()
        => Ok(new
        {
            playerStats = new[] { new { player = "s1mple", kills = 87 }, new { player = "m0NESY", kills = 80 } },
            disciplinePopularity = new[] { new { discipline = "CS2", value = 70 }, new { discipline = "Dota 2", value = 30 } }
        });

    [HttpGet("export/csv")]
    public IActionResult ExportCsv()
    {
        var csv = "player,kills\ns1mple,87\nm0NESY,80\n";
        return File(Encoding.UTF8.GetBytes(csv), "text/csv", "analytics.csv");
    }
}
