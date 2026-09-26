using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Jellyfin.Plugin.Common.Resilience;
using Jellyfin.Plugin.Common.Secrets;
using Xunit;

namespace Jellyfin.Plugin.Common.Tests;

public sealed class SecretsAndHttpTests : IDisposable
{
    private const string Key = "sk-test-0123456789abcdefXYZ";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "common-keys-" + Guid.NewGuid().ToString("N"));

    private string KeyPath => Path.Combine(_dir, "keys.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Keys_are_set_replaced_cleared_and_never_reported()
    {
        var file = new KeyFile(KeyPath, ["deepgram", "openai"]);
        Assert.All(file.Status().Values, Assert.False);

        file.Set("deepgram", "  " + Key + "  ");
        Assert.True(file.Status()["deepgram"]);
        Assert.Equal(Key, new KeyFile(KeyPath, ["deepgram", "openai"]).Get("deepgram"));

        file.Clear("deepgram");
        Assert.False(file.Status()["deepgram"]);
    }

    [Fact]
    public void The_key_file_is_owner_only()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        new KeyFile(KeyPath, ["openai"]).Set("openai", Key);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(KeyPath));
    }

    [Fact]
    public void Unknown_providers_and_malformed_keys_are_refused()
    {
        var file = new KeyFile(KeyPath, ["openai"]);
        Assert.Throws<ArgumentException>(() => file.Set("../x", Key));
        Assert.Throws<ArgumentException>(() => file.Set("openai", "short"));
        Assert.Throws<ArgumentException>(() => file.Set("openai", "has space 0123456789"));
    }

    [Fact]
    public void Keys_stored_for_a_provider_no_longer_allowed_are_ignored()
    {
        new KeyFile(KeyPath, ["openai", "old"]).Set("old", Key);

        Assert.Null(new KeyFile(KeyPath, ["openai"]).Get("old"));
    }

    [Fact]
    public void Redaction_removes_whole_and_partial_keys_and_token_shapes()
    {
        var text = $"invalid key {Key}; key ending ...{Key[^10..]}; Authorization: Bearer abcdefghijklmnop; other sk-ant-api03-ZZZZZZZZZZZZZZZZZZZZ";

        var safe = Redaction.Redact(text, [Key]);

        Assert.DoesNotContain(Key, safe, StringComparison.Ordinal);
        Assert.DoesNotContain(Key[^10..], safe, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdefghijklmnop", safe, StringComparison.Ordinal);
        Assert.DoesNotContain("ZZZZZZZZZZZZZZZZ", safe, StringComparison.Ordinal);
        Assert.Contains("invalid key", safe, StringComparison.Ordinal);
    }

    [Fact]
    public void Long_bodies_are_cut()
        => Assert.True(Redaction.Redact(new string('a', 5000) + " x", []).Length <= Redaction.MaxLength + 1);

    [Theory]
    [InlineData(401, null, (int)FailureClass.Authentication)]
    [InlineData(403, null, (int)FailureClass.Authentication)]
    [InlineData(403, "Your credit balance is too low", (int)FailureClass.ProviderLimit)]
    [InlineData(402, null, (int)FailureClass.ProviderLimit)]
    [InlineData(429, "Rate limit reached", (int)FailureClass.Transient)]
    [InlineData(429, "You exceeded your current quota", (int)FailureClass.ProviderLimit)]
    [InlineData(400, "unsupported audio format", (int)FailureClass.BadRequest)]
    [InlineData(400, "insufficient_quota", (int)FailureClass.ProviderLimit)]
    [InlineData(500, null, (int)FailureClass.Transient)]
    [InlineData(503, null, (int)FailureClass.Transient)]
    [InlineData(408, null, (int)FailureClass.Transient)]
    public void Statuses_are_classified(int status, string? body, int expected)
        => Assert.Equal((FailureClass)expected, HttpFailure.Classify((HttpStatusCode)status, body));

    // FAM-06 follow-up: a 429 is a provider limit only when the provider says its allowance is used up
    [Theory]
    [InlineData(null, null, (int)FailureClass.Transient)]
    [InlineData("Rate limit reached", 30, (int)FailureClass.Transient)]
    [InlineData(null, 60, (int)FailureClass.Transient)]
    [InlineData(null, 61, (int)FailureClass.ProviderLimit)]
    [InlineData(null, 86400, (int)FailureClass.ProviderLimit)]
    [InlineData("quota exceeded for today", null, (int)FailureClass.ProviderLimit)]
    [InlineData("quota exceeded for today", 5, (int)FailureClass.ProviderLimit)]
    public void A_429_is_a_provider_limit_only_when_the_provider_says_so(string? body, int? waitSeconds, int expected)
    {
        TimeSpan? wait = waitSeconds is { } w ? TimeSpan.FromSeconds(w) : null;
        Assert.Equal((FailureClass)expected, HttpFailure.Classify(HttpStatusCode.TooManyRequests, body, wait));
    }

    [Fact]
    public void A_long_wait_only_changes_the_class_of_a_429()
    {
        var day = TimeSpan.FromDays(1);
        Assert.Equal(FailureClass.Transient, HttpFailure.Classify(HttpStatusCode.ServiceUnavailable, null, day));
        Assert.Equal(FailureClass.BadRequest, HttpFailure.Classify(HttpStatusCode.BadRequest, "bad audio", day));
        Assert.Equal(FailureClass.Authentication, HttpFailure.Classify(HttpStatusCode.Forbidden, null, day));
    }

    [Fact]
    public void An_already_classified_failure_keeps_its_class()
    {
        Assert.Equal(FailureClass.Authentication, HttpFailure.Classify(new ProviderException("x") { Failure = FailureClass.Authentication }));
        Assert.Equal(FailureClass.ProviderLimit, HttpFailure.Classify(new ProviderException("x") { Failure = FailureClass.ProviderLimit }));
    }

    [Fact]
    public void Any_429_is_rate_limited_whatever_its_class()
    {
        Assert.True(HttpFailure.IsRateLimited(new ProviderException("x") { StatusCode = HttpStatusCode.TooManyRequests, Failure = FailureClass.Transient }));
        Assert.True(HttpFailure.IsRateLimited(new ProviderException("x") { StatusCode = HttpStatusCode.TooManyRequests, Failure = FailureClass.ProviderLimit }));
        Assert.True(HttpFailure.IsRateLimited(new HttpRequestException("x", null, HttpStatusCode.TooManyRequests)));
        Assert.True(new ProviderException("x") { StatusCode = HttpStatusCode.TooManyRequests }.RateLimited);

        Assert.False(new ProviderException("x") { StatusCode = HttpStatusCode.ServiceUnavailable }.RateLimited);
        Assert.False(new ProviderException("x") { Failure = FailureClass.ProviderLimit }.RateLimited);
        Assert.False(HttpFailure.IsRateLimited(new HttpRequestException("x", null, HttpStatusCode.PaymentRequired)));
        Assert.False(HttpFailure.IsRateLimited(new TimeoutException()));
        Assert.False(HttpFailure.IsRateLimited(null));
    }

    [Fact]
    public void No_network_is_told_apart_from_a_failing_service()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(FailureClass.NoConnection, HttpFailure.Classify(new HttpRequestException("dns", new SocketException((int)SocketError.HostNotFound)), ct));
        Assert.Equal(FailureClass.Transient, HttpFailure.Classify(new HttpRequestException("reset", new SocketException((int)SocketError.ConnectionReset)), ct));
        Assert.Equal(FailureClass.Authentication, HttpFailure.Classify(new HttpRequestException("no", null, HttpStatusCode.Unauthorized), ct));
        Assert.Equal(FailureClass.Transient, HttpFailure.Classify(new TimeoutException(), ct));
    }

    [Fact]
    public void Retry_after_reads_seconds_dates_and_reset_headers()
    {
        var now = new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromSeconds(30), HttpFailure.RetryAfter("30", now));
        Assert.Equal(TimeSpan.FromMinutes(5), HttpFailure.RetryAfter("Fri, 25 Sep 2026 12:05:00 GMT", now));
        Assert.Null(HttpFailure.RetryAfter("soon", now));
        Assert.Null(HttpFailure.RetryAfter("Fri, 25 Sep 2026 11:00:00 GMT", now));

        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.TryAddWithoutValidation("x-ratelimit-reset-requests", "6m0s");
        Assert.Equal(TimeSpan.FromMinutes(6), HttpFailure.RetryAfter(response, now));

        using var ms = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        ms.Headers.TryAddWithoutValidation("retry-after-ms", "1500");
        Assert.Equal(TimeSpan.FromMilliseconds(1500), HttpFailure.RetryAfter(ms, now));
    }
}
