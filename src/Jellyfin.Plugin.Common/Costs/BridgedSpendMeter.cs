using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Common.Costs;

/// <summary>
/// Meters paid calls against the budget the AI plugin owns when it is installed and allows the caller, and against the
/// caller's own ledger and settings otherwise (see <see cref="SpendingBridgeClient.MeansOwnBudget"/>). Each reservation
/// remembers where it was made, so it is settled or released in the same ledger: a call is counted exactly once.
/// <para>
/// When the AI plugin has the budget but can't be asked right now (a transient failure), the call is refused rather than
/// metered on the caller's own ledger, which the AI plugin's limits wouldn't see.
/// </para>
/// </summary>
internal sealed class BridgedSpendMeter : ISpendMeter
{
    private readonly SpendingBridgeClient _bridge;
    private readonly Func<ISpendMeter> _ownBudget;
    private readonly Func<string, string> _bridgeProvider;
    private readonly SpendCarry? _carry;
    private readonly ConcurrentDictionary<Guid, bool> _viaBridge = new();
    private ISpendMeter? _own;

    /// <summary>
    /// Initializes a new instance of the <see cref="BridgedSpendMeter"/> class.
    /// </summary>
    /// <param name="bridge">The AI plugin's spending entry point.</param>
    /// <param name="ownBudget">The caller's own ledger and limits, made when first needed.</param>
    /// <param name="bridgeProvider">The AI plugin's name for one of the caller's providers (see
    /// <see cref="SpendingBridgeClient.OpenAiSpeech"/>), or <c>null</c> to use the caller's own.</param>
    /// <param name="carry">The caller's own spending this month, reported before its first reservation through the AI
    /// plugin (and again whenever it changes), so the AI plugin's month includes it; or <c>null</c>.</param>
    public BridgedSpendMeter(SpendingBridgeClient bridge, Func<ISpendMeter> ownBudget, Func<string, string>? bridgeProvider = null, SpendCarry? carry = null)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _ownBudget = ownBudget ?? throw new ArgumentNullException(nameof(ownBudget));
        _bridgeProvider = bridgeProvider ?? (static p => p);
        _carry = carry;
    }

    /// <inheritdoc />
    public async Task<SpendDecision> ReserveAsync(string provider, string purpose, Money estimate, CancellationToken cancellationToken)
    {
        if (_carry is not null)
        {
            await _carry.SendAsync(_bridge, cancellationToken).ConfigureAwait(false);
        }

        var reply = await _bridge.ReserveAsync(purpose, _bridgeProvider(provider), estimate, cancellationToken).ConfigureAwait(false);
        if (reply is { Ok: true, ReservationId: { } id })
        {
            _viaBridge[id] = true;
            return new SpendDecision(id, null);
        }

        if (SpendingBridgeClient.MeansOwnBudget(reply.Failure))
        {
            var own = _own ??= _ownBudget();
            var decision = await own.ReserveAsync(provider, purpose, estimate, cancellationToken).ConfigureAwait(false);
            if (decision.ReservationId is { } ownId)
            {
                _viaBridge[ownId] = false;
            }

            return decision;
        }

        return new SpendDecision(null, reply.Failure == "provider-limit"
            ? (reply.Error ?? "This would go over a spending limit set in Shoal AI.") + " (Spending limits are set in Shoal AI.)"
            : "The spending limits in Shoal AI couldn't be checked, so paid services wait: " + (reply.Error ?? "no answer."));
    }

    /// <inheritdoc />
    public async Task SettleAsync(Guid reservationId, Money actual)
    {
        if (!_viaBridge.TryRemove(reservationId, out var viaBridge))
        {
            return;
        }

        // Settled even if the call was cancelled; a settle that can't be delivered leaves the estimate counting until the
        // reservation expires at it
        if (viaBridge)
        {
            await _bridge.SettleAsync(reservationId, actual, CancellationToken.None).ConfigureAwait(false);
        }
        else if (_own is { } own)
        {
            await own.SettleAsync(reservationId, actual).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(Guid reservationId)
    {
        if (!_viaBridge.TryRemove(reservationId, out var viaBridge))
        {
            return;
        }

        if (viaBridge)
        {
            await _bridge.ReleaseAsync(reservationId, CancellationToken.None).ConfigureAwait(false);
        }
        else if (_own is { } own)
        {
            await own.ReleaseAsync(reservationId).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Reports a plugin's own spending this month to the AI plugin once it keeps the budget, so the month's limit counts what
/// was spent before (see <see cref="SpendingBridgeClient.CarryAsync"/>). Each provider and currency is sent as a total that
/// replaces the one sent before, and only when it changed, so nothing is counted twice however often it runs, and a
/// restart simply sends it again. The plugin's own ledger keeps its entries, for when it has to fall back to it.
/// Keep one instance per plugin.
/// </summary>
internal sealed class SpendCarry
{
    private readonly Func<IReadOnlyList<(string Provider, Money Amount)>> _ownMonth;
    private readonly Func<string, string> _bridgeProvider;
    private readonly TimeProvider _clock;
    private readonly Lock _lock = new();
    private readonly Dictionary<(string Provider, string Currency), decimal> _sent = [];
    private string? _month;

    /// <summary>
    /// Initializes a new instance of the <see cref="SpendCarry"/> class.
    /// </summary>
    /// <param name="ownMonth">The plugin's own spending this month (see <see cref="SpendLedger.ThisMonthAsCharged"/>).</param>
    /// <param name="bridgeProvider">The AI plugin's name for one of the plugin's providers, or <c>null</c> for the same.</param>
    /// <param name="clock">Clock (months in the server's local time).</param>
    public SpendCarry(Func<IReadOnlyList<(string Provider, Money Amount)>> ownMonth, Func<string, string>? bridgeProvider = null, TimeProvider? clock = null)
    {
        _ownMonth = ownMonth ?? throw new ArgumentNullException(nameof(ownMonth));
        _bridgeProvider = bridgeProvider ?? (static p => p);
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Sends what changed since the last time. Never throws; what couldn't be sent is tried again next time.
    /// </summary>
    /// <param name="bridge">The AI plugin's spending entry point.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task SendAsync(SpendingBridgeClient bridge, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        var month = SpendingBridgeClient.MonthOf(_clock.GetLocalNow());
        List<((string Provider, string Currency) Key, decimal Amount)> changes;
        lock (_lock)
        {
            if (_month != month)
            {
                _sent.Clear();
                _month = month;
            }

            var now = _ownMonth()
                .GroupBy(p => (Provider: _bridgeProvider(p.Provider), p.Amount.Currency))
                .ToDictionary(g => g.Key, g => g.Sum(p => p.Amount.Amount));
            changes = [.. now.Where(p => !_sent.TryGetValue(p.Key, out var sent) || sent != p.Value).Select(p => (p.Key, p.Value))];

            // A total that has gone (a reservation released since) is sent as 0, which removes it
            changes.AddRange(_sent.Keys.Where(k => !now.ContainsKey(k)).Select(k => (k, 0m)));
        }

        foreach (var (key, amount) in changes)
        {
            var reply = await bridge.CarryAsync(key.Provider, new Money(amount, key.Currency), month, cancellationToken).ConfigureAwait(false);
            // A total the AI plugin won't take (a provider it doesn't know) won't be taken later either
            if (reply.Ok || reply.Failure == "bad-request")
            {
                lock (_lock)
                {
                    if (_month == month)
                    {
                        _sent[key] = amount;
                    }
                }
            }
            else
            {
                // Not installed, not allowed or not answering: nothing more to send now
                return;
            }
        }
    }
}
