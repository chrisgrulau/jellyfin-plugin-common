using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Common.Ai;

/// <summary>
/// The AI plugin's reply.
/// </summary>
/// <param name="Ok">Whether it answered.</param>
/// <param name="Answer">The answer (matching the request's schema), when it did. Always check it against what was offered.</param>
/// <param name="Model">The model that answered.</param>
/// <param name="Error">Why it didn't, in words safe to show.</param>
/// <param name="Failure">What kind of failure: <c>not-installed</c>, <c>not-allowed</c>, <c>authentication</c>,
/// <c>provider-limit</c>, <c>transient</c>, <c>bad-request</c>, <c>no-connection</c>.</param>
internal sealed record AiReply(bool Ok, JsonElement? Answer, string? Model, string? Error, string? Failure);

/// <summary>
/// Asks the family's AI plugin, if it is installed, through its in-process entry point (JSON in and out, found by name,
/// so no types are shared between plugins). Every reply that isn't a usable answer says why, and callers fall back to
/// what they do without AI (usually a review).
/// </summary>
internal static class AiBridgeClient
{
    /// <summary>The AI plugin's assembly.</summary>
    public const string AssemblyName = "Jellyfin.Plugin.Ai";

    /// <summary>The entry point's type.</summary>
    public const string TypeName = "Jellyfin.Plugin.Ai.Bridge.AiBridge";

    /// <summary>The contract version spoken.</summary>
    public const int Version = 1;

    /// <summary>The longest a reply may be.</summary>
    public const int MaxReply = 256 * 1024;

    /// <summary>
    /// The largest <c>data</c> the AI plugin accepts, in UTF-8 bytes of its JSON (see <see cref="BridgeJson.Bytes"/>).
    /// Callers with a lot to send shrink it to fit (the least useful items first) rather than being refused.
    /// </summary>
    public const int MaxDataBytes = 64 * 1024;

    /// <summary>Gets or sets a stand-in for the entry point (tests only).</summary>
    internal static Func<string, CancellationToken, Task<string>>? Override { get; set; }

    /// <summary>
    /// Asks the AI plugin.
    /// </summary>
    /// <param name="caller">The calling plugin (<c>ingest</c> or <c>subtitles</c>).</param>
    /// <param name="purpose">What for (<c>ingest.match</c> …); must start with the caller.</param>
    /// <param name="instructions">Fixed instructions.</param>
    /// <param name="data">The data to decide on (untrusted content goes here only).</param>
    /// <param name="schema">The JSON schema the answer must match.</param>
    /// <param name="maxOutputTokens">Output allowance (thinking included).</param>
    /// <param name="effort"><c>low</c>, <c>medium</c> or <c>high</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The reply; never throws for a missing plugin or a failed call.</returns>
    public static async Task<AiReply> AskAsync(string caller, string purpose, string instructions, object data, object schema, int maxOutputTokens, string effort, CancellationToken cancellationToken)
    {
        var call = Override ?? Find();
        if (call is null)
        {
            return new AiReply(false, null, null, "The AI plugin isn't installed.", "not-installed");
        }

        var request = JsonSerializer.Serialize(new { version = Version, caller, purpose, instructions, data, schema, maxOutputTokens, effort }, BridgeJson.Options);
        string reply;
        try
        {
            reply = await call(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AiReply(false, null, null, "The AI plugin failed: " + ex.GetType().Name, "transient");
        }

        return Read(reply);
    }

    /// <summary>
    /// Reads a reply.
    /// </summary>
    /// <param name="reply">The reply JSON.</param>
    /// <returns>The reply.</returns>
    internal static AiReply Read(string? reply)
    {
        if (string.IsNullOrEmpty(reply) || reply.Length > MaxReply)
        {
            return new AiReply(false, null, null, "The AI plugin's reply was empty or too large.", "transient");
        }

        try
        {
            using var doc = JsonDocument.Parse(reply);
            var root = doc.RootElement;
            var ok = root.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
            string? Text(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return ok && root.TryGetProperty("answer", out var answer)
                ? new AiReply(true, answer.Clone(), Text("model"), null, null)
                : new AiReply(false, null, null, Text("error") ?? "The AI plugin didn't answer.", Text("failure") ?? "transient");
        }
        catch (JsonException)
        {
            return new AiReply(false, null, null, "The AI plugin's reply wasn't valid JSON.", "transient");
        }
    }

    // The entry point in the loaded AI plugin (the newest, if more than one version is loaded)
    private static Func<string, CancellationToken, Task<string>>? Find()
    {
        var method = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => string.Equals(a.GetName().Name, AssemblyName, StringComparison.Ordinal))
            .OrderByDescending(a => a.GetName().Version)
            .Select(a => a.GetType(TypeName, throwOnError: false)?.GetMethod("AskAsync", BindingFlags.Public | BindingFlags.Static, [typeof(string), typeof(CancellationToken)]))
            .FirstOrDefault(m => m is not null && m.ReturnType == typeof(Task<string>));
        return method is null ? null : (json, ct) => (Task<string>)method.Invoke(null, [json, ct])!;
    }
}
