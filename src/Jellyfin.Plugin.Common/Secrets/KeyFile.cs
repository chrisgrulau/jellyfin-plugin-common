using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.Common.Storage;

namespace Jellyfin.Plugin.Common.Secrets;

/// <summary>
/// Keeps API keys in a file only the server's own account can read (mode 0600 on Linux and macOS; on Windows an access
/// list granting only that account, SYSTEM and Administrators, not inherited from the folder), separate from the
/// plugin configuration, so they are never sent back to a settings page, included in a configuration export, or logged.
/// Callers can only ask whether a key is set, replace it or clear it; the key itself is read only to make a call.
/// </summary>
internal sealed class KeyFile
{
    /// <summary>The longest key accepted.</summary>
    public const int MaxKeyLength = 512;

    private readonly string _path;
    private readonly IReadOnlyList<string> _providers;
    private readonly Lock _lock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="KeyFile"/> class.
    /// </summary>
    /// <param name="path">Absolute path of the key file.</param>
    /// <param name="providers">The provider ids keys may be stored for; anything else is refused.</param>
    public KeyFile(string path, IReadOnlyList<string> providers)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(providers);
        _path = path;
        _providers = providers;
    }

    /// <summary>
    /// Whether a key looks usable: 8 to <see cref="MaxKeyLength"/> printable ASCII characters, no spaces.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <returns><c>true</c> if acceptable.</returns>
    public static bool IsWellFormed(string? key)
        => key is { Length: >= 8 and <= MaxKeyLength } && key.All(c => c is > ' ' and < '\u007f');

    /// <summary>
    /// Whether a provider id is one keys may be stored for.
    /// </summary>
    /// <param name="provider">Provider id.</param>
    /// <returns><c>true</c> if allowed.</returns>
    public bool IsKnown(string? provider) => provider is not null && _providers.Contains(provider, StringComparer.Ordinal);

    /// <summary>
    /// Which providers have a key.
    /// </summary>
    /// <returns>Provider id → whether a key is set, for every allowed provider.</returns>
    public IReadOnlyDictionary<string, bool> Status()
    {
        lock (_lock)
        {
            var keys = Load();
            return _providers.ToDictionary(p => p, keys.ContainsKey, StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Gets why the keys can't be read, if the last read failed (the file exists but this account can't open it, for
    /// example after the service account or user id changed). Saving a key again replaces the file.
    /// </summary>
    public string? Problem { get; private set; }

    /// <summary>
    /// Stores (or replaces) a provider's key.
    /// </summary>
    /// <param name="provider">An allowed provider id.</param>
    /// <param name="key">The key (surrounding whitespace is ignored).</param>
    /// <exception cref="ArgumentException">Unknown provider or malformed key.</exception>
    public void Set(string provider, string key)
    {
        if (!IsKnown(provider))
        {
            throw new ArgumentException("Unknown provider.", nameof(provider));
        }

        var trimmed = key?.Trim();
        if (!IsWellFormed(trimmed))
        {
            throw new ArgumentException("That doesn't look like an API key.", nameof(key));
        }

        lock (_lock)
        {
            var keys = Load();
            keys[provider] = trimmed!;
            Save(keys);
        }
    }

    /// <summary>
    /// Removes a provider's key.
    /// </summary>
    /// <param name="provider">Provider id.</param>
    public void Clear(string provider)
    {
        lock (_lock)
        {
            var keys = Load();
            if (keys.Remove(provider))
            {
                Save(keys);
            }
        }
    }

    /// <summary>
    /// Gets a provider's key, for making a call. Never pass it to a log, an alert or a response.
    /// </summary>
    /// <param name="provider">Provider id.</param>
    /// <returns>The key, or <c>null</c>.</returns>
    public string? Get(string provider)
    {
        lock (_lock)
        {
            return Load().TryGetValue(provider, out var key) ? key : null;
        }
    }

    private Dictionary<string, string> Load()
    {
        var read = JsonFile.Read<Dictionary<string, string>>(_path);
        if (read.State == JsonFileState.Unreadable)
        {
            // Unreadable (permissions changed, locked): report it and carry on without keys; never throw to the pages
            Problem = "The saved keys can't be read by the account Jellyfin runs as. Enter them again to replace the file.";
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        // A damaged key file means the keys have to be entered again; nothing else depends on it
        Problem = null;
        return read.Value is { } keys
            ? new Dictionary<string, string>(keys.Where(k => IsKnown(k.Key) && IsWellFormed(k.Value)), StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private void Save(Dictionary<string, string> keys)
    {
        // Created owner-only before anything is written to it; a file left by another account is replaced
        JsonFile.WriteAtomic(_path, keys, ownerOnly: true);
        Problem = null;
    }
}
