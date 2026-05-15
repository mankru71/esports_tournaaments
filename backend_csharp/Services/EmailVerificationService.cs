using System.Security.Cryptography;
using System.Text;
using Data;
using Models;

namespace EsportsBackend.Services;

/// <summary>
/// Secure token lifecycle: generate → store SHA-256 hash in DB → validate via constant-time compare → consume.
///
/// WHY NO REDIS: This project uses EF Core + Postgres with no Redis dependency.
/// If you add Redis later, swap this implementation behind IEmailVerificationService.
///
/// SECURITY NOTES:
/// - Token = 32 random bytes (256-bit entropy), Base64-encoded. NOT Guid.NewGuid().
/// - DB stores SHA-256(token), never the raw token.
/// - Comparison uses CryptographicOperations.FixedTimeEquals — no timing oracle.
/// - Token is single-use: cleared immediately on successful validation.
/// </summary>
public interface IEmailVerificationService
{
    /// <summary>Returns the raw token to embed in the email URL. Stores only its hash.</summary>
    Task<string> GenerateAndStoreAsync(AppUser user, CancellationToken ct = default);

    /// <summary>Validates rawToken, marks user verified, clears the token. Returns false on any failure.</summary>
    Task<bool> ValidateAndConsumeAsync(AppUser user, string rawToken, CancellationToken ct = default);
}

public sealed class DbEmailVerificationService : IEmailVerificationService
{
    private readonly AppDbContext _db;

    public DbEmailVerificationService(AppDbContext db) => _db = db;

    public async Task<string> GenerateAndStoreAsync(AppUser user, CancellationToken ct = default)
    {
        // 32 bytes = 256 bits — CSPRNG guaranteed by RandomNumberGenerator
        var rawBytes = RandomNumberGenerator.GetBytes(32);
        var rawToken = Convert.ToBase64String(rawBytes);     // 44-char Base64

        // Store only the hash — DB breach ≠ instant account takeover
        user.EmailVerificationToken        = Hash(rawToken);
        user.EmailVerificationTokenExpiry  = DateTime.UtcNow.AddHours(24);
        // Overwriting revokes any previously issued link implicitly

        await _db.SaveChangesAsync(ct);
        return rawToken;
    }

    public async Task<bool> ValidateAndConsumeAsync(AppUser user, string rawToken, CancellationToken ct = default)
    {
        // Always run the hash + compare — no early exits that differ on "user has token" vs "not"
        // This prevents callers from accidentally introducing a timing oracle.
        var storedHash    = user.EmailVerificationToken ?? string.Empty;
        var candidateHash = Hash(rawToken);

        var storedBytes    = Encoding.UTF8.GetBytes(storedHash);
        var candidateBytes = Encoding.UTF8.GetBytes(candidateHash);

        // Constant-time: both branches execute in equal time regardless of match
        var hashMatch = CryptographicOperations.FixedTimeEquals(storedBytes, candidateBytes);

        // Expiry check AFTER hash check — don't short-circuit before FixedTimeEquals
        var notExpired = user.EmailVerificationTokenExpiry.HasValue
                      && user.EmailVerificationTokenExpiry.Value > DateTime.UtcNow;

        var tokenPresent = !string.IsNullOrEmpty(user.EmailVerificationToken);

        if (!hashMatch || !notExpired || !tokenPresent)
            return false;

        // Consume: single-use — cleared immediately
        user.IsEmailVerified               = true;
        user.EmailVerificationToken        = null;
        user.EmailVerificationTokenExpiry  = null;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    // SHA-256 of the raw token. Deterministic, one-way, fast.
    private static string Hash(string rawToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken))).ToLowerInvariant();
}
