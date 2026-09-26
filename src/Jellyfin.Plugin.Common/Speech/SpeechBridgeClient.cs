using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Common.Speech;

/// <summary>
/// The Subtitles plugin's reply to a transcription request.
/// </summary>
/// <param name="Ok">Whether it transcribed.</param>
/// <param name="Text">The words heard, in order (may be empty for silence or music).</param>
/// <param name="Language">The language heard or used, if known (two letters).</param>
/// <param name="Provider">The speech-to-text service used (<c>builtin</c>, <c>local</c>, <c>deepgram</c> …).</param>
/// <param name="Error">Why it didn't, in words safe to show.</param>
/// <param name="Failure">What kind of failure: <c>not-installed</c>, <c>not-allowed</c>, <c>not-set-up</c>,
/// <c>authentication</c>, <c>provider-limit</c>, <c>transient</c>, <c>bad-request</c>, <c>no-connection</c>.</param>
internal sealed record SpeechReply(bool Ok, string? Text, string? Language, string? Provider, string? Error, string? Failure);

/// <summary>
/// Asks the family's Subtitles plugin, if it is installed, to transcribe a short stretch of a video, through its
/// in-process entry point (JSON in and out, found by name, so no types are shared between plugins). The Subtitles
/// plugin decides which speech-to-text service is used and applies its own spending limits. Every reply that isn't a
/// transcript says why, and callers fall back to what they do without one (usually a review).
/// </summary>
internal static class SpeechBridgeClient
{
    /// <summary>The Subtitles plugin's assembly.</summary>
    public const string AssemblyName = "Jellyfin.Plugin.Subtitles";

    /// <summary>The entry point's type.</summary>
    public const string TypeName = "Jellyfin.Plugin.Subtitles.Bridge.SpeechBridge";

    /// <summary>The contract version spoken.</summary>
    public const int Version = 1;

    /// <summary>The longest stretch that may be asked for, in seconds.</summary>
    public const int MaxSeconds = 180;

    /// <summary>The longest a reply may be.</summary>
    public const int MaxReply = 256 * 1024;

    /// <summary>Gets or sets a stand-in for the entry point (tests only).</summary>
    internal static Func<string, CancellationToken, Task<string>>? Override { get; set; }

    /// <summary>
    /// Asks the Subtitles plugin for a transcript.
    /// </summary>
    /// <param name="caller">The calling plugin (<c>ingest</c>).</param>
    /// <param name="purpose">What for (<c>ingest.episode</c> …); must start with the caller.</param>
    /// <param name="videoPath">The video's full path on this server.</param>
    /// <param name="start">Where to start.</param>
    /// <param name="length">How long a stretch (at most <see cref="MaxSeconds"/>).</param>
    /// <param name="language">The expected language (two letters), or <c>null</c> to detect it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The reply; never throws for a missing plugin or a failed call.</returns>
    public static async Task<SpeechReply> TranscribeAsync(string caller, string purpose, string videoPath, TimeSpan start, TimeSpan length, string? language, CancellationToken cancellationToken)
    {
        var call = Override ?? Find();
        if (call is null)
        {
            return new SpeechReply(false, null, null, null, "The Subtitles plugin isn't installed.", "not-installed");
        }

        var request = JsonSerializer.Serialize(new
        {
            version = Version,
            caller,
            purpose,
            path = videoPath,
            start = Math.Max(0, start.TotalSeconds),
            length = Math.Clamp(length.TotalSeconds, 0, MaxSeconds),
            language,
        });
        string reply;
        try
        {
            reply = await call(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new SpeechReply(false, null, null, null, "The Subtitles plugin failed: " + ex.GetType().Name, "transient");
        }

        return Read(reply);
    }

    /// <summary>
    /// Reads a reply.
    /// </summary>
    /// <param name="reply">The reply JSON.</param>
    /// <returns>The reply.</returns>
    internal static SpeechReply Read(string? reply)
    {
        if (string.IsNullOrEmpty(reply) || reply.Length > MaxReply)
        {
            return new SpeechReply(false, null, null, null, "The Subtitles plugin's reply was empty or too large.", "transient");
        }

        try
        {
            using var doc = JsonDocument.Parse(reply);
            var root = doc.RootElement;
            var ok = root.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
            string? Text(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return ok && Text("text") is { } text
                ? new SpeechReply(true, text, Text("language"), Text("provider"), null, null)
                : new SpeechReply(false, null, null, null, Text("error") ?? "The Subtitles plugin didn't transcribe.", Text("failure") ?? "transient");
        }
        catch (JsonException)
        {
            return new SpeechReply(false, null, null, null, "The Subtitles plugin's reply wasn't valid JSON.", "transient");
        }
    }

    // The entry point in the loaded Subtitles plugin (the newest, if more than one version is loaded)
    private static Func<string, CancellationToken, Task<string>>? Find()
    {
        var method = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => string.Equals(a.GetName().Name, AssemblyName, StringComparison.Ordinal))
            .OrderByDescending(a => a.GetName().Version)
            .Select(a => a.GetType(TypeName, throwOnError: false)?.GetMethod("TranscribeAsync", BindingFlags.Public | BindingFlags.Static, [typeof(string), typeof(CancellationToken)]))
            .FirstOrDefault(m => m is not null && m.ReturnType == typeof(Task<string>));
        return method is null ? null : (json, ct) => (Task<string>)method.Invoke(null, [json, ct])!;
    }
}
