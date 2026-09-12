package com.printly.agent.credentials

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Test

/**
 * Real Windows Credential Manager round-trip - not a mock. Confirms the JNA
 * bindings in [CredentialStore] actually work against the live OS API on
 * this machine, not just that they compile.
 */
class CredentialStoreSmokeTest {

    @Test
    fun `agent credential round-trips through the real Windows Credential Manager`() {
        val original = CredentialStore.AgentCredential(agentId = "smoke-test-agent", shopId = "smoke-test-shop", secret = "smoke-test-secret")
        try {
            CredentialStore.saveAgentCredential(original)
            val loaded = CredentialStore.loadAgentCredential()
            assertEquals(original, loaded)
        } finally {
            CredentialStore.clearAgentCredential()
        }
        assertNull(CredentialStore.loadAgentCredential())
    }

    @Test
    fun `owner session round-trips through the real Windows Credential Manager`() {
        val original = CredentialStore.OwnerSession(
            accessToken = "smoke-access-token", refreshToken = "smoke-refresh-token",
            shopId = "smoke-test-shop", shopName = "Smoke Test Shop",
        )
        try {
            CredentialStore.saveOwnerSession(original)
            val loaded = CredentialStore.loadOwnerSession()
            assertEquals(original, loaded)
        } finally {
            CredentialStore.clearOwnerSession()
        }
        assertNull(CredentialStore.loadOwnerSession())
    }
}
