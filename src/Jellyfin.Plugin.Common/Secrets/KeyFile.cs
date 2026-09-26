using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

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
        try
        {
            if (File.Exists(_path) && JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path)) is { } keys)
            {
                Problem = null;
                return new Dictionary<string, string>(keys.Where(k => IsKnown(k.Key) && IsWellFormed(k.Value)), StringComparer.Ordinal);
            }
        }
        catch (JsonException)
        {
            // A damaged key file means the keys have to be entered again; nothing else depends on it
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable (permissions changed, locked): report it and carry on without keys; never throw to the pages
            Problem = "The saved keys can't be read by the account Jellyfin runs as. Enter them again to replace the file.";
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        Problem = null;
        return new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private void Save(Dictionary<string, string> keys)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";

        // Create the file owner-only before anything is written to it
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(temp, options))
        {
            if (OperatingSystem.IsWindows())
            {
                // Before anything is written: folders under ProgramData are readable by every local user by default
                WindowsOwnerOnly(new FileInfo(temp));
            }

            JsonSerializer.Serialize(stream, keys);
        }

        try
        {
            File.Move(temp, _path, overwrite: true);
        }
        catch (UnauthorizedAccessException)
        {
            // A file this account can't overwrite (left by another account): remove it where the folder allows, then move
            File.Delete(_path);
            File.Move(temp, _path, overwrite: true);
        }

        Problem = null;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void WindowsOwnerOnly(FileInfo file)
    {
        var security = new System.Security.AccessControl.FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var owner = System.Security.Principal.WindowsIdentity.GetCurrent().User;
        foreach (var who in new System.Security.Principal.IdentityReference?[]
        {
            owner,
            new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null),
            new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null),
        })
        {
            if (who is not null)
            {
                security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(who, System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
            }
        }

        file.SetAccessControl(security);
    }
}
