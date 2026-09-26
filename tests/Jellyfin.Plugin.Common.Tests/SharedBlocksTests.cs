using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Common.Costs;
using Jellyfin.Plugin.Common.Resilience;
using Jellyfin.Plugin.Common.Storage;
using Xunit;

namespace Jellyfin.Plugin.Common.Tests;

// FAM-06: the shared building blocks (JsonFile, ProviderHttp, ProviderException, MeteredCall, CurrencyCode.NormaliseOr, SpendingStore)
public sealed class SharedBlocksTests : IDisposable
{
    private const string Key = "sk-test-0123456789abcdefghij";

    private static readonly SpendLimits Usd = new("USD", 5m, new Dictionary<string, decimal>(), 0m);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "common-fam06-" + Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new(new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            foreach (var f in Directory.GetFiles(_dir))
            {
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(f, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }

            Directory.Delete(_dir, recursive: true);
        }
    }

    private static void NeedsPermissions() => Assert.SkipWhen(OperatingSystem.IsWindows() || Environment.UserName == "root", "Needs a non-root Unix account.");

    private string PathOf(string name) => Path.Combine(_dir, name);

    // ---- JsonFile ----

    [Fact]
    public void A_missing_file_is_missing_and_a_written_one_loads_without_leaving_a_temporary_file()
    {
        var path = PathOf("state.json");
        Assert.Equal(JsonFileState.Missing, JsonFile.Read<Dictionary<string, int>>(path).State);

        JsonFile.WriteAtomic(path, new Dictionary<string, int> { ["a"] = 1 });
        JsonFile.WriteAtomic(path, new Dictionary<string, int> { ["a"] = 2 });
        var read = JsonFile.Read<Dictionary<string, int>>(path);

        Assert.True(read.IsLoaded);
        Assert.Equal(2, read.Value!["a"]);
        Assert.Null(read.Error);
        Assert.Equal([path], Directory.GetFiles(_dir));
    }

