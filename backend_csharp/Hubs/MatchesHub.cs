using Data;
using Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Models;

namespace Hubs;

[Authorize]
public class MatchesHub : Hub
{
    private readonly AppDbContext _db;

    public MatchesHub(AppDbContext db)
    {
        _db = db;
    }

    [AllowAnonymous]
    public Task JoinTournament(string tournamentId)
        => Groups.AddToGroupAsync(Context.ConnectionId, $"tournament:{tournamentId}");

    [AllowAnonymous]
    public Task LeaveTournament(string tournamentId)
        => Groups.RemoveFromGroupAsync(Context.ConnectionId, $"tournament:{tournamentId}");

    [AllowAnonymous]
    public Task JoinMatchLobby(string matchId)
        => Groups.AddToGroupAsync(Context.ConnectionId, $"match:{matchId}");

    [AllowAnonymous]
    public Task LeaveMatchLobby(string matchId)
        => Groups.RemoveFromGroupAsync(Context.ConnectionId, $"match:{matchId}");

    public Task JoinUserGroup(string userId)
        => Groups.AddToGroupAsync(Context.ConnectionId, $"user:{userId}");

    public async Task SendMessage(int matchId, string message, bool isInternal)
    {
        var userId = Context.User?.GetUserId();
        if (userId == null)
            return;

        var user = await _db.Users.FindAsync(userId.Value);
        if (user == null || string.IsNullOrWhiteSpace(message))
            return;

        if (isInternal)
        {
            var match = await _db.Matches.Include(m => m.TeamA).Include(m => m.TeamB).FirstOrDefaultAsync(m => m.Id == matchId);
            if (match == null) return;

            bool isParticipant = false;
            var isTeamA = match.TeamA != null && await _db.TeamPlayers.AnyAsync(tp => tp.TeamId == match.TeamAId && tp.Nickname == user.Nickname);
            var isTeamB = match.TeamB != null && await _db.TeamPlayers.AnyAsync(tp => tp.TeamId == match.TeamBId && tp.Nickname == user.Nickname);
            
            if (isTeamA || isTeamB || user.Role == "admin" || user.Role == "judge")
                isParticipant = true;

            if (!isParticipant) return; // Unauthorized for internal lobby
        }

        var comment = new MatchComment
        {
            MatchId = matchId,
            UserId = userId.Value,
            Message = message.Trim(),
            IsInternalLobby = isInternal,
            TimestampUtc = DateTime.UtcNow
        };

        _db.MatchComments.Add(comment);
        await _db.SaveChangesAsync();

        var commentData = new
        {
            id = comment.Id,
            matchId = matchId,
            userId = user.Id,
            nickname = user.Nickname,
            avatarUrl = user.AvatarUrl ?? user.FaceitAvatar,
            message = comment.Message,
            isInternalLobby = comment.IsInternalLobby,
            timestampUtc = comment.TimestampUtc,
            predictorMMR = user.PredictorMMR,
            badges = await _db.UserBadges.Where(ub => ub.UserId == user.Id).Select(ub => new { ub.Badge.Name, ub.Badge.IconUrlOrCss, ub.Badge.ColorCss }).ToListAsync()
        };

        await Clients.Group($"match:{matchId}").SendAsync("ReceiveMessage", commentData);
    }
}
