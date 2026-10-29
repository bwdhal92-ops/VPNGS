package com.example.services

import android.content.Context
import android.os.Build
import com.example.BuildConfig
import com.example.models.UserProfile
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.FormBody
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import org.json.JSONObject
import java.security.MessageDigest
import java.util.Locale
import java.util.UUID
import java.util.concurrent.TimeUnit

class FirebaseTelemetryRepository(context: Context) {
    private companion object {
        const val API_KEY = "AIzaSyBGsqWBMGg5DxfmzuvAyypJ_LqYFLp9ZtI"
        const val PROJECT_ID = "invertible-pipe-452821-h7"
        const val COLLECTION = "VBNGS"
        const val BASE = "https://firestore.googleapis.com/v1/projects/$PROJECT_ID/databases/(default)/documents"
    }

    private data class Auth(val uid: String, val idToken: String, val refreshToken: String, val expiresAt: Long)
    private val tokenStore = SecretTokenStore(context)
    private val http = OkHttpClient.Builder()
        .connectTimeout(6, TimeUnit.SECONDS)
        .readTimeout(6, TimeUnit.SECONDS)
        .callTimeout(10, TimeUnit.SECONDS)
        .build()
    @Volatile private var cachedAuth: Auth? = null

    suspend fun startSession(profile: UserProfile): UserProfile = withContext(Dispatchers.IO) {
        val auth = getAuth()
        val profileId = profile.installId
        val existing = getDocument(auth, "$COLLECTION/${auth.uid}/profiles/$profileId")
        val fields = existing?.optJSONObject("fields")
        val banned = fields?.optJSONObject("isBanned")?.optBoolean("booleanValue", false) ?: false
        if (banned) {
            val reason = fields?.optJSONObject("banReason")?.optString("stringValue").orEmpty()
            throw AccountBannedException(reason.ifBlank { "تم إيقاف هذا الحساب بواسطة الإدارة." })
        }

        // Close a session interrupted by process death using its last heartbeat.
        if (profile.firebaseUid == auth.uid && profile.activeSessionId != null && profile.activeProfileId != null) {
            val started = profile.activeSessionStartedAtEpochMs ?: profile.createdAtEpochMs
            val ended = profile.lastHeartbeatAtEpochMs ?: started
            patch(auth, "$COLLECTION/${auth.uid}/profiles/${profile.activeProfileId}/sessions/${profile.activeSessionId}", mapOf(
                "endedAtUtc" to timestamp(ended),
                "durationSeconds" to integer(((ended - started).coerceAtLeast(0) / 1000)),
                "endReason" to string("unexpected_exit")
            ))
        }

        val now = System.currentTimeMillis()
        val profileFields = mutableMapOf(
            "displayName" to string(profile.displayName),
            "installId" to string(profile.installId),
            "firebaseUid" to string(auth.uid),
            "profileId" to string(profileId),
            "appVersion" to string(BuildConfig.VERSION_NAME),
            "lastSeenAtUtc" to timestamp(now),
            "platform" to string("Android ${Build.VERSION.SDK_INT}")
        )
        if (existing == null) profileFields["firstSeenAtUtc"] = timestamp(profile.createdAtEpochMs)
        patch(auth, "$COLLECTION/${auth.uid}/profiles/$profileId", profileFields)
        val sessionId = UUID.randomUUID().toString().replace("-", "")
        patch(auth, "$COLLECTION/${auth.uid}/profiles/$profileId/sessions/$sessionId", mapOf(
            "startedAtUtc" to timestamp(now),
            "lastHeartbeatAtUtc" to timestamp(now),
            "appVersion" to string(BuildConfig.VERSION_NAME),
            "platform" to string("Android")
        ))
        profile.copy(
            firebaseUid = auth.uid,
            activeSessionId = sessionId,
            activeProfileId = profileId,
            activeSessionStartedAtEpochMs = now,
            lastHeartbeatAtEpochMs = now
        )
    }

    suspend fun heartbeat(profile: UserProfile): UserProfile = withContext(Dispatchers.IO) {
        val sessionId = profile.activeSessionId ?: return@withContext profile
        val profileId = profile.activeProfileId ?: profile.installId
        val auth = getAuth()
        val now = System.currentTimeMillis()
        patch(auth, "$COLLECTION/${auth.uid}/profiles/$profileId/sessions/$sessionId", mapOf("lastHeartbeatAtUtc" to timestamp(now)))
        patch(auth, "$COLLECTION/${auth.uid}/profiles/$profileId", mapOf("lastSeenAtUtc" to timestamp(now)))
        profile.copy(lastHeartbeatAtEpochMs = now)
    }

    suspend fun endSession(profile: UserProfile, reason: String = "normal_exit") = withContext(Dispatchers.IO) {
        val sessionId = profile.activeSessionId ?: return@withContext
        val profileId = profile.activeProfileId ?: profile.installId
        val auth = getAuth()
        val now = System.currentTimeMillis()
        val started = profile.activeSessionStartedAtEpochMs ?: now
        patch(auth, "$COLLECTION/${auth.uid}/profiles/$profileId/sessions/$sessionId", mapOf(
            "endedAtUtc" to timestamp(now),
            "lastHeartbeatAtUtc" to timestamp(now),
            "durationSeconds" to integer(((now - started).coerceAtLeast(0) / 1000)),
            "endReason" to string(reason)
        ))
        patch(auth, "$COLLECTION/${auth.uid}/profiles/$profileId", mapOf("lastSeenAtUtc" to timestamp(now)))
    }

