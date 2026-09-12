package com.printly.agent.core

import com.printly.agent.credentials.CredentialStore
import com.printly.agent.net.ApiError
import com.printly.agent.net.PrintlyApiClient
import java.util.UUID
import java.util.logging.Logger

/**
 * Owner login and first-run pairing - direct port of the Python agent's
 * `auth.py`. Two ways in, both ending at the same [CredentialStore.OwnerSession]:
 * email/phone + password (the normal, daily way in), or the MSG91 OTP widget
 * (first-run only, to prove phone ownership before a password can be set).
 */
object Auth {

    const val AGENT_VERSION = "0.1.0"

    private val log = Logger.getLogger(javaClass.name)

    class NoOwnedShopError(message: String) : RuntimeException(message)
    class PasswordNotSetError(message: String) : RuntimeException(message)

    @Suppress("UNCHECKED_CAST")
    private fun establishSession(body: Map<String, Any?>): CredentialStore.OwnerSession {
        val tokens = body["tokens"] as Map<String, Any?>
        val user = body["user"] as Map<String, Any?>
        val ownedShopIds = (user["ownedShopIds"] as? List<String>).orEmpty()
        if (ownedShopIds.isEmpty()) {
            throw NoOwnedShopError("This account does not own a shop. Staff accounts are not yet supported here.")
        }

        val session = CredentialStore.OwnerSession(
            accessToken = tokens["accessToken"] as String,
            refreshToken = tokens["refreshToken"] as String,
            shopId = ownedShopIds[0],
            shopName = null,
        )
        CredentialStore.saveOwnerSession(session)
        return session
    }

    fun loginWithPassword(api: PrintlyApiClient, identifier: String, password: String): CredentialStore.OwnerSession {
        val body = try {
            api.loginWithPassword(identifier, password)
        } catch (exc: ApiError) {
            if (exc.code == "PASSWORD_NOT_SET") {
                throw PasswordNotSetError("No password has been set for this account yet. Verify by phone, then set one.")
            }
            throw exc
        }
        val session = establishSession(body)
        log.info("owner_signed_in method=password")
        return session
    }

    fun finishLogin(api: PrintlyApiClient, widgetAccessToken: String): CredentialStore.OwnerSession {
        val body = api.verifyWidget(widgetAccessToken)
        val session = establishSession(body)
        log.info("owner_signed_in method=otp")
        return session
    }

    fun setPassword(api: PrintlyApiClient, session: CredentialStore.OwnerSession, password: String) {
        api.setPassword(session, password)
        log.info("owner_password_set")
    }

    /**
     * Idempotent: returns the existing agent credential if this PC has
     * already been paired to this shop, otherwise pairs and exchanges
     * silently - no code for the owner to copy between two apps, since this
     * app is both the one minting the code and the one consuming it.
     */
    fun ensurePaired(api: PrintlyApiClient, session: CredentialStore.OwnerSession): CredentialStore.AgentCredential {
        val existing = CredentialStore.loadAgentCredential()
        if (existing != null && existing.shopId == session.shopId) return existing

        val pairing = api.pair(session)
        val exchanged = api.exchange(
            code = pairing.code,
            agentName = hostName(),
            machineFingerprint = machineFingerprint(),
            agentVersion = AGENT_VERSION,
        )

        val credential = CredentialStore.AgentCredential(exchanged.agentId, exchanged.shopId, exchanged.secret)
        CredentialStore.saveAgentCredential(credential)
        log.info("agent_paired agentId=${credential.agentId} shopId=${credential.shopId}")
        return credential
    }

    private fun hostName(): String = runCatching { java.net.InetAddress.getLocalHost().hostName }.getOrDefault("Print Agent")

    /** Not a security boundary - purely diagnostic. A fresh random id, never the real machine identity. */
    private fun machineFingerprint(): String = UUID.randomUUID().toString().replace("-", "")
}
