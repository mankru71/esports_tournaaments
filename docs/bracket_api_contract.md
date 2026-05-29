# Tournament Bracket API Contract

## Endpoint
`GET /api/tournaments/{id}/bracket`

## Full JSON Response

```json
{
  "summary": "Quarter-finals · Semi-finals · Grand Final",
  "groups": [],
  "matches": [
    { "id":1, "round":"Quarter-final", "teamA":"Team Neon",   "teamB":"Team Shadow",  "scoreA":2, "scoreB":1, "status":"finished" },
    { "id":2, "round":"Quarter-final", "teamA":"Team Blaze",  "teamB":"Team Frost",   "scoreA":2, "scoreB":0, "status":"finished" },
    { "id":3, "round":"Quarter-final", "teamA":"Team Apex",   "teamB":"Team Vortex",  "scoreA":0, "scoreB":2, "status":"finished" },
    { "id":4, "round":"Quarter-final", "teamA":"Team Cipher", "teamB":"Team Storm",   "scoreA":2, "scoreB":1, "status":"finished" },
    { "id":5, "round":"Semi-final",    "teamA":"Team Neon",   "teamB":"Team Blaze",   "scoreA":2, "scoreB":1, "status":"finished" },
    { "id":6, "round":"Semi-final",    "teamA":"Team Vortex", "teamB":"Team Cipher",  "scoreA":1, "scoreB":2, "status":"live"     },
    { "id":7, "round":"Grand Final",   "teamA":"Team Neon",   "teamB":"TBD",          "scoreA":0, "scoreB":0, "status":"upcoming" }
  ]
}
```

## Round Name → Bracket Column Mapping

| round value       | Column |
|-------------------|--------|
| "Round of 64"     | 1      |
| "Round of 32"     | 2      |
| "Round of 16"     | 3      |
| "Quarter-final"   | 4      |
| "Semi-final"      | 5      |
| "Grand Final"     | 6      |

## Status Values

| status     | Visual                          |
|------------|---------------------------------|
| finished   | Amber border, winner highlight  |
| live       | Green border + glow pulse       |
| upcoming   | Dark border (default)           |

## C# Snippet (TournamentController.cs)

```csharp
[HttpGet("{id}/bracket")]
public async Task<IActionResult> GetBracket(int id)
{
    var matches = await _db.Matches
        .Where(m => m.TournamentId == id)
        .OrderBy(m => m.Round).ThenBy(m => m.Id)
        .Select(m => new {
            id     = m.Id,
            round  = m.Round,            // "Quarter-final" | "Semi-final" | "Grand Final"
            teamA  = m.TeamA.Name,
            teamB  = m.TeamB != null ? m.TeamB.Name : "TBD",
            scoreA = m.ScoreA,
            scoreB = m.ScoreB,
            status = m.Status.ToString().ToLower()
        }).ToListAsync();

    return Ok(new { summary = $"{matches.Count} matches", groups = Array.Empty<object>(), matches });
}
```
