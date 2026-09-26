using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace Jellyfin.Plugin.Common.Costs;

/// <summary>
/// What a provider charges for one unit of use, in the currency it charges in.
/// </summary>
/// <param name="Provider">Provider id (<c>deepgram</c>, <c>openai</c> …).</param>
/// <param name="Model">Model name, or <c>*</c> for the provider's price when the model isn't listed.</param>
/// <param name="Unit">What is counted: <see cref="PriceTable.AudioMinute"/>, <see cref="PriceTable.InputMillionTokens"/>
/// or <see cref="PriceTable.OutputMillionTokens"/>.</param>
/// <param name="Price">The price of one unit.</param>
internal sealed record PriceEntry(string Provider, string Model, string Unit, Money Price);

/// <summary>
/// Published prices, shipped with each plugin as a small JSON file and pinned by its version: the fallback when a
/// provider's reply doesn't say what a call cost. A price that isn't listed is unknown, and a paid call whose cost is
/// unknown isn't made: it never counts as free.
/// </summary>
internal sealed class PriceTable
{
    /// <summary>A minute of audio.</summary>
    public const string AudioMinute = "audio-minute";

    /// <summary>A million input tokens.</summary>
    public const string InputMillionTokens = "input-mtok";

    /// <summary>A million output tokens.</summary>
    public const string OutputMillionTokens = "output-mtok";

    /// <summary>The largest price accepted for one unit (a guard against a typo turning into a huge estimate).</summary>
    public const decimal MaxUnitPrice = 1000m;

    private static readonly string[] Units = [AudioMinute, InputMillionTokens, OutputMillionTokens];

    private PriceTable(string version, IReadOnlyList<PriceEntry> entries)
    {
        Version = version;
        Entries = entries;
    }

    /// <summary>Gets the table's version (its date, <c>yyyy-MM-dd</c>).</summary>
    public string Version { get; }

    /// <summary>Gets the prices.</summary>
    public IReadOnlyList<PriceEntry> Entries { get; }

    /// <summary>
    /// Reads a price table. Every entry is checked; a table with anything wrong in it is refused as a whole, so a bad
    /// file never yields a partly right table.
    /// </summary>
    /// <param name="json">The JSON: <c>{"version":"2026-09-26","prices":[{"provider":"deepgram","model":"nova-3",
    /// "unit":"audio-minute","amount":"0.0043","currency":"USD"}, …]}</c>. Amounts are strings, so they stay exact.</param>
    /// <returns>The table, or <c>null</c> if it isn't valid.</returns>
    public static PriceTable? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = doc.RootElement;
            if (!root.TryGetProperty("version", out var v) || v.GetString() is not { } version
                || !DateOnly.TryParseExact(version, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                || !root.TryGetProperty("prices", out var prices) || prices.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var entries = new List<PriceEntry>();
            foreach (var p in prices.EnumerateArray())
            {
                var provider = Text(p, "provider");
                var model = Text(p, "model");
                var unit = Text(p, "unit");
                var amountText = Text(p, "amount");
                if (provider is null || model is null || unit is null || amountText is null || !Units.Contains(unit, StringComparer.Ordinal)
                    || !decimal.TryParse(amountText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount)
                    || amount <= 0 || amount > MaxUnitPrice
                    || CurrencyCode.Normalise(Text(p, "currency")) is not { } currency)
                {
                    return null;
                }

                entries.Add(new PriceEntry(provider.Trim(), model.Trim(), unit, new Money(amount, currency)));
            }

            var keys = entries.Select(e => (Norm(e.Provider), Norm(e.Model), e.Unit)).ToList();
            return entries.Count == 0 || keys.Distinct().Count() != keys.Count ? null : new PriceTable(version, entries);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The price of a unit for a provider and model: the model's own price, else the provider's <c>*</c> price.
    /// </summary>
    /// <param name="provider">Provider id.</param>
    /// <param name="model">Model name (empty for the provider's default).</param>
    /// <param name="unit">The unit.</param>
    /// <returns>The price, or <c>null</c> if unknown.</returns>
    public Money? PriceOf(string provider, string? model, string unit)
    {
        var p = Norm(provider);
        var m = Norm(string.IsNullOrWhiteSpace(model) ? "*" : model);
        return (Entries.FirstOrDefault(e => Norm(e.Provider) == p && Norm(e.Model) == m && e.Unit == unit)
            ?? Entries.FirstOrDefault(e => Norm(e.Provider) == p && e.Model == "*" && e.Unit == unit))?.Price;
    }

    /// <summary>
    /// The cost of an amount of audio.
    /// </summary>
    /// <param name="provider">Provider id.</param>
    /// <param name="model">Model name.</param>
    /// <param name="seconds">Audio length in seconds.</param>
    /// <returns>The cost, or <c>null</c> if the price is unknown.</returns>
    public Money? AudioCost(string provider, string? model, double seconds)
        => PriceOf(provider, model, AudioMinute) is { } perMinute && double.IsFinite(seconds) && seconds >= 0
            ? perMinute with { Amount = perMinute.Amount * (decimal)seconds / 60m }
            : null;

    private static string Norm(string s) => s.Trim().ToUpperInvariant();

    private static string? Text(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 and <= 100 } s ? s : null;
}
