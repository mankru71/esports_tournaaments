namespace Infrastructure;

/// <summary>
/// Delegating handler that enforces Liquipedia's strict 1 request / 2 seconds rate limit.
///
/// WHY A DELEGATING HANDLER and not Polly RateLimiter:
/// Polly's RateLimiter rejects excess requests with an exception — callers must handle it.
/// Liquipedia's use-case is simple serialized access from a single service instance:
/// queue the request and wait rather than reject it. A SemaphoreSlim + Task.Delay achieves
/// this with zero extra packages and is safe for the single-process scenario.
///
/// IF you scale to multiple instances, move rate-limit state to Redis with a
/// sliding window key so the 1-req/2-sec applies across the cluster.
///
/// NOTE: _lastRequest is static — shared across all HttpClient instances using this handler.
/// This is intentional: Liquipedia bans per IP, not per HttpClient instance.
/// </summary>
public sealed class LiquipediaRateLimitHandler : DelegatingHandler
{
    // Static: shared across all instances — the IP only has 1 slot globally
    private static readonly SemaphoreSlim _gate = new(1, 1);
    private static          DateTime      _lastRequest = DateTime.MinValue;

    // 2.1s instead of exactly 2.0s — small safety margin to avoid edge-case bans
    private static readonly TimeSpan _minInterval = TimeSpan.FromMilliseconds(2100);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var elapsed = DateTime.UtcNow - _lastRequest;
            if (elapsed < _minInterval)
            {
                var delay = _minInterval - elapsed;
                await Task.Delay(delay, ct);
            }

            _lastRequest = DateTime.UtcNow;
            var response = await base.SendAsync(request, ct);

            // Liquipedia returns 429 if you still somehow burst — back off harder
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(10);
                await Task.Delay(retryAfter, ct);
                // Do NOT retry automatically here — return 429 to caller.
                // Polly retry at the HttpClient level handles re-attempts.
            }

            return response;
        }
        finally
        {
            _gate.Release();
        }
    }
}
