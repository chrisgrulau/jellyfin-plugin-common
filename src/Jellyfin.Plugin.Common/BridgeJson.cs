using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace Jellyfin.Plugin.Common;

/// <summary>
/// JSON settings for the in-process entry points between the plugins (both sides use them).
/// <list type="bullet">
/// <item>Letters in every script are written as themselves, not as <c>\uXXXX</c> escapes, so non-English text isn't six
/// times its size and reaches the model readable.</item>
/// <item><c>&lt;</c>, <c>&gt;</c> and <c>&amp;</c> are still escaped (this isn't the "unsafe relaxed" encoder), so text inside
/// JSON can't close an XML-style block such as <c>&lt;/data&gt;</c>.</item>
/// </list>
/// </summary>
internal static class BridgeJson
{
    /// <summary>The serializer settings.</summary>
    public static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };

    /// <summary>
    /// The size of a value as the entry points measure it: UTF-8 bytes of its JSON.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>The size in bytes.</returns>
    public static int Bytes(object? value) => Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(value, Options));
}
