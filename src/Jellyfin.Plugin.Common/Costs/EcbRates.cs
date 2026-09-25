using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;

namespace Jellyfin.Plugin.Common.Costs;

/// <summary>
/// Reads the European Central Bank's daily euro reference rates: free, no key, about 30 currencies including AUD and
/// USD. The file is remote input, so it is size-limited, parsed without DTDs, and every value is checked; anything odd
/// means no rates, never a guess.
/// </summary>
internal static class EcbRates
{
    /// <summary>Where the daily rates are published (HTTPS, one fixed origin).</summary>
    public static readonly Uri DailyUrl = new("https://www.ecb.europa.eu/stats/eurofxref/eurofxref-daily.xml");

    /// <summary>The largest file accepted (the real one is about 2 KB).</summary>
    public const int MaxBytes = 64 * 1024;

    /// <summary>The source name shown to users.</summary>
    public const string SourceName = "European Central Bank";

    /// <summary>
    /// Parses the daily rates file.
    /// </summary>
    /// <param name="xml">The file content.</param>
    /// <returns>The rates, or <c>null</c> if the file is too big, malformed, or has an implausible value.</returns>
    public static ExchangeRates? Parse(string xml)
    {
        if (string.IsNullOrEmpty(xml) || xml.Length > MaxBytes)
        {
            return null;
        }

        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxBytes, IgnoreComments = true };
        DateOnly? date = null;
        var rates = new Dictionary<string, decimal>(StringComparer.Ordinal) { ["EUR"] = 1m };
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Cube")
                {
                    continue;
                }

                if (reader.GetAttribute("time") is { } time)
                {
                    // One day per file: a second date means this isn't the daily file
                    if (date is not null || !DateOnly.TryParseExact(time, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                    {
                        return null;
                    }

                    date = d;
                }

                var currency = reader.GetAttribute("currency");
                var rate = reader.GetAttribute("rate");
                if (currency is null && rate is null)
                {
                    continue;
                }

                if (CurrencyCode.Normalise(currency) is not { } code || !string.Equals(code, currency, StringComparison.Ordinal)
                    || !decimal.TryParse(rate, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
                    || value <= 0 || value > 1_000_000m || rates.ContainsKey(code))
                {
                    return null;
                }

                rates[code] = value;
            }
        }
        catch (XmlException)
        {
            return null;
        }

        // The file always has US dollars; without them (or a date) it isn't the file we expect
        return date is { } day && rates.ContainsKey("USD") ? new ExchangeRates(day, SourceName, rates) : null;
    }
}
