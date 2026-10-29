package com.example.models

import java.security.MessageDigest
import java.util.Locale

enum class VpnProtocol {
    VLESS, VMESS, TROJAN, HYSTERIA2, TUIC, UNKNOWN;

    override fun toString(): String = when (this) {
        VLESS -> "VLESS"
        VMESS -> "VMess"
        TROJAN -> "Trojan"
        HYSTERIA2 -> "Hysteria2"
        TUIC -> "TUIC"
        UNKNOWN -> "Unknown"
    }
}

data class VpnServer(
    var id: String = "",
    var protocol: VpnProtocol = VpnProtocol.UNKNOWN,
    var rawLink: String = "",
    var remark: String = "",
    var host: String = "",
    var port: Int = 443,
    var country: String = "",
    var verifiedExitCountry: String? = null,
    var verifiedExitCountryCode: String? = null,
    var tcpLatencyMs: Double? = null,
    var tcpReachable: Boolean? = null,
    var udpLatencyMs: Double? = null,
    var udpProbePassed: Boolean? = null,
    var webProbePassed: Boolean? = null,
    var downloadMbps: Double? = null,
    val params: MutableMap<String, String> = mutableMapOf()
) {
    val displayName: String
        get() = if (remark.isBlank()) "$protocol $host:$port" else remark

    val uiDisplayName: String
        get() = when {
            !verifiedExitCountry.isNullOrBlank() -> "$displayName · خروج $verifiedExitCountry"
            else -> displayName
        }

    val isCloudflare: Boolean
        get() = country.equals("Cloudflare", ignoreCase = true) ||
                remark.contains("Cloudflare", ignoreCase = true) ||
                host.contains("cloudflare", ignoreCase = true)

    val countryFlagCode: String?
        get() = verifiedExitCountryCode ?: getCountryCode(country, remark, id)

    val pingLabel: String
        get() {
            if (id == "auto") return "الأفضل"
            val parts = mutableListOf<String>()
            parts.add(tcpLatencyMs?.let { String.format(Locale.US, "%.1f ms TCP", it) } ?: "— TCP")
            udpProbePassed?.let { parts.add(if (it) "U✓" else "U✕") }
            webProbePassed?.let { parts.add(if (it) "W✓" else "W✕") }
            downloadMbps?.let { parts.add(String.format(Locale.US, "↓%.1f Mbps", it)) }
            return parts.joinToString(" · ")
        }

    val flagEmoji: String
        get() = countryFlagCodeToEmoji(countryFlagCode)

    companion object {
        fun createAutoNode(label: String = "التحديد التلقائي الأفضل"): VpnServer {
            return VpnServer(
                id = "auto",
                protocol = VpnProtocol.VLESS,
                remark = label,
                host = "auto.server",
                port = 443,
                country = "Auto"
            )
        }

        fun countryFlagCodeToEmoji(code: String?): String {
            if (code == null || code.length != 2) return "🌐"
            val upper = code.uppercase(Locale.US)
            val firstChar = Character.codePointAt(upper, 0) - 0x41 + 0x1F1E6
            val secondChar = Character.codePointAt(upper, 1) - 0x41 + 0x1F1E6
            return String(Character.toChars(firstChar)) + String(Character.toChars(secondChar))
        }

        private val SUPPORTED_FLAG_CODES = setOf(
            "ae","ar","at","au","be","bh","br","ca","ch","cn","de","dk","dz","ee","eg","es",
            "fi","fr","gb","hk","il","in","iq","ir","it","jo","jp","kr","kw","lb","ma","mx",
            "nl","no","om","pl","qa","ro","ru","sa","se","sg","sy","tn","tr","ua","us","ye","za"
        )

        private val COUNTRY_MAP = mapOf(
            "Finland" to "fi", "Sweden" to "se", "Netherlands" to "nl", "Germany" to "de",
            "Ukraine" to "ua", "Estonia" to "ee", "France" to "fr", "United Kingdom" to "gb",
            "UK" to "gb", "USA" to "us", "United States" to "us", "Canada" to "ca",
            "Singapore" to "sg", "Japan" to "jp", "Hong Kong" to "hk", "Israel" to "il",
            "Poland" to "pl", "Romania" to "ro", "Turkey" to "tr", "Türkiye" to "tr",
            "Italy" to "it", "Spain" to "es", "Russia" to "ru", "Switzerland" to "ch",
            "Austria" to "at", "Belgium" to "be", "Norway" to "no", "Denmark" to "dk",
            "Australia" to "au", "India" to "in", "South Korea" to "kr", "China" to "cn",
            "Brazil" to "br", "Argentina" to "ar", "Mexico" to "mx", "South Africa" to "za",
            "UAE" to "ae", "Saudi Arabia" to "sa", "Iran" to "ir", "Iraq" to "iq",
            "Egypt" to "eg", "Morocco" to "ma", "Algeria" to "dz", "Tunisia" to "tn",
            "Jordan" to "jo", "Lebanon" to "lb", "Syria" to "sy", "Yemen" to "ye",
            "Kuwait" to "kw", "Qatar" to "qa", "Bahrain" to "bh", "Oman" to "om"
        )

        fun getCountryCode(country: String, remark: String, id: String): String? {
            if (id == "auto" || country.contains("Cloudflare", ignoreCase = true) ||
                remark.contains("Cloudflare", ignoreCase = true)) return null

            val cTrim = country.trim()
            COUNTRY_MAP[cTrim]?.let { return it }
            if (cTrim.length == 2 && cTrim.lowercase() in SUPPORTED_FLAG_CODES) {
                return cTrim.lowercase()
            }

            for ((name, code) in COUNTRY_MAP.entries.sortedByDescending { it.key.length }) {
                val regex = Regex("(?i)(?<![\\p{L}])${Regex.escape(name)}(?![\\p{L}])")
                if (regex.containsMatchIn(remark)) {
                    return code
                }
            }
            return null
        }

        fun generateStableId(protocol: VpnProtocol, host: String, port: Int, params: Map<String, String>): String {
            val canonical = buildString {
                append(protocol.name).append("\n")
                append(host.trim().trimEnd('.').lowercase(Locale.US)).append("\n")
                append(port).append("\n")
                val sortedParams = params.entries
                    .sortedWith(compareBy({ it.key.lowercase(Locale.US) }, { it.key }))
                    .joinToString("\n") { "${it.key.lowercase(Locale.US)}=${it.value}" }
                append(sortedParams)
            }
            val md = MessageDigest.getInstance("SHA-256")
            val hash = md.digest(canonical.toByteArray(Charsets.UTF_8))
            return hash.take(6).joinToString("") { "%02x".format(it) }
        }
    }
}
