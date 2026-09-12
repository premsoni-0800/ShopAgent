package com.printly.agent.net

import com.fasterxml.jackson.databind.DeserializationFeature
import com.fasterxml.jackson.databind.ObjectMapper
import com.fasterxml.jackson.datatype.jsr310.JavaTimeModule
import com.fasterxml.jackson.module.kotlin.jacksonObjectMapper
import com.fasterxml.jackson.module.kotlin.readValue
import com.printly.agent.credentials.CredentialStore
import com.printly.agent.models.*
import okhttp3.HttpUrl.Companion.toHttpUrl
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response
import java.time.Duration

class ApiError(val statusCode: Int, val code: String?, message: String) : RuntimeException("$statusCode $code: $message")

private val JSON = "application/json".toMediaType()

/**
 * HTTP calls to the Printly backend - direct port of the Python agent's
 * `api_client.py`. Two distinct credential shapes, matching the backend's two
 * disjoint authentication filters: owner-session calls carry the owner's own
 * JWT and only ever reach owner-facing routes; agent calls carry the
 * `<agentId>.<secret>` bearer and only ever reach the print-agent device routes.
 */
class PrintlyApiClient(val baseUrl: String) {

    val http: OkHttpClient = OkHttpClient.Builder()
        .callTimeout(Duration.ofSeconds(30))
        .build()

    val mapper: ObjectMapper = jacksonObjectMapper()
        .registerModule(JavaTimeModule())
        .disable(DeserializationFeature.FAIL_ON_UNKNOWN_PROPERTIES)

    private fun url(path: String) = (baseUrl.trimEnd('/') + path)

    // --- owner-session calls ---

    fun verifyWidget(accessToken: String): Map<String, Any?> =
        postJson(url("/api/v1/auth/verify-widget"), mapOf("accessToken" to accessToken))

    /** `identifier` is a phone number or an email address - the backend branches on `@`. */
    fun loginWithPassword(identifier: String, password: String): Map<String, Any?> =
        postJson(url("/api/v1/auth/login-password"), mapOf("identifier" to identifier, "password" to password))

    fun setPassword(session: CredentialStore.OwnerSession, password: String) {
        request(
            Request.Builder().url(url("/api/v1/auth/set-password"))
                .post(mapper.writeValueAsString(mapOf("password" to password)).toRequestBody(JSON))
                .headers(ownerHeaders(session)).build(),
        )
    }

    fun refreshOwnerSession(refreshToken: String): Map<String, Any?> =
        postJson(url("/api/v1/auth/refresh"), mapOf("refreshToken" to refreshToken))

    fun pair(session: CredentialStore.OwnerSession): PairingCodeResponse {
        val response = request(
            Request.Builder().url(url("/api/v1/shop/${session.shopId}/print-agents/pair"))
                .post("".toRequestBody(null)).headers(ownerHeaders(session)).build(),
        )
        return mapper.readValue(response)
    }

    fun listPrintAgents(session: CredentialStore.OwnerSession): List<PrintAgentSummary> = mapper.readValue(
        request(
            Request.Builder().url(url("/api/v1/shop/${session.shopId}/print-agents"))
                .headers(ownerHeaders(session)).build(),
        ),
    )

    fun revokePrintAgent(session: CredentialStore.OwnerSession, agentId: String) {
        request(
            Request.Builder().url(url("/api/v1/shop/${session.shopId}/print-agents/$agentId/revoke"))
                .post("".toRequestBody(null)).headers(ownerHeaders(session)).build(),
        )
    }

    fun ownerGet(session: CredentialStore.OwnerSession, path: String): Any =
        mapper.readValue(request(Request.Builder().url(url(path)).headers(ownerHeaders(session)).build()))

    fun ownerPut(session: CredentialStore.OwnerSession, path: String, body: Map<String, Any?>): Any =
        mapper.readValue(
            request(
                Request.Builder().url(url(path)).put(mapper.writeValueAsString(body).toRequestBody(JSON))
                    .headers(ownerHeaders(session)).build(),
            ),
        )

    fun ownerPost(session: CredentialStore.OwnerSession, path: String, body: Map<String, Any?>) {
        request(
            Request.Builder().url(url(path)).post(mapper.writeValueAsString(body).toRequestBody(JSON))
                .headers(ownerHeaders(session)).build(),
        )
    }

    // --- device (agent-credential) calls ---

    fun exchange(code: String, agentName: String, machineFingerprint: String?, agentVersion: String?): PrintAgentExchangeResponse {
        val req = PrintAgentExchangeRequest(code, agentName, machineFingerprint, agentVersion)
        val response = postJsonRaw(url("/api/v1/print-agents/exchange"), req)
        return mapper.readValue(response)
    }

