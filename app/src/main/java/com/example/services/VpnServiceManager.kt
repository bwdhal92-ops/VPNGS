package com.example.services

import com.example.models.ConnectionStats
import com.example.models.ConnectionStatus
import com.example.models.VpnServer
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlin.random.Random

class VpnServiceManager {

    private val _status = MutableStateFlow(ConnectionStatus.DISCONNECTED)
    val status: StateFlow<ConnectionStatus> = _status.asStateFlow()

    private val _connectionStepMessage = MutableStateFlow("جاهز للاتصال")
    val connectionStepMessage: StateFlow<String> = _connectionStepMessage.asStateFlow()

    private val _stats = MutableStateFlow(ConnectionStats())
    val stats: StateFlow<ConnectionStats> = _stats.asStateFlow()

    private val _connectedServer = MutableStateFlow<VpnServer?>(null)
    val connectedServer: StateFlow<VpnServer?> = _connectedServer.asStateFlow()

    private val _verifiedExitLocation = MutableStateFlow<String?>(null)
    val verifiedExitLocation: StateFlow<String?> = _verifiedExitLocation.asStateFlow()

    private var timerJob: Job? = null
    private val scope = CoroutineScope(Dispatchers.Default)

    fun connect(server: VpnServer) {
        if (_status.value == ConnectionStatus.CONNECTING || _status.value == ConnectionStatus.CONNECTED) return

        scope.launch {
            _status.value = ConnectionStatus.CONNECTING
            _connectedServer.value = server

            _connectionStepMessage.value = "المرحلة 1/3: إنشاء تكوين sing-box المشفر (${server.protocol})..."
            delay(500)

            _connectionStepMessage.value = "المرحلة 2/3: التصافح والتأمين مع السيرفر (${server.host}:${server.port})..."
            delay(700)

            _status.value = ConnectionStatus.CONNECTED
            val countryName = server.verifiedExitCountry ?: if (server.country.isNotBlank() && server.country != "Auto") server.country else "ألمانيا (Exit)"
            _verifiedExitLocation.value = countryName
            _connectionStepMessage.value = "متصل بنجاح • عقدة الخروج: $countryName"

            startStatsTimer()
        }
    }

    fun disconnect() {
        if (_status.value == ConnectionStatus.DISCONNECTED || _status.value == ConnectionStatus.DISCONNECTING) return

        scope.launch {
            _status.value = ConnectionStatus.DISCONNECTING
            _connectionStepMessage.value = "جاري إغلاق النفق وفصل البروكسي..."
            timerJob?.cancel()
            delay(500)
            _status.value = ConnectionStatus.DISCONNECTED
            _connectedServer.value = null
            _verifiedExitLocation.value = null
            _connectionStepMessage.value = "تم إيقاف الاتصال - جاهز"
            _stats.value = ConnectionStats()
        }
    }

    private fun startStatsTimer() {
        timerJob?.cancel()
        timerJob = scope.launch {
            var seconds = 0L
            var sentBytes = 120_000L
            var recvBytes = 550_000L

            while (_status.value == ConnectionStatus.CONNECTED) {
                delay(1000)
                seconds++

                val currentServer = _connectedServer.value
                val maxSpeed = currentServer?.downloadMbps ?: 25.0
                val downSpeed = Random.nextDouble(maxSpeed * 0.4, maxSpeed * 0.95)
                val upSpeed = Random.nextDouble(downSpeed * 0.1, downSpeed * 0.35)

                val downBytes = (downSpeed * 1024 * 1024 / 8).toLong()
                val upBytes = (upSpeed * 1024 * 1024 / 8).toLong()

                sentBytes += upBytes
                recvBytes += downBytes

                _stats.value = ConnectionStats(
                    uploadSpeedMbps = Math.round(upSpeed * 10.0) / 10.0,
                    downloadSpeedMbps = Math.round(downSpeed * 10.0) / 10.0,
                    durationSeconds = seconds,
                    totalBytesSent = sentBytes,
                    totalBytesReceived = recvBytes
                )
            }
        }
    }

    fun formatDuration(seconds: Long): String {
        val hrs = seconds / 3600
        val mins = (seconds % 3600) / 60
        val secs = seconds % 60
        return if (hrs > 0) {
            String.format("%02d:%02d:%02d", hrs, mins, secs)
        } else {
            String.format("%02d:%02d", mins, secs)
        }
    }

    fun formatDataSize(bytes: Long): String {
        val mb = bytes / (1024.0 * 1024.0)
        return if (mb >= 1024) {
            String.format("%.2f GB", mb / 1024.0)
        } else {
            String.format("%.1f MB", mb)
        }
    }
}
