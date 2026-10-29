package com.example.services

import com.example.models.ConnectionStats
import com.example.models.ConnectionStatus
import com.example.models.VpnServer
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/**
 * Connection facade for the Android UI.
 * The project currently contains no Android VPN engine binding (libbox/Xray) or VpnService
 * packet bridge, so it must never report a successful connection until that engine is wired.
 */
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

    fun connect(server: VpnServer) {
        _connectedServer.value = null
        _verifiedExitLocation.value = null
        _stats.value = ConnectionStats()
        _status.value = ConnectionStatus.ERROR
        _connectionStepMessage.value = "تعذر الاتصال: محرك VPN الخاص بـ Android غير مضمّن بعد. لم يتم إنشاء نفق."
    }

    fun disconnect() {
        _status.value = ConnectionStatus.DISCONNECTED
        _connectedServer.value = null
        _verifiedExitLocation.value = null
        _stats.value = ConnectionStats()
        _connectionStepMessage.value = "تم الإيقاف • لا يوجد نفق نشط"
    }

    fun formatDuration(seconds: Long): String {
        val hrs = seconds / 3600
        val mins = (seconds % 3600) / 60
        val secs = seconds % 60
        return if (hrs > 0) String.format("%02d:%02d:%02d", hrs, mins, secs) else String.format("%02d:%02d", mins, secs)
    }

    fun formatDataSize(bytes: Long): String {
        val mb = bytes / (1024.0 * 1024.0)
        return if (mb >= 1024) String.format("%.2f GB", mb / 1024.0) else String.format("%.1f MB", mb)
    }
}
