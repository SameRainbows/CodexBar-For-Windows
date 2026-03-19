using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Serilog;

namespace CodexBar.App.Platform;

/// <summary>
/// Windows Credential Manager wrapper for secure token storage.
/// Uses the Windows DPAPI-backed credential store — no admin privileges required.
/// Never logs raw credential values.
/// </summary>
public sealed class SecureStorage
{
    private static readonly ILogger Log = Serilog.Log.ForContext<SecureStorage>();
    private const string CredentialPrefix = "CodexBar:";

    /// <summary>Store a credential securely.</summary>
    public bool Store(string key, string value)
    {
        try
        {
            var targetName = CredentialPrefix + key;
            var credentialBlob = Encoding.UTF8.GetBytes(value);

            var credential = new NativeMethods.CREDENTIAL
            {
                Type = NativeMethods.CRED_TYPE_GENERIC,
                TargetName = targetName,
                CredentialBlobSize = (uint)credentialBlob.Length,
                CredentialBlob = Marshal.AllocHGlobal(credentialBlob.Length),
                Persist = NativeMethods.CRED_PERSIST_LOCAL_MACHINE,
                UserName = Environment.UserName,
            };

            try
            {
                Marshal.Copy(credentialBlob, 0, credential.CredentialBlob, credentialBlob.Length);

                if (!NativeMethods.CredWrite(ref credential, 0))
                {
                    Log.Warning("Failed to store credential for key {Key}", key);
                    return false;
                }

                Log.Debug("Stored credential for key {Key}", key);
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(credential.CredentialBlob);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error storing credential for key {Key}", key);
            return false;
        }
    }

    /// <summary>Retrieve a credential. Returns null if not found.</summary>
    public string? Retrieve(string key)
    {
        try
        {
            var targetName = CredentialPrefix + key;

            if (!NativeMethods.CredRead(targetName, NativeMethods.CRED_TYPE_GENERIC, 0, out var credentialPtr))
            {
                return null;
            }

            try
            {
                var credential = Marshal.PtrToStructure<NativeMethods.CREDENTIAL>(credentialPtr);
                if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
                    return null;

                var blob = new byte[credential.CredentialBlobSize];
                Marshal.Copy(credential.CredentialBlob, blob, 0, (int)credential.CredentialBlobSize);
                return Encoding.UTF8.GetString(blob);
            }
            finally
            {
                NativeMethods.CredFree(credentialPtr);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error retrieving credential for key {Key}", key);
            return null;
        }
    }

    /// <summary>Delete a stored credential.</summary>
    public bool Delete(string key)
    {
        try
        {
            var targetName = CredentialPrefix + key;
            var result = NativeMethods.CredDelete(targetName, NativeMethods.CRED_TYPE_GENERIC, 0);
            if (result)
                Log.Debug("Deleted credential for key {Key}", key);
            return result;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error deleting credential for key {Key}", key);
            return false;
        }
    }

    /// <summary>P/Invoke declarations for Windows Credential Manager.</summary>
    private static class NativeMethods
    {
        public const int CRED_TYPE_GENERIC = 1;
        public const int CRED_PERSIST_LOCAL_MACHINE = 2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct CREDENTIAL
        {
            public uint Flags;
            public int Type;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string TargetName;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string? Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public int Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string? TargetAlias;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string? UserName;
        }

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CredDelete(string target, int type, int flags);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern void CredFree(IntPtr credential);
    }
}