    fun heartbeat(credential: CredentialStore.AgentCredential, agentVersion: String): PrintAgentHeartbeatResponse {
        val response = request(
            Request.Builder().url(url("/api/v1/print-agent/heartbeat"))
                .post(mapper.writeValueAsString(PrintAgentHeartbeatRequest(agentVersion)).toRequestBody(JSON))
                .headers(agentHeaders(credential)).build(),
        )
        return mapper.readValue(response)
    }

    fun syncPrinters(credential: CredentialStore.AgentCredential, request: PrinterSyncRequest) {
        this.request(
            Request.Builder().url(url("/api/v1/print-agent/printers"))
                .put(mapper.writeValueAsString(request).toRequestBody(JSON))
                .headers(agentHeaders(credential)).build(),
        )
    }

    fun outstandingJobs(credential: CredentialStore.AgentCredential): List<PrintJobSummary> {
        val response = request(
            Request.Builder().url(url("/api/v1/print-agent/jobs")).headers(agentHeaders(credential)).build(),
        )
        return mapper.readValue(response)
    }

    fun jobDetail(credential: CredentialStore.AgentCredential, jobId: String): PrintJobDetail = mapper.readValue(
        request(Request.Builder().url(url("/api/v1/print-agent/jobs/$jobId")).headers(agentHeaders(credential)).build()),
    )

    fun claimJob(credential: CredentialStore.AgentCredential, jobId: String): PrintJobDetail = mapper.readValue(
        request(
            Request.Builder().url(url("/api/v1/print-agent/jobs/$jobId/claim"))
                .post("".toRequestBody(null)).headers(agentHeaders(credential)).build(),
        ),
    )

    fun downloadUrls(credential: CredentialStore.AgentCredential, jobId: String): PrintJobDownloadUrls = mapper.readValue(
        request(
            Request.Builder().url(url("/api/v1/print-agent/jobs/$jobId/download-url"))
                .post("".toRequestBody(null)).headers(agentHeaders(credential)).build(),
        ),
    )

    fun reportStatus(
        credential: CredentialStore.AgentCredential,
        jobId: String,
        status: PrintJobStatus,
        error: String? = null,
        reasonCode: PrintJobFailureReason? = null,
    ) {
        request(
            Request.Builder().url(url("/api/v1/print-agent/jobs/$jobId/status"))
                .post(mapper.writeValueAsString(PrintJobStatusUpdateRequest(status, error, reasonCode)).toRequestBody(JSON))
                .headers(agentHeaders(credential)).build(),
        )
    }

    fun reportProgress(credential: CredentialStore.AgentCredential, jobId: String, stage: PrintJobProgressStage) {
        request(
            Request.Builder().url(url("/api/v1/print-agent/jobs/$jobId/progress"))
                .post(mapper.writeValueAsString(PrintJobProgressRequest(stage)).toRequestBody(JSON))
                .headers(agentHeaders(credential)).build(),
        )
    }

    // -------------------------------------------------------------------

    private fun ownerHeaders(session: CredentialStore.OwnerSession) =
        okhttp3.Headers.headersOf("Authorization", "Bearer ${session.accessToken}")

    private fun agentHeaders(credential: CredentialStore.AgentCredential) =
        okhttp3.Headers.headersOf("Authorization", "Bearer ${credential.bearerToken}")

    private fun postJson(fullUrl: String, body: Map<String, Any?>): Map<String, Any?> =
        mapper.readValue(request(Request.Builder().url(fullUrl).post(mapper.writeValueAsString(body).toRequestBody(JSON)).build()))

    private fun postJsonRaw(fullUrl: String, body: Any): String =
        request(Request.Builder().url(fullUrl).post(mapper.writeValueAsString(body).toRequestBody(JSON)).build())

    /**
     * Every error this backend returns is wrapped as
     * `{"error": {"code": ..., "message": ...}}` - reading the wrong level
     * here would silently leave [ApiError.code] null for every failure, which
     * breaks every caller that branches on it (owner-session refresh-on-
     * expiry, PASSWORD_NOT_SET detection, PRINT_JOB_ALREADY_CLAIMED race
     * handling) - the same pitfall the Python client's own docstring warns
     * about.
     */
    private fun request(req: Request): String {
        http.newCall(req).execute().use { response ->
            val bodyString = response.body?.string().orEmpty()
            if (response.isSuccessful) return bodyString
            var code: String? = null
            var message = bodyString
            try {
                val parsed = mapper.readValue<Map<String, Any?>>(bodyString)
                @Suppress("UNCHECKED_CAST")
                val error = (parsed["error"] as? Map<String, Any?>) ?: parsed
                code = error["code"] as? String
                message = (error["message"] as? String) ?: message
            } catch (_: Exception) {
                // Non-JSON error body - fall back to the raw text.
            }
            throw ApiError(response.code, code, message)
        }
    }
}
