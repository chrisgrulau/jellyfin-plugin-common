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

    /// <summary>
    /// A currency setting as a supported code, or the fallback: "the currency setting, or USD" in one place, so an
    /// unsupported code never gets through.
    /// </summary>
    /// <param name="code">The setting, any case, surrounding spaces allowed.</param>
    /// <param name="fallback">What to use when <paramref name="code"/> isn't supported; must itself be supported.</param>
    /// <returns>The upper-case supported code.</returns>
    /// <exception cref="ArgumentException"><paramref name="fallback"/> isn't a supported code.</exception>
    public static string NormaliseOr(string? code, string fallback)
    {
        if (!IsSupported(fallback))
        {
            throw new ArgumentException("The fallback currency must be a supported code.", nameof(fallback));
        }

        return IsSupported(code) ? Normalise(code)! : Normalise(fallback)!;
    }
}
