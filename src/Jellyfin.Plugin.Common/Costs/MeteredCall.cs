using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Common.Resilience;

namespace Jellyfin.Plugin.Common.Costs;

/// <summary>
/// How a <see cref="MeteredCall"/> recognises a billed failure and refuses a call. The defaults suit callers that throw
/// <see cref="ProviderException"/>; a plugin with its own exception type passes its own recognisers.
/// </summary>
internal sealed record MeteredCallOptions
{
    /// <summary>Gets the defaults.</summary>
    public static MeteredCallOptions Default { get; } = new();

    /// <summary>
    /// Gets how to tell that a failed call was billed anyway (its reservation is then settled, not released). By default,
    /// a <see cref="ProviderException"/> with <see cref="ProviderException.Charged"/> set.
    /// </summary>
    public Func<Exception, bool> IsCharged { get; init; } = static ex => ex is ProviderException { Charged: true };

    /// <summary>
    /// Gets what a billed failure cost, or <c>null</c> if it can't be worked out (it is then recorded at the estimate, which
    /// errs on the side of spending less). By default, <see cref="ProviderException.ChargedCost"/>.
    /// </summary>
    public Func<Exception, Money?> ChargedCost { get; init; } = static ex => (ex as ProviderException)?.ChargedCost;

    /// <summary>
    /// Gets how to tell that a failed call certainly wasn't billed (its reservation is then released). By default, a
    /// <see cref="ProviderException"/> without <see cref="ProviderException.Charged"/>, or a cancellation. Any other
    /// failure might have been billed, so it is recorded at the estimate, erring on the side of spending less.
    /// </summary>
    public Func<Exception, bool> IsUncharged { get; init; } = static ex => ex is ProviderException or OperationCanceledException;

    /// <summary>
    /// Gets the exception thrown when the limits refuse the call, from the reason to show people. By default a
    /// <see cref="ProviderException"/> of class <see cref="FailureClass.ProviderLimit"/>.
    /// </summary>
    public Func<string, Exception> Refuse { get; init; } = static why => new ProviderException(why) { Failure = FailureClass.ProviderLimit };

    /// <summary>Gets a callback told what each billed call was recorded at (success or billed failure), if wanted.</summary>
    public Action<Money>? Recorded { get; init; }
}

/// <summary>
/// Runs a paid call within the spending limits, in one place for every plugin: its estimated cost is reserved first, on
/// the plugin's <see cref="SpendLedger"/> or another <see cref="ISpendMeter"/> such as the AI plugin's budget (the call
/// isn't made if the limits refuse it); a successful call is settled at its actual cost; a call that failed but was billed anyway (see <see cref="MeteredCallOptions.IsCharged"/>) is settled at
/// what it used; a failure that certainly wasn't billed (see <see cref="MeteredCallOptions.IsUncharged"/>, cancellation
/// included) releases the reservation; an unexpected failure is recorded at the estimate.
/// </summary>
internal static class MeteredCall
{
    /// <summary>
    /// Reserves on this plugin's own ledger, runs the call, then settles or releases.
    /// </summary>
    /// <typeparam name="T">What the call returns.</typeparam>
    /// <param name="ledger">The spend ledger.</param>
    /// <param name="limits">The spending limits.</param>
    /// <param name="rates">The latest exchange rates, if any.</param>
    /// <param name="provider">Provider id.</param>
    /// <param name="purpose">What the call is for (<c>subtitles.sync</c> …), for the record.</param>
    /// <param name="estimate">The most the call is expected to cost, as the provider charges it.</param>
    /// <param name="call">The call.</param>
    /// <param name="actualCost">What a successful call cost, from its result, or <c>null</c> to record the estimate.</param>
    /// <param name="options">How billed failures are recognised and refusals thrown, or <c>null</c> for <see cref="MeteredCallOptions.Default"/>.</param>
    /// <param name="cancellationToken">Cancellation token, passed to the call.</param>
    /// <returns>The call's result.</returns>
    /// <exception cref="Exception">The refusal from <see cref="MeteredCallOptions.Refuse"/>, or whatever the call threw.</exception>
    public static Task<T> RunAsync<T>(
        SpendLedger ledger,
        SpendLimits limits,
        ExchangeRates? rates,
        string provider,
        string purpose,
        Money estimate,
        Func<CancellationToken, Task<T>> call,
        Func<T, Money?> actualCost,
        MeteredCallOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(limits);
        return RunAsync(new LocalSpendMeter(ledger, limits, () => rates), provider, purpose, estimate, call, actualCost, options, cancellationToken);
    }

    /// <summary>
    /// Reserves on a meter (this plugin's ledger, or a budget another plugin owns), runs the call, then settles or releases
    /// on the same meter.
    /// </summary>
    /// <typeparam name="T">What the call returns.</typeparam>
    /// <param name="meter">Where the cost is reserved and settled.</param>
    /// <param name="provider">Provider id.</param>
    /// <param name="purpose">What the call is for (<c>subtitles.sync</c> …), for the record.</param>
    /// <param name="estimate">The most the call is expected to cost, as the provider charges it.</param>
    /// <param name="call">The call.</param>
    /// <param name="actualCost">What a successful call cost, from its result, or <c>null</c> to record the estimate.</param>
    /// <param name="options">How billed failures are recognised and refusals thrown, or <c>null</c> for <see cref="MeteredCallOptions.Default"/>.</param>
    /// <param name="cancellationToken">Cancellation token, passed to the call.</param>
    /// <returns>The call's result.</returns>
    /// <exception cref="Exception">The refusal from <see cref="MeteredCallOptions.Refuse"/>, or whatever the call threw.</exception>
    public static async Task<T> RunAsync<T>(
        ISpendMeter meter,
        string provider,
        string purpose,
        Money estimate,
        Func<CancellationToken, Task<T>> call,
        Func<T, Money?> actualCost,
        MeteredCallOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(meter);
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(actualCost);
        var o = options ?? MeteredCallOptions.Default;

        var decision = await meter.ReserveAsync(provider, purpose, estimate, cancellationToken).ConfigureAwait(false);
        if (decision.ReservationId is not { } reservation)
        {
            throw o.Refuse(decision.Refusal ?? "Not allowed by the spending limits.");
        }

        T result;
        try
        {
            result = await call(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (o.IsCharged(ex))
        {
            // Answered and billed, but unusable (a refusal, cut off, unreadable): recorded at what it used
            await SettleAsync(meter, reservation, o.ChargedCost(ex) ?? estimate, o).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (o.IsUncharged(ex))
        {
            // Refused by the provider, never answered, or cancelled: not charged
            await meter.ReleaseAsync(reservation).ConfigureAwait(false);
            throw;
        }
        catch (Exception)
        {
            // Unexpected: it may have been billed, so it is recorded at the estimate
            await SettleAsync(meter, reservation, estimate, o).ConfigureAwait(false);
            throw;
        }

        await SettleAsync(meter, reservation, SafeCost(actualCost, result) ?? estimate, o).ConfigureAwait(false);
        return result;
    }

    private static Money? SafeCost<T>(Func<T, Money?> actualCost, T result)
    {
        // A cost that can't be worked out is recorded at the estimate; the call itself succeeded
        var cost = actualCost(result);
        return cost is { Amount: >= 0 } ? cost : null;
    }

    private static async Task SettleAsync(ISpendMeter meter, Guid reservation, Money actual, MeteredCallOptions o)
    {
        await meter.SettleAsync(reservation, actual).ConfigureAwait(false);
        o.Recorded?.Invoke(actual);
    }
}
