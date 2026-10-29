package com.example.services

import android.content.Context
import android.util.Base64
import com.example.models.VpnProtocol
import com.example.models.VpnServer
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import okhttp3.OkHttpClient
import okhttp3.Request
import java.io.File
import java.net.URI
import java.net.URLDecoder
import java.util.Locale
import java.util.concurrent.TimeUnit

class SubscriptionRepository(private val context: Context) {

    val mirrorUrls = listOf(
        "https://vless.svinakraft.workers.dev/fastest.txt",
        "https://raw.githubusercontent.com/svinakraft-maker/FlareFeed/main/fastest.txt",
        "https://cdn.jsdelivr.net/gh/svinakraft-maker/FlareFeed@main/fastest.txt",
        "https://gh-proxy.com/https://raw.githubusercontent.com/svinakraft-maker/FlareFeed/main/fastest.txt",
        "https://vless.svinakraft.workers.dev/Top500.txt",
        "https://raw.githubusercontent.com/svinakraft-maker/FlareFeed/main/Top500.txt"
    )

    private val httpClient = OkHttpClient.Builder()
        .connectTimeout(8, TimeUnit.SECONDS)
        .readTimeout(8, TimeUnit.SECONDS)
        .addInterceptor { chain ->
            val request = chain.request().newBuilder()
                .header("User-Agent", "VPNGS-Android/1.0")
                .build()
            chain.proceed(request)
        }
        .build()

    private val jsonParser = Json { ignoreUnknownKeys = true }

    private val cacheFile: File
        get() = File(context.cacheDir, "server-list-cache.txt")

    suspend fun loadCachedList(): String? = withContext(Dispatchers.IO) {
        try {
            if (cacheFile.exists() && cacheFile.length() > 0) {
                val text = cacheFile.readText()
                if (text.isNotBlank() && text.contains("://")) {
                    return@withContext text
                }
            }
        } catch (e: Exception) {
            e.printStackTrace()
        }
        null
    }

    suspend fun fetchServers(customUrls: List<String> = emptyList()): Pair<String, List<VpnServer>> = withContext(Dispatchers.IO) {
        val allUrls = customUrls + mirrorUrls
        val fetchedTexts = mutableListOf<String>()
        val sources = mutableListOf<String>()

        coroutineScope {
            val deferreds = allUrls.take(6).map { url ->
                async { fetchSingleMirror(url) }
            }
            deferreds.forEach { def ->
                val res = def.await()
                if (res != null) {
                    fetchedTexts.add(res.second)
                    sources.add(res.first)
                }
            }
        }

        val combinedText = if (fetchedTexts.isNotEmpty()) {
            val joined = fetchedTexts.joinToString("\n")
            try {
                cacheFile.writeText(joined)
            } catch (e: Exception) {
                e.printStackTrace()
            }
            joined
        } else {
            loadCachedList()
        }

        val parsedServers = if (combinedText != null) parseServerList(combinedText) else emptyList()

        if (parsedServers.isNotEmpty()) {
            val sourceLabel = if (sources.isNotEmpty()) sources.take(2).joinToString(" + ") else "المخزن المحلي"
            Pair(sourceLabel, parsedServers)
        } else {
            Pair("FlareFeed Bundled Nodes", getBundledDefaultServers())
        }
    }

    private fun fetchSingleMirror(url: String): Pair<String, String>? {
        return try {
            val request = Request.Builder().url(url).build()
            val response = httpClient.newCall(request).execute()
            if (response.isSuccessful) {
                val body = response.body?.string()
                if (!body.isNullOrBlank() && body.contains("://")) {
                    Pair(url, body)
                } else null
            } else null
        } catch (e: Exception) {
            null
        }
    }

