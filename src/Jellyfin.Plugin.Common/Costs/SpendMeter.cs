using System;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Common.Costs;

/// <summary>
/// Where a paid call's cost is reserved and settled: this plugin's own <see cref="SpendLedger"/> (see
/// <see cref="LocalSpendMeter"/>), or the budget another plugin owns, through its spending entry point (see
/// <see cref="BridgedSpendMeter"/>). <see cref="MeteredCall"/> works the same with either, and a call is recorded in
/// exactly one ledger.
/// </summary>
internal interface ISpendMeter
{
    /// <summary>
    /// Reserves the estimated cost of a call, if the limits allow it.
    /// </summary>
    /// <param name="provider">Provider id (this plugin's own).</param>
    /// <param name="purpose">What the call is for (<c>subtitles.sync</c> …), for the record.</param>
    /// <param name="estimate">The estimated cost, as the provider charges it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The decision.</returns>
    Task<SpendDecision> ReserveAsync(string provider, string purpose, Money estimate, CancellationToken cancellationToken);

    /// <summary>
    /// Records what a reserved call actually cost. Never throws; a settle that can't be delivered leaves the reservation
    /// counting at its estimate.
    /// </summary>
    /// <param name="reservationId">The reservation.</param>
    /// <param name="actual">The actual cost, as charged.</param>
    /// <returns>A task.</returns>
    Task SettleAsync(Guid reservationId, Money actual);

    /// <summary>
    /// Drops a reservation for a call that wasn't charged. Never throws.
    /// </summary>
    /// <param name="reservationId">The reservation.</param>
    /// <returns>A task.</returns>
    Task ReleaseAsync(Guid reservationId);
}

/// <summary>
/// This plugin's own ledger and limits as an <see cref="ISpendMeter"/>.
/// </summary>
internal sealed class LocalSpendMeter : ISpendMeter
{
    private readonly SpendLedger _ledger;
    private readonly SpendLimits _limits;
    private readonly Func<ExchangeRates?> _rates;

    /// <summary>
    /// Initializes a new instance of the <see cref="LocalSpendMeter"/> class.
    /// </summary>
    /// <param name="ledger">The ledger.</param>
    /// <param name="limits">The limits.</param>
    /// <param name="rates">The latest exchange rates when a call is reserved.</param>
    public LocalSpendMeter(SpendLedger ledger, SpendLimits limits, Func<ExchangeRates?> rates)
    {
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _rates = rates ?? throw new ArgumentNullException(nameof(rates));
    }

    /// <inheritdoc />
    public Task<SpendDecision> ReserveAsync(string provider, string purpose, Money estimate, CancellationToken cancellationToken)
        => Task.FromResult(_ledger.TryReserve(provider, purpose, estimate, _limits, _rates()));

    /// <inheritdoc />
    public Task SettleAsync(Guid reservationId, Money actual)
    {
        _ledger.Settle(reservationId, actual);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ReleaseAsync(Guid reservationId)
    {
        _ledger.Release(reservationId);
        return Task.CompletedTask;
    }
}
