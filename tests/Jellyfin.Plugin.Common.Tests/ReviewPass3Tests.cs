using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.Common.Ai;
using Jellyfin.Plugin.Common.Costs;
using Jellyfin.Plugin.Common.Secrets;
using Xunit;

namespace Jellyfin.Plugin.Common.Tests;

// Review pass 3: FAM-02 (text crosses the bridges unescaped), COM-06 (unreadable key file), COM-07 (locked ledger).
[Collection("AiBridgeClient")]
public sealed class ReviewPass3Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "common-pass3-" + Guid.NewGuid().ToString("N"));

    public ReviewPass3Tests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        AiBridgeClient.Override = null;
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

    // Permission tests need a non-root Unix account (root reads anything)
    private static void NeedsPermissions() => Assert.SkipWhen(OperatingSystem.IsWindows() || Environment.UserName == "root", "Needs a non-root Unix account.");

    [Fact]
    public void Letters_in_any_script_are_sent_as_themselves_but_markup_stays_escaped()
    {
        var json = JsonSerializer.Serialize(new { text = "Привет </data> 日本語 & more" }, BridgeJson.Options);

        Assert.Contains("Привет", json, StringComparison.Ordinal);
        Assert.Contains("日本語", json, StringComparison.Ordinal);
        Assert.DoesNotContain("</data>", json, StringComparison.Ordinal);
        Assert.DoesNotContain(" & ", json, StringComparison.Ordinal);
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(json), BridgeJson.Bytes(new { text = "Привет </data> 日本語 & more" }));
    }

    [Fact]
    public async Task The_AI_request_carries_cyrillic_unescaped()
    {
        string? sent = null;
        AiBridgeClient.Override = (json, _) =>
        {
            sent = json;
            return Task.FromResult("{\"ok\":true,\"answer\":{},\"model\":\"m\"}");
        };

        await AiBridgeClient.AskAsync("subtitles", "subtitles.lines", "x", new { lines = new[] { "Где ты был?" } }, new { }, 100, "low", TestContext.Current.CancellationToken);

        Assert.Contains("Где ты был?", sent, StringComparison.Ordinal);
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void An_unreadable_key_file_is_reported_not_thrown_and_saving_replaces_it()
    {
        NeedsPermissions();
        var path = Path.Combine(_dir, "keys.json");
        new KeyFile(path, ["openai"]).Set("openai", "sk-test-0123456789abcdef");
        File.SetUnixFileMode(path, UnixFileMode.None);
        var file = new KeyFile(path, ["openai"]);

        Assert.False(file.Status()["openai"]);
        Assert.Null(file.Get("openai"));
        Assert.NotNull(file.Problem);

        file.Set("openai", "sk-test-9876543210fedcba");

        Assert.Equal("sk-test-9876543210fedcba", file.Get("openai"));
        Assert.Null(file.Problem);
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void A_ledger_that_cant_be_read_refuses_this_time_and_is_neither_moved_nor_overwritten()
    {
        NeedsPermissions();
        var path = Path.Combine(_dir, "spend.json");
        var rates = new ExchangeRates(new DateOnly(2026, 9, 25), "test", new Dictionary<string, decimal> { ["USD"] = 1.10m, ["AUD"] = 1.65m });
        var limits = new SpendLimits("AUD", 5m, new Dictionary<string, decimal>(), 0m);
        var first = new SpendLedger(path);
        var id = first.TryReserve("deepgram", "p", Money.Of(1m, "USD"), limits, rates).ReservationId!.Value;
        first.Settle(id, Money.Of(1m, "USD"));
        var before = File.ReadAllBytes(path);
        File.SetUnixFileMode(path, UnixFileMode.None);

        var locked = new SpendLedger(path);
        var refused = locked.TryReserve("deepgram", "p", Money.Of(1m, "USD"), limits, rates);

        Assert.False(refused.Allowed);
        Assert.Contains("can't be read right now", refused.Refusal, StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(_dir));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.Equal(before, File.ReadAllBytes(path));

        // Readable again: the history is still there and counts
        Assert.True(locked.TryReserve("deepgram", "p", Money.Of(1m, "USD"), limits, rates).Allowed);
        Assert.Equal(2, JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetArrayLength());
    }
}