    fun getBundledDefaultServers(): List<VpnServer> {
        val rawLinks = listOf(
            "vless://93b7c293-1111-4882-9382-019283746281@de.flarefeed.net:443?security=tls&sni=de.flarefeed.net&fp=chrome&type=ws&path=%2Fvless#0001%20%7C%20Germany%20%7C%20FlareFeed%20Fast",
            "vless://a2849102-2222-4882-9382-019283746282@fi.flarefeed.net:443?security=tls&sni=fi.flarefeed.net&fp=chrome&type=ws&path=%2Fvless#0002%20%7C%20Finland%20%7C%20FlareFeed%20Ultra",
            "vless://c3950213-3333-4882-9382-019283746283@nl.flarefeed.net:443?security=tls&sni=nl.flarefeed.net&fp=chrome&type=ws&path=%2Fvless#0003%20%7C%20Netherlands%20%7C%20FlareFeed%20Speed",
            "hysteria2://user123:pass123@us.flarefeed.net:443?sni=us.flarefeed.net#0004%20%7C%20USA%20%7C%20Hysteria2%20Gaming",
            "trojan://trojanpass99@sg.flarefeed.net:443?sni=sg.flarefeed.net#0005%20%7C%20Singapore%20%7C%20Trojan%20Gaming",
            "vless://d4061324-4444-4882-9382-019283746284@se.flarefeed.net:443?security=tls&sni=se.flarefeed.net&fp=chrome&type=ws&path=%2Fvless#0006%20%7C%20Sweden%20%7C%20FlareFeed",
            "vless://e5172435-5555-4882-9382-019283746285@gb.flarefeed.net:443?security=tls&sni=gb.flarefeed.net&fp=chrome&type=ws&path=%2Fvless#0007%20%7C%20United%20Kingdom%20%7C%20FlareFeed",
            "vless://f6283546-6666-4882-9382-019283746286@fr.flarefeed.net:443?security=tls&sni=fr.flarefeed.net&fp=chrome&type=ws&path=%2Fvless#0008%20%7C%20France%20%7C%20FlareFeed",
            "tuic://tuicuser:tuicpass@jp.flarefeed.net:443?sni=jp.flarefeed.net#0009%20%7C%20Japan%20%7C%20TUIC%20Stream",
            "vless://a7394657-7777-4882-9382-019283746287@ee.flarefeed.net:443?security=tls&sni=ee.flarefeed.net&fp=chrome&type=ws&path=%2Fvless#0010%20%7C%20Estonia%20%7C%20FlareFeed"
        )
        return rawLinks.mapNotNull { parseLink(it) }
    }

    fun parseServerList(text: String): List<VpnServer> {
        val list = mutableListOf<VpnServer>()
        val seenIds = mutableSetOf<String>()

        val lines = text.split("\r\n", "\n", "\r")
        for (rawLine in lines) {
            val line = rawLine.trim()
            if (line.isBlank() || line.startsWith("#")) continue
            val token = line.replace(" ", "%20")
            if (!token.contains("://")) continue
            val server = parseLink(token)
            if (server != null) {
                if (seenIds.add(server.id)) {
                    list.add(server)
                }
            }
        }
        return list
    }

    fun parseLink(link: String): VpnServer? {
        val trimmed = link.trim()
        if (trimmed.isBlank()) return null
        return try {
            when {
                trimmed.startsWith("vmess://", ignoreCase = true) -> parseVmess(trimmed)
                trimmed.startsWith("vless://", ignoreCase = true) -> parseGeneric(trimmed, VpnProtocol.VLESS)
                trimmed.startsWith("trojan://", ignoreCase = true) -> parseGeneric(trimmed, VpnProtocol.TROJAN)
                trimmed.startsWith("hysteria2://", ignoreCase = true) || trimmed.startsWith("hy2://", ignoreCase = true) ->
                    parseGeneric(trimmed, VpnProtocol.HYSTERIA2)
                trimmed.startsWith("tuic://", ignoreCase = true) -> parseGeneric(trimmed, VpnProtocol.TUIC)
                else -> null
            }
        } catch (e: Exception) {
            null
        }
    }

    private fun parseGeneric(linkStr: String, proto: VpnProtocol): VpnServer? {
        var normLink = linkStr
        if (normLink.startsWith("hy2://", ignoreCase = true)) {
            normLink = "hysteria2://" + normLink.substring(6)
        }
        val uri = try { URI(normLink) } catch (e: Exception) { return null }

        val host = uri.host ?: return null
        val port = if (uri.port > 0) uri.port else 443
        val rawFragment = uri.rawFragment ?: ""
        val unescapedFragment = try { URLDecoder.decode(rawFragment, "UTF-8") } catch (e: Exception) { rawFragment }

        val server = VpnServer(
            protocol = proto,
            rawLink = linkStr,
            host = host,
            port = port,
            remark = formatRemark(unescapedFragment.trimStart('#').trim(), host)
        )

        val userInfo = uri.rawUserInfo ?: ""
        val unescapedUser = try { URLDecoder.decode(userInfo, "UTF-8") } catch (e: Exception) { userInfo }

        when (proto) {
            VpnProtocol.TROJAN -> server.params["password"] = unescapedUser
            VpnProtocol.HYSTERIA2, VpnProtocol.TUIC -> server.params["userinfo"] = unescapedUser
            else -> server.params["uuid"] = unescapedUser
        }

        uri.rawQuery?.let { q ->
            parseQueryParams(q).forEach { (k, v) -> server.params[k] = v }
        }

        server.id = VpnServer.generateStableId(proto, host, port, server.params)
        if (server.remark.isBlank()) {
            server.remark = "$proto $host"
        }
        server.country = guessCountry(server.remark)
        return server
    }

