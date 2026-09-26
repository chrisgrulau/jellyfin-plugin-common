using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Common.Resilience;
using Jellyfin.Plugin.Common.Secrets;
using Xunit;

namespace Jellyfin.Plugin.Common.Tests;

public class ReviewPass2Tests
{
    // COM-03: absurd waits are bounded, never an overflow
    [Theory]
    [InlineData("retry-after-ms", "999999999999999999999999999")]
    [InlineData("x-ratelimit-reset-requests", "99999999999999999h")]
    [InlineData("x-ratelimit-reset", "99999999999999999999m99999999999999999999s")]
    public void Absurd_waits_are_bounded(string header, string value)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.TryAddWithoutValidation(header, value);

        Assert.Equal(HttpFailure.MaxWait, HttpFailure.RetryAfter(response, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void Ordinary_waits_are_read_as_before()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.TryAddWithoutValidation("x-ratelimit-reset-requests", "6m1.5s");
        Assert.Equal(TimeSpan.FromSeconds(361.5), HttpFailure.RetryAfter(response, DateTimeOffset.UnixEpoch));
        Assert.Equal(HttpFailure.MaxWait, HttpFailure.RetryAfter("1000000000000", DateTimeOffset.UnixEpoch));
        Assert.Null(HttpFailure.RetryAfter("-5", DateTimeOffset.UnixEpoch));
    }

    // COM-03: only provider-specific limit wording pauses a provider
    [Theory]
    [InlineData(400, "insufficient audio length", (int)FailureClass.BadRequest)]
    [InlineData(400, "credits are shown at the end", (int)FailureClass.BadRequest)]
    [InlineData(403, "billing address missing", (int)FailureClass.Authentication)]
    [InlineData(400, "{\"error\":{\"code\":\"billing_hard_limit_reached\"}}", (int)FailureClass.ProviderLimit)]
    [InlineData(429, "RESOURCE_EXHAUSTED: quota exceeded", (int)FailureClass.ProviderLimit)]
    public void Limits_are_recognised_by_their_provider_wording(int status, string body, int expected)
        => Assert.Equal((FailureClass)expected, HttpFailure.Classify((HttpStatusCode)status, body));

    // COM-03: the caller's own cancellation isn't a service failure
    [Fact]
    public void A_cancellation_the_caller_asked_for_is_rethrown()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => HttpFailure.Classify(new TaskCanceledException(), cts.Token));
        Assert.Equal(FailureClass.Transient, HttpFailure.Classify(new TaskCanceledException(), TestContext.Current.CancellationToken));
    }

    // COM-04: a key straddling the cut is still removed
    [Fact]
    public void A_key_across_the_cut_is_redacted()
    {
        const string key = "sk-test-0123456789abcdefghij";
        var text = new string('x', Redaction.MaxLength - 4) + " " + key + " tail";

        var safe = Redaction.Redact(text, [key]);

        Assert.DoesNotContain("sk-t", safe, StringComparison.Ordinal);
        Assert.True(safe.Length <= Redaction.MaxLength + 1);
    }

    // COM-05: one definition of "local"
    [Theory]
    [InlineData("http://localhost:8000/v1", true)]
    [InlineData("http://127.0.0.1:9000", true)]
    [InlineData("http://[::1]:8000", true)]
    [InlineData("http://10.1.2.3", true)]
    [InlineData("http://172.20.0.5", true)]
    [InlineData("http://192.168.1.10", true)]
    [InlineData("http://169.254.1.1", true)]
    [InlineData("http://[fd00::1]", true)]
    [InlineData("http://[fe80::1]", true)]
    [InlineData("http://[::ffff:192.168.1.10]", true)]
    [InlineData("http://nas.local:8000", true)]
    [InlineData("http://172.32.0.1", false)]
    [InlineData("https://api.openai.com/v1", false)]
    [InlineData("http://8.8.8.8", false)]
    [InlineData("not a url", false)]
    public void Local_addresses_are_recognised(string address, bool local)
        => Assert.Equal(local, NetworkAddress.IsLocal(address));
}
