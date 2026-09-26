using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Common.Costs;
using Xunit;

namespace Jellyfin.Plugin.Common.Tests;

public sealed class SpendTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "common-spend-" + Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new(new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.FromHours(10)));

    // 1 EUR = 1.10 USD = 1.65 AUD, so 1 USD = 1.5 AUD
    private static readonly ExchangeRates Rates = new(new DateOnly(2026, 9, 25), "test", new Dictionary<string, decimal> { ["USD"] = 1.10m, ["AUD"] = 1.65m });

    private static SpendLimits Aud(decimal? overall, decimal deepgram = -1)
        => new("AUD", overall, deepgram < 0 ? new Dictionary<string, decimal>() : new Dictionary<string, decimal> { ["deepgram"] = deepgram }, 0m);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private SpendLedger Ledger() => new(Path.Combine(_dir, "spend.json"), _clock);

    [Fact]
    public void Calls_are_allowed_until_the_monthly_limit_in_the_users_currency()
    {
        var ledger = Ledger();
        var usd1 = Money.Of(1m, "USD"); // AUD 1.50

        Assert.True(ledger.TryReserve("deepgram", "subtitles.sync", usd1, Aud(5m), Rates).Allowed);
        Assert.True(ledger.TryReserve("deepgram", "subtitles.sync", usd1, Aud(5m), Rates).Allowed);
        Assert.True(ledger.TryReserve("openai", "subtitles.sync", usd1, Aud(5m), Rates).Allowed);
        var refused = ledger.TryReserve("openai", "subtitles.sync", usd1, Aud(5m), Rates);

        Assert.False(refused.Allowed);
        Assert.Contains("monthly limit", refused.Refusal, StringComparison.Ordinal);
        Assert.Equal(4.5m, ledger.ThisMonth(Aud(5m), Rates).Total);
    }

    [Fact]
    public void A_provider_stops_at_its_own_limit_and_zero_means_no_paid_use()
    {
        var ledger = Ledger();
        Assert.True(ledger.TryReserve("deepgram", "p", Money.Of(1m, "USD"), Aud(null, 2m), Rates).Allowed);
        Assert.False(ledger.TryReserve("deepgram", "p", Money.Of(1m, "USD"), Aud(null, 2m), Rates).Allowed);
        Assert.True(ledger.TryReserve("openai", "p", Money.Of(1m, "USD"), Aud(null, 2m), Rates).Allowed);
        Assert.Contains("is 0", ledger.TryReserve("openai", "p", Money.Of(0.01m, "USD"), Aud(0m), Rates).Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_or_stale_rates_refuse_instead_of_counting_as_free()
    {
        var ledger = Ledger();
        Assert.False(ledger.TryReserve("deepgram", "p", Money.Of(0.01m, "USD"), Aud(5m), null).Allowed);
        var stale = Rates with { Date = new DateOnly(2026, 9, 1) };
        Assert.Contains("exchange rates", ledger.TryReserve("deepgram", "p", Money.Of(0.01m, "USD"), Aud(5m), stale).Refusal, StringComparison.Ordinal);
        Assert.True(ledger.TryReserve("local-aud", "p", Money.Of(1m, "AUD"), Aud(5m), null).Allowed);
    }

    [Fact]
    public void Settling_records_the_actual_cost_and_releasing_frees_the_reservation()
    {
        var ledger = Ledger();
        var a = ledger.TryReserve("deepgram", "p", Money.Of(2m, "USD"), Aud(5m), Rates).ReservationId!.Value;
        ledger.Settle(a, Money.Of(1m, "USD"));
        var b = ledger.TryReserve("deepgram", "p", Money.Of(2m, "USD"), Aud(5m), Rates).ReservationId!.Value;
        ledger.Release(b);

        Assert.Equal(1.5m, ledger.ThisMonth(Aud(5m), Rates).Total);
        Assert.Equal(1.5m, Ledger().ThisMonth(Aud(5m), Rates).Total);
    }

    [Fact]
    public void Concurrent_reservations_never_overspend()
    {
        var ledger = Ledger();
        var allowed = 0;
        Parallel.For(0, 200, _ =>
        {
            if (ledger.TryReserve("deepgram", "p", Money.Of(0.1m, "USD"), Aud(3m), Rates).Allowed)
            {
                System.Threading.Interlocked.Increment(ref allowed);
            }
        });

        Assert.Equal(20, allowed);
    }

    [Fact]
    public void A_new_month_starts_afresh()
    {
        var ledger = Ledger();
        Assert.True(ledger.TryReserve("deepgram", "p", Money.Of(3m, "USD"), Aud(5m), Rates).Allowed);
        Assert.False(ledger.TryReserve("deepgram", "p", Money.Of(1m, "USD"), Aud(5m), Rates).Allowed);

        _clock.Now = new DateTimeOffset(2026, 10, 1, 0, 30, 0, TimeSpan.FromHours(10));

        Assert.True(ledger.TryReserve("deepgram", "p", Money.Of(1m, "USD"), Aud(5m), Rates with { Date = new DateOnly(2026, 9, 30) }).Allowed);
    }

    [Fact]
    public void A_damaged_ledger_blocks_paid_use_for_the_month()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "spend.json"), "{ not json");

        var refused = Ledger().TryReserve("deepgram", "p", Money.Of(0.01m, "USD"), Aud(5m), Rates);

        Assert.False(refused.Allowed);
        Assert.Single(Directory.GetFiles(_dir, "spend.json.damaged-*"));
        Assert.False(Ledger().TryReserve("deepgram", "p", Money.Of(0.01m, "USD"), Aud(5m), Rates).Allowed);
    }

    [Fact]
    public void Price_tables_are_validated_and_looked_up_by_model()
    {
        const string json = """
            {"version":"2026-09-26","prices":[
              {"provider":"deepgram","model":"nova-3","unit":"audio-minute","amount":"0.0043","currency":"USD"},
              {"provider":"deepgram","model":"*","unit":"audio-minute","amount":"0.0048","currency":"USD"},
              {"provider":"openai","model":"whisper-1","unit":"audio-minute","amount":"0.006","currency":"usd"}
            ]}
            """;
        var table = PriceTable.Parse(json)!;

        Assert.Equal(Money.Of(0.0043m, "USD"), table.PriceOf("Deepgram", "nova-3", PriceTable.AudioMinute));
        Assert.Equal(Money.Of(0.0048m, "USD"), table.PriceOf("deepgram", "whisper", PriceTable.AudioMinute));
        Assert.Null(table.PriceOf("openai", "gpt-4o-transcribe", PriceTable.AudioMinute));
        Assert.Equal(0.003m, table.AudioCost("openai", "whisper-1", 30)!.Value.Amount);

        Assert.Null(PriceTable.Parse(json.Replace("0.0043", "-1", StringComparison.Ordinal)));
        Assert.Null(PriceTable.Parse(json.Replace("audio-minute\",\"amount\":\"0.006", "per-call\",\"amount\":\"0.006", StringComparison.Ordinal)));
        Assert.Null(PriceTable.Parse(json.Replace("\"0.0048\"", "\"5000\"", StringComparison.Ordinal)));
        Assert.Null(PriceTable.Parse("{\"version\":\"soon\",\"prices\":[]}"));
        Assert.Null(PriceTable.Parse("not json"));
    }

    private const string Ecb = """
        <?xml version="1.0" encoding="UTF-8"?>
        <gesmes:Envelope xmlns:gesmes="http://www.gesmes.org/xml/2002-08-01" xmlns="http://www.ecb.int/vocabulary/2002-08-01/eurofxref">
          <Cube>
            <Cube time='2026-09-25'>
              <Cube currency='USD' rate='1.1000'/>
              <Cube currency='AUD' rate='1.6500'/>
            </Cube>
          </Cube>
        </gesmes:Envelope>
        """;

    [Fact]
    public async Task Rates_are_fetched_saved_and_kept_when_a_fetch_fails()
    {
        var path = Path.Combine(_dir, "rates.json");
        using (var store = new ExchangeRateStore(path, _clock))
        using (var http = new System.Net.Http.HttpClient(new Serve(Ecb)))
        {
            var rates = await store.RefreshAsync(http, TestContext.Current.CancellationToken);
            Assert.Equal(new DateOnly(2026, 9, 25), rates!.Date);
            Assert.Equal(1.65m, rates.PerEuro["AUD"]);
        }

        using var again = new ExchangeRateStore(path, _clock);
        Assert.Equal(new DateOnly(2026, 9, 25), again.Current!.Date);
        _clock.Now = _clock.Now.AddDays(1);
        using var failing = new System.Net.Http.HttpClient(new Serve(null));
        Assert.Equal(new DateOnly(2026, 9, 25), (await again.RefreshAsync(failing, TestContext.Current.CancellationToken))!.Date);

        using var huge = new System.Net.Http.HttpClient(new Serve(Ecb + new string(' ', EcbRates.MaxBytes)));
        using var fresh = new ExchangeRateStore(Path.Combine(_dir, "other.json"), _clock);
        Assert.Null(await fresh.RefreshAsync(huge, TestContext.Current.CancellationToken));
    }

    private sealed class Serve(string? body) : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
            => body is null
                ? throw new System.Net.Http.HttpRequestException("blocked")
                : Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent(body), RequestMessage = request });
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.CreateCustomTimeZone("Test+10", TimeSpan.FromHours(10), "Test", "Test");
    }
}
