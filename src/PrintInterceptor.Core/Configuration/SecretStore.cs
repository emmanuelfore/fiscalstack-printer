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

        try
        {
            Directory.CreateDirectory(_directory);
            RestrictDirectory(_directory);

            var path = PathFor(entry);
            var temp = path + ".tmp";
            var bytes = ProtectedData.Protect(
                System.Text.Encoding.UTF8.GetBytes(value.Trim()), Entropy, DataProtectionScope.LocalMachine);
            try
            {
                File.WriteAllBytes(temp, bytes);
                RestrictFile(temp);
                File.Move(temp, path, overwrite: true);
                RestrictFile(path);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
                if (File.Exists(temp))
                {
                    try
                    {
                        File.Delete(temp);
                    }
                    catch (IOException)
                    {
                        // Leftover temp file holds ciphertext only; never fatal.
                    }
                }
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
    /// A form safe to write to a log or show in a window: a static prefix and the
    /// length, never the secret itself.
    /// </summary>
    public static string Describe(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            return "not configured";
        }

        var trimmed = secret.Trim();
        var prefix = trimmed.Length <= 4 ? "" : trimmed[..4];
        return $"'{prefix}…' ({trimmed.Length} characters)";
    }

    /// <summary>
    /// Applies the same restriction the secret blobs get to a file that still has
    /// to hold a secret because no store is available for it - a cache written by
    /// an older version, or a non-Windows host where DPAPI does not exist.
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
    /// Replaces inherited permissions on the secrets folder. The account this
    /// process runs as must keep write access - it has to create and replace the
    /// blobs in there - and authenticated users keep traversal, because a file
    /// grant is useless if the parent folder cannot be entered; reading the blobs
    /// is still blocked by their own ACL.
    /// </summary>
    private static void RestrictDirectory(string directory)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            AddAllow(security, WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl);
            AddAllow(security, WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl);
            AddAllowSelf(security, FileSystemRights.FullControl);
            AddAllow(
                security,
                WellKnownSidType.AuthenticatedUserSid,
                FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize);

            new DirectoryInfo(directory).SetAccessControl(security);
        }
        catch (Exception ex)
        {
            // The folder stays usable; the blob ACLs below still restrict the content.
            System.Diagnostics.Debug.WriteLine($"Could not restrict the secret folder: {ex.Message}");
        }
    }

    /// <summary>
    /// Breaks inheritance on a single blob and grants it to SYSTEM, Administrators
    /// and the account this process runs as (the till user in tray mode). The
    /// running account gets full control so it can overwrite and delete its own
    /// blobs; everyone else is left with nothing.
    /// </summary>
    private static void RestrictFile(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
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
            // Never fail a till over a permission we could not set: the value is
            // still DPAPI ciphertext. The installer applies the folder-wide ACL.
            System.Diagnostics.Debug.WriteLine($"Could not restrict a secret file: {ex.Message}");
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

        security.AddAccessRule(new FileSystemAccessRule(
            self,
            rights,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
    }
}
