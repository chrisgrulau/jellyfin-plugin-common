using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Common.Secrets;

namespace Jellyfin.Plugin.Common.Resilience;

/// <summary>
/// Sends a request to an external provider the way every provider call in the plugin family should: failures are
/// classified by <see cref="HttpFailure"/> and thrown as a <see cref="ProviderException"/> carrying the wait the provider
/// asked for, keys are removed from every message, and no body (reply or error) is ever read beyond a size limit.
/// </summary>
internal static class ProviderHttp
{
    /// <summary>The most characters of a provider's error reply kept in <see cref="ProviderException.Detail"/>.</summary>
    public const int DetailMaxLength = 500;

    private const int ChunkBytes = 16 * 1024;

    /// <summary>
    /// Sends a request and returns the reply body as text (UTF-8).
    /// </summary>
    /// <param name="http">HTTP client.</param>
    /// <param name="request">The request (the caller keeps ownership and disposes it).</param>
    /// <param name="maxBytes">The largest body read, for a reply or an error.</param>
    /// <param name="secrets">The keys in use, removed from every message.</param>
    /// <param name="cancellationToken">The caller's token; its cancellation is rethrown as <see cref="OperationCanceledException"/>.</param>
    /// <returns>The body of a successful reply.</returns>
    /// <exception cref="ProviderException">The call failed, or the reply was larger than <paramref name="maxBytes"/>.</exception>
    public static async Task<string> SendAsync(HttpClient http, HttpRequestMessage request, int maxBytes, IEnumerable<string?> secrets, CancellationToken cancellationToken)
    {
        var bytes = await SendForBytesAsync(http, request, maxBytes, secrets, cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// Sends a request and returns the reply body as bytes (downloads such as archives).
    /// </summary>
    /// <param name="http">HTTP client.</param>
    /// <param name="request">The request (the caller keeps ownership and disposes it).</param>
    /// <param name="maxBytes">The largest body read, for a reply or an error.</param>
    /// <param name="secrets">The keys in use, removed from every message.</param>
    /// <param name="cancellationToken">The caller's token; its cancellation is rethrown as <see cref="OperationCanceledException"/>.</param>
    /// <returns>The body of a successful reply.</returns>
    /// <exception cref="ProviderException">The call failed, or the reply was larger than <paramref name="maxBytes"/>.</exception>
    public static async Task<byte[]> SendForBytesAsync(HttpClient http, HttpRequestMessage request, int maxBytes, IEnumerable<string?> secrets, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        var keys = secrets.ToList();
        var host = request.RequestUri?.Host ?? "The provider";

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException)
        {
            // A cancellation the caller asked for is rethrown by Classify; anything else is the provider's (or the network's)
            var failure = HttpFailure.Classify(ex, cancellationToken);
            throw new ProviderException(host + " couldn't be reached: " + Redaction.Redact(ex.Message, keys), ex) { Failure = failure };
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                // Only as much of the error as fits the limit is read: enough to classify and show it, never the whole body
                var (errorBytes, _) = await ReadAsync(response, maxBytes, host, keys, cancellationToken).ConfigureAwait(false);
                var body = Encoding.UTF8.GetString(errorBytes);
                var status = (int)response.StatusCode;
                var safe = Redaction.Redact(body, keys);
                var wait = HttpFailure.RetryAfter(response, DateTimeOffset.UtcNow);
                throw new ProviderException(string.Create(CultureInfo.InvariantCulture, $"{host} answered HTTP {status}: {safe}"))
                {
                    Failure = HttpFailure.Classify(response.StatusCode, body, wait),
                    RetryAfter = wait,
                    StatusCode = response.StatusCode,
                    Detail = DetailOf(safe),
                };
            }

            if (response.Content.Headers.ContentLength > maxBytes)
            {
                throw TooLarge(host, maxBytes, response);
            }

            var (bytes, complete) = await ReadAsync(response, maxBytes, host, keys, cancellationToken).ConfigureAwait(false);
            return complete ? bytes : throw TooLarge(host, maxBytes, response);
        }
    }

    /// <summary>
    /// Shortens an already redacted error body for <see cref="ProviderException.Detail"/>: trimmed, at most
    /// <see cref="DetailMaxLength"/> characters (never splitting a surrogate pair), <c>null</c> when empty.
    /// </summary>
    /// <param name="redacted">The error body with keys removed.</param>
    /// <returns>The detail, or <c>null</c>.</returns>
    internal static string? DetailOf(string redacted)
    {
        var s = redacted.Trim();
        if (s.Length == 0)
        {
            return null;
        }

        if (s.Length <= DetailMaxLength)
        {
            return s;
        }

        var cut = char.IsHighSurrogate(s[DetailMaxLength - 1]) ? DetailMaxLength - 1 : DetailMaxLength;
        return s[..cut].TrimEnd() + "…";
    }

    private static ProviderException TooLarge(string host, int maxBytes, HttpResponseMessage response)
        => new(string.Create(CultureInfo.InvariantCulture, $"{host}'s reply was larger than {maxBytes} bytes.")) { Failure = FailureClass.BadRequest, StatusCode = response.StatusCode };

    // Reads at most maxBytes; complete is false if there was more (which is left unread)
    private static async Task<(byte[] Bytes, bool Complete)> ReadAsync(HttpResponseMessage response, int maxBytes, string host, List<string?> keys, CancellationToken cancellationToken)
    {
        try
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                using var buffer = new MemoryStream();
                var chunk = new byte[ChunkBytes];
                while (true)
                {
                    // Ask for at most one byte past the limit, so an oversized body is noticed without reading more of it
                    var wanted = (int)Math.Min(chunk.Length, maxBytes + 1L - buffer.Length);
                    var n = await stream.ReadAsync(chunk.AsMemory(0, wanted), cancellationToken).ConfigureAwait(false);
                    if (n == 0)
                    {
                        return (buffer.ToArray(), true);
                    }

                    if (buffer.Length + n > maxBytes)
                    {
                        await buffer.WriteAsync(chunk.AsMemory(0, maxBytes - (int)buffer.Length), cancellationToken).ConfigureAwait(false);
                        return (buffer.ToArray(), false);
                    }

                    await buffer.WriteAsync(chunk.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new ProviderException(host + "'s reply was cut short: " + Redaction.Redact(ex.Message, keys), ex) { Failure = FailureClass.Transient, StatusCode = response.StatusCode };
        }
    }
}
