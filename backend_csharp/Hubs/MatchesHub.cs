using Microsoft.AspNetCore.SignalR;

namespace Hubs;

public class MatchesHub : Hub
{
    public Task JoinTournament(string tournamentId)
        => Groups.AddToGroupAsync(Context.ConnectionId, $"tournament:{tournamentId}");

    public Task LeaveTournament(string tournamentId)
        => Groups.RemoveFromGroupAsync(Context.ConnectionId, $"tournament:{tournamentId}");
}
