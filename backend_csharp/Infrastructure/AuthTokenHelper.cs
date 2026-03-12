using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Infrastructure;

public static class AuthTokenHelper
{
    public static string? GetBearerToken(HttpRequest request)
    {
        var auth = request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(auth) || !auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return null;

        return auth.Substring("Bearer ".Length).Trim();
    }

    public static Dictionary<string, string> ParseClaims(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return new Dictionary<string, string>();

        var parts = token.Split('.');
        if (parts.Length < 2)
            return new Dictionary<string, string>();

        try
        {
            var padded = parts[1] + new string('=', (4 - parts[1].Length % 4) % 4);
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(padded.Replace('-', '+').Replace('_', '/')));
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ToString());
        }
        catch
        {
            return new Dictionary<string, string>();
        }
    }

    public static int? GetUserId(HttpRequest request)
    {
        var claims = ParseClaims(GetBearerToken(request));
        return claims.TryGetValue("sub", out var raw) && int.TryParse(raw, out var userId)
            ? userId
            : null;
    }

    public static string? GetRole(HttpRequest request)
    {
        var claims = ParseClaims(GetBearerToken(request));
        return claims.TryGetValue("role", out var raw) ? raw?.Trim().ToLowerInvariant() : null;
    }

    public static bool IsInAnyRole(HttpRequest request, params string[] roles)
    {
        var role = GetRole(request);
        return !string.IsNullOrWhiteSpace(role)
            && roles.Any(r => string.Equals(r, role, StringComparison.OrdinalIgnoreCase));
    }
}
