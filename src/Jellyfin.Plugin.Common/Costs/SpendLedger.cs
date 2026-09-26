using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Jellyfin.Plugin.Common.Storage;

namespace Jellyfin.Plugin.Common.Costs;

/// <summary>
/// The spending limits a paid call is checked against, in the user's currency, for the current calendar month.
/// </summary>
/// <param name="Currency">The user's currency.</param>
/// <param name="Overall">The limit for all paid services together, or <c>null</c> for no limit (an explicit choice). 0
/// means no paid use at all.</param>
/// <param name="PerProvider">Limits for single providers (a provider stops at whichever limit it reaches first).</param>
/// <param name="ExtraPercent">Percentage added to every charge (taxes, card fees).</param>
internal sealed record SpendLimits(string Currency, decimal? Overall, IReadOnlyDictionary<string, decimal> PerProvider, decimal ExtraPercent);

/// <summary>
/// The answer to a request to spend: allowed (with a reservation to settle or release), or refused with a reason to
/// show people.
/// </summary>
/// <param name="ReservationId">The reservation, when allowed.</param>
/// <param name="Refusal">Why it was refused.</param>
internal sealed record SpendDecision(Guid? ReservationId, string? Refusal)
{
    /// <summary>Gets a value indicating whether the call may go ahead.</summary>
    public bool Allowed => ReservationId is not null;
}

/// <summary>
/// Month-to-date spending in the user's currency.
/// </summary>
/// <param name="Currency">The user's currency.</param>
/// <param name="Total">All providers, or <c>null</c> if some of it can't be converted (no or stale exchange rates).</param>
/// <param name="PerProvider">Per provider (only those that can be converted).</param>
internal sealed record MonthSpend(string Currency, decimal? Total, IReadOnlyDictionary<string, decimal> PerProvider);

/// <summary>
/// Records what paid calls cost and keeps them within the spending limits (see "Spending limits" in the AI plugin's
/// design notes). Each call first reserves its estimated cost, checked against the month's spending so far and every
/// other open reservation, atomically; afterwards the actual cost is settled, or the reservation released if nothing
/// was charged. Amounts are kept in the currency they were charged in and only converted to check a limit or to show
/// them; a cost that can't be converted is unknown, and the call is refused rather than counted as free.
/// <para>
/// A reservation never settled (the server stopped mid-call) keeps counting at its estimate, which errs on the side of
/// spending less. The ledger is saved atomically after every change; entries older than about a year are dropped.
/// </para>
/// </summary>
internal sealed class SpendLedger
{
    /// <summary>How long entries are kept.</summary>
    public static readonly TimeSpan KeptFor = TimeSpan.FromDays(400);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly string _path;
    private readonly TimeProvider _clock;
    private readonly Lock _lock = new();
    private List<SpendEntry>? _entries;

