using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Common.Resilience;

/// <summary>
/// Sorts a failed call to an external service into a <see cref="FailureClass"/>, and reads how long the provider asked
/// us to wait, so the right retry behaviour applies (see <see cref="BackoffSchedule"/>).
/// </summary>
internal static class HttpFailure
{
    /// <summary>
    /// Classifies an HTTP response status.
    /// </summary>
    /// <param name="status">The status code.</param>
    /// <param name="body">The response body, if read (some providers report an exhausted quota as 400 or 403).</param>
    /// <returns>The class.</returns>
    public static FailureClass Classify(HttpStatusCode status, string? body = null)
    {
        var quota = body is not null && (body.Contains("quota", StringComparison.OrdinalIgnoreCase)
            || body.Contains("insufficient", StringComparison.OrdinalIgnoreCase)
            || body.Contains("credit", StringComparison.OrdinalIgnoreCase)
            || body.Contains("billing", StringComparison.OrdinalIgnoreCase));
        return (int)status switch
        {
            401 => FailureClass.Authentication,
            402 => FailureClass.ProviderLimit,
            403 => quota ? FailureClass.ProviderLimit : FailureClass.Authentication,
            408 or 409 or 425 => FailureClass.Transient,
            429 => quota ? FailureClass.ProviderLimit : FailureClass.Transient,
            >= 500 and <= 599 => FailureClass.Transient,
            400 when quota => FailureClass.ProviderLimit,
            >= 400 and <= 499 => FailureClass.BadRequest,
            _ => FailureClass.Transient,
        };
    }

    /// <summary>
    /// Classifies an exception thrown while calling a service.
    /// </summary>
    /// <param name="exception">The exception.</param>
    /// <returns>The class.</returns>
    public static FailureClass Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            HttpRequestException { StatusCode: { } status } => Classify(status),
            HttpRequestException { InnerException: SocketException s } when s.SocketErrorCode is SocketError.HostNotFound or SocketError.NetworkUnreachable or SocketError.HostUnreachable or SocketError.NetworkDown or SocketError.TryAgain
                => FailureClass.NoConnection,
            HttpRequestException => FailureClass.Transient,
            TaskCanceledException or TimeoutException => FailureClass.Transient,
            _ => FailureClass.Transient,
        };
    }

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
        if (double.TryParse(v, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0 && seconds < 1e9)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        return DateTimeOffset.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when) && when > now
            ? when - now
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
                return delta;
            }

            if (ra.Date is { } date && date > now)
            {
                return date - now;
            }
        }

        foreach (var name in new[] { "retry-after-ms", "x-ratelimit-reset-requests", "x-ratelimit-reset", "anthropic-ratelimit-requests-reset" })
        {
            if (response.Headers.TryGetValues(name, out var values))
            {
                foreach (var v in values)
                {
                    if (name == "retry-after-ms" && double.TryParse(v, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var ms) && ms >= 0)
                    {
                        return TimeSpan.FromMilliseconds(ms);
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

    private static TimeSpan? Duration(string value)
    {
        var total = TimeSpan.Zero;
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
                total += TimeSpan.FromMilliseconds(n);
                i++;
            }
            else
            {
                total += c switch
                {
                    'h' => TimeSpan.FromHours(n),
                    'm' => TimeSpan.FromMinutes(n),
                    's' => TimeSpan.FromSeconds(n),
                    _ => TimeSpan.MinValue,
                };
                if (total < TimeSpan.Zero)
                {
                    return null;
                }
            }

            number = string.Empty;
            any = true;
        }

        return any && number.Length == 0 ? total : null;
    }
}
