using ElApp.Watch.Wpf.Services.Interface;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace ElApp.Watch.Wpf.Services;

/// <summary>
/// <see cref="IForecourtCredentialStore"/> backed by the real Windows Credential Manager (not a hand-rolled
/// DPAPI-encrypted file) via direct P/Invoke to advapi32.dll - there is no first-party managed wrapper for
/// Credential Manager itself in .NET (as distinct from the lower-level DPAPI
/// Protect/Unprotect API, which Credential Manager uses internally). The client_id/client_secret pair is
/// stored as one JSON blob under a single fixed target name, persisted at <see cref="CredPersistLocalMachine"/>
/// scope so it survives across logon sessions on this station PC.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialManagerStore : IForecourtCredentialStore
{
    private const string DefaultTargetName = "EagleLens:ForecourtClient";
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;

    private readonly string _targetName;

    /// <param name="targetName">
    /// The Credential Manager target name to store under. Defaults to the fixed production value; tests
    /// pass a unique value per test so each starts from a guaranteed-absent state without needing to share
    /// state via <see cref="Delete"/> ordering.
    /// </param>
    public WindowsCredentialManagerStore(string targetName = DefaultTargetName)
    {
        _targetName = targetName;
    }

    public ForecourtCredential? TryGet()
    {
        if (!NativeMethods.CredRead(_targetName, CredTypeGeneric, 0, out var credentialPtr))
        {
            return null;
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeMethods.CREDENTIAL>(credentialPtr);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return null;
            }

            var blob = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, blob, 0, blob.Length);
            var json = Encoding.UTF8.GetString(blob);
            return JsonSerializer.Deserialize<ForecourtCredential>(json);
        }
        finally
        {
            NativeMethods.CredFree(credentialPtr);
        }
    }

    public void Save(ForecourtCredential credential)
    {
        var json = JsonSerializer.Serialize(credential);
        var blob = Encoding.UTF8.GetBytes(json);
        var blobPtr = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, blobPtr, blob.Length);

            var nativeCredential = new NativeMethods.CREDENTIAL
            {
                Type = CredTypeGeneric,
                TargetName = _targetName,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = blobPtr,
                Persist = CredPersistLocalMachine,
                UserName = credential.ClientId,
            };

            if (!NativeMethods.CredWrite(ref nativeCredential, 0))
            {
                throw new InvalidOperationException(
                    $"Failed to write forecourt credential to Windows Credential Manager (Win32 error {Marshal.GetLastWin32Error()}).");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(blobPtr);
        }
    }

    /// <summary>
    /// Removes the stored credential, if any. Not part of <see cref="IForecourtCredentialStore"/> - a
    /// forecourt device never deletes its own credential in normal operation - exposed on the concrete
    /// type for test cleanup.
    /// </summary>
    public void Delete()
    {
        NativeMethods.CredDelete(_targetName, CredTypeGeneric, 0);
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct CREDENTIAL
        {
            public uint Flags;
            public uint Type;
            [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
            [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
            public long LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
            [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CredRead(string target, uint type, int reservedFlag, out IntPtr credentialPtr);

        [DllImport("advapi32.dll", EntryPoint = "CredFree", SetLastError = true)]
        public static extern void CredFree(IntPtr credentialPtr);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CredDelete(string target, uint type, int flags);
    }
}
