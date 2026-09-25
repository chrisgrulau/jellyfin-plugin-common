using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Common.Costs;

/// <summary>
/// Exchange rates on one day, as units of each currency per euro (the European Central Bank's convention).
/// </summary>
/// <param name="Date">The day the rates are for.</param>
/// <param name="Source">Where they came from, for display (e.g. "European Central Bank").</param>
/// <param name="PerEuro">Units of each currency per euro; the euro itself is 1.</param>
internal sealed record ExchangeRates(DateOnly Date, string Source, IReadOnlyDictionary<string, decimal> PerEuro)
{
    /// <summary>
    /// How old rates may be and still be used. The ECB publishes on working days only, so a few days' gap is normal;
    /// older rates count as unknown.
    /// </summary>
    public const int MaxAgeDays = 7;

    /// <summary>
    /// Whether the rates are recent enough to use.
    /// </summary>
    /// <param name="today">Today's date.</param>
    /// <returns><c>true</c> if at most <see cref="MaxAgeDays"/> old (and not from the future).</returns>
    public bool IsFresh(DateOnly today) => Date <= today.AddDays(1) && today.DayNumber - Date.DayNumber <= MaxAgeDays;

    /// <summary>
    /// Converts an amount to another currency.
    /// </summary>
    /// <param name="money">The amount.</param>
    /// <param name="currency">The target currency code.</param>
    /// <param name="result">The converted amount.</param>
    /// <returns><c>false</c> if either currency isn't in the table.</returns>
    public bool TryConvert(Money money, string currency, out Money result)
    {
        result = default;
        if (CurrencyCode.Normalise(currency) is not { } to)
        {
            return false;
        }

        if (string.Equals(money.Currency, to, StringComparison.Ordinal))
        {
            result = money;
            return true;
        }

        if (!PerEuro.TryGetValue(money.Currency, out var from) || !PerEuro.TryGetValue(to, out var target) || from <= 0 || target <= 0)
        {
            return false;
        }

        result = new Money(money.Amount / from * target, to);
        return true;
    }
}
