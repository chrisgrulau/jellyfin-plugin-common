using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Common.Storage;

namespace Jellyfin.Plugin.Common.Costs;

/// <summary>
/// Keeps the latest good exchange rates: fetched from the ECB's one fixed address at most about once a day, checked by
/// <see cref="EcbRates.Parse"/>, and saved in the plugin's data folder so they survive restarts. A failed or odd fetch
/// keeps the last good rates; rates older than <see cref="ExchangeRates.MaxAgeDays"/> are treated as unknown by
/// everything that uses them.
/// </summary>
internal sealed class ExchangeRateStore : IDisposable
{
    /// <summary>How long after a fetch another is tried.</summary>
    public static readonly TimeSpan RefreshAfter = TimeSpan.FromHours(20);

    private readonly string _path;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private ExchangeRates? _rates;
    private DateTimeOffset _lastAttempt = DateTimeOffset.MinValue;
    private bool _loaded;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExchangeRateStore"/> class.
    /// </summary>
    /// <param name="path">Where the last good rates are saved.</param>
    /// <param name="clock">Clock.</param>
    public ExchangeRateStore(string path, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Gets the latest good rates, if any (they may be stale; check <see cref="ExchangeRates.IsFresh"/>).</summary>
    public ExchangeRates? Current
    {
        get
        {
            EnsureLoaded();
            return _rates;
        }
    }

    /// <summary>
    /// Fetches new rates if the last attempt was long enough ago. Never throws for network or content problems.
    /// </summary>
    /// <param name="http">HTTP client.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The latest good rates, if any.</returns>
    public async Task<ExchangeRates?> RefreshAsync(HttpClient http, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        EnsureLoaded();
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _clock.GetUtcNow();
            if (now - _lastAttempt < RefreshAfter && _rates is not null && _rates.IsFresh(DateOnly.FromDateTime(now.UtcDateTime)))
            {
                return _rates;
            }

            _lastAttempt = now;
            try
            {
                using var response = await http.GetAsync(EcbRates.DailyUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode || response.RequestMessage?.RequestUri?.Host != EcbRates.DailyUrl.Host)
                {
                    return _rates;
                }

                var body = await ReadCappedAsync(response, cancellationToken).ConfigureAwait(false);
                if (body is not null && EcbRates.Parse(body) is { } fresh && (_rates is null || fresh.Date >= _rates.Date))
                {
                    _rates = fresh;
                    Save(fresh);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                // Offline or blocked: the last good rates stay
            }

            return _rates;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _lock.Dispose();

    private static async Task<string?> ReadCappedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > EcbRates.MaxBytes)
        {
            return null;
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var buffer = new byte[EcbRates.MaxBytes + 1];
            var total = 0;
            int read;
            while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
            }

            return total > EcbRates.MaxBytes ? null : Encoding.UTF8.GetString(buffer, 0, total);
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;

        // Missing, damaged or unreadable all mean no saved rates: they are fetched again (and a good fetch replaces the file)
        if (JsonFile.Read<Saved>(_path).Value is { Rates: { Count: > 0 } perEuro } saved
            && DateOnly.TryParseExact(saved.Date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date))
        {
            var valid = new Dictionary<string, decimal>(StringComparer.Ordinal);
            foreach (var (code, rate) in perEuro)
            {
                if (CurrencyCode.Normalise(code) is { } c && rate > 0 && rate < 1_000_000m)
                {
                    valid[c] = rate;
                }
            }

            _rates = new ExchangeRates(date, saved.Source ?? EcbRates.SourceName, valid);
        }
    }

    private void Save(ExchangeRates rates)
    {
        try
        {
            var saved = new Saved { Date = rates.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), Source = rates.Source, Rates = new Dictionary<string, decimal>(rates.PerEuro) };
            JsonFile.WriteAtomic(_path, saved);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Kept in memory
        }
    }

    private sealed class Saved
    {
        public string? Date { get; set; }

        public string? Source { get; set; }

        public Dictionary<string, decimal>? Rates { get; set; }
    }
}
