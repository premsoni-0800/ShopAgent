using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace PrintlyAgent.Credentials;

/// <summary>
/// Secure local credential storage, backed by Windows Credential Manager.
///
/// Port of credentials/CredentialStore.kt. Kotlin reached Win32 through a
/// hand-written JNA binding because jna-platform ships no CredWrite/CredRead
/// wrapper; .NET reaches the same four functions through P/Invoke, which is the
/// same binding written a different way - the struct layout, the blob encoding
/// and the persistence scope all have to match or the credential written by one
/// build is unreadable by the other.
///
/// Nothing sensitive is ever written to the SQLite database, a config file, or a
/// log line.
///
/// Two independent secrets are held under distinct target names, mirroring the
/// backend's own two disjoint authentication filters - an agent-authenticated
/// request never carries the owner's JWT, and vice versa:
///  - the owner's own session (access + refresh token) - first-run pairing and
///    the owner-facing Orders/Printers screens;
///  - the agent's own long-lived device credential, minted once by
///    PrintAgentDeviceService.exchange and used for every device call after.
/// </summary>
public static class CredentialStore
{
    // Deliberately distinct from the Kotlin build's service name
    // ("PrintlyAgentKt") - the two can coexist on the same dev machine during
    // validation without overwriting each other's stored credentials. That
    // separation is the whole reason both can be run side by side.
    private const string Service = "PrintlyAgentNet";
    private const string OwnerSessionTarget = Service + "/owner_session";
    private const string AgentCredentialTarget = Service + "/agent_credential";

    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void SaveOwnerSession(OwnerSession session) =>
        Write(OwnerSessionTarget, JsonSerializer.Serialize(session, Json));

    public static OwnerSession? LoadOwnerSession()
    {
        var raw = Read(OwnerSessionTarget);
        return raw is null ? null : JsonSerializer.Deserialize<OwnerSession>(raw, Json);
    }

    public static void ClearOwnerSession() => Delete(OwnerSessionTarget);

    public static void SaveAgentCredential(AgentCredential credential) =>
        Write(AgentCredentialTarget, JsonSerializer.Serialize(credential, Json));

    public static AgentCredential? LoadAgentCredential()
    {
        var raw = Read(AgentCredentialTarget);
        return raw is null ? null : JsonSerializer.Deserialize<AgentCredential>(raw, Json);
    }

    public static void ClearAgentCredential() => Delete(AgentCredentialTarget);

    // -------------------------------------------------------------------------
    // Raw Windows Credential Manager access (CRED_TYPE_GENERIC, per-machine)
    //
    // `internal` rather than private purely so the P/Invoke round-trip can be
    // exercised against a test-only target name. A test must never touch the two
    // real targets: clearing those in a teardown signs the shop owner out and
    // unpairs the PC for real, which then dead-ends the next sign-in on
    // PRINT_AGENT_ALREADY_ACTIVE.
    // -------------------------------------------------------------------------

    internal static void Write(string target, string json)
    {
        // UTF-16LE, matching the Kotlin side exactly. Windows does not care what
        // is in the blob, so the encoding is purely an agreement between writer
        // and reader - and getting it wrong produces a credential that reads
        // back as mojibake rather than as an error.
        var blob = Encoding.Unicode.GetBytes(json);
        var blobPtr = Marshal.AllocHGlobal(Math.Max(blob.Length, 1));
        try
        {
            Marshal.Copy(blob, 0, blobPtr, blob.Length);

            var credential = new CREDENTIALW
            {
                Flags = 0,
                Type = CredTypeGeneric,
                TargetName = target,
                CredentialBlobSize = blob.Length,
                CredentialBlob = blobPtr,
                Persist = CredPersistLocalMachine,
                UserName = Service,
            };

            if (!CredWriteW(ref credential, 0))
            {
                throw new InvalidOperationException(
                    $"CredWrite failed for {target} (error {Marshal.GetLastWin32Error()})");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(blobPtr);
        }
    }

    internal static string? Read(string target)
    {
        // Not found is never an error for callers checking "is anything stored
        // yet" - that is the ordinary first-run answer.
        if (!CredReadW(target, CredTypeGeneric, 0, out var handle)) return null;

        try
        {
            var credential = Marshal.PtrToStructure<CREDENTIALW>(handle);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0) return null;

            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, credential.CredentialBlobSize);
            return Encoding.Unicode.GetString(bytes);
        }
        finally
        {
            CredFree(handle);
        }
    }

    internal static void Delete(string target)
    {
        // Idempotent: signing out twice, or clearing a credential that was never
        // set, is a no-op rather than a failure.
        CredDeleteW(target, CredTypeGeneric, 0);
    }

    /// <summary>
    /// Win32 CREDENTIALW (wincred.h) - only the fields this code actually reads
    /// or writes are meaningful; the rest exist purely to keep the struct layout
    /// correct. A wrong layout here does not fail loudly, it reads the wrong
    /// bytes, so the field order matches the header exactly.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIALW
    {
        public int Flags;
        public int Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string UserName;
    }

    [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWriteW(ref CREDENTIALW credential, int flags);

    [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredReadW(string targetName, int type, int flags, out IntPtr credential);

    [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDeleteW(string targetName, int type, int flags);

    [DllImport("Advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