    /// <summary>
    /// Initializes a new instance of the <see cref="SpendLedger"/> class.
    /// </summary>
    /// <param name="path">The ledger file (in the owning plugin's data folder).</param>
    /// <param name="clock">Clock; months are calendar months in the server's local time.</param>
    public SpendLedger(string path, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Reserves the estimated cost of a call, if the limits allow it.
    /// </summary>
    /// <param name="provider">Provider id.</param>
    /// <param name="purpose">What the call is for (<c>subtitles.sync</c> …), for the record.</param>
    /// <param name="estimate">The estimated cost, as the provider charges it.</param>
    /// <param name="limits">The limits.</param>
    /// <param name="rates">The latest exchange rates, if any.</param>
    /// <returns>The decision.</returns>
    public SpendDecision TryReserve(string provider, string purpose, Money estimate, SpendLimits limits, ExchangeRates? rates)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentNullException.ThrowIfNull(limits);
        if (estimate.Amount < 0)
        {
            return new SpendDecision(null, "The estimated cost is invalid.");
        }

        lock (_lock)
        {
            var now = _clock.GetLocalNow();
            var today = DateOnly.FromDateTime(now.DateTime);
            var month = MonthOf(now);
            if (limits.Overall is 0m)
            {
                return new SpendDecision(null, "The monthly spending limit is 0, so paid services aren't used.");
            }

            if (Convert(estimate, limits, rates, today) is not { } cost)
            {
                return new SpendDecision(null, "The cost can't be worked out in " + limits.Currency + " (no recent exchange rates), so paid services wait.");
            }

            var entries = Load().Where(e => MonthOf(e.Time) == month).ToList();
            if (entries.Any(e => e.Purpose == UnreadablePurpose))
            {
                return new SpendDecision(null, "The spending record can't be read right now (another program may have it open), so paid services wait; it's tried again next time.");
            }

            decimal spentAll = 0, spentProvider = 0;
            foreach (var e in entries)
            {
                if (Convert(e.Amount, limits, rates, today) is not { } c)
                {
                    return new SpendDecision(null, "Earlier spending this month can't be converted to " + limits.Currency + ", so paid services wait.");
                }

                spentAll += c;
                if (SameProvider(e.Provider, provider))
                {
                    spentProvider += c;
                }
            }

            if (limits.Overall is { } overall && spentAll + cost > overall)
            {
                return new SpendDecision(null, $"This would go over the monthly limit ({Show(spentAll, limits)} of {Show(overall, limits)} used).");
            }

            var own = limits.PerProvider.FirstOrDefault(p => SameProvider(p.Key, provider));
            if (own.Key is not null && spentProvider + cost > own.Value)
            {
                return new SpendDecision(null, $"This would go over {provider}'s monthly limit ({Show(spentProvider, limits)} of {Show(own.Value, limits)} used).");
            }

            var entry = new SpendEntry { Id = Guid.NewGuid(), Provider = provider, Purpose = purpose ?? string.Empty, Amount = estimate, Time = now, Settled = false };
            _entries!.Add(entry);
            Save();
            return new SpendDecision(entry.Id, null);
        }
    }

    /// <summary>
    /// Records what a reserved call actually cost.
    /// </summary>
    /// <param name="reservationId">The reservation.</param>
    /// <param name="actual">The actual cost, as charged.</param>
    public void Settle(Guid reservationId, Money actual)
    {
        lock (_lock)
        {
            var list = Load();
            var i = list.FindIndex(e => e.Id == reservationId);
            if (i >= 0 && actual.Amount >= 0)
            {
                list[i] = list[i] with { Amount = actual, Settled = true };
                Save();
            }
        }
    }

    /// <summary>
    /// Drops a reservation for a call that wasn't charged (it failed before the provider did any work).
    /// </summary>
    /// <param name="reservationId">The reservation.</param>
    public void Release(Guid reservationId)
    {
        lock (_lock)
        {
            if (Load().RemoveAll(e => e.Id == reservationId && !e.Settled) > 0)
            {
                Save();
            }
        }
    }

    /// <summary>
    /// This month's spending in the user's currency, open reservations included.
    /// </summary>
    /// <param name="limits">The limits (for the currency and extra percentage).</param>
    /// <param name="rates">The latest exchange rates, if any.</param>
    /// <returns>The spending.</returns>
    public MonthSpend ThisMonth(SpendLimits limits, ExchangeRates? rates)
    {
        ArgumentNullException.ThrowIfNull(limits);
        lock (_lock)
        {
            var now = _clock.GetLocalNow();
            var today = DateOnly.FromDateTime(now.DateTime);
            var month = MonthOf(now);
            var per = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            decimal total = 0;
            var known = true;
            foreach (var e in Load().Where(e => MonthOf(e.Time) == month))
            {
                if (Convert(e.Amount, limits, rates, today) is { } c)
                {
                    total += c;
                    per[e.Provider] = per.GetValueOrDefault(e.Provider) + c;
                }
                else
                {
                    known = false;
                }
            }

            return new MonthSpend(limits.Currency, known ? total : null, per);
        }
    }

