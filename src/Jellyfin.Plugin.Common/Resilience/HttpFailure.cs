using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Common.Resilience;

/// <summary>
/// Sorts a failed call to an external service into a <see cref="FailureClass"/>, and reads how long the provider asked
/// us to wait, so the right retry behaviour applies (see <see cref="BackoffSchedule"/>).
/// </summary>
internal static class HttpFailure
{
    /// <summary>The longest wait read from a response; anything longer (or absurd) is treated as this.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromDays(31);

    /// <summary>
    /// The longest wait an HTTP 429 may ask for and still count as a short rate limit. Per-second and per-minute windows
    /// reset within a minute; a provider asking for longer is pointing at an hourly, daily or monthly allowance, so the
    /// 429 is a <see cref="FailureClass.ProviderLimit"/>.
    /// </summary>
    public static readonly TimeSpan RateLimitWindow = TimeSpan.FromSeconds(60);

    // Provider-specific ways of saying the account's quota, credit or billing limit is used up. Matched as phrases, not
    // single words, so an ordinary rejected request ("insufficient audio", "credits" in a title) isn't mistaken for one
    private static readonly string[] LimitPhrases =
    [
        "insufficient_quota", "exceeded your current quota", "quota_exceeded", "quota exceeded", "billing_hard_limit_reached",
        "billing_not_active", "credit balance is too low", "insufficient credit", "insufficient_credit", "insufficient funds",
        "insufficient_funds", "out of credits", "payment required", "payment_required", "resource_exhausted",
    ];

    /// <summary>
    /// Classifies an HTTP response status. An HTTP 429 (too many requests) is a <see cref="FailureClass.ProviderLimit"/>
    /// when the provider says its allowance is used up: quota or billing wording in the body, or a wait longer than
    /// <see cref="RateLimitWindow"/>. Otherwise (a short wait, or none stated) it is <see cref="FailureClass.Transient"/>.
    /// A caller that stops asking a provider on any 429 checks <see cref="IsRateLimited"/> instead.
    /// </summary>
    /// <param name="status">The status code.</param>
    /// <param name="body">The response body, if read (some providers report an exhausted quota as 400 or 403).</param>
    /// <param name="retryAfter">The wait the provider asked for, if any (see <see cref="RetryAfter(HttpResponseMessage, DateTimeOffset)"/>).</param>
    /// <returns>The class.</returns>
    public static FailureClass Classify(HttpStatusCode status, string? body = null, TimeSpan? retryAfter = null)
    {
        var quota = body is not null && LimitPhrases.Any(p => body.Contains(p, StringComparison.OrdinalIgnoreCase));
        return (int)status switch
        {
            401 => FailureClass.Authentication,
            402 => FailureClass.ProviderLimit,
            403 => quota ? FailureClass.ProviderLimit : FailureClass.Authentication,
            408 or 409 or 425 => FailureClass.Transient,
            429 => quota || retryAfter > RateLimitWindow ? FailureClass.ProviderLimit : FailureClass.Transient,
            >= 500 and <= 599 => FailureClass.Transient,
            400 when quota => FailureClass.ProviderLimit,
            >= 400 and <= 499 => FailureClass.BadRequest,
            _ => FailureClass.Transient,
        };
    }

    /// <summary>
    /// Classifies an exception thrown while calling a service. A cancellation the caller asked for (shutdown) isn't a
    /// failure of the service: pass the caller's token, and it is rethrown as <see cref="OperationCanceledException"/>
    /// instead of being classed as <see cref="FailureClass.Transient"/>.
    /// </summary>
    /// <param name="exception">The exception.</param>
    /// <param name="cancellationToken">The caller's own token.</param>
    /// <returns>The class.</returns>
    /// <exception cref="OperationCanceledException">The caller's token was cancelled.</exception>
    public static FailureClass Classify(Exception exception, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("Cancelled by the caller.", exception, cancellationToken);
        }

