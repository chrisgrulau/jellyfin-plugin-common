using System;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.Common.Speech;
using Xunit;

namespace Jellyfin.Plugin.Common.Tests;

// The override is static, so these tests don't run in parallel with each other
[Collection("SpeechBridgeClient")]
public sealed class SpeechBridgeClientTests : IDisposable
{
    public void Dispose() => SpeechBridgeClient.Override = null;

    [Fact]
    public async Task Without_the_Subtitles_plugin_the_reply_says_so()
    {
        SpeechBridgeClient.Override = null;
        var reply = await SpeechBridgeClient.TranscribeAsync("ingest", "ingest.episode", "/media/a.mkv", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(2), null, TestContext.Current.CancellationToken);

        Assert.False(reply.Ok);
        Assert.Equal("not-installed", reply.Failure);
    }

    [Fact]
    public async Task The_request_is_version_1_JSON_with_the_stretch_capped_and_the_transcript_read_back()
    {
        string? sent = null;
        SpeechBridgeClient.Override = (json, _) =>
        {
            sent = json;
            return Task.FromResult("{\"version\":1,\"ok\":true,\"text\":\"Where were you last night?\",\"language\":\"en\",\"provider\":\"builtin\"}");
        };

        var reply = await SpeechBridgeClient.TranscribeAsync("ingest", "ingest.episode", "/media/a.mkv", TimeSpan.FromSeconds(-3), TimeSpan.FromMinutes(10), "en", TestContext.Current.CancellationToken);

        Assert.True(reply.Ok);
        Assert.Equal("Where were you last night?", reply.Text);
        Assert.Equal("builtin", reply.Provider);
        using var doc = JsonDocument.Parse(sent!);
        Assert.Equal(1, doc.RootElement.GetProperty("version").GetInt32());
        Assert.Equal("/media/a.mkv", doc.RootElement.GetProperty("path").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("start").GetDouble());
        Assert.Equal(SpeechBridgeClient.MaxSeconds, doc.RootElement.GetProperty("length").GetDouble());
    }

    [Theory]
    [InlineData("{\"version\":1,\"ok\":false,\"error\":\"Not allowed.\",\"failure\":\"not-allowed\"}", "not-allowed")]
    [InlineData("not json", "transient")]
    [InlineData("", "transient")]
    [InlineData("{\"ok\":true}", "transient")]
    public void Failures_and_bad_replies_are_read_safely(string json, string failure)
    {
        var reply = SpeechBridgeClient.Read(json);
        Assert.False(reply.Ok);
        Assert.Equal(failure, reply.Failure);
    }

    [Fact]
    public async Task A_throwing_plugin_is_a_failed_reply_not_an_exception()
    {
        SpeechBridgeClient.Override = (_, _) => throw new InvalidOperationException("boom");

        var reply = await SpeechBridgeClient.TranscribeAsync("ingest", "ingest.episode", "/media/a.mkv", TimeSpan.Zero, TimeSpan.FromMinutes(2), null, TestContext.Current.CancellationToken);

        Assert.False(reply.Ok);
        Assert.Equal("transient", reply.Failure);
    }
}
