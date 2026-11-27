using System.Net;

namespace RelayStack.Platform.Deliveries;

public enum AttemptClassification
{
    Success,
    Retryable,
    Permanent,
}

/// <summary>
/// Retry rules for webhook delivery (docs/DELIVERY_SEMANTICS.md):
/// 2xx succeeds; 408, 425, 429, 5xx, timeouts and network errors are retried with exponential backoff;
/// other responses (3xx, 4xx) are permanent because repeating the same request cannot change the answer.
/// Blocked destinations (SSRF policy) are permanent.
/// </summary>
public sealed class RetryPolicy(double baseDelaySeconds, double maxDelaySeconds)
{
    public static AttemptClassification Classify(HttpStatusCode status)
    {
        var code = (int)status;
        if (code is >= 200 and < 300)
        {
            return AttemptClassification.Success;
        }

        return code is 408 or 425 or 429 or >= 500 ? AttemptClassification.Retryable : AttemptClassification.Permanent;
    }

    /// <summary>
    /// Delay before attempt <paramref name="nextAttemptNumber"/> (2 = first retry). Exponential with "equal jitter":
    /// half of the exponential delay is fixed and half is random, which spreads retries without allowing near-zero waits.
    /// A server-provided Retry-After is honoured when it is longer, up to the maximum delay.
    /// </summary>
    public TimeSpan NextDelay(int nextAttemptNumber, double random01, TimeSpan? retryAfter = null)
    {
        var exponent = Math.Clamp(nextAttemptNumber - 2, 0, 30);
        var ceiling = Math.Min(maxDelaySeconds, baseDelaySeconds * Math.Pow(2, exponent));
        var seconds = (ceiling / 2) + (random01 * ceiling / 2);
        if (retryAfter is { } ra && ra.TotalSeconds > seconds)
        {
            seconds = Math.Min(ra.TotalSeconds, maxDelaySeconds);
        }

        return TimeSpan.FromSeconds(seconds);
    }
}
