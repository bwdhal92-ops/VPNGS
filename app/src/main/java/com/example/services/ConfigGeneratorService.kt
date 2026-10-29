package com.example.services

import com.example.models.VpnProtocol
import com.example.models.VpnServer
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.buildJsonArray
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put

class ConfigGeneratorService {

    fun generateSingBoxConfig(server: VpnServer, socksPort: Int = 10808, httpPort: Int = 10809, tunMode: Boolean = false): String {
        val outboundObj = buildOutbound(server)

        val root = buildJsonObject {
            put("log", buildJsonObject {
                put("level", "info")
                put("timestamp", true)
            })

            put("inbounds", buildJsonArray {
                if (tunMode) {
                    add(buildJsonObject {
                        put("type", "tun")
                        put("tag", "tun-in")
                        put("interface_name", "tun0")
                        put("inet4_address", "172.19.0.1/30")
                        put("auto_route", true)
                        put("strict_route", true)
                        put("stack", "mixed")
                        put("sniff", true)
                    })
                }
                add(buildJsonObject {
                    put("type", "mixed")
                    put("tag", "mixed-in")
                    put("listen", "127.0.0.1")
                    put("listen_port", httpPort)
                    put("sniff", true)
                })
                add(buildJsonObject {
                    put("type", "socks")
                    put("tag", "socks-in")
                    put("listen", "127.0.0.1")
                    put("listen_port", socksPort)
                    put("sniff", true)
                })
            })

            put("outbounds", buildJsonArray {
                add(outboundObj)
                add(buildJsonObject {
                    put("type", "direct")
                    put("tag", "direct")
                })
                add(buildJsonObject {
                    put("type", "dns")
                    put("tag", "dns-out")
                })
            })

            put("dns", buildJsonObject {
                put("servers", buildJsonArray {
                    add(buildJsonObject {
                        put("tag", "remote")
                        put("address", "https://1.1.1.1/dns-query")
                        put("detour", "proxy")
                    })
                    add(buildJsonObject {
                        put("tag", "local")
                        put("address", "223.5.5.5")
                        put("detour", "direct")
                    })
                })
            })
        }

        return root.toString()
    }

    private fun buildOutbound(server: VpnServer): JsonObject {
        return buildJsonObject {
            val typeStr = when (server.protocol) {
                VpnProtocol.VLESS -> "vless"
                VpnProtocol.VMESS -> "vmess"
                VpnProtocol.TROJAN -> "trojan"
                VpnProtocol.HYSTERIA2 -> "hysteria2"
                VpnProtocol.TUIC -> "tuic"
                else -> "vless"
            }
            put("type", typeStr)
            put("tag", "proxy")
            put("server", server.host)
            put("server_port", server.port)

            when (server.protocol) {
                VpnProtocol.VLESS -> {
                    put("uuid", server.params["uuid"] ?: "")
                    val flow = server.params["flow"]
                    if (!flow.isNull_or_empty()) put("flow", flow)
                }
                VpnProtocol.VMESS -> {
                    put("uuid", server.params["uuid"] ?: "")
                    put("security", server.params["security"] ?: "auto")
                    val alterId = server.params["aid"]?.toIntOrNull() ?: 0
                    put("alter_id", alterId)
                }
                VpnProtocol.TROJAN -> {
                    put("password", server.params["password"] ?: server.params["uuid"] ?: "")
                }
                VpnProtocol.HYSTERIA2 -> {
                    put("password", server.params["userinfo"] ?: server.params["password"] ?: "")
                }
                VpnProtocol.TUIC -> {
                    put("uuid", server.params["uuid"] ?: "")
                    put("password", server.params["userinfo"] ?: server.params["password"] ?: "")
                }
                else -> {}
            }

            val security = server.params["security"] ?: server.params["tls"]
            if (security == "tls" || security == "reality" || server.params.containsKey("sni")) {
                put("tls", buildJsonObject {
                    put("enabled", true)
                    val sni = server.params["sni"] ?: server.params["host"]
                    if (!sni.isNullOrBlank()) put("server_name", sni)
                    val fp = server.params["fp"]
                    if (!fp.isNullOrBlank()) put("utls", buildJsonObject {
                        put("enabled", true)
                        put("fingerprint", fp)
                    })
                })
            }

            val net = server.params["net"] ?: server.params["type"]
            if (net == "ws") {
                put("transport", buildJsonObject {
                    put("type", "ws")
                    val path = server.params["path"]
                    if (!path.isNullOrBlank()) put("path", path)
                    val host = server.params["host"]
                    if (!host.isNullOrBlank()) put("headers", buildJsonObject { put("Host", host) })
                })
            } else if (net == "grpc") {
                put("transport", buildJsonObject {
                    put("type", "grpc")
                    val serviceName = server.params["serviceName"] ?: server.params["path"]
                    if (!serviceName.isNullOrBlank()) put("service_name", serviceName)
                })
            }
        }
    }

    private fun String?.isNull_or_empty(): Boolean = this.isNullOrEmpty()
}
