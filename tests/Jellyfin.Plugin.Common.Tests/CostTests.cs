using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Common.Costs;
using Xunit;

namespace Jellyfin.Plugin.Common.Tests;

public class CostTests
{
    // The shape of the ECB's daily file, with made-up rates
    private const string Daily = """
        <?xml version="1.0" encoding="UTF-8"?>
        <gesmes:Envelope xmlns:gesmes="http://www.gesmes.org/xml/2002-08-01" xmlns="http://www.ecb.int/vocabulary/2002-08-01/eurofxref">
          <gesmes:subject>Reference rates</gesmes:subject>
          <gesmes:Sender><gesmes:name>European Central Bank</gesmes:name></gesmes:Sender>
          <Cube>
            <Cube time='2026-09-24'>
              <Cube currency='USD' rate='1.2000'/>
              <Cube currency='JPY' rate='160.00'/>
              <Cube currency='AUD' rate='1.8000'/>
            </Cube>
          </Cube>
        </gesmes:Envelope>
        """;

    private static readonly DateOnly Today = new(2026, 9, 25);

    [Fact]
    public void The_daily_file_is_read()
    {
        var rates = EcbRates.Parse(Daily)!;

        Assert.Equal(new DateOnly(2026, 9, 24), rates.Date);
        Assert.Equal(1.8m, rates.PerEuro["AUD"]);
        Assert.Equal(1m, rates.PerEuro["EUR"]);
        Assert.Equal(EcbRates.SourceName, rates.Source);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not xml")]
    [InlineData("<Cube><Cube time='2026-09-24'><Cube currency='AUD' rate='1.8'/></Cube></Cube>")]
    [InlineData("<Cube><Cube time='2026-09-24'><Cube currency='USD' rate='-1'/></Cube></Cube>")]
    [InlineData("<Cube><Cube time='2026-09-24'><Cube currency='USD' rate='1e3'/></Cube></Cube>")]
    [InlineData("<Cube><Cube time='2026-09-24'><Cube currency='usd' rate='1.2'/></Cube></Cube>")]
    [InlineData("<Cube><Cube time='2026-09-24'><Cube currency='USD' rate='1.2'/><Cube currency='USD' rate='1.3'/></Cube></Cube>")]
    [InlineData("<Cube><Cube time='2026-09-24'><Cube currency='USD' rate='1.2'/></Cube><Cube time='2026-09-25'/></Cube>")]
    [InlineData("<Cube><Cube time='yesterday'><Cube currency='USD' rate='1.2'/></Cube></Cube>")]
    [InlineData("<Cube><Cube currency='USD' rate='1.2'/></Cube>")]
    [InlineData("<!DOCTYPE x [<!ENTITY e 'y'>]><Cube><Cube time='2026-09-24'><Cube currency='USD' rate='1.2'/></Cube></Cube>")]
    public void Anything_odd_means_no_rates(string xml) => Assert.Null(EcbRates.Parse(xml));

    [Fact]
    public void An_oversized_file_is_refused()
        => Assert.Null(EcbRates.Parse(Daily + new string(' ', EcbRates.MaxBytes)));

    [Fact]
    public void Us_dollars_convert_to_australian_dollars_through_the_euro()
    {
        var rates = EcbRates.Parse(Daily)!;

        Assert.True(rates.TryConvert(Money.Of(1.20m, "USD"), "aud", out var aud));
        Assert.Equal(Money.Of(1.80m, "AUD"), aud);
        Assert.False(rates.TryConvert(Money.Of(1m, "USD"), "GBP", out _));
    }

    [Fact]
    public void A_charge_in_the_users_own_currency_needs_no_rates()
        => Assert.Equal(Money.Of(5.5m, "AUD"), CostConverter.ToUserCurrency(Money.Of(5m, "AUD"), "AUD", null, Today, 10m));

    [Fact]
    public void Extra_charges_are_added_after_conversion()
    {
        var cost = CostConverter.ToUserCurrency(Money.Of(1.20m, "USD"), "AUD", EcbRates.Parse(Daily), Today, 10m);

        Assert.Equal(Money.Of(1.98m, "AUD"), cost);
    }

    [Fact]
    public void Stale_or_missing_rates_mean_an_unknown_cost_never_zero()
    {
        var rates = EcbRates.Parse(Daily)!;

        Assert.Null(CostConverter.ToUserCurrency(Money.Of(1m, "USD"), "AUD", null, Today, 0));
        Assert.Null(CostConverter.ToUserCurrency(Money.Of(1m, "USD"), "AUD", rates, Today.AddDays(ExchangeRates.MaxAgeDays + 2), 0));
        Assert.NotNull(CostConverter.ToUserCurrency(Money.Of(1m, "USD"), "AUD", rates, Today.AddDays(ExchangeRates.MaxAgeDays - 2), 0));
        Assert.Null(CostConverter.ToUserCurrency(Money.Of(1m, "USD"), "dollars", rates, Today, 0));
    }

    [Fact]
    public void Extra_percentage_is_kept_within_bounds()
    {
        Assert.Equal(Money.Of(1m, "AUD"), CostConverter.ToUserCurrency(Money.Of(1m, "AUD"), "AUD", null, Today, -5m));
        Assert.Equal(Money.Of(2m, "AUD"), CostConverter.ToUserCurrency(Money.Of(1m, "AUD"), "AUD", null, Today, 500m));
    }

    [Theory]
    [InlineData(" aud ", "AUD")]
    [InlineData("USD", "USD")]
    [InlineData("A$", null)]
    [InlineData("dollars", null)]
    [InlineData(null, null)]
    public void Currency_codes_are_normalised(string? input, string? expected) => Assert.Equal(expected, CurrencyCode.Normalise(input));

    [Fact]
    public void Australian_and_us_dollars_can_be_chosen()
    {
        Assert.True(CurrencyCode.IsSupported("aud"));
        Assert.True(CurrencyCode.IsSupported("USD"));
        Assert.False(CurrencyCode.IsSupported("XYZ"));
        Assert.Throws<ArgumentException>(() => Money.Of(1m, "A$"));
    }

    [Fact]
    public void Money_shows_its_currency_and_small_amounts_keep_their_digits()
    {
        Assert.Equal("AUD 7.50", Money.Of(7.5m, "AUD").ToString());
        Assert.Equal("USD 0.0043", Money.Of(0.0043m, "USD").ToString());
    }
}
