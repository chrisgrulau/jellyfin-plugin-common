using System;
using System.IO;
using System.Text.Json;

namespace Jellyfin.Plugin.Common.Storage;

/// <summary>
/// What reading a JSON file found. Each store decides what to do about each state (its policy); the file helper only
/// tells them apart, so a passing lock is never mistaken for damage.
/// </summary>
internal enum JsonFileState
{
    /// <summary>There is no file (nothing saved yet).</summary>
    Missing = 0,

    /// <summary>The file was read and parsed.</summary>
    Loaded,

    /// <summary>The file was read but isn't valid JSON for the type: it won't get better by itself.</summary>
    Damaged,

    /// <summary>
    /// The file couldn't be read right now (locked by a backup or antivirus, a share hiccup, permissions): it may be fine,
    /// so it must not be overwritten or set aside; try again later.
    /// </summary>
    Unreadable,
}

/// <summary>
/// The result of <see cref="JsonFile.Read{T}"/>.
/// </summary>
/// <typeparam name="T">The stored type.</typeparam>
/// <param name="State">What was found.</param>
/// <param name="Value">The value when <see cref="JsonFileState.Loaded"/> (<c>null</c> if the file holds the JSON literal
/// <c>null</c>); otherwise <c>default</c>.</param>
/// <param name="Error">Why it was <see cref="JsonFileState.Damaged"/> or <see cref="JsonFileState.Unreadable"/>, for a log
/// line. The message may name the file's path but never its contents.</param>
internal readonly record struct JsonFileResult<T>(JsonFileState State, T? Value, Exception? Error)
{
    /// <summary>Gets a value indicating whether the file was read and parsed.</summary>
    public bool IsLoaded => State == JsonFileState.Loaded;
}

/// <summary>
/// Reads and writes small JSON files the way every store in the plugin family should: a read tells a missing, damaged
/// or unreadable file apart, a damaged file can be set aside for inspection, and a write goes to a temporary file in the
/// same folder that is flushed and then renamed over the old one, so a crash never leaves a half-written file.
/// </summary>
internal static class JsonFile
{
    /// <summary>What <see cref="WriteAtomic{T}"/> appends to the path for its temporary file.</summary>
    public const string TempSuffix = ".tmp";

    /// <summary>What <see cref="SetAside"/> appends to the path, before the time in Unix seconds.</summary>
    public const string DamagedSuffix = ".damaged-";

    /// <summary>
    /// Reads a JSON file. Never throws for a missing, damaged or unreadable file.
    /// </summary>
    /// <typeparam name="T">The stored type.</typeparam>
    /// <param name="path">The file.</param>
    /// <param name="options">Serializer options, or <c>null</c> for the defaults.</param>
    /// <returns>What was found, and the value if it was loaded.</returns>
    public static JsonFileResult<T> Read<T>(string path, JsonSerializerOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            if (!File.Exists(path))
            {
                return new(JsonFileState.Missing, default, null);
            }

            return new(JsonFileState.Loaded, JsonSerializer.Deserialize<T>(File.ReadAllText(path), options), null);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Removed between the check and the read
            return new(JsonFileState.Missing, default, null);
        }
        catch (JsonException ex)
        {
            return new(JsonFileState.Damaged, default, ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(JsonFileState.Unreadable, default, ex);
        }
    }

    /// <summary>
    /// Moves a damaged file aside (to <c>path.damaged-UNIXSECONDS</c>, replacing one of the same second), keeping it for
    /// inspection so the next write starts afresh. Never throws for file-system problems.
    /// </summary>
    /// <param name="path">The damaged file.</param>
    /// <param name="clock">Clock, for the time in the name.</param>
    /// <returns>Where it was moved, or <c>null</c> if there was nothing to move or it couldn't be moved (it is left in place).</returns>
    public static string? SetAside(string path, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var aside = path + DamagedSuffix + (clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            File.Move(path, aside, overwrite: true);
            return aside;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes a value as JSON, atomically: to a temporary file in the same folder (created along with the folder if
    /// needed), flushed to disk, then renamed over <paramref name="path"/>. The old file stays intact until the rename.
    /// </summary>
    /// <remarks>
    /// With <paramref name="ownerOnly"/> the file is readable only by the account the server runs as: created with mode
    /// 0600 on Linux and macOS; on Windows given an access list granting only that account, SYSTEM and Administrators,
    /// not inherited from the folder. Both are applied before anything is written. If a file left by another account
    /// can't be replaced, it is deleted (where the folder allows) and the rename tried again.
    /// </remarks>
    /// <typeparam name="T">The stored type.</typeparam>
    /// <param name="path">The file.</param>
    /// <param name="value">The value.</param>
    /// <param name="options">Serializer options, or <c>null</c> for the defaults.</param>
    /// <param name="ownerOnly">Whether only the server's own account may read the file (keys and other secrets).</param>
    /// <exception cref="IOException">The file couldn't be written; the old one is unchanged.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder or file isn't writable; the old one is unchanged.</exception>
    public static void WriteAtomic<T>(string path, T value, JsonSerializerOptions? options = null, bool ownerOnly = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = full + TempSuffix;
        try
        {
            // A leftover temporary file keeps its old permissions when opened again, so start from a new one
            File.Delete(temp);
            var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (ownerOnly && !OperatingSystem.IsWindows())
            {
                fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using (var stream = new FileStream(temp, fileOptions))
            {
                if (ownerOnly && OperatingSystem.IsWindows())
                {
                    // Before anything is written: folders under ProgramData are readable by every local user by default
                    WindowsOwnerOnly(new FileInfo(temp));
                }

                JsonSerializer.Serialize(stream, value, options);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(temp, full, overwrite: true);
            }
            catch (UnauthorizedAccessException) when (ownerOnly)
            {
                // A file this account can't overwrite (left by another account): remove it where the folder allows, then move
                File.Delete(full);
                File.Move(temp, full, overwrite: true);
            }
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Replaced by the next write
        }
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