    private fun parseVmess(linkStr: String): VpnServer? {
        val b64Part = linkStr.substring("vmess://".length).trim()
        val decodedBytes = try {
            Base64.decode(b64Part, Base64.DEFAULT or Base64.URL_SAFE)
        } catch (e: Exception) {
            return null
        }
        val jsonStr = String(decodedBytes, Charsets.UTF_8)
        val jsonObj = try {
            jsonParser.parseToJsonElement(jsonStr).jsonObject
        } catch (e: Exception) {
            return null
        }

        fun getStr(key: String): String = jsonObj[key]?.jsonPrimitive?.content ?: ""

        val host = getStr("add")
        val port = getStr("port").toIntOrNull() ?: 443
        val remark = formatRemark(getStr("ps"), host)

        val server = VpnServer(
            protocol = VpnProtocol.VMESS,
            rawLink = linkStr,
            host = host,
            port = port,
            remark = remark
        )

        server.params["uuid"] = getStr("id")
        server.params["aid"] = getStr("aid")
        server.params["security"] = getStr("scy")
        server.params["net"] = getStr("net")
        server.params["type"] = getStr("type")
        server.params["host"] = getStr("host")
        server.params["path"] = getStr("path")
        server.params["tls"] = getStr("tls")
        server.params["sni"] = getStr("sni")
        server.params["fp"] = getStr("fp")
        server.params["alpn"] = getStr("alpn")

        server.id = VpnServer.generateStableId(VpnProtocol.VMESS, host, port, server.params)
        if (server.remark.isBlank()) {
            server.remark = "VMess $host"
        }
        server.country = guessCountry(server.remark)
        return server
    }

    private fun parseQueryParams(query: String): Map<String, String> {
        val map = mutableMapOf<String, String>()
        query.trimStart('?').split('&').forEach { part ->
            if (part.isNotBlank()) {
                val idx = part.indexOf('=')
                if (idx < 0) {
                    map[URLDecoder.decode(part, "UTF-8")] = ""
                } else {
                    val k = URLDecoder.decode(part.substring(0, idx), "UTF-8")
                    val v = URLDecoder.decode(part.substring(idx + 1), "UTF-8")
                    map[k] = v
                }
            }
        }
        return map
    }

    private fun formatRemark(raw: String, host: String): String {
        if (raw.isBlank()) return raw
        val decoded = try { URLDecoder.decode(raw, "UTF-8") } catch (e: Exception) { raw }
        if (decoded.contains("|")) {
            val parts = decoded.split('|').map { it.trim() }
            val id = parts.getOrNull(0) ?: ""
            var country = parts.getOrNull(1) ?: ""
            if (country.contains('[')) country = country.split('[')[0].trim()
            country = cleanServerLabel(country)
            return when {
                country.isBlank() -> if (id.isBlank()) decoded else "سيرفر $id"
                id.isBlank() -> country
                else -> "$country • $id"
            }
        }
        return cleanServerLabel(decoded)
    }

    private fun cleanServerLabel(value: String): String {
        var v = value.replace(Regex("^(?:\\uD83C[\\uDDE6-\\uDDFF]){1,2}\\s*"), "")
        v = v.replace(Regex("^(?:☁\\uFE0F?|⚡|🌐|🌍|🌎|🌏)\\s*"), "")
        v = v.replace(Regex("\\s*\\(([^)]*)\\)"), " • $1")
        return v.replace("(", " ").replace(")", "").trim()
    }

    private fun guessCountry(remark: String): String {
        if (remark.isBlank()) return ""
        val countries = listOf(
            "Finland","Sweden","Netherlands","Germany","Estonia","France","United Kingdom","UK","USA","Canada",
            "Singapore","Japan","Hong Kong","Cloudflare","Israel","Poland","Romania","Turkey","Italy","Spain","Ukraine"
        )
        for (c in countries.sortedByDescending { it.length }) {
            val regex = Regex("(?i)(?<![\\p{L}])${Regex.escape(c)}(?![\\p{L}])")
            if (regex.containsMatchIn(remark)) return c
        }
        return ""
    }
}
