using System;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.Common.Ai;
using Xunit;

namespace Jellyfin.Plugin.Common.Tests;

// The override is static, so these tests don't run in parallel with each other
[Collection("AiBridgeClient")]
public sealed class AiBridgeClientTests : IDisposable
{
    public void Dispose() => AiBridgeClient.Override = null;

    [Fact]
    public async Task Without_the_AI_plugin_the_reply_says_so()
    {
        AiBridgeClient.Override = null;
        var reply = await AiBridgeClient.AskAsync("ingest", "ingest.match", "Pick one.", new { }, new { type = "object" }, 1024, "low", TestContext.Current.CancellationToken);

        Assert.False(reply.Ok);
        Assert.Equal("not-installed", reply.Failure);
    }

    [Fact]
    public async Task The_request_is_sent_as_version_1_JSON_and_the_answer_read_back()
    {
        string? sent = null;
        AiBridgeClient.Override = (json, _) =>
        {
            sent = json;
            return Task.FromResult("{\"version\":1,\"ok\":true,\"answer\":{\"choice\":1},\"model\":\"claude-opus-5-5\"}");
        };

        var reply = await AiBridgeClient.AskAsync("ingest", "ingest.match", "Pick one.", new { file = "x" }, new { type = "object" }, 1024, "low", TestContext.Current.CancellationToken);

        Assert.True(reply.Ok);
        Assert.Equal(1, reply.Answer!.Value.GetProperty("choice").GetInt32());
        Assert.Equal("claude-opus-5-5", reply.Model);
        using var doc = JsonDocument.Parse(sent!);
        Assert.Equal(1, doc.RootElement.GetProperty("version").GetInt32());
        Assert.Equal("ingest.match", doc.RootElement.GetProperty("purpose").GetString());
        Assert.Equal("x", doc.RootElement.GetProperty("data").GetProperty("file").GetString());
    }

    [Theory]
    [InlineData("{\"version\":1,\"ok\":false,\"error\":\"Not allowed.\",\"failure\":\"not-allowed\"}", "not-allowed")]
    [InlineData("not json", "transient")]
    [InlineData("", "transient")]
    [InlineData("{\"ok\":true}", "transient")]
    public void Failures_and_bad_replies_are_read_safely(string json, string failure)
    {
        var reply = AiBridgeClient.Read(json);
        Assert.False(reply.Ok);
        Assert.Equal(failure, reply.Failure);
    }

    [Fact]
    public async Task A_throwing_plugin_is_a_failed_reply_not_an_exception()
    {
        AiBridgeClient.Override = (_, _) => throw new InvalidOperationException("boom");

        var reply = await AiBridgeClient.AskAsync("ingest", "ingest.match", "x", new { }, new { }, 1024, "low", TestContext.Current.CancellationToken);

        Assert.False(reply.Ok);
        Assert.Equal("transient", reply.Failure);
    }
}
