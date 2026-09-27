using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Common.Costs;
using Jellyfin.Plugin.Common.Resilience;
using Xunit;

namespace Jellyfin.Plugin.Common.Tests;

// One budget page: the spending entry point's client, the bridged meter and the ledger's owned reservations
public sealed class SpendingBridgeTests : IDisposable
{
    private static readonly SpendLimits Usd5 = new("USD", 5m, new Dictionary<string, decimal>(), 0m);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "common-spendbridge-" + Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new(new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private SpendLedger Ledger(string name = "spend.json") => new(Path.Combine(_dir, name), _clock);

    [Fact]
    public void Only_the_owner_settles_or_releases_a_reservation()
    {
        var ledger = Ledger();
        var id = ledger.TryReserve("deepgram", "subtitles.sync", Money.Of(1m, "USD"), Usd5, null, owner: "subtitles").ReservationId!.Value;

        Assert.False(ledger.Settle(id, Money.Of(0.2m, "USD")));
        Assert.False(ledger.Release(id, "ingest"));
        Assert.True(ledger.Settle(id, Money.Of(0.2m, "USD"), "subtitles"));
        Assert.Equal(0.2m, ledger.ThisMonth(Usd5, null).Total);
        Assert.False(ledger.Release(id, "subtitles"));
    }

    [Fact]
    public void An_owned_reservation_left_open_expires_at_its_estimate()
    {
        var ledger = Ledger();
        var owned = ledger.TryReserve("deepgram", "subtitles.sync", Money.Of(1m, "USD"), Usd5, null, owner: "subtitles").ReservationId!.Value;
        var own = ledger.TryReserve("anthropic", "ingest.match", Money.Of(0.5m, "USD"), Usd5, null).ReservationId!.Value;

        _clock.Now += TimeSpan.FromMinutes(59);
        Assert.Equal(0, ledger.ExpireOpen(SpendingBridgeClient.ReservationLifetime));
        _clock.Now += TimeSpan.FromMinutes(2);
        Assert.Equal(1, ledger.ExpireOpen(SpendingBridgeClient.ReservationLifetime));

        // A late settle or release changes nothing: it stays at the estimate
        Assert.False(ledger.Settle(owned, Money.Of(0.1m, "USD"), "subtitles"));
        Assert.False(ledger.Release(owned, "subtitles"));
        Assert.Equal(1.5m, ledger.ThisMonth(Usd5, null).Total);

        // This plugin's own reservations never expire, and still settle
        Assert.True(ledger.Settle(own, Money.Of(0.25m, "USD")));
        Assert.Equal(1.25m, Ledger().ThisMonth(Usd5, null).Total);
    }

    [Fact]
    public void Carried_spending_is_replaced_not_added_and_zero_removes_it()
    {
        var ledger = Ledger();
        ledger.RecordCarried("subtitles", "deepgram", "subtitles.carried", Money.Of(2m, "USD"));
        ledger.RecordCarried("subtitles", "deepgram", "subtitles.carried", Money.Of(2.5m, "USD"));
        Assert.Equal(2.5m, ledger.ThisMonth(Usd5, null).Total);

        // It counts against the limits
        Assert.False(ledger.TryReserve("deepgram", "subtitles.sync", Money.Of(3m, "USD"), Usd5, null, "subtitles").Allowed);

        ledger.RecordCarried("subtitles", "deepgram", "subtitles.carried", Money.Of(0m, "USD"));
        Assert.Equal(0m, Ledger().ThisMonth(Usd5, null).Total);
    }

    [Fact]
    public void This_months_own_spending_as_charged_leaves_out_owned_entries()
    {
        var ledger = Ledger();
        ledger.TryReserve("deepgram", "subtitles.sync", Money.Of(1m, "USD"), Usd5, null);
        ledger.TryReserve("deepgram", "subtitles.sync", Money.Of(0.5m, "USD"), Usd5, null);
        ledger.TryReserve("openai", "subtitles.sync", Money.Of(0.25m, "USD"), Usd5, null);
        ledger.TryReserve("openai", "subtitles.sync", Money.Of(9m, "USD"), Usd5 with { Overall = null }, null, owner: "subtitles");

        var month = ledger.ThisMonthAsCharged();
        Assert.Equal([("deepgram", Money.Of(1.5m, "USD")), ("openai", Money.Of(0.25m, "USD"))], month);
    }

    [Fact]
    public async Task Without_the_AI_plugin_the_client_says_not_installed()
    {
        var client = new SpendingBridgeClient("subtitles");
        Assert.False(client.IsInstalled);
        var reply = await client.ReserveAsync("subtitles.sync", "deepgram", Money.Of(1m, "USD"), TestContext.Current.CancellationToken);
        Assert.Equal("not-installed", reply.Failure);
        Assert.True(SpendingBridgeClient.MeansOwnBudget(reply.Failure));
        Assert.Equal("not-installed", (await client.SummaryAsync(TestContext.Current.CancellationToken)).Failure);
    }

    [Fact]
    public async Task Requests_carry_the_contract_fields()
    {
        var sent = new List<JsonElement>();
        var client = new SpendingBridgeClient("subtitles", (json, _) =>
        {
            sent.Add(JsonDocument.Parse(json).RootElement.Clone());
            return Task.FromResult("{\"version\":1,\"ok\":true,\"reservationId\":\"" + Guid.Empty.ToString("D") + "\"}");
        });
        var id = Guid.NewGuid();
        var ct = TestContext.Current.CancellationToken;

        await client.ReserveAsync("subtitles.sync", "deepgram", Money.Of(0.0125m, "USD"), ct);
        await client.SettleAsync(id, Money.Of(0.01m, "USD"), ct);
        await client.ReleaseAsync(id, ct);
        await client.CarryAsync("openai-speech", Money.Of(3m, "USD"), "2026-09", ct);

        Assert.All(sent, r => Assert.Equal(1, r.GetProperty("version").GetInt32()));
        Assert.All(sent, r => Assert.Equal("subtitles", r.GetProperty("caller").GetString()));
        Assert.Equal(["reserve", "settle", "release", "carry"], sent.Select(r => r.GetProperty("op").GetString()));
        Assert.Equal(0.0125m, sent[0].GetProperty("estimate").GetProperty("amount").GetDecimal());
        Assert.Equal("USD", sent[0].GetProperty("estimate").GetProperty("currency").GetString());
        Assert.Equal("deepgram", sent[0].GetProperty("provider").GetString());
        Assert.Equal(id.ToString("D"), sent[1].GetProperty("reservationId").GetString());
        Assert.Equal(0.01m, sent[1].GetProperty("actual").GetProperty("amount").GetDecimal());
        Assert.Equal("2026-09", sent[3].GetProperty("month").GetString());
    }

    [Fact]
    public void Replies_are_read_safely()
    {
        var id = Guid.NewGuid();
        Assert.Equal(id, SpendingBridgeClient.Read("{\"ok\":true,\"reservationId\":\"" + id + "\"}").ReservationId);
        Assert.Equal("provider-limit", SpendingBridgeClient.Read("{\"ok\":false,\"error\":\"Over.\",\"failure\":\"provider-limit\"}").Failure);
        Assert.Equal("transient", SpendingBridgeClient.Read("not json").Failure);
        Assert.Equal("transient", SpendingBridgeClient.Read("[1]").Failure);
        Assert.Equal("transient", SpendingBridgeClient.Read(null).Failure);
        Assert.Equal("transient", SpendingBridgeClient.Read(new string(' ', SpendingBridgeClient.MaxReply + 1)).Failure);

        var summary = SpendingBridgeClient.ReadSummary("{\"ok\":true,\"currency\":\"aud\",\"limit\":null,\"spent\":1.5,\"perProvider\":{\"deepgram\":1.5,\"x\":\"no\"},\"providerLimits\":{\"deepgram\":2},\"ratesDate\":\"2026-09-25\",\"ratesFresh\":true}");
        Assert.True(summary.Ok);
        Assert.Equal("AUD", summary.Currency);
        Assert.Null(summary.Limit);
        Assert.Equal(1.5m, summary.Spent);
        Assert.Equal(1.5m, Assert.Single(summary.PerProvider).Value);
        Assert.Equal(2m, summary.ProviderLimits["deepgram"]);
        Assert.True(summary.RatesFresh);
        Assert.False(SpendingBridgeClient.ReadSummary("{\"ok\":true}").Ok);
    }

    [Fact]
    public async Task Calls_metered_through_the_AI_plugin_are_settled_there_and_never_on_the_own_ledger()
    {
        var server = new FakeServer();
        var own = Ledger();
        var meter = new BridgedSpendMeter(server.Client(), () => new LocalSpendMeter(own, Usd5, () => null), p => p == "openai" ? SpendingBridgeClient.OpenAiSpeech : p);

        var result = await MeteredCall.RunAsync(meter, "openai", "subtitles.sync", Money.Of(1m, "USD"), _ => Task.FromResult(42), _ => Money.Of(0.4m, "USD"), null, TestContext.Current.CancellationToken);

        Assert.Equal(42, result);
        Assert.Equal(["reserve:openai-speech", "settle:0.4"], server.Ops);
        Assert.Equal(0m, own.ThisMonth(Usd5, null).Total);
    }

    [Fact]
    public async Task A_call_that_failed_uncharged_is_released_through_the_AI_plugin()
    {
        var server = new FakeServer();
        var meter = new BridgedSpendMeter(server.Client(), () => throw new InvalidOperationException("not used"));

        await Assert.ThrowsAsync<ProviderException>(() => MeteredCall.RunAsync<int>(meter, "deepgram", "subtitles.sync", Money.Of(1m, "USD"), _ => throw new ProviderException("down"), _ => null, null, TestContext.Current.CancellationToken));
        Assert.Equal(["reserve:deepgram", "release"], server.Ops);
    }

    [Theory]
    [InlineData("not-installed")]
    [InlineData("not-allowed")]
    [InlineData("unsupported-version")]
    public async Task Without_the_AI_plugins_budget_the_own_ledger_is_used(string failure)
    {
        var server = new FakeServer { Refuse = failure };
        var own = Ledger();
        var meter = new BridgedSpendMeter(server.Client(), () => new LocalSpendMeter(own, Usd5, () => null));

        await MeteredCall.RunAsync(meter, "deepgram", "subtitles.sync", Money.Of(1m, "USD"), _ => Task.FromResult(1), _ => Money.Of(0.3m, "USD"), null, TestContext.Current.CancellationToken);

        Assert.Equal(0.3m, own.ThisMonth(Usd5, null).Total);
        Assert.Equal(["reserve:deepgram"], server.Ops);
    }

    [Theory]
    [InlineData("provider-limit", "Over the limit.")]
    [InlineData("transient", "couldn't be checked")]
    public async Task A_refusal_or_no_answer_from_the_AI_plugin_stops_the_call_without_using_the_own_ledger(string failure, string says)
    {
        var server = new FakeServer { Refuse = failure };
        var own = Ledger();
        var meter = new BridgedSpendMeter(server.Client(), () => new LocalSpendMeter(own, Usd5, () => null));
        var called = false;

        var ex = await Assert.ThrowsAsync<ProviderException>(() => MeteredCall.RunAsync(meter, "deepgram", "subtitles.sync", Money.Of(1m, "USD"), _ => Task.FromResult(called = true), _ => null, null, TestContext.Current.CancellationToken));

        Assert.False(called);
        Assert.Equal(FailureClass.ProviderLimit, ex.Failure);
        Assert.Contains(says, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0m, own.ThisMonth(Usd5, null).Total);
    }

    [Fact]
    public async Task Own_spending_this_month_is_carried_once_and_again_only_when_it_changes()
    {
        var server = new FakeServer();
        var own = Ledger();
        own.TryReserve("openai", "subtitles.sync", Money.Of(2m, "USD"), Usd5, null);
        var carry = new SpendCarry(own.ThisMonthAsCharged, p => p == "openai" ? SpendingBridgeClient.OpenAiSpeech : p, _clock);
        var meter = new BridgedSpendMeter(server.Client(), () => new LocalSpendMeter(own, Usd5, () => null), null, carry);
        var ct = TestContext.Current.CancellationToken;

        await MeteredCall.RunAsync(meter, "deepgram", "p.q", Money.Of(1m, "USD"), _ => Task.FromResult(1), _ => null, null, ct);
        await MeteredCall.RunAsync(meter, "deepgram", "p.q", Money.Of(1m, "USD"), _ => Task.FromResult(1), _ => null, null, ct);
        Assert.Single(server.Ops, o => o == "carry:openai-speech:2:2026-09");

        own.TryReserve("openai", "subtitles.sync", Money.Of(0.5m, "USD"), Usd5, null);
        await carry.SendAsync(server.Client(), ct);
        Assert.Contains("carry:openai-speech:2.5:2026-09", server.Ops);
        Assert.Equal(2, server.Ops.Count(o => o.StartsWith("carry:", StringComparison.Ordinal)));
    }

    // Stands in for the AI plugin's entry point: records each operation, answers ok or with a failure
    private sealed class FakeServer
    {
        public List<string> Ops { get; } = [];

        public string? Refuse { get; init; }

        public SpendingBridgeClient Client() => new("subtitles", (json, _) =>
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var op = r.GetProperty("op").GetString();
            Ops.Add(op switch
            {
                "reserve" => "reserve:" + r.GetProperty("provider").GetString(),
                "settle" => "settle:" + r.GetProperty("actual").GetProperty("amount").GetDecimal().ToString(System.Globalization.CultureInfo.InvariantCulture),
                "carry" => "carry:" + r.GetProperty("provider").GetString() + ":" + r.GetProperty("amount").GetProperty("amount").GetDecimal().ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + r.GetProperty("month").GetString(),
                _ => op!,
            });
            return Task.FromResult(Refuse is { } f && op == "reserve"
                ? "{\"version\":1,\"ok\":false,\"error\":\"Over the limit.\",\"failure\":\"" + f + "\"}"
                : "{\"version\":1,\"ok\":true,\"reservationId\":\"" + Guid.NewGuid() + "\"}");
        });
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