    suspend fun reportError(profile: UserProfile, context: String, error: Throwable) = withContext(Dispatchers.IO) {
        val sessionId = profile.activeSessionId ?: return@withContext
        val profileId = profile.activeProfileId ?: profile.installId
        val auth = getAuth()
        val signatureInput = (error::class.java.name + "\n" + (error.stackTrace.firstOrNull()?.toString() ?: ""))
        val signature = MessageDigest.getInstance("SHA-256").digest(signatureInput.toByteArray())
            .take(10).joinToString("") { "%02x".format(Locale.ROOT, it) }
        val eventId = UUID.randomUUID().toString().replace("-", "")
        patch(auth, "$COLLECTION/${auth.uid}/profiles/$profileId/errors/$eventId", mapOf(
            "occurredAtUtc" to timestamp(System.currentTimeMillis()),
            "context" to string(context.take(80)),
            "errorType" to string(error::class.java.simpleName.take(80)),
            "signature" to string(signature),
            "appVersion" to string(BuildConfig.VERSION_NAME)
        ))
    }

    private fun getAuth(): Auth {
        cachedAuth?.takeIf { it.expiresAt > System.currentTimeMillis() + 120_000 }?.let { return it }
        tokenStore.read()?.let { token ->
            try {
                return refresh(token).also { cachedAuth = it }
            } catch (error: Exception) {
                val detail = error.message.orEmpty()
                val invalidRefresh = detail.contains("HTTP 400") &&
                    (detail.contains("INVALID", ignoreCase = true) || detail.contains("USER_NOT_FOUND", ignoreCase = true))
                if (!invalidRefresh) throw error
            }
        }
        return createAnonymousUser().also { cachedAuth = it }
    }

    private fun refresh(token: String): Auth {
        val body = FormBody.Builder().add("grant_type", "refresh_token").add("refresh_token", token).build()
        val request = Request.Builder().url("https://securetoken.googleapis.com/v1/token?key=$API_KEY").post(body).build()
        val data = execute(request)
        return Auth(data.getString("user_id"), data.getString("id_token"), data.getString("refresh_token"),
            System.currentTimeMillis() + data.optString("expires_in", "3600").toLong() * 1000L)
            .also { tokenStore.write(it.refreshToken) }
    }

    private fun createAnonymousUser(): Auth {
        val body = JSONObject().put("returnSecureToken", true).toString().toRequestBody("application/json".toMediaType())
        val request = Request.Builder().url("https://identitytoolkit.googleapis.com/v1/accounts:signUp?key=$API_KEY").post(body).build()
        val data = execute(request)
        val auth = Auth(data.getString("localId"), data.getString("idToken"), data.getString("refreshToken"),
            System.currentTimeMillis() + data.optString("expiresIn", "3600").toLong() * 1000L)
        tokenStore.write(auth.refreshToken)
        return auth
    }

    private fun getDocument(auth: Auth, path: String): JSONObject? {
        val request = Request.Builder().url("$BASE/$path").header("Authorization", "Bearer ${auth.idToken}").get().build()
        http.newCall(request).execute().use { response ->
            if (response.code == 404) return null
            val body = response.body?.string().orEmpty()
            if (!response.isSuccessful) throw IllegalStateException("Firestore read HTTP ${response.code}: $body")
            return JSONObject(body)
        }
    }

    private fun patch(auth: Auth, path: String, values: Map<String, JSONObject>) {
        val url = buildString {
            append("$BASE/$path?")
            append(values.keys.joinToString("&") { "updateMask.fieldPaths=${java.net.URLEncoder.encode(it, "UTF-8")}" })
        }
        val fields = JSONObject()
        values.forEach { (name, value) -> fields.put(name, value) }
        val payload = JSONObject().put("fields", fields).toString().toRequestBody("application/json".toMediaType())
        val request = Request.Builder().url(url).header("Authorization", "Bearer ${auth.idToken}").patch(payload).build()
        http.newCall(request).execute().use { response ->
            val body = response.body?.string().orEmpty()
            if (!response.isSuccessful) throw IllegalStateException("Firestore write HTTP ${response.code}: $body")
        }
    }

    private fun execute(request: Request): JSONObject = http.newCall(request).execute().use { response ->
        val body = response.body?.string().orEmpty()
        if (!response.isSuccessful) throw IllegalStateException("Firebase HTTP ${response.code}: $body")
        JSONObject(body)
    }

    private fun timestamp(epochMs: Long) = JSONObject().put("timestampValue", java.time.Instant.ofEpochMilli(epochMs).toString())
    private fun integer(value: Long) = JSONObject().put("integerValue", value.toString())
    private fun string(value: String) = JSONObject().put("stringValue", value)

    class AccountBannedException(message: String) : Exception(message)
}
