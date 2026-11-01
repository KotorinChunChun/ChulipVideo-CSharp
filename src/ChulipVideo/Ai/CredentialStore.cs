using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace ChulipVideo.Ai;

/// <summary>APIキーをWindows資格情報マネージャー（利用者ごと）へ保存する。設定ファイルには書かない。</summary>
public static class CredentialStore
{
    public const string AnthropicTarget = "KotorinToys/ChulipVideo/Anthropic";
    private const int Generic = 1, LocalMachine = 2, NotFound = 1168;

    public static string? Read(string target)
    {
        if (!CredRead(target, Generic, 0, out IntPtr pointer))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == NotFound) return null;
            throw new Win32Exception(error);
        }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.CredentialBlobSize == 0 || credential.CredentialBlob == IntPtr.Zero) return null;
            return Marshal.PtrToStringUni(credential.CredentialBlob, credential.CredentialBlobSize / 2);
        }
        finally { CredFree(pointer); }
    }

    public static void Write(string target, string secret)
    {
        byte[] blob = Encoding.Unicode.GetBytes(secret);
        IntPtr memory = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, memory, blob.Length);
            var credential = new Credential { Type = Generic, TargetName = target, CredentialBlobSize = blob.Length, CredentialBlob = memory, Persist = LocalMachine, UserName = "ChulipVideo" };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            // 平文のコピーを残さない。
            Marshal.Copy(new byte[blob.Length], 0, memory, blob.Length);
            Array.Clear(blob);
            Marshal.FreeHGlobal(memory);
        }
    }

    public static void Delete(string target)
    {
        if (!CredDelete(target, Generic, 0) && Marshal.GetLastWin32Error() is int error && error != NotFound) throw new Win32Exception(error);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref Credential credential, int flags);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree(IntPtr buffer);
}
