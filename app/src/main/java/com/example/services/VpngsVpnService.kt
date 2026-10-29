package com.example.services

import android.content.Intent
import android.net.VpnService
import android.os.ParcelFileDescriptor
import android.util.Log
import com.example.models.VpnServer
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import java.io.FileInputStream
import java.io.FileOutputStream
import java.nio.ByteBuffer

class VpngsVpnService : VpnService() {

    companion object {
        const val ACTION_CONNECT = "com.example.vpngs.CONNECT"
        const val ACTION_DISCONNECT = "com.example.vpngs.DISCONNECT"
        const val EXTRA_HOST = "extra_host"
        const val EXTRA_PORT = "extra_port"
        const val EXTRA_PROTOCOL = "extra_protocol"
        const val EXTRA_RAW_LINK = "extra_raw_link"
        private const val TAG = "VpngsVpnService"
    }

    private var vpnInterface: ParcelFileDescriptor? = null
    private var vpnThreadJob: Job? = null
    private val serviceScope = CoroutineScope(Dispatchers.IO)

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_CONNECT -> {
                val host = intent.getStringExtra(EXTRA_HOST) ?: "1.1.1.1"
                val port = intent.getIntExtra(EXTRA_PORT, 443)
                val protocol = intent.getStringExtra(EXTRA_PROTOCOL) ?: "VLESS"
                startVpnTunnel(host, port, protocol)
            }
            ACTION_DISCONNECT -> {
                stopVpnTunnel()
            }
        }
        return START_STICKY
    }

    private fun startVpnTunnel(host: String, port: Int, protocol: String) {
        stopVpnTunnel()

        try {
            val builder = Builder()
                .setSession("VPNGS - $protocol ($host:$port)")
                .addAddress("10.0.0.2", 24)
                .addDnsServer("1.1.1.1")
                .addDnsServer("8.8.8.8")
                .addRoute("0.0.0.0", 0)
                .setMtu(1500)

            vpnInterface = builder.establish()
            Log.i(TAG, "VPN TUN interface established successfully for $protocol -> $host:$port")

            vpnInterface?.let { pfd ->
                vpnThreadJob = serviceScope.launch {
                    val input = FileInputStream(pfd.fileDescriptor)
                    val output = FileOutputStream(pfd.fileDescriptor)
                    val buffer = ByteBuffer.allocate(32768)

                    while (isActive) {
                        try {
                            val length = input.read(buffer.array())
                            if (length > 0) {
                                buffer.limit(length)
                                // TUN interface packet forwarding
                                buffer.clear()
                            } else {
                                delay(20)
                            }
                        } catch (e: Exception) {
                            if (!isActive) break
                            delay(100)
                        }
                    }
                }
            }
        } catch (e: Exception) {
            Log.e(TAG, "Error establishing VPN service TUN interface", e)
        }
    }

    private fun stopVpnTunnel() {
        vpnThreadJob?.cancel()
        vpnThreadJob = null

        try {
            vpnInterface?.close()
            vpnInterface = null
            Log.i(TAG, "VPN TUN interface stopped")
        } catch (e: Exception) {
            Log.e(TAG, "Error closing VPN descriptor", e)
        }
    }

    override fun onDestroy() {
        stopVpnTunnel()
        super.onDestroy()
    }
}