        return exception switch
        {
            // Already classified (by ProviderHttp or a plugin): keep its class
            ProviderException p => p.Failure,
            HttpRequestException { StatusCode: { } status } => Classify(status),
            HttpRequestException { InnerException: SocketException s } when s.SocketErrorCode is SocketError.HostNotFound or SocketError.NetworkUnreachable or SocketError.HostUnreachable or SocketError.NetworkDown or SocketError.TryAgain
                => FailureClass.NoConnection,
            HttpRequestException => FailureClass.Transient,
            TaskCanceledException or TimeoutException => FailureClass.Transient,
            _ => FailureClass.Transient,
        };
    }

    /// <summary>
    /// Tells whether a failure was an HTTP 429 (too many requests), whatever its class: for callers that stop asking a
    /// provider for the rest of a run on any rate limit, short or long.
    /// </summary>
    /// <param name="exception">The failure.</param>
    /// <returns><c>true</c> for a <see cref="ProviderException"/> or <see cref="HttpRequestException"/> with status 429.</returns>
    public static bool IsRateLimited(Exception? exception)
        => exception switch
        {
            ProviderException p => p.RateLimited,
            HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } => true,
            _ => false,
        };

    /// <summary>
    /// Reads a <c>Retry-After</c> value: seconds, or an HTTP date.
    /// </summary>
    /// <param name="value">The header value.</param>
    /// <param name="now">The current time.</param>
    /// <returns>How long to wait, or <c>null</c> if absent or unreadable (bounded later by <see cref="BackoffSchedule"/>).</returns>
    public static TimeSpan? RetryAfter(string? value, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var v = value.Trim();
        if (double.TryParse(v, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds))
        {
            return Seconds(seconds);
        }

        return DateTimeOffset.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when) && when > now
            ? Bound(when - now)
            : null;
    }

    /// <summary>
    /// Reads how long to wait from a response: <c>Retry-After</c>, or the common rate-limit reset headers.
    /// </summary>
    /// <param name="response">The response.</param>
    /// <param name="now">The current time.</param>
    /// <returns>How long to wait, or <c>null</c>.</returns>
    public static TimeSpan? RetryAfter(HttpResponseMessage response, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.Headers.RetryAfter is { } ra)
        {
            if (ra.Delta is { } delta)
            {
                return Bound(delta);
            }

            if (ra.Date is { } date && date > now)
            {
                return Bound(date - now);
            }
        }

        foreach (var name in new[] { "retry-after-ms", "x-ratelimit-reset-requests", "x-ratelimit-reset", "anthropic-ratelimit-requests-reset" })
        {
            if (response.Headers.TryGetValues(name, out var values))
            {
                foreach (var v in values)
                {
                    if (name == "retry-after-ms" && double.TryParse(v, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var ms))
                    {
                        return Seconds(ms / 1000);
                    }

                    if (RetryAfter(v, now) is { } t)
                    {
                        return t;
                    }

                    // OpenAI style durations: "6m0s", "1.5s", "20ms"
                    if (Duration(v) is { } d)
                    {
                        return d;
                    }
                }
            }
        }

        return null;
    }

    // A number of seconds as a wait: negative or not a number is unreadable; anything over MaxWait is MaxWait
    private static TimeSpan? Seconds(double seconds)
        => double.IsNaN(seconds) || seconds < 0 ? null : seconds >= MaxWait.TotalSeconds ? MaxWait : TimeSpan.FromSeconds(seconds);

    private static TimeSpan Bound(TimeSpan wait) => wait > MaxWait ? MaxWait : wait;

    private static TimeSpan? Duration(string value)
    {
        // Summed in seconds (a double), converted once and bounded, so absurd values can't overflow
        var total = 0.0;
        var number = string.Empty;
        var any = false;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsAsciiDigit(c) || c == '.')
            {
                number += c;
                continue;
            }

            if (!double.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n))
            {
                return null;
            }

            if (c == 'm' && i + 1 < value.Length && value[i + 1] == 's')
            {
                total += n / 1000;
                i++;
            }
            else
            {
                var unit = c switch { 'h' => 3600.0, 'm' => 60.0, 's' => 1.0, _ => double.NaN };
                if (double.IsNaN(unit))
                {
                    return null;
                }

                total += n * unit;
            }

            number = string.Empty;
            any = true;
        }

        return any && number.Length == 0 ? Seconds(total) : null;
    }
}
