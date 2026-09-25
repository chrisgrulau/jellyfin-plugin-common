using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Common.Secrets;

/// <summary>
/// Removes secrets from text before it is logged or shown: provider error bodies sometimes echo part of the key.
/// </summary>
internal static partial class Redaction
{
    /// <summary>What a removed secret is replaced with.</summary>
    public const string Mask = "[redacted]";

    /// <summary>The longest text kept (error bodies can be large); the rest is cut.</summary>
    public const int MaxLength = 2000;

    /// <summary>
    /// Removes every known key (and any 8-character or longer piece of one), bearer tokens and common key patterns, and
    /// cuts the text to <see cref="MaxLength"/>.
    /// </summary>
    /// <param name="text">Text that may contain a secret.</param>
    /// <param name="secrets">The keys in use.</param>
    /// <returns>Safe text.</returns>
    public static string Redact(string? text, IEnumerable<string?> secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var s = text.Length > MaxLength ? text[..MaxLength] + "…" : text;
        foreach (var secret in secrets)
        {
            if (string.IsNullOrEmpty(secret) || secret.Length < 8)
            {
                continue;
            }

            s = s.Replace(secret, Mask, StringComparison.Ordinal);

            // Partial echoes ("sk-abc…xyz", the last few characters): any 8-character slice of the key
            for (var i = 0; i + 8 <= secret.Length; i++)
            {
                s = s.Replace(secret.Substring(i, 8), Mask, StringComparison.Ordinal);
            }
        }

        s = Bearer().Replace(s, "$1" + Mask);
        return KeyLike().Replace(s, Mask);
    }

    [GeneratedRegex(@"((?:Bearer|Token|Basic)\s+)[A-Za-z0-9._~+/=-]{8,}", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Bearer();

    // Common provider key shapes: sk-…, sk-ant-…, AIza…, long hex or base64-ish runs
    [GeneratedRegex(@"\b(?:sk-(?:ant-)?[A-Za-z0-9_-]{16,}|AIza[0-9A-Za-z_-]{30,}|[0-9a-f]{32,}|[A-Za-z0-9_-]{40,})\b", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex KeyLike();
}
