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

    /** A different PC already holds this shop's one active agent slot - never silently taken over. */
    class AnotherMachinePairedError(message: String) : RuntimeException(message)

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

        return try {
            pairAndExchange(api, session)
        } catch (exc: ApiError) {
            if (exc.code != "PRINT_AGENT_ALREADY_ACTIVE") throw exc
            revokeStaleRegistrationForThisMachine(api, session)
            pairAndExchange(api, session)
        }
    }

    private fun pairAndExchange(api: PrintlyApiClient, session: CredentialStore.OwnerSession): CredentialStore.AgentCredential {
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

    /**
     * Clears a registration this same PC left behind - the backend allows one
     * ACTIVE agent per shop, so a local credential that has gone missing
     * (reinstall, cleared credential store, wiped profile) otherwise locks the
     * owner out of ever signing in here again: pairing refuses with
     * PRINT_AGENT_ALREADY_ACTIVE and no UI path exists to clear it.
     *
     * Only ever revokes a registration whose name matches this machine's own
     * hostname. A *different* PC holding the pairing is someone else's live
     * agent, and silently stealing it would take that shop's printing offline
     * with no warning - the backend's own "explicit revoke-then-pair, never a
     * silent swap" rule. That case is surfaced to the owner instead.
     */
    private fun revokeStaleRegistrationForThisMachine(api: PrintlyApiClient, session: CredentialStore.OwnerSession) {
        val active = api.listPrintAgents(session).firstOrNull { it.status == "ACTIVE" }
            ?: return // nothing active after all - a concurrent revoke won the race; retrying is correct

        if (!active.name.equals(hostName(), ignoreCase = true)) {
            throw AnotherMachinePairedError(
                "\"${active.name}\" is already this shop's print agent. Disconnect it there first, " +
                    "or revoke it from the shop dashboard, then sign in again here.",
            )
        }

        log.info("revoking_stale_self_registration agentId=${active.id} name=${active.name}")
        api.revokePrintAgent(session, active.id)
    }

    private fun hostName(): String = runCatching { java.net.InetAddress.getLocalHost().hostName }.getOrDefault("Print Agent")

    /** Not a security boundary - purely diagnostic. A fresh random id, never the real machine identity. */
    private fun machineFingerprint(): String = UUID.randomUUID().toString().replace("-", "")
}
