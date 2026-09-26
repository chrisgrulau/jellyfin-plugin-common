using System;
using System.Net;
using Jellyfin.Plugin.Common.Costs;

namespace Jellyfin.Plugin.Common.Resilience;

/// <summary>
/// A call to an external provider failed. The message is safe to show and log (keys are removed). It carries what the
/// caller needs to decide what happens next: the <see cref="Failure"/> class (retries and alerts), how long the provider
/// asked us to wait, and whether the provider billed the call anyway, with the usage to record it at (see
/// <see cref="MeteredCall"/>).
/// </summary>
/// <remarks>
/// Not sealed, so a plugin can derive its own internal provider exceptions from it. A plugin whose exception type is
/// public can't derive from this internal type; it can still use <see cref="MeteredCall"/> by passing
/// <see cref="MeteredCallOptions.IsCharged"/> and <see cref="MeteredCallOptions.ChargedCost"/>.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1064:Exceptions should be public", Justification = "Shared source is compiled into each plugin as internal types, so two plugins never clash.")]
internal class ProviderException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ProviderException"/> class.</summary>
    public ProviderException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ProviderException"/> class.</summary>
    /// <param name="message">A safe message (no keys).</param>
    public ProviderException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ProviderException"/> class.</summary>
    /// <param name="message">A safe message (no keys).</param>
    /// <param name="innerException">The cause.</param>
    public ProviderException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Gets what kind of failure it was (decides retries and alerts).</summary>
    public FailureClass Failure { get; init; } = FailureClass.Transient;

    /// <summary>Gets how long the provider asked us to wait before trying again, if it said.</summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>Gets the HTTP status the provider answered with, if the failure was an HTTP error.</summary>
    public HttpStatusCode? StatusCode { get; init; }

    /// <summary>
    /// Gets a value indicating whether the provider answered HTTP 429 (too many requests). This holds for a short rate
    /// limit (<see cref="FailureClass.Transient"/>) as well as a used-up allowance (<see cref="FailureClass.ProviderLimit"/>),
    /// for callers that stop asking the provider on any 429.
    /// </summary>
    public bool RateLimited => StatusCode == HttpStatusCode.TooManyRequests;

    /// <summary>
    /// Gets what the provider said in its error reply, keys removed and cut short, or <c>null</c> if it said nothing.
    /// A plugin can word its own message around it ("the provider said: …") instead of showing <see cref="Exception.Message"/>.
    /// </summary>
    public string? Detail { get; init; }

    /// <summary>
    /// Gets a value indicating whether the provider billed the call even so (it answered, but the answer was a refusal,
    /// was cut off or couldn't be read). A metered call then records the usage below instead of releasing its reservation.
    /// </summary>
    public bool Charged { get; init; }

    /// <summary>Gets what the provider charged, when <see cref="Charged"/> and the caller could work it out.</summary>
    public Money? ChargedCost { get; init; }

    /// <summary>Gets the model that was billed, when <see cref="Charged"/> and the provider named it.</summary>
    public string? ChargedModel { get; init; }

    /// <summary>Gets the input tokens billed, when <see cref="Charged"/> (token-priced providers).</summary>
    public long InputTokens { get; init; }

    /// <summary>Gets the output tokens billed (thinking included), when <see cref="Charged"/> (token-priced providers).</summary>
    public long OutputTokens { get; init; }

    /// <summary>Gets the seconds of audio billed, when <see cref="Charged"/> (audio-priced providers).</summary>
    public double AudioSeconds { get; init; }
}
