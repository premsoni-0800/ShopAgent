package com.printly.agent.credentials

import com.fasterxml.jackson.annotation.JsonIgnore
import com.fasterxml.jackson.module.kotlin.jacksonObjectMapper
import com.sun.jna.Memory
import com.sun.jna.Native
import com.sun.jna.Pointer
import com.sun.jna.Structure
import com.sun.jna.WString
import com.sun.jna.ptr.PointerByReference
import com.sun.jna.win32.StdCallLibrary
import com.sun.jna.win32.W32APIOptions
import java.nio.charset.StandardCharsets

/**
 * Secure local credential storage, backed by Windows Credential Manager via
 * a small hand-written JNA binding (jna-platform ships no `CredWrite`/
 * `CredRead` wrapper) - the direct Kotlin/JVM equivalent of the Python
 * agent's `credentials.py` (which goes through the `keyring` library's own
 * Windows backend). Nothing sensitive is ever written to the SQLite
 * database, a config file, or a log line.
 *
 * Two independent secrets are held under distinct target names, mirroring
 * the backend's own two disjoint authentication filters - an agent-
 * authenticated request never carries the owner's JWT, and vice versa:
 * - the owner's own session (access + refresh token) - first-run pairing and
 *   the owner-facing Orders/Printers screens;
 * - the agent's own long-lived device credential, minted once by
 *   `PrintAgentDeviceService.exchange` and used for every device call after.
 */
object CredentialStore {

    // Deliberately distinct from the Python agent's own keyring service name
    // ("PrintlyAgent") - the two can coexist on the same dev machine during
    // validation without overwriting each other's stored credentials.
    private const val SERVICE = "PrintlyAgentKt"
    private const val OWNER_SESSION_TARGET = "$SERVICE/owner_session"
    private const val AGENT_CREDENTIAL_TARGET = "$SERVICE/agent_credential"

    private const val CRED_TYPE_GENERIC = 1
    private const val CRED_PERSIST_LOCAL_MACHINE = 2

    private val mapper = jacksonObjectMapper()

    data class OwnerSession(
        val accessToken: String,
        val refreshToken: String,
        val shopId: String,
        val shopName: String?,
    )

    data class AgentCredential(val agentId: String, val shopId: String, val secret: String) {
        // Derived, never persisted - a stored "bearerToken" field would be an
        // unrecognized property on deserialization otherwise.
        @get:JsonIgnore
        val bearerToken: String get() = "$agentId.$secret"
    }

    fun saveOwnerSession(session: OwnerSession) = write(OWNER_SESSION_TARGET, mapper.writeValueAsString(session))
    fun loadOwnerSession(): OwnerSession? = read(OWNER_SESSION_TARGET)?.let { mapper.readValue(it, OwnerSession::class.java) }
    fun clearOwnerSession() = delete(OWNER_SESSION_TARGET)

    fun saveAgentCredential(credential: AgentCredential) = write(AGENT_CREDENTIAL_TARGET, mapper.writeValueAsString(credential))
    fun loadAgentCredential(): AgentCredential? = read(AGENT_CREDENTIAL_TARGET)?.let { mapper.readValue(it, AgentCredential::class.java) }
    fun clearAgentCredential() = delete(AGENT_CREDENTIAL_TARGET)

    // -------------------------------------------------------------------
    // Raw Windows Credential Manager access (CRED_TYPE_GENERIC, per-machine)
    // -------------------------------------------------------------------

    private fun write(target: String, json: String) {
        val blob = json.toByteArray(StandardCharsets.UTF_16LE)
        val blobMemory = Memory(blob.size.toLong().coerceAtLeast(1))
        blobMemory.write(0, blob, 0, blob.size)

        val credential = CREDENTIALW()
        credential.flags = 0
        credential.type = CRED_TYPE_GENERIC
        credential.targetName = WString(target)
        credential.credentialBlobSize = blob.size
        credential.credentialBlob = blobMemory
        credential.persist = CRED_PERSIST_LOCAL_MACHINE
        credential.userName = WString(SERVICE)

        val ok = Advapi32Cred.INSTANCE.CredWriteW(credential, 0)
        check(ok) { "CredWrite failed for $target (error ${Native.getLastError()})" }
    }

    private fun read(target: String): String? {
        val ref = PointerByReference()
        val ok = Advapi32Cred.INSTANCE.CredReadW(target, CRED_TYPE_GENERIC, 0, ref)
        if (!ok) return null // not found - never an error for callers checking "is anything stored yet"

        try {
            val credential = CREDENTIALW(ref.value)
            val bytes = credential.credentialBlob!!.getByteArray(0, credential.credentialBlobSize)
            return String(bytes, StandardCharsets.UTF_16LE)
        } finally {
            Advapi32Cred.INSTANCE.CredFree(ref.value)
        }
    }

    private fun delete(target: String) {
        // Idempotent, same as the Python side's `_delete_quietly`: signing out
        // twice, or clearing a credential that was never set, is a no-op.
        Advapi32Cred.INSTANCE.CredDeleteW(target, CRED_TYPE_GENERIC, 0)
    }

    interface Advapi32Cred : StdCallLibrary {
        fun CredWriteW(credential: CREDENTIALW, flags: Int): Boolean
        fun CredReadW(targetName: String, type: Int, flags: Int, credential: PointerByReference): Boolean
        fun CredDeleteW(targetName: String, type: Int, flags: Int): Boolean
        fun CredFree(credential: Pointer): Unit

        companion object {
            val INSTANCE: Advapi32Cred = Native.load("Advapi32", Advapi32Cred::class.java, W32APIOptions.UNICODE_OPTIONS)
        }
    }

    /** Win32 `CREDENTIALW` (wincred.h) - only the fields this code actually reads or writes are meaningful; the rest exist purely to keep the struct layout correct. */
    @Structure.FieldOrder(
        "flags", "type", "targetName", "comment", "lastWrittenLow", "lastWrittenHigh",
        "credentialBlobSize", "credentialBlob", "persist", "attributeCount", "attributes", "targetAlias", "userName",
    )
    class CREDENTIALW() : Structure() {
        constructor(p: Pointer) : this() {
            useMemory(p)
            read()
        }

        @JvmField var flags: Int = 0
        @JvmField var type: Int = 0
        @JvmField var targetName: WString? = null
        @JvmField var comment: WString? = null
        @JvmField var lastWrittenLow: Int = 0
        @JvmField var lastWrittenHigh: Int = 0
        @JvmField var credentialBlobSize: Int = 0
        @JvmField var credentialBlob: Pointer? = null
        @JvmField var persist: Int = 0
        @JvmField var attributeCount: Int = 0
        @JvmField var attributes: Pointer? = null
        @JvmField var targetAlias: WString? = null
        @JvmField var userName: WString? = null
    }
}
