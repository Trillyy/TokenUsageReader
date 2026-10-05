using System.Runtime.InteropServices;
using System.Text;

namespace TokenUsageReader.Providers;

/// <summary>Reads and writes generic credentials in the Windows Credential Manager.</summary>
internal static class WindowsCredentials
{
    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref Credential credential, int flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr credential);

    public static void WriteSecret(string target, string secret)
    {
        var bytes = Encoding.UTF8.GetBytes(secret);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var cred = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = target,
                UserName = Environment.UserName,
                CredentialBlobSize = bytes.Length,
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
            };
            if (!CredWrite(ref cred, 0)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            // Wipe both copies of the secret before releasing them.
            for (var i = 0; i < bytes.Length; i++) Marshal.WriteByte(blob, i, 0);
            Array.Clear(bytes);
            Marshal.FreeHGlobal(blob);
        }
    }

    public static void DeleteSecret(string target)
    {
        if (!CredDelete(target, CredTypeGeneric, 0) && Marshal.GetLastWin32Error() != ErrorNotFound)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    public static string? ReadSecret(string target)
    {
        if (!CredRead(target, CredTypeGeneric, 0, out var ptr)) return null;
        try
        {
            var cred = Marshal.PtrToStructure<Credential>(ptr);
            if (cred.CredentialBlobSize == 0) return null;
            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
            // Node-based tools store UTF-8, native Windows tools usually UTF-16: pick whichever is printable ASCII.
            var utf8 = Encoding.UTF8.GetString(bytes);
            return utf8.All(c => c is >= ' ' and <= '~') ? utf8 : Encoding.Unicode.GetString(bytes);
        }
        finally
        {
            CredFree(ptr);
        }
    }
}
