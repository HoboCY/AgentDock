using System.Runtime.InteropServices;
using AgentDock.Core;

namespace AgentDock.Engine;

/// <summary>
/// Stores the single OpenCode Go credential for the current user.
///
/// Windows uses DPAPI (CurrentUser) and writes only the protected blob; the key
/// is never passed on a command line or placed in a child environment. The blob
/// is replaced atomically (write to a temporary file, flush, replace) so a crash
/// cannot leave a truncated credential.
///
/// DPAPI CurrentUser is not read isolation between processes of the same user:
/// any code running as this user can call CryptUnprotectData on the file, so
/// there is no confidentiality guarantee against malicious same-user code (this
/// includes code inside the unelevated sandbox, whose read access to the home
/// directory is a required part of the supported policy, not a credential
/// boundary). macOS Keychain support is not enabled in this build: Set/Read fail
/// closed with an explicit error until the OS contract has been validated on
/// real hardware.
/// </summary>
public sealed class CredentialVault
{
    /// <summary>File name used inside the AgentDock home directory.</summary>
    public const string FileName = "opencode-go.key.dpapi";

    private readonly string _path;

    public CredentialVault(string home)
    {
        ArgumentException.ThrowIfNullOrEmpty(home);
        _path = Path.Combine(Path.GetFullPath(home), FileName);
    }

    /// <summary>True when a protected credential blob exists (it may still fail to decrypt).</summary>
    public bool IsConfigured => File.Exists(_path);

    /// <summary>
    /// Protects and stores the key for the current Windows user. The key must be
    /// a single non-whitespace token; an invalid value is rejected.
    /// </summary>
    public void Set(string key)
    {
        Validate(key);
        if (!OperatingSystem.IsWindows())
        {
            throw new DockException("credential-platform-unsupported",
                "Credential storage on macOS requires the validated Keychain contract; not enabled in this build.");
        }
        var protectedBytes = Dpapi.Protect(key);
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, FileName + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(protectedBytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // The replace already removed the temporary file or cleanup is not permitted.
            }
        }
    }

    /// <summary>
    /// Reads the key for the current Windows user. Returns null when nothing is
    /// configured; throws <see cref="DockException"/> when a blob exists but
    /// cannot be decrypted for this user or platform.
    /// </summary>
    public string? Read()
    {
        if (!File.Exists(_path))
        {
            return null;
        }
        if (!OperatingSystem.IsWindows())
        {
            throw new DockException("credential-platform-unsupported",
                "Credential storage on macOS requires the validated Keychain contract; not enabled in this build.");
        }
        string key;
        try
        {
            key = Dpapi.Unprotect(File.ReadAllBytes(_path));
        }
        catch (Exception error) when (error is not DockException)
        {
            throw new DockException("credential-invalid",
                "Cannot decrypt the stored OpenCode Go key for this Windows user.");
        }
        try
        {
            Validate(key);
        }
        catch (DockException)
        {
            throw new DockException("credential-invalid", "The stored OpenCode Go key is invalid.");
        }
        return key;
    }

    internal static void Validate(string? key)
    {
        if (string.IsNullOrEmpty(key) || key.Trim() != key || key.Any(char.IsWhiteSpace))
        {
            throw new DockException("credential-invalid", "The OpenCode Go key must be a single non-whitespace value.");
        }
    }

    private static class Dpapi
    {
        private const uint CryptProtectUiForbidden = 0x1;

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            public int Size;
            public IntPtr Data;
        }

        [DllImport("crypt32.dll", SetLastError = true)]
        private static extern bool CryptProtectData(ref DataBlob input, IntPtr description, IntPtr entropy,
            IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

        [DllImport("crypt32.dll", SetLastError = true)]
        private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy,
            IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

        public static byte[] Protect(string value)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(value);
            return Transform(bytes, protect: true);
        }

        public static string Unprotect(byte[] blob)
        {
            var bytes = Transform(blob, protect: false);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }

        private static byte[] Transform(byte[] input, bool protect)
        {
            var managed = GCHandle.Alloc(input, GCHandleType.Pinned);
            var blob = new DataBlob { Size = input.Length, Data = managed.AddrOfPinnedObject() };
            DataBlob output;
            bool ok;
            try
            {
                ok = protect
                    ? CryptProtectData(ref blob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output)
                    : CryptUnprotectData(ref blob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output);
            }
            finally
            {
                managed.Free();
            }
            if (!ok || output.Data == IntPtr.Zero || output.Size <= 0)
            {
                if (output.Data != IntPtr.Zero)
                {
                    LocalFree(output.Data);
                }
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
    }
}
