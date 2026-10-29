package com.example.services

import com.example.models.VpnServer
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.withContext
import java.net.InetSocketAddress
import java.net.Socket

class ServerScanService {

    suspend fun scanServers(
        servers: List<VpnServer>,
        onProgress: (scanned: Int, total: Int) -> Unit = { _, _ -> }
    ): List<VpnServer> = withContext(Dispatchers.IO) {
        val total = servers.size
        var completed = 0

        servers.chunked(20).flatMap { batch ->
            batch.map { server ->
                async {
                    val scanned = scanSingleServer(server)
                    synchronized(this@ServerScanService) {
                        completed++
                        onProgress(completed, total)
                    }
                    scanned
                }
            }.awaitAll()
        }
    }

    suspend fun scanSingleServer(server: VpnServer): VpnServer = withContext(Dispatchers.IO) {
        if (server.id == "auto") return@withContext server

        val latency = try {
            val start = System.nanoTime()
            Socket().use { socket ->
                socket.connect(InetSocketAddress(server.host, server.port), 1800)
            }
            (System.nanoTime() - start) / 1_000_000.0
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            null
        }
        server.copy(
            tcpLatencyMs = latency?.let { Math.round(it * 10.0) / 10.0 },
            tcpReachable = latency != null
        )
    }

    fun sortServersByPerformance(servers: List<VpnServer>, filter: String): List<VpnServer> {
        val autoNode = servers.find { it.id == "auto" }
        val regularServers = servers.filter { it.id != "auto" }

        val sorted = regularServers.sortedWith(
            compareBy<VpnServer> {
                when (filter) {
                    "games" -> if (it.udpProbePassed == true) 0 else 1
                    "browse" -> if (it.webProbePassed == true) 0 else 1
                    "stream" -> if (it.downloadMbps != null) 0 else 1
                    else -> 0
                }
            }.thenBy {
                if (filter == "stream") -(it.downloadMbps ?: 0.0)
                else it.tcpLatencyMs ?: Double.MAX_VALUE
            }.thenBy { it.displayName }
        )

        val result = mutableListOf<VpnServer>()
        if (autoNode != null) {
            val best = getRecommendedServer(regularServers, filter)
            val label = when {
                best == null -> "لا توجد نتيجة اختبار حقيقية بعد"
                filter == "games" -> "أفضل UDP مختبر: ${best.displayName} (${best.udpLatencyMs?.toInt()} ms)"
                filter == "stream" -> "أفضل تنزيل مختبر: ${best.displayName} (${best.downloadMbps} Mbps)"
                else -> "أقل زمن TCP مقاس: ${best.displayName} (${best.tcpLatencyMs?.toInt() ?: 0} ms)"
            }
            autoNode.remark = label
            result.add(autoNode)
        }
        result.addAll(sorted)
        return result
    }

    fun getRecommendedServer(servers: List<VpnServer>, filter: String): VpnServer? {
        val regular = servers.filter { it.id != "auto" && it.tcpReachable == true }
        if (regular.isEmpty()) return null

        val targetPool = when (filter) {
            "games" -> regular.filter { it.udpProbePassed == true }
            "browse" -> regular.filter { it.webProbePassed == true }
            "stream" -> regular.filter { it.downloadMbps != null }
            else -> regular
        }
        if (targetPool.isEmpty()) return null

        return targetPool.minByOrNull {
            if (filter == "stream") -(it.downloadMbps ?: 0.0)
            else it.tcpLatencyMs ?: Double.MAX_VALUE
        } ?: regular.firstOrNull()
    }
}
