package com.example.ui.components

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.AutoMode
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.Info
import androidx.compose.material.icons.filled.RadioButtonUnchecked
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.example.models.VpnProtocol
import com.example.models.VpnServer
import com.example.ui.theme.CyberCardBorder
import com.example.ui.theme.CyberSurface
import com.example.ui.theme.ErrorRed
import com.example.ui.theme.FlameCyan
import com.example.ui.theme.FlameOrange
import com.example.ui.theme.NeonPurple
import com.example.ui.theme.SuccessGreen
import com.example.ui.theme.TextMuted
import com.example.ui.theme.TextPrimary
import com.example.ui.theme.TextSecondary

@Composable
fun ServerCard(
    server: VpnServer,
    isSelected: Boolean,
    onSelect: () -> Unit,
    onDetailClick: () -> Unit,
    modifier: Modifier = Modifier
) {
    val borderColor = if (isSelected) FlameCyan else CyberCardBorder
    val bgColor = if (isSelected) FlameCyan.copy(alpha = 0.08f) else CyberSurface

    Box(
        modifier = modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(12.dp))
            .background(bgColor)
            .border(1.dp, borderColor, RoundedCornerShape(12.dp))
            .clickable(onClick = onSelect)
            .padding(12.dp)
            .testTag("server_card_${server.id}")
    ) {
        Row(
            verticalAlignment = Alignment.CenterVertically,
            modifier = Modifier.fillMaxWidth()
        ) {
            // Radio / Selected Check
            Icon(
                imageVector = if (isSelected) Icons.Default.CheckCircle else Icons.Default.RadioButtonUnchecked,
                contentDescription = if (isSelected) "محدد" else "غير محدد",
                tint = if (isSelected) FlameCyan else TextMuted,
                modifier = Modifier.size(20.dp)
            )

            Spacer(modifier = Modifier.width(10.dp))

            // Flag Icon or Auto Icon
            if (server.id == "auto") {
                Box(
                    contentAlignment = Alignment.Center,
                    modifier = Modifier
                        .size(36.dp)
                        .clip(RoundedCornerShape(8.dp))
                        .background(FlameOrange.copy(alpha = 0.2f))
                ) {
                    Icon(
                        imageVector = Icons.Default.AutoMode,
                        contentDescription = "التحديد التلقائي",
                        tint = FlameOrange,
                        modifier = Modifier.size(20.dp)
                    )
                }
            } else {
                Text(
                    text = server.flagEmoji,
                    fontSize = 26.sp,
                    modifier = Modifier.padding(end = 4.dp)
                )
            }

            Spacer(modifier = Modifier.width(10.dp))

            // Server details
            Column(
                modifier = Modifier.weight(1f)
            ) {
                Row(
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Text(
                        text = server.displayName,
                        color = TextPrimary,
                        fontSize = 14.sp,
                        fontWeight = FontWeight.Bold,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                        modifier = Modifier.weight(1f, fill = false)
                    )

                    if (server.id != "auto") {
                        Spacer(modifier = Modifier.width(6.dp))
                        ProtocolBadge(protocol = server.protocol)
                    }
                }

                Spacer(modifier = Modifier.height(4.dp))

                // Ping / Status Pill label
                Text(
                    text = server.pingLabel,
                    color = when {
                        server.id == "auto" -> FlameOrange
                        server.tcpLatencyMs == null -> TextMuted
                        server.tcpLatencyMs!! < 120 -> SuccessGreen
                        server.tcpLatencyMs!! < 280 -> FlameCyan
                        else -> ErrorRed
                    },
                    fontSize = 12.sp,
                    fontWeight = FontWeight.Medium
                )
            }

            // Info button for details
            if (server.id != "auto") {
                IconButton(
                    onClick = onDetailClick,
                    modifier = Modifier.testTag("server_info_${server.id}")
                ) {
                    Icon(
                        imageVector = Icons.Default.Info,
                        contentDescription = "تفاصيل السيرفر",
                        tint = TextSecondary,
                        modifier = Modifier.size(20.dp)
                    )
                }
            }
        }
    }
}

@Composable
fun ProtocolBadge(protocol: VpnProtocol) {
    val (bg, text) = when (protocol) {
        VpnProtocol.VLESS -> Pair(FlameCyan.copy(alpha = 0.2f), FlameCyan)
        VpnProtocol.VMESS -> Pair(NeonPurple.copy(alpha = 0.2f), NeonPurple)
        VpnProtocol.TROJAN -> Pair(FlameOrange.copy(alpha = 0.2f), FlameOrange)
        VpnProtocol.HYSTERIA2 -> Pair(SuccessGreen.copy(alpha = 0.2f), SuccessGreen)
        VpnProtocol.TUIC -> Pair(Color(0xFFFF4081).copy(alpha = 0.2f), Color(0xFFFF4081))
        VpnProtocol.UNKNOWN -> Pair(TextMuted.copy(alpha = 0.2f), TextMuted)
    }

    Box(
        modifier = Modifier
            .clip(RoundedCornerShape(4.dp))
            .background(bg)
            .padding(horizontal = 6.dp, vertical = 2.dp)
    ) {
        Text(
            text = protocol.toString(),
            color = text,
            fontSize = 10.sp,
            fontWeight = FontWeight.Bold
        )
    }
}
