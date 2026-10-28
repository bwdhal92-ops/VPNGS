package com.example.models

enum class ConnectionStatus {
    DISCONNECTED, CONNECTING, CONNECTED, DISCONNECTING
}

data class ConnectionStats(
    val uploadSpeedMbps: Double = 0.0,
    val downloadSpeedMbps: Double = 0.0,
    val durationSeconds: Long = 0,
    val totalBytesSent: Long = 0,
    val totalBytesReceived: Long = 0
)