    [Fact]
    public void A_damaged_file_is_reported_and_can_be_set_aside_with_a_timestamp()
    {
        var path = PathOf("state.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, "{ not json");

        var read = JsonFile.Read<Dictionary<string, int>>(path);
        Assert.Equal(JsonFileState.Damaged, read.State);
        Assert.IsType<System.Text.Json.JsonException>(read.Error);

        var aside = JsonFile.SetAside(path, _clock);
        Assert.Equal(path + ".damaged-" + _clock.GetUtcNow().ToUnixTimeSeconds(), aside);
        Assert.False(File.Exists(path));
        Assert.Equal("{ not json", File.ReadAllText(aside!));
        Assert.Null(JsonFile.SetAside(path, _clock));
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void An_unreadable_file_is_unreadable_not_damaged_and_is_left_alone()
    {
        NeedsPermissions();
        var path = PathOf("state.json");
        JsonFile.WriteAtomic(path, new[] { 1, 2, 3 });
        File.SetUnixFileMode(path, UnixFileMode.None);

        var read = JsonFile.Read<int[]>(path);

        Assert.Equal(JsonFileState.Unreadable, read.State);
        Assert.IsType<UnauthorizedAccessException>(read.Error);
        Assert.True(File.Exists(path));
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void An_owner_only_write_is_0600_even_over_a_leftover_readable_temporary_file()
    {
        var path = PathOf("keys.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path + JsonFile.TempSuffix, "old");
        File.SetUnixFileMode(path + JsonFile.TempSuffix, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        JsonFile.WriteAtomic(path, new Dictionary<string, string> { ["p"] = "v" }, ownerOnly: true);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.False(File.Exists(path + JsonFile.TempSuffix));
    }

    [Fact]
    public void A_failed_write_leaves_the_old_file_and_no_temporary_file()
    {
        var path = PathOf("state.json");
        JsonFile.WriteAtomic(path, new[] { 1 });
        var loop = new Node();
        loop.Next = loop;

        Assert.Throws<System.Text.Json.JsonException>(() => JsonFile.WriteAtomic(path, loop));

        Assert.Equal([1], JsonFile.Read<int[]>(path).Value!);
        Assert.Equal([path], Directory.GetFiles(_dir));
    }

    // ---- ProviderHttp ----

    [Fact]
    public async Task A_successful_reply_is_returned_as_text_or_bytes()
    {
        using var http = new HttpClient(new Reply(_ => Text(HttpStatusCode.OK, "{\"ok\":true}")));
        using var one = new HttpRequestMessage(HttpMethod.Get, "https://provider.example/v1");
        using var two = new HttpRequestMessage(HttpMethod.Get, "https://provider.example/v1");

        Assert.Equal("{\"ok\":true}", await ProviderHttp.SendAsync(http, one, 1024, [Key], TestContext.Current.CancellationToken));
        Assert.Equal("{\"ok\":true}"u8.ToArray(), await ProviderHttp.SendForBytesAsync(http, two, 1024, [Key], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_oversized_reply_is_refused_without_reading_it_all()
    {
        var endless = new EndlessContent();
        using var http = new HttpClient(new Reply(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = endless }));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://provider.example/v1");

        var ex = await Assert.ThrowsAsync<ProviderException>(() => ProviderHttp.SendAsync(http, request, 1000, [Key], TestContext.Current.CancellationToken));

        Assert.Equal(FailureClass.BadRequest, ex.Failure);
        Assert.True(endless.Served <= 1001, "read " + endless.Served + " bytes");

        using var declared = new HttpClient(new Reply(_ => Text(HttpStatusCode.OK, new string('x', 2000))));
        using var again = new HttpRequestMessage(HttpMethod.Get, "https://provider.example/v1");
        Assert.Equal(FailureClass.BadRequest, (await Assert.ThrowsAsync<ProviderException>(() => ProviderHttp.SendAsync(declared, again, 1000, [Key], TestContext.Current.CancellationToken))).Failure);
    }

    [Fact]
    public async Task An_error_is_classified_with_its_wait_and_never_shows_the_key()
    {
        using var http = new HttpClient(new Reply(_ =>
        {
            var r = Text(HttpStatusCode.TooManyRequests, "slow down, " + Key);
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return r;
        }));
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://provider.example/v1");

        var ex = await Assert.ThrowsAsync<ProviderException>(() => ProviderHttp.SendAsync(http, request, 1024, [Key], TestContext.Current.CancellationToken));

        Assert.Equal(FailureClass.Transient, ex.Failure);
        Assert.Equal(TimeSpan.FromSeconds(30), ex.RetryAfter);
        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.Contains("429", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Key[..12], ex.Message, StringComparison.Ordinal);
        Assert.False(ex.Charged);
        Assert.True(ex.RateLimited);
        Assert.StartsWith("slow down, ", ex.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(Key[..12], ex.Detail, StringComparison.Ordinal);
    }

    // FAM-06 follow-up: the provider's reply as a detail a plugin can word its own message around
    [Fact]
    public async Task An_error_carries_the_providers_reply_as_a_redacted_detail()
    {
        using var http = new HttpClient(new Reply(_ => Text(HttpStatusCode.BadRequest, "  {\"error\":\"bad file\",\"key\":\"" + Key + "\"}\n")));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://provider.example/v1");

        var ex = await Assert.ThrowsAsync<ProviderException>(() => ProviderHttp.SendAsync(http, request, 1024, [Key], TestContext.Current.CancellationToken));

        Assert.NotNull(ex.Detail);
        Assert.StartsWith("{\"error\":\"bad file\"", ex.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(Key[..12], ex.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("provider.example", ex.Detail, StringComparison.Ordinal);
        Assert.StartsWith("provider.example answered HTTP 400: ", ex.Message, StringComparison.Ordinal);
        Assert.Contains(ex.Detail, ex.Message, StringComparison.Ordinal);
        Assert.False(ex.RateLimited);
    }

    [Fact]
    public async Task A_long_or_empty_error_reply_gives_a_short_or_no_detail()
    {
        using var longReply = new HttpClient(new Reply(_ => Text(HttpStatusCode.InternalServerError, string.Concat(Enumerable.Repeat("error ", 250)))));
        using var one = new HttpRequestMessage(HttpMethod.Get, "https://provider.example/v1");
        var ex = await Assert.ThrowsAsync<ProviderException>(() => ProviderHttp.SendAsync(longReply, one, 4096, [Key], TestContext.Current.CancellationToken));
        Assert.True(ex.Detail!.Length <= ProviderHttp.DetailMaxLength + 1, "detail " + ex.Detail.Length);
        Assert.StartsWith("error error", ex.Detail, StringComparison.Ordinal);
        Assert.EndsWith("…", ex.Detail, StringComparison.Ordinal);
        Assert.Contains(string.Concat(Enumerable.Repeat("error ", 250)).Trim(), ex.Message, StringComparison.Ordinal);

        using var empty = new HttpClient(new Reply(_ => Text(HttpStatusCode.BadGateway, "   ")));
        using var two = new HttpRequestMessage(HttpMethod.Get, "https://provider.example/v1");
        Assert.Null((await Assert.ThrowsAsync<ProviderException>(() => ProviderHttp.SendAsync(empty, two, 1024, [Key], TestContext.Current.CancellationToken))).Detail);

        // A surrogate pair at the cut is dropped whole, never split
        var emoji = new string('a', ProviderHttp.DetailMaxLength - 1) + "\U0001F600" + "tail";
        var cut = ProviderHttp.DetailOf(emoji)!;
        Assert.False(char.IsHighSurrogate(cut[^2]));
        Assert.Equal(new string('a', ProviderHttp.DetailMaxLength - 1) + "…", cut);
    }

    [Fact]
    public async Task A_429_with_a_long_wait_is_a_provider_limit_and_any_429_is_rate_limited()
    {
        using var http = new HttpClient(new Reply(_ =>
        {
            var r = Text(HttpStatusCode.TooManyRequests, "too many requests");
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromHours(6));
            return r;
        }));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://provider.example/v1");

        var ex = await Assert.ThrowsAsync<ProviderException>(() => ProviderHttp.SendAsync(http, request, 1024, [Key], TestContext.Current.CancellationToken));

        Assert.Equal(FailureClass.ProviderLimit, ex.Failure);
        Assert.Equal(TimeSpan.FromHours(6), ex.RetryAfter);
        Assert.True(ex.RateLimited);
        Assert.True(HttpFailure.IsRateLimited(ex));
        Assert.Equal("too many requests", ex.Detail);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "bad key", (int)FailureClass.Authentication)]
    [InlineData(HttpStatusCode.PaymentRequired, "", (int)FailureClass.ProviderLimit)]
    [InlineData(HttpStatusCode.BadRequest, "{\"error\":\"insufficient_quota\"}", (int)FailureClass.ProviderLimit)]
    [InlineData(HttpStatusCode.BadRequest, "bad audio", (int)FailureClass.BadRequest)]
    [InlineData(HttpStatusCode.BadGateway, "", (int)FailureClass.Transient)]
    public async Task Error_statuses_use_the_shared_classification(HttpStatusCode status, string body, int expected)
    {
        using var http = new HttpClient(new Reply(_ => Text(status, body)));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://provider.example/v1");

        var ex = await Assert.ThrowsAsync<ProviderException>(() => ProviderHttp.SendAsync(http, request, 1024, [Key], TestContext.Current.CancellationToken));

        Assert.Equal((FailureClass)expected, ex.Failure);
    }

    [Fact]
    public async Task An_endless_error_body_is_read_only_up_to_the_limit()
    {
        var endless = new EndlessContent();
        using var http = new HttpClient(new Reply(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = endless }));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://provider.example/v1");

        var ex = await Assert.ThrowsAsync<ProviderException>(() => ProviderHttp.SendAsync(http, request, 500, [Key], TestContext.Current.CancellationToken));

        Assert.Equal(FailureClass.Transient, ex.Failure);
        Assert.True(endless.Served <= 501, "read " + endless.Served + " bytes");
    }

    [Fact]
    public async Task No_network_is_no_connection_and_the_callers_cancellation_is_not_a_failure()
    {
        using var offline = new HttpClient(new Reply(_ => throw new HttpRequestException("lookup failed for " + Key, new SocketException((int)SocketError.HostNotFound))));
        using var one = new HttpRequestMessage(HttpMethod.Get, "https://provider.example/v1");
        var ex = await Assert.ThrowsAsync<ProviderException>(() => ProviderHttp.SendAsync(offline, one, 1024, [Key], TestContext.Current.CancellationToken));
        Assert.Equal(FailureClass.NoConnection, ex.Failure);
        Assert.DoesNotContain(Key, ex.Message, StringComparison.Ordinal);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        using var slow = new HttpClient(new Reply(_ => throw new TaskCanceledException()));
        using var two = new HttpRequestMessage(HttpMethod.Get, "https://provider.example/v1");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProviderHttp.SendAsync(slow, two, 1024, [Key], cts.Token));
    }

    // ---- MeteredCall ----

    private SpendLedger Ledger() => new(PathOf("spend.json"), _clock);

    private static Task<T> Run<T>(SpendLedger ledger, Func<CancellationToken, Task<T>> call, Func<T, Money?> actual, MeteredCallOptions? options = null, CancellationToken token = default)
        => MeteredCall.RunAsync(ledger, Usd, null, "openai", "test", Money.Of(2m, "USD"), call, actual, options, token);

    [Fact]
    public async Task A_successful_call_is_settled_at_its_actual_cost()
    {
        var ledger = Ledger();
        Money? recorded = null;

        var result = await Run(ledger, _ => Task.FromResult(42), _ => Money.Of(0.5m, "USD"), new MeteredCallOptions { Recorded = m => recorded = m }, TestContext.Current.CancellationToken);

        Assert.Equal(42, result);
        Assert.Equal(0.5m, ledger.ThisMonth(Usd, null).Total);
        Assert.Equal(Money.Of(0.5m, "USD"), recorded);
    }

    [Fact]
    public async Task A_billed_failure_is_settled_at_what_it_used_or_the_estimate()
    {
        var ledger = Ledger();
        await Assert.ThrowsAsync<ProviderException>(() => Run<int>(ledger, _ => throw new ProviderException("cut off") { Charged = true, ChargedCost = Money.Of(0.25m, "USD") }, _ => null, token: TestContext.Current.CancellationToken));
        Assert.Equal(0.25m, ledger.ThisMonth(Usd, null).Total);

        await Assert.ThrowsAsync<ProviderException>(() => Run<int>(ledger, _ => throw new ProviderException("unreadable") { Charged = true }, _ => null, token: TestContext.Current.CancellationToken));
        Assert.Equal(2.25m, ledger.ThisMonth(Usd, null).Total);
    }

    [Fact]
    public async Task An_unbilled_failure_or_cancellation_releases_the_reservation()
    {
        var ledger = Ledger();
        await Assert.ThrowsAsync<ProviderException>(() => Run<int>(ledger, _ => throw new ProviderException("401") { Failure = FailureClass.Authentication }, _ => null, token: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<OperationCanceledException>(() => Run<int>(ledger, _ => throw new OperationCanceledException(), _ => null, token: TestContext.Current.CancellationToken));

        Assert.Equal(0m, ledger.ThisMonth(Usd, null).Total);
    }

    [Fact]
    public async Task An_unexpected_failure_is_recorded_at_the_estimate()
    {
        // It might have been billed, so it errs on the side of spending less
        var ledger = Ledger();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run<int>(ledger, _ => throw new InvalidOperationException("bug"), _ => null, token: TestContext.Current.CancellationToken));

        Assert.Equal(2m, ledger.ThisMonth(Usd, null).Total);
    }

    [Fact]
    public async Task A_call_over_the_limit_is_refused_and_never_made()
    {
        var ledger = Ledger();
        var made = false;
        var over = new SpendLimits("USD", 1m, new Dictionary<string, decimal>(), 0m);

        var ex = await Assert.ThrowsAsync<ProviderException>(() => MeteredCall.RunAsync(ledger, over, null, "openai", "test", Money.Of(2m, "USD"), _ => Task.FromResult(made = true), _ => null, null, TestContext.Current.CancellationToken));

        Assert.Equal(FailureClass.ProviderLimit, ex.Failure);
        Assert.Contains("monthly limit", ex.Message, StringComparison.Ordinal);
        Assert.False(made);
    }

    [Fact]
    public async Task A_plugin_with_its_own_exception_type_can_say_what_was_billed_and_how_to_refuse()
    {
        var ledger = Ledger();
        var options = new MeteredCallOptions
        {
            IsCharged = ex => ex is InvalidOperationException { Message: "billed" },
            IsUncharged = ex => ex is InvalidOperationException { Message: "not billed" },
            ChargedCost = _ => Money.Of(0.75m, "USD"),
            Refuse = why => new InvalidOperationException("refused: " + why),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => Run<int>(ledger, _ => throw new InvalidOperationException("billed"), _ => null, options, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run<int>(ledger, _ => throw new InvalidOperationException("not billed"), _ => null, options, TestContext.Current.CancellationToken));
        Assert.Equal(0.75m, ledger.ThisMonth(Usd, null).Total);

        var zero = new SpendLimits("USD", 0m, new Dictionary<string, decimal>(), 0m);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => MeteredCall.RunAsync(ledger, zero, null, "openai", "test", Money.Of(1m, "USD"), _ => Task.FromResult(1), _ => null, options, TestContext.Current.CancellationToken));
        Assert.StartsWith("refused: ", refused.Message, StringComparison.Ordinal);
    }

    // ---- CurrencyCode.NormaliseOr ----

    [Fact]
    public void Only_supported_currencies_pass_otherwise_the_fallback()
    {
        Assert.Equal("AUD", CurrencyCode.NormaliseOr(" aud ", "USD"));
        Assert.Equal("USD", CurrencyCode.NormaliseOr("XYZ", "USD"));
        Assert.Equal("USD", CurrencyCode.NormaliseOr(null, "usd"));
        Assert.Equal("EUR", CurrencyCode.NormaliseOr("", "EUR"));
        Assert.Throws<ArgumentException>(() => CurrencyCode.NormaliseOr("AUD", "XYZ"));
    }

    // ---- SpendingStore ----

    [Fact]
    public async Task The_spending_store_refreshes_rates_itself_but_not_on_every_call_while_the_source_is_down()
    {
        using var store = new SpendingStore(_dir, null, _clock);
        var calls = 0;
        var up = false;
        using var http = new HttpClient(new Reply(request =>
        {
            calls++;
            return up
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Ecb), RequestMessage = request }
                : throw new HttpRequestException("blocked");
        }));

        Assert.Null(await store.CurrentRatesAsync(http, TestContext.Current.CancellationToken));
        Assert.Null(await store.CurrentRatesAsync(http, TestContext.Current.CancellationToken));
        Assert.Equal(1, calls);

        _clock.Now += SpendingStore.RetryStaleAfter;
        up = true;
        var rates = await store.CurrentRatesAsync(http, TestContext.Current.CancellationToken);
        Assert.Equal(2, calls);
        Assert.Equal(1.65m, rates!.PerEuro["AUD"]);

        _clock.Now += TimeSpan.FromHours(1);
        Assert.Same(rates, await store.CurrentRatesAsync(http, TestContext.Current.CancellationToken));
        Assert.Equal(2, calls);
        Assert.True(File.Exists(PathOf(SpendingStore.RatesFileName)));
        Assert.Equal(CurrencyCode.Supported, SpendingStore.Currencies);
        Assert.Null(SpendingStore.ShippedPrices(typeof(SharedBlocksTests).Assembly, "missing.prices.json"));
        Assert.Equal(0m, store.ThisMonth(Usd).Total);
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

    private static HttpResponseMessage Text(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body) };

    private sealed class Node
    {
        public Node? Next { get; set; }
    }

    private sealed class Reply(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(answer(request));
    }

    // A body with no declared length that never ends, counting what was read
    private sealed class EndlessContent : HttpContent
    {
        public long Served { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new Endless(this));

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        private sealed class Endless(EndlessContent owner) : Stream
        {
            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override int Read(byte[] buffer, int offset, int count)
            {
                Array.Fill(buffer, (byte)'x', offset, count);
                owner.Served += count;
                return count;
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
    }
}
