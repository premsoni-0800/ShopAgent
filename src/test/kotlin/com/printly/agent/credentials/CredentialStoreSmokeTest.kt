package com.printly.agent.credentials

import com.fasterxml.jackson.module.kotlin.jacksonObjectMapper
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Test

/**
 * Real Windows Credential Manager round-trip - not a mock. Confirms the JNA
 * bindings in [CredentialStore] actually work against the live OS API on this
 * machine, not just that they compile.
 *
 * Deliberately uses its own target name rather than
 * `saveAgentCredential`/`saveOwnerSession`: those write to the targets the
 * running agent reads, and clearing them in teardown really does sign the
 * shop owner out and unpair the PC - which then dead-ends the next sign-in
 * on PRINT_AGENT_ALREADY_ACTIVE, since the backend still holds the old
 * pairing. A test must not have side effects on a live install.
 */
class CredentialStoreSmokeTest {

    private val testTarget = "PrintlyAgentKt-test/smoke"
    private val mapper = jacksonObjectMapper()

    @Test
    fun `a credential round-trips through the real Windows Credential Manager`() {
        val original = CredentialStore.AgentCredential(
            agentId = "smoke-test-agent",
            shopId = "smoke-test-shop",
            secret = "smoke-test-secret",
        )
        try {
            CredentialStore.write(testTarget, mapper.writeValueAsString(original))
            val loaded = CredentialStore.read(testTarget)
                ?.let { mapper.readValue(it, CredentialStore.AgentCredential::class.java) }
            assertEquals(original, loaded)
        } finally {
            CredentialStore.delete(testTarget)
        }
        assertNull(CredentialStore.read(testTarget))
    }

    @Test
    fun `unicode and punctuation survive the UTF-16LE blob round-trip`() {
        // Tokens are base64url and JSON-escaped, but the blob encoding is the
        // one place a subtle corruption would go unnoticed until sign-in broke.
        val payload = """{"token":"a/b+c=ü€","note":"line1\nline2"}"""
        try {
            CredentialStore.write(testTarget, payload)
            assertEquals(payload, CredentialStore.read(testTarget))
        } finally {
            CredentialStore.delete(testTarget)
        }
    }
}
