package com.example.models

import kotlinx.serialization.Serializable

@Serializable
data class AppSettings(
    val tunMode: Boolean = true,
    val autoRefresh: Boolean = true,
    val refreshMinutes: Int = 60,
    val selectedServerId: String = "auto",
    val socksPort: Int = 10808,
    val httpPort: Int = 10809,
    val language: String = "ar",
    val showCloudflareServers: Boolean = false,
    val customSubscriptions: List<String> = emptyList(),
    val themeMode: String = "dark" // "dark", "light", "system"
)
