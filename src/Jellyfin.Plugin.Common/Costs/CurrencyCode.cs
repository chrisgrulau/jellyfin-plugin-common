using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Common.Costs;

/// <summary>
/// ISO 4217 currency codes.
/// </summary>
internal static class CurrencyCode
{
    /// <summary>
    /// Currencies that can be chosen for display and budgets: the euro plus every currency in the European Central
    /// Bank's daily reference rates, so each one can be converted to and from the others.
    /// </summary>
    public static readonly IReadOnlyList<string> Supported =
    [
        "AUD", "BGN", "BRL", "CAD", "CHF", "CNY", "CZK", "DKK", "EUR", "GBP", "HKD", "HUF", "IDR", "ILS", "INR", "ISK",
        "JPY", "KRW", "MXN", "MYR", "NOK", "NZD", "PHP", "PLN", "RON", "SEK", "SGD", "THB", "TRY", "USD", "ZAR",
    ];

    /// <summary>
    /// Normalises a currency code.
    /// </summary>
    /// <param name="code">The code, any case, surrounding spaces allowed.</param>
    /// <returns>The upper-case code, or <c>null</c> if it isn't three ASCII letters.</returns>
    public static string? Normalise(string? code)
    {
        var c = (code ?? string.Empty).Trim().ToUpperInvariant();
        return c.Length == 3 && char.IsAsciiLetterUpper(c[0]) && char.IsAsciiLetterUpper(c[1]) && char.IsAsciiLetterUpper(c[2]) ? c : null;
    }

    /// <summary>
    /// Whether a code can be chosen (see <see cref="Supported"/>).
    /// </summary>
    /// <param name="code">The code.</param>
    /// <returns><c>true</c> if supported.</returns>
    public static bool IsSupported(string? code)
        => Normalise(code) is { } c && ((IList<string>)Supported).Contains(c);
}
