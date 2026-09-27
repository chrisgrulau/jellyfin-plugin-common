using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Common.Costs;

/// <summary>
/// The AI plugin's reply to a spending request.
/// </summary>
/// <param name="Ok">Whether it was done (reserved, settled, released or recorded).</param>
/// <param name="ReservationId">The reservation, for a reserve that was allowed.</param>
/// <param name="Error">Why not, in words safe to show (for a refused reserve, the limit reached).</param>
/// <param name="Failure">What kind of failure: <c>not-installed</c>, <c>not-allowed</c>, <c>unsupported-version</c>,
/// <c>bad-request</c>, <c>provider-limit</c> (the limits refuse it), <c>transient</c>.</param>
internal sealed record SpendingReply(bool Ok, Guid? ReservationId, string? Error, string? Failure);

/// <summary>
/// This month's spending as the AI plugin, which owns the budget, sees it: for a settings page that shows it rather than
/// its own settings.
/// </summary>
/// <param name="Ok">Whether it answered.</param>
/// <param name="Currency">The budget's currency.</param>
/// <param name="Limit">The overall monthly limit, or <c>null</c> for no limit (0 = no paid use).</param>
/// <param name="Spent">Spent this month, all providers (open reservations included), or <c>null</c> if some of it can't be
/// converted.</param>
/// <param name="PerProvider">Spent this month per provider id (the AI plugin's ids).</param>
/// <param name="ProviderLimits">Each provider's own monthly limit, where it has one.</param>
/// <param name="RatesDate">The date of the exchange rates in use (<c>yyyy-MM-dd</c>), if any.</param>
/// <param name="RatesFresh">Whether those rates are recent enough to use.</param>
/// <param name="Error">Why it didn't answer.</param>
/// <param name="Failure">What kind of failure (as <see cref="SpendingReply.Failure"/>).</param>
internal sealed record SpendingSummaryReply(
    bool Ok,
    string? Currency,
    decimal? Limit,
    decimal? Spent,
    IReadOnlyDictionary<string, decimal> PerProvider,
    IReadOnlyDictionary<string, decimal> ProviderLimits,
    string? RatesDate,
    bool RatesFresh,
    string? Error,
    string? Failure);

/// <summary>
/// Reserves and settles paid calls against the budget the family's AI plugin owns, if it is installed and allows the
/// caller, through its in-process spending entry point (JSON in and out, found by name, so no types are shared between
/// plugins). The AI plugin keeps the one ledger, the currency and every limit; the caller prices its own calls (it has
/// the price table) and the AI plugin converts them with its exchange rates.
/// <para>
/// Request (version 1): <c>{"version":1,"caller":"subtitles","op":…}</c> with, per <c>op</c>:
/// <c>reserve</c> <c>purpose</c>, <c>provider</c>, <c>estimate</c> <c>{"amount":0.01,"currency":"USD"}</c>;
/// <c>settle</c> <c>reservationId</c>, <c>actual</c> (money); <c>release</c> <c>reservationId</c>;
/// <c>carry</c> <c>provider</c>, <c>amount</c> (money), <c>month</c> (<c>yyyy-MM</c>): the caller's own spending this
/// month with that provider in that currency, which replaces what it reported before;
/// <c>summary</c> (nothing else). Reply: <c>{"version":1,"ok":true,…}</c> (<c>reservationId</c> for a reserve; the
/// figures for a summary) or <c>{"version":1,"ok":false,"error":"…","failure":"…"}</c>.
/// </para>
/// Never throws for a missing plugin or a failed call: every reply that isn't a success says why.
/// </summary>
internal sealed class SpendingBridgeClient
{
    /// <summary>The AI plugin's assembly.</summary>
    public const string AssemblyName = "Jellyfin.Plugin.Ai";

    /// <summary>The entry point's type.</summary>
    public const string TypeName = "Jellyfin.Plugin.Ai.Bridge.SpendingBridge";

    /// <summary>The entry point's method: <c>Task&lt;string&gt; HandleAsync(string, CancellationToken)</c>, public and static.</summary>
    public const string MethodName = "HandleAsync";

    /// <summary>The contract version spoken.</summary>
    public const int Version = 1;

    /// <summary>The longest a reply may be.</summary>
    public const int MaxReply = 64 * 1024;

    /// <summary>The largest single amount accepted, in any currency.</summary>
    public const decimal MaxAmount = 100_000m;

    /// <summary>Deepgram speech-to-text, as the AI plugin's budget names it.</summary>
    public const string Deepgram = "deepgram";

