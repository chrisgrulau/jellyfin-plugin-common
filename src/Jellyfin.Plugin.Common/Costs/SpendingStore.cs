using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Common.Costs;

/// <summary>
/// What paid services cost and how much has been spent: the price table shipped with the plugin, the spend ledger and the
/// latest exchange rates, kept in the plugin's data folder. Each plugin keeps one shared instance, so its scheduled tasks,
/// entry points and settings page see the same spending. The plugin itself only decides its limits (its settings differ).
/// </summary>
internal sealed class SpendingStore : IDisposable
{
    /// <summary>The ledger's file name in the data folder.</summary>
    public const string LedgerFileName = "spend.json";

    /// <summary>The exchange rates' file name in the data folder.</summary>
    public const string RatesFileName = "rates.json";

    /// <summary>
    /// How long after a failed refresh another is tried while the rates are missing or stale. (While they are fresh,
    /// <see cref="ExchangeRateStore.RefreshAfter"/> applies.)
    /// </summary>
    public static readonly TimeSpan RetryStaleAfter = TimeSpan.FromMinutes(30);

    private readonly TimeProvider _clock;
    private readonly Lock _lock = new();
    private DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="SpendingStore"/> class.
    /// </summary>
    /// <param name="dataFolder">The plugin's data folder.</param>
    /// <param name="prices">The price table (see <see cref="ShippedPrices"/>), or <c>null</c> if it couldn't be read (paid calls then wait).</param>
    /// <param name="clock">Clock.</param>
    public SpendingStore(string dataFolder, PriceTable? prices, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataFolder);
        _clock = clock ?? TimeProvider.System;
        Ledger = new SpendLedger(Path.Combine(dataFolder, LedgerFileName), clock);
        Rates = new ExchangeRateStore(Path.Combine(dataFolder, RatesFileName), clock);
        Prices = prices;
    }

    /// <summary>Gets the currencies a settings page can offer (so pages don't copy the list by hand).</summary>
    public static IReadOnlyList<string> Currencies => CurrencyCode.Supported;

    /// <summary>Gets the spend ledger.</summary>
    public SpendLedger Ledger { get; }

    /// <summary>Gets the exchange rates.</summary>
    public ExchangeRateStore Rates { get; }

    /// <summary>Gets the prices, or <c>null</c> if the table couldn't be read (paid calls then wait).</summary>
    public PriceTable? Prices { get; }

    /// <summary>
    /// Reads the price table a plugin ships as an embedded resource.
    /// </summary>
    /// <param name="assembly">The plugin's assembly.</param>
    /// <param name="resourceName">The resource's full name (<c>Namespace.prices.json</c>).</param>
    /// <returns>The table, or <c>null</c> if it is missing or invalid.</returns>
    public static PriceTable? ShippedPrices(Assembly assembly, string resourceName)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return PriceTable.Parse(reader.ReadToEnd());
    }

    /// <summary>
    /// The exchange rates to check a paid call against, refreshed first when due: about once a day while they are
    /// fresh (see <see cref="ExchangeRateStore.RefreshAfter"/>), and at most every <see cref="RetryStaleAfter"/> while
    /// they are missing or stale, so an unreachable rates source isn't asked on every call. Call it before reserving;
    /// never throws for network or content problems.
    /// </summary>
    /// <param name="http">HTTP client.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The latest good rates, if any (they may be stale; the ledger then refuses calls that need them).</returns>
    public async Task<ExchangeRates?> CurrentRatesAsync(HttpClient http, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        var now = _clock.GetUtcNow();
        var current = Rates.Current;
        var fresh = current is not null && current.IsFresh(DateOnly.FromDateTime(now.UtcDateTime));
        lock (_lock)
        {
            if (!fresh && now - _lastRefresh < RetryStaleAfter)
            {
                return current;
            }

            _lastRefresh = now;
        }

        return await Rates.RefreshAsync(http, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// This month's spending in the user's currency, open reservations included, at the latest rates.
    /// </summary>
    /// <param name="limits">The limits (for the currency and extra percentage).</param>
    /// <returns>The spending.</returns>
    public MonthSpend ThisMonth(SpendLimits limits) => Ledger.ThisMonth(limits, Rates.Current);

    /// <inheritdoc />
    public void Dispose() => Rates.Dispose();
}