    /// <summary>
    /// What a provider has cost since a moment (open reservations included), in one currency: for counting down a
    /// prepaid credit bought on a date. No extra percentage is added (a prepaid credit is spent as charged).
    /// </summary>
    /// <param name="provider">Provider id.</param>
    /// <param name="since">From when.</param>
    /// <param name="currency">The currency to add up in (the credit's).</param>
    /// <param name="rates">The latest exchange rates, if any (only needed for charges in another currency).</param>
    /// <returns>The total, or <c>null</c> if some of it can't be converted.</returns>
    public Money? SpentSince(string provider, DateTimeOffset since, string currency, ExchangeRates? rates)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        var to = CurrencyCode.Normalise(currency) ?? throw new ArgumentException("Not an ISO 4217 currency code.", nameof(currency));
        lock (_lock)
        {
            var today = DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);
            decimal total = 0;
            foreach (var e in Load().Where(e => e.Time >= since && SameProvider(e.Provider, provider)))
            {
                if (CostConverter.ToUserCurrency(e.Amount, to, rates, today, 0m) is not { } c)
                {
                    return null;
                }

                total += c.Amount;
            }

            return new Money(total, to);
        }
    }

    private static int MonthOf(DateTimeOffset t) => (t.Year * 12) + t.Month;

    private static bool SameProvider(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static decimal? Convert(Money m, SpendLimits limits, ExchangeRates? rates, DateOnly today)
        => CostConverter.ToUserCurrency(m, limits.Currency, rates, today, limits.ExtraPercent)?.Amount;

    private static string Show(decimal amount, SpendLimits limits) => new Money(decimal.Round(amount, 2), limits.Currency).ToString();

    private const string UnreadablePurpose = "unreadable";

    private List<SpendEntry> Load()
    {
        if (_entries is not null)
        {
            return _entries;
        }

        var read = JsonFile.Read<List<SpendEntry>>(_path, JsonOptions);
        switch (read.State)
        {
            case JsonFileState.Unreadable:
                // Unreadable for now (locked by a backup or antivirus, a share hiccup): refuse paid calls this time, leave the
                // file where it is, and read it again next time. Nothing is cached, so nothing can be saved over it.
                _entries = null;
                return [Unreadable(_clock.GetLocalNow())];
            case JsonFileState.Damaged:
                // Damaged: keep the file for inspection and refuse to guess; a damaged ledger blocks paid calls this month
                JsonFile.SetAside(_path, _clock);
                _entries = [Blocker(_clock.GetLocalNow())];
                Save();
                return _entries;
            default:
                _entries = read.Value;
                break;
        }

        var cutoff = _clock.GetLocalNow() - KeptFor;
        _entries = [.. (_entries ?? []).Where(e => e is not null && e.Provider is not null && (CurrencyCode.IsSupported(e.Amount.Currency) || e.Amount.Currency == "XXX") && e.Time >= cutoff)];
        return _entries;
    }

    // The ledger can't be read at the moment: an entry that refuses this call, never saved
    private static SpendEntry Unreadable(DateTimeOffset now)
        => new() { Id = Guid.NewGuid(), Provider = "(ledger can't be read)", Purpose = UnreadablePurpose, Amount = new Money(decimal.MaxValue / 2, "XXX"), Time = now, Settled = true };

    // A damaged ledger means this month's spending is unknown; an unconvertible marker makes every check refuse until
    // the month ends, rather than letting spending start again from zero
    private static SpendEntry Blocker(DateTimeOffset now)
        => new() { Id = Guid.NewGuid(), Provider = "(unreadable ledger)", Purpose = "blocker", Amount = new Money(decimal.MaxValue / 2, "XXX"), Time = now, Settled = true };

    private void Save()
    {
        if (_entries is null)
        {
            return;
        }

        try
        {
            JsonFile.WriteAtomic(_path, _entries, JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Kept in memory; the next change tries again
        }
    }

    private sealed record SpendEntry
    {
        [JsonPropertyName("id")]
        public Guid Id { get; init; }

        [JsonPropertyName("provider")]
        public string Provider { get; init; } = string.Empty;

        [JsonPropertyName("purpose")]
        public string Purpose { get; init; } = string.Empty;

        [JsonPropertyName("amount")]
        public Money Amount { get; init; }

        [JsonPropertyName("time")]
        public DateTimeOffset Time { get; init; }

        [JsonPropertyName("settled")]
        public bool Settled { get; init; }
    }
}
