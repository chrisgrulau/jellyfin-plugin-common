using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Common.Resilience;

/// <summary>
/// Retry delays per <see cref="FailureClass"/>. A delay the provider states itself (a <c>Retry-After</c> header, a
/// rate-limit reset time or the end of a billing period) always wins over these defaults.
/// </summary>
internal static class BackoffSchedule
{
    /// <summary>Provider-limit retries when the provider gives no reset time: 1 hour, 1 day, 3 days, then weekly.</summary>
    public static readonly IReadOnlyList<TimeSpan> ProviderLimitSteps =
        [TimeSpan.FromHours(1), TimeSpan.FromDays(1), TimeSpan.FromDays(3), TimeSpan.FromDays(7)];

    /// <summary>First transient retry delay; it doubles on each attempt up to <see cref="TransientCap"/>.</summary>
    public static readonly TimeSpan TransientFirst = TimeSpan.FromSeconds(2);

    /// <summary>Longest wait between transient retries.</summary>
    public static readonly TimeSpan TransientCap = TimeSpan.FromMinutes(5);

    /// <summary>How long transient failures may persist before the user is alerted.</summary>
    public static readonly TimeSpan TransientAlertAfter = TimeSpan.FromMinutes(30);

    /// <summary>How often connectivity is re-checked while there is no network connection.</summary>
    public static readonly TimeSpan ConnectivityProbeInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The delay before retry number <paramref name="attempt"/> (1 = first retry).
    /// </summary>
    /// <param name="failure">What went wrong.</param>
    /// <param name="attempt">Retry number, starting at 1.</param>
    /// <param name="providerSaid">The delay the provider asked for, if it gave one.</param>
    /// <param name="jitter">A value in [0, 1) used to spread retries so they don't all fire together.</param>
    /// <returns>The delay, or <c>null</c> when this failure is never retried automatically.</returns>
    public static TimeSpan? Delay(FailureClass failure, int attempt, TimeSpan? providerSaid, double jitter)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(jitter);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(jitter, 1);

        if (failure is FailureClass.Authentication or FailureClass.BadRequest)
        {
            return null;
        }

        if (providerSaid is { } stated && stated > TimeSpan.Zero)
        {
            return stated;
        }

        return failure switch
        {
            FailureClass.NoConnection => ConnectivityProbeInterval,
            // Short waits: anywhere from half to all of the delay, so bursts of retries spread out
            FailureClass.Transient => Min(TransientFirst * Math.Pow(2, Math.Min(attempt - 1, 20)), TransientCap) * (0.5 + (0.5 * jitter)),

            // Long waits: within ±10 % of the step
            FailureClass.ProviderLimit => ProviderLimitSteps[Math.Min(attempt, ProviderLimitSteps.Count) - 1] * (0.9 + (0.2 * jitter)),
            _ => null,
        };
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
