using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using PrintInterceptor.Core.Diagnostics;

namespace PrintInterceptor.Core.Configuration;

/// <summary>
/// Machine-bound store for the values that must never sit in <c>config.json</c>:
/// the FiscalStack API key and the cached ZIMRA device private key.
/// <para>
/// Each entry is a DPAPI (LocalMachine scope) blob written with an explicit NTFS
/// ACL. The two layers do different jobs and neither is enough alone: DPAPI makes
/// the bytes unreadable in a backup, a support bundle or a copy of the folder,
/// while the ACL is what actually stops a local account from reading the blob —
/// Windows grants <c>Everyone</c> read on the machine DPAPI master key, so a
/// machine-scope blob is trivially decryptable by anyone who can read it.
/// </para>
/// </summary>
public sealed class SecretStore
{
    public const string ApiKeyEntry = "api-key";
    public const string DeviceKeyEntry = "device-key";

    /// <summary>
    /// Additional entropy binding a blob to this purpose. It is not a secret and
    /// gives no protection on its own; it only stops an attacker swapping in a
    /// blob produced for another DPAPI consumer on the same machine.
    /// </summary>
    private static readonly byte[] Entropy =
        System.Text.Encoding.UTF8.GetBytes("FiscalStack.PrintInterceptor.SecretStore.v1");

    private readonly string _directory;
    private readonly ILog _log;

    public SecretStore(string directory, ILog log)
    {
        _log = log;
        _directory = Path.GetFullPath(directory);
    }

    public string DirectoryPath => _directory;

    /// <summary>DPAPI is Windows-only; elsewhere the store is inert and callers fall back.</summary>
    public static bool Supported => OperatingSystem.IsWindows();

    /// <summary>Secrets live beside the config file, so <c>--config</c> selects the whole till profile.</summary>
    public static string DefaultDirectory(string? configPath)
    {
        var parent = string.IsNullOrWhiteSpace(configPath)
            ? Directory.GetCurrentDirectory()
            : Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? Directory.GetCurrentDirectory();
        return Path.Combine(parent, "secrets");
    }

    public string PathFor(string entry) => Path.Combine(_directory, entry + ".protected");

    public bool Contains(string entry) => File.Exists(PathFor(entry));

