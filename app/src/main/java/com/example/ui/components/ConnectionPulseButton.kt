package com.example.ui.components

import androidx.compose.animation.core.FastOutSlowInEasing
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.PowerSettingsNew
import androidx.compose.material.icons.filled.Shield
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.scale
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.example.models.ConnectionStatus
import com.example.ui.theme.CyberSurfaceVariant
import com.example.ui.theme.ErrorRed
import com.example.ui.theme.FlameCyan
import com.example.ui.theme.FlameOrange
import com.example.ui.theme.SuccessGreen
import com.example.ui.theme.TextMuted
import com.example.ui.theme.TextPrimary

@Composable
fun ConnectionPulseButton(
    status: ConnectionStatus,
    durationText: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier
) {
    val infiniteTransition = rememberInfiniteTransition(label = "pulse")
    val pulseScale by infiniteTransition.animateFloat(
        initialValue = 1.0f,
        targetValue = if (status == ConnectionStatus.CONNECTED) 1.15f else if (status == ConnectionStatus.CONNECTING) 1.25f else 1.02f,
        animationSpec = infiniteRepeatable(
            animation = tween(1200, easing = FastOutSlowInEasing),
            repeatMode = RepeatMode.Reverse
        ),
        label = "pulseScale"
    )

    val buttonColor = when (status) {
        ConnectionStatus.CONNECTED -> SuccessGreen
        ConnectionStatus.CONNECTING -> FlameOrange
        ConnectionStatus.DISCONNECTING -> ErrorRed
        ConnectionStatus.ERROR -> ErrorRed
        ConnectionStatus.DISCONNECTED -> FlameCyan
    }

    Box(
        contentAlignment = Alignment.Center,
        modifier = modifier.testTag("connection_button_container")
    ) {
        // Outer Glowing Pulse Ring
        if (status == ConnectionStatus.CONNECTED || status == ConnectionStatus.CONNECTING) {
            Box(
                modifier = Modifier
                    .size(170.dp)
                    .scale(pulseScale)
                    .clip(CircleShape)
                    .background(buttonColor.copy(alpha = 0.18f))
            )
        }

        // Main Circular Button
        Box(
            contentAlignment = Alignment.Center,
            modifier = Modifier
                .size(140.dp)
                .clip(CircleShape)
                .background(
                    brush = Brush.radialGradient(
                        colors = listOf(
                            CyberSurfaceVariant,
                            Color(0xFF0F141F)
                        )
                    )
                )
                .border(3.dp, buttonColor, CircleShape)
                .clickable(onClick = onClick)
                .testTag("connect_toggle_button")
        ) {
            Column(
                horizontalAlignment = Alignment.CenterHorizontally
            ) {
                Icon(
                    imageVector = if (status == ConnectionStatus.CONNECTED) Icons.Default.Shield else Icons.Default.PowerSettingsNew,
                    contentDescription = "الاتصال",
                    tint = buttonColor,
                    modifier = Modifier.size(42.dp)
                )

                Spacer(modifier = Modifier.height(4.dp))

                Text(
                    text = when (status) {
                        ConnectionStatus.CONNECTED -> "إيقاف"
                        ConnectionStatus.CONNECTING -> "جاري الاتصال..."
                        ConnectionStatus.DISCONNECTING -> "جاري الفصل..."
                        ConnectionStatus.ERROR -> "غير متاح"
                        ConnectionStatus.DISCONNECTED -> "اتصال"
                    },
                    color = TextPrimary,
                    fontSize = 14.sp,
                    fontWeight = FontWeight.Bold
                )

                if (status == ConnectionStatus.CONNECTED && durationText.isNotBlank()) {
                    Text(
                        text = durationText,
                        color = SuccessGreen,
                        fontSize = 12.sp,
                        fontWeight = FontWeight.SemiBold,
                        modifier = Modifier.padding(top = 2.dp)
                    )
                } else if (status == ConnectionStatus.DISCONNECTED) {
                    Text(
                        text = "اضغط للاتصال",
                        color = TextMuted,
                        fontSize = 11.sp
                    )
                }
            }
        }
    }
}
