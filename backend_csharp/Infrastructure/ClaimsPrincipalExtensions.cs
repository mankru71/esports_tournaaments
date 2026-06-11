using System.Security.Claims;

namespace Infrastructure;

public static class ClaimsPrincipalExtensions
{
    public static int? GetUserId(this ClaimsPrincipal principal)
    {
        var idClaim = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
        if (int.TryParse(idClaim, out var id))
        {
            return id;
        }
        return null;
    }

    public static string? GetRole(this ClaimsPrincipal principal)
    {
        return principal.FindFirstValue(ClaimTypes.Role) ?? principal.FindFirstValue("role");
    }
}