    /// <summary>
    /// Reads and decrypts an entry. Returns false when it is absent, unreadable,
    /// or produced on a different machine — a corrupted or foreign blob must never
    /// be reported as a usable secret, so the caller falls back to another source.
    /// </summary>
    public bool TryGet(string entry, out string value)
    {
        value = "";
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var path = PathFor(entry);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var protectedBytes = File.ReadAllBytes(path);
            var clear = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.LocalMachine);
            value = System.Text.Encoding.UTF8.GetString(clear);
            CryptographicOperations.ZeroMemory(clear);
            return !string.IsNullOrWhiteSpace(value);
        }
        catch (CryptographicException ex)
        {
            _log.Warn(
                $"The stored secret '{entry}' could not be decrypted on this machine ({ex.Message}); " +
                "it was protected by a different machine, account or application version.");
            return false;
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not read the stored secret '{entry}': {ex.Message}");
            return false;
        }
    }

    public bool TrySet(string entry, string? value, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "Refusing to store an empty secret.";
            return false;
        }

        if (!OperatingSystem.IsWindows())
        {
            error = "The secret store requires Windows.";
            return false;
        }

        string? temp = null;
        try
        {
            Directory.CreateDirectory(_directory);
            RestrictDirectory(_directory);

            var path = PathFor(entry);
            // A predictable "api-key.protected.tmp" can be pre-created by another
            // local user to hold a place in the folder. A random name also avoids
            // colliding with a stale temp from an interrupted write.
            temp = Path.Combine(_directory, $".{entry}.{Guid.NewGuid():N}.tmp");
            var bytes = ProtectedData.Protect(
                System.Text.Encoding.UTF8.GetBytes(value.Trim()), Entropy, DataProtectionScope.LocalMachine);
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    // Restrict the empty file before a single ciphertext byte is
                    // written. An ACL applied afterwards leaves a window in which
                    // the blob is readable under the folder's inherited rights.
                    RestrictFile(temp);
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temp, path, overwrite: true);
                RestrictFile(path);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
                DeleteQuietly(temp);
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _log.Error($"Could not store the secret '{entry}'", ex);
            return false;
        }
    }

    private static void DeleteQuietly(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Leftover temp file holds ciphertext only; never fatal.
        }
        catch (UnauthorizedAccessException)
        {
            // As above: no plaintext is ever in a temp file.
        }
    }

    public bool TryRemove(string entry, out string error)
    {
        error = "";
        var path = PathFor(entry);
        if (!File.Exists(path))
        {
            return true;
        }

        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _log.Error($"Could not delete the stored secret '{entry}'", ex);
            return false;
        }
    }

    public void Remove(string entry)
    {
        TryRemove(entry, out _);
    }

    /// <summary>
    /// A form safe to write to a log or show in a window. Only a coarse length
    /// bucket is reported: no prefix and no exact length, either of which helps
    /// an attacker checking a guessed key.
    /// </summary>
    public static string Describe(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            return "not configured";
        }

        var length = secret.Trim().Length;
        var bucket = length < 20 ? "short" : length < 40 ? "typical" : "long";
        return $"configured ({bucket} length)";
    }

    /// <summary>
    /// Applies the same restriction as a secret blob to a file that must stay
    /// private without being a DPAPI entry — currently the offline provisioning
    /// cache on non-Windows hosts, where DPAPI does not exist. On Windows this
    /// calls <see cref="RestrictFile"/> and fails closed.
    /// </summary>
    public static void RestrictExistingFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            RestrictFile(path);
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // Best effort; the data directory is already per-user on Unix.
        }
    }

    /// <summary>
    /// Replaces inherited permissions on a folder that holds secrets (or the
    /// configuration). Only SYSTEM, Administrators and the account this process
    /// runs as get access: those same trustees are granted on the files inside,
    /// so no broader directory grant is needed for traversal.
    /// <para>
    /// Fails closed: a store whose ACL could not be applied must never be used.
    /// </para>
    /// </summary>
    public static void RestrictDirectory(string directory)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return;
        }

        try
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            AddAllow(security, WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl);
            AddAllow(security, WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl);
            AddAllowSelf(security, FileSystemRights.FullControl);

            new DirectoryInfo(directory).SetAccessControl(security);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not restrict access to {directory}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Breaks inheritance on one file and grants it only to SYSTEM, Administrators
    /// and the account this process runs as (the till user in tray mode). Used
    /// for both DPAPI blobs and config.json, which can hold a legacy plaintext key
    /// until it is registered.
    /// <para>
    /// Fails closed: callers must not write a secret under an inherited ACL.
    /// </para>
    /// </summary>
    public static void RestrictFile(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return;
        }

        try
        {
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            AddAllow(security, WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl);
            AddAllow(security, WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl);
            AddAllowSelf(security, FileSystemRights.FullControl);

            new FileInfo(path).SetAccessControl(security);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not restrict access to {path}: {ex.Message}", ex);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void AddAllow(
        FileSystemSecurity security,
        WellKnownSidType sidType,
        FileSystemRights rights)
    {
        var identity = new SecurityIdentifier(sidType, null);
        security.AddAccessRule(new FileSystemAccessRule(
            identity,
            rights,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));
    }

    /// <summary>
    /// Grants <paramref name="rights"/> to the account this process runs as. Added
    /// last so that when the process also belongs to Administrators, the more
    /// specific explicit allow wins over any conflicting rule.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void AddAllowSelf(FileSystemSecurity security, FileSystemRights rights)
    {
        var self = WindowsIdentity.GetCurrent().User;
        if (self is null)
        {
            return;
        }

        // A file ACL cannot carry inheritance flags; passing them throws
        // "No flags can be set" on Windows. Directories need them so newly
        // created secrets inherit the same restriction.
        var inheritance = security is DirectorySecurity
            ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit
            : InheritanceFlags.None;

        security.AddAccessRule(new FileSystemAccessRule(
            self,
            rights,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
    }
}
