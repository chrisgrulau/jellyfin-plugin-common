using System;

namespace Jellyfin.Plugin.Common.Costs;

/// <summary>
/// Turns a provider's charge into the user's currency, for display and budget checks.
/// </summary>
internal static class CostConverter
{
    /// <summary>The largest extra percentage accepted (see <see cref="ToUserCurrency"/>).</summary>
    public const decimal MaxExtraPercent = 100m;

    /// <summary>
    /// Converts a charge to the user's currency and adds their extra charges (taxes such as GST on overseas services,
    /// or a card's foreign-transaction fee).
    /// </summary>
    /// <param name="charge">What the provider charges, in its own currency.</param>
    /// <param name="userCurrency">The user's currency.</param>
    /// <param name="rates">The latest exchange rates, if any.</param>
    /// <param name="today">Today's date (rates older than <see cref="ExchangeRates.MaxAgeDays"/> aren't used).</param>
    /// <param name="extraPercent">Percentage added on top, 0 to <see cref="MaxExtraPercent"/>.</param>
    /// <returns>The cost in the user's currency, or <c>null</c> when it can't be worked out (no or stale rates, unknown
    /// currency). Callers must then treat the cost as unknown and not make paid calls: never assume it is zero.</returns>
    public static Money? ToUserCurrency(Money charge, string userCurrency, ExchangeRates? rates, DateOnly today, decimal extraPercent)
    {
        if (CurrencyCode.Normalise(userCurrency) is not { } to || CurrencyCode.Normalise(charge.Currency) is null)
        {
            return null;
        }

        Money converted;
        if (string.Equals(charge.Currency, to, StringComparison.Ordinal))
        {
            converted = charge;
        }
        else if (rates is null || !rates.IsFresh(today) || !rates.TryConvert(charge, to, out converted))
        {
            return null;
        }

        var extra = Math.Clamp(extraPercent, 0m, MaxExtraPercent);
        return converted with { Amount = converted.Amount * (1m + (extra / 100m)) };
    }
}
