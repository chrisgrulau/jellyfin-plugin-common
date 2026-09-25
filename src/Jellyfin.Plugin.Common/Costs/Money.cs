using System;
using System.Globalization;

namespace Jellyfin.Plugin.Common.Costs;

/// <summary>
/// An amount in a currency. Money is always <see cref="decimal"/>, and every cost keeps the currency it was charged in
/// (most AI and speech-to-text providers charge in US dollars, some bill in the account's local currency); conversion
/// only happens for display and for checking a budget (see <see cref="CostConverter"/>).
/// </summary>
/// <param name="Amount">The amount.</param>
/// <param name="Currency">The ISO 4217 currency code, upper case (<c>USD</c>, <c>AUD</c> …).</param>
internal readonly record struct Money(decimal Amount, string Currency)
{
    /// <summary>
    /// Creates an amount, checking the currency code.
    /// </summary>
    /// <param name="amount">The amount.</param>
    /// <param name="currency">An ISO 4217 code, any case.</param>
    /// <returns>The money.</returns>
    /// <exception cref="ArgumentException">The code isn't three letters.</exception>
    public static Money Of(decimal amount, string currency)
        => CurrencyCode.Normalise(currency) is { } code
            ? new Money(amount, code)
            : throw new ArgumentException("Not an ISO 4217 currency code: " + currency, nameof(currency));

    /// <summary>
    /// Formats for people, with the code so there's never doubt which dollar is meant: <c>AUD 7.50</c>.
    /// Amounts under a cent keep more places, so tiny per-call costs don't show as zero.
    /// </summary>
    /// <returns>The text.</returns>
    public override string ToString()
        => Currency + " " + Amount.ToString(Math.Abs(Amount) is > 0 and < 0.01m ? "0.00####" : "0.00", CultureInfo.InvariantCulture);
}