    /// <summary>OpenAI speech-to-text, as the AI plugin's budget names it (apart from OpenAI's text models, <c>openai</c>).</summary>
    public const string OpenAiSpeech = "openai-speech";

    /// <summary>
    /// How long a reservation made through the entry point may stay open. After that it is settled at its estimate (the
    /// caller stopped or crashed mid-call), and a late settle or release changes nothing.
    /// </summary>
    public static readonly TimeSpan ReservationLifetime = TimeSpan.FromHours(1);

    private readonly string _caller;
    private readonly Func<string, CancellationToken, Task<string>>? _entry;

    /// <summary>
    /// Initializes a new instance of the <see cref="SpendingBridgeClient"/> class.
    /// </summary>
    /// <param name="caller">The calling plugin (<c>subtitles</c>).</param>
    /// <param name="entry">A stand-in for the entry point (tests only); by default the loaded AI plugin's.</param>
    public SpendingBridgeClient(string caller, Func<string, CancellationToken, Task<string>>? entry = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caller);
        _caller = caller;
        _entry = entry;
    }

    /// <summary>Gets a value indicating whether an AI plugin with the spending entry point is loaded (it may still refuse the caller).</summary>
    public bool IsInstalled => (_entry ?? Find()) is not null;

    /// <summary>
    /// Whether a failure means the AI plugin doesn't keep this caller's budget (not installed, too old or new, or not
    /// allowed to), so the caller uses its own ledger and settings instead.
    /// </summary>
    /// <param name="failure">The failure name.</param>
    /// <returns><c>true</c> to fall back to the caller's own budget.</returns>
    public static bool MeansOwnBudget(string? failure) => failure is "not-installed" or "unsupported-version" or "not-allowed" or "off";

    /// <summary>
    /// Reserves the estimated cost of a call.
    /// </summary>
    /// <param name="purpose">What for (<c>subtitles.sync</c> …).</param>
    /// <param name="provider">The provider, as the AI plugin's budget names it (see <see cref="Deepgram"/>).</param>
    /// <param name="estimate">The estimate, as the provider charges it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The reply.</returns>
    public Task<SpendingReply> ReserveAsync(string purpose, string provider, Money estimate, CancellationToken cancellationToken)
        => SendAsync(new { version = Version, caller = _caller, op = "reserve", purpose, provider, estimate = Json(estimate) }, cancellationToken);

    /// <summary>
    /// Settles a reservation at what the call cost.
    /// </summary>
    /// <param name="reservationId">The reservation.</param>
    /// <param name="actual">What it cost, as charged.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The reply.</returns>
    public Task<SpendingReply> SettleAsync(Guid reservationId, Money actual, CancellationToken cancellationToken)
        => SendAsync(new { version = Version, caller = _caller, op = "settle", reservationId = reservationId.ToString("D"), actual = Json(actual) }, cancellationToken);

    /// <summary>
    /// Releases a reservation (nothing was charged).
    /// </summary>
    /// <param name="reservationId">The reservation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The reply.</returns>
    public Task<SpendingReply> ReleaseAsync(Guid reservationId, CancellationToken cancellationToken)
        => SendAsync(new { version = Version, caller = _caller, op = "release", reservationId = reservationId.ToString("D") }, cancellationToken);

    /// <summary>
    /// Reports what the caller spent this month on its own ledger with one provider in one currency, so the AI plugin's
    /// month includes it. Sending it again replaces it (it is never added twice); 0 removes it.
    /// </summary>
    /// <param name="provider">The provider, as the AI plugin's budget names it.</param>
    /// <param name="amount">The month's total, as charged.</param>
    /// <param name="month">The month (<c>yyyy-MM</c>, the server's local time): ignored by the AI plugin if it's no longer that month.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The reply.</returns>
    public Task<SpendingReply> CarryAsync(string provider, Money amount, string month, CancellationToken cancellationToken)
        => SendAsync(new { version = Version, caller = _caller, op = "carry", provider, amount = Json(amount), month }, cancellationToken);

    /// <summary>
    /// This month's spending and the limits, as the AI plugin sees them.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The summary.</returns>
    public async Task<SpendingSummaryReply> SummaryAsync(CancellationToken cancellationToken)
    {
        var (reply, failed) = await CallAsync(new { version = Version, caller = _caller, op = "summary" }, cancellationToken).ConfigureAwait(false);
        return failed is { } f ? Failed(f.Error, f.Failure) : ReadSummary(reply);
    }

    /// <summary>
    /// The month as the carry request names it.
    /// </summary>
    /// <param name="now">The server's local time.</param>
    /// <returns><c>yyyy-MM</c>.</returns>
    public static string MonthOf(DateTimeOffset now) => now.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads a reply to reserve, settle, release or carry.
    /// </summary>
    /// <param name="reply">The reply JSON.</param>
    /// <returns>The reply.</returns>
    internal static SpendingReply Read(string? reply)
    {
        if (string.IsNullOrEmpty(reply) || reply.Length > MaxReply)
        {
            return new SpendingReply(false, null, "The AI plugin's reply was empty or too large.", "transient");
        }

        try
        {
            using var doc = JsonDocument.Parse(reply);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new SpendingReply(false, null, "The AI plugin's reply wasn't a JSON object.", "transient");
            }

            if (root.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True)
            {
                var id = Text(root, "reservationId") is { } s && Guid.TryParse(s, out var g) ? g : (Guid?)null;
                return new SpendingReply(true, id, null, null);
            }

            return new SpendingReply(false, null, Text(root, "error") ?? "The AI plugin refused.", Text(root, "failure") ?? "transient");
        }
        catch (JsonException)
        {
            return new SpendingReply(false, null, "The AI plugin's reply wasn't valid JSON.", "transient");
        }
    }

    /// <summary>
    /// Reads a reply to a summary.
    /// </summary>
    /// <param name="reply">The reply JSON.</param>
    /// <returns>The summary.</returns>
    internal static SpendingSummaryReply ReadSummary(string? reply)
    {
        var basic = Read(reply);
        if (!basic.Ok)
        {
            return Failed(basic.Error, basic.Failure);
        }

        using var doc = JsonDocument.Parse(reply!);
        var root = doc.RootElement;
        if (Text(root, "currency") is not { } c || CurrencyCode.Normalise(c) is not { } currency)
        {
            return Failed("The AI plugin's summary has no currency.", "transient");
        }

        return new SpendingSummaryReply(
            true,
            currency,
            Amount(root, "limit"),
            Amount(root, "spent"),
            Amounts(root, "perProvider"),
            Amounts(root, "providerLimits"),
            Text(root, "ratesDate"),
            root.TryGetProperty("ratesFresh", out var fresh) && fresh.ValueKind == JsonValueKind.True,
            null,
            null);
    }

    // Money as the contract writes it: amount and ISO 4217 code
    private static object Json(Money m) => new { amount = m.Amount, currency = m.Currency };

    private static SpendingSummaryReply Failed(string? error, string? failure)
        => new(false, null, null, null, new Dictionary<string, decimal>(), new Dictionary<string, decimal>(), null, false, error, failure);

    private static string? Text(JsonElement root, string name)
        => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static decimal? Amount(JsonElement root, string name)
        => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d : null;

    private static Dictionary<string, decimal> Amounts(JsonElement root, string name)
    {
        var map = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in v.EnumerateObject().Take(64))
            {
                if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetDecimal(out var d) && p.Name.Length <= 64)
                {
                    map[p.Name] = d;
                }
            }
        }

        return map;
    }

    private async Task<SpendingReply> SendAsync(object request, CancellationToken cancellationToken)
    {
        var (reply, failed) = await CallAsync(request, cancellationToken).ConfigureAwait(false);
        return failed ?? Read(reply);
    }

    private async Task<(string? Reply, SpendingReply? Failed)> CallAsync(object request, CancellationToken cancellationToken)
    {
        var call = _entry ?? Find();
        if (call is null)
        {
            return (null, new SpendingReply(false, null, "The AI plugin isn't installed.", "not-installed"));
        }

        try
        {
            return (await call(JsonSerializer.Serialize(request, BridgeJson.Options), cancellationToken).ConfigureAwait(false), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, new SpendingReply(false, null, "The AI plugin failed: " + ex.GetType().Name, "transient"));
        }
    }

    // The entry point in the loaded AI plugin (the newest, if more than one version is loaded). An AI plugin from before
    // this contract has no such type, and counts as not installed
    private static Func<string, CancellationToken, Task<string>>? Find()
    {
        var method = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => string.Equals(a.GetName().Name, AssemblyName, StringComparison.Ordinal))
            .OrderByDescending(a => a.GetName().Version)
            .Select(a => a.GetType(TypeName, throwOnError: false)?.GetMethod(MethodName, BindingFlags.Public | BindingFlags.Static, [typeof(string), typeof(CancellationToken)]))
            .FirstOrDefault(m => m is not null && m.ReturnType == typeof(Task<string>));
        return method is null ? null : (json, ct) => (Task<string>)method.Invoke(null, [json, ct])!;
    }
}
