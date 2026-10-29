package com.example.models

import kotlinx.serialization.Serializable
import java.util.UUID

@Serializable
data class UserProfile(
    val displayName: String = "",
    val installId: String = UUID.randomUUID().toString().replace("-", ""),
    val createdAtEpochMs: Long = System.currentTimeMillis(),
    val firebaseUid: String = "",
    val activeSessionId: String? = null,
    val activeProfileId: String? = null,
    val activeSessionStartedAtEpochMs: Long? = null,
    val lastHeartbeatAtEpochMs: Long? = null
)
