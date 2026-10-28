package com.example.ui.theme

import android.app.Activity
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.SideEffect
import androidx.compose.ui.graphics.toArgb
import androidx.compose.ui.platform.LocalView

private val DarkColorScheme = darkColorScheme(
    primary = FlameCyan,
    onPrimary = CyberDarkBg,
    primaryContainer = CyberSurfaceVariant,
    onPrimaryContainer = FlameCyan,
    secondary = FlameOrange,
    onSecondary = CyberDarkBg,
    tertiary = NeonPurple,
    background = CyberDarkBg,
    onBackground = TextPrimary,
    surface = CyberSurface,
    onSurface = TextPrimary,
    surfaceVariant = CyberSurfaceVariant,
    onSurfaceVariant = TextSecondary,
    outline = CyberCardBorder
)

private val LightColorScheme = lightColorScheme(
    primary = FlameCyan,
    onPrimary = CyberDarkBg,
    primaryContainer = CyberSurfaceVariant,
    onPrimaryContainer = FlameCyan,
    secondary = FlameOrange,
    onSecondary = CyberDarkBg,
    tertiary = NeonPurple,
    background = CyberDarkBg,
    onBackground = TextPrimary,
    surface = CyberSurface,
    onSurface = TextPrimary,
    surfaceVariant = CyberSurfaceVariant,
    onSurfaceVariant = TextSecondary,
    outline = CyberCardBorder
)

@Composable
fun VPNGSTheme(
    darkTheme: Boolean = isSystemInDarkTheme(),
    content: @Composable () -> Unit
) {
    val colorScheme = DarkColorScheme
    val view = LocalView.current
    if (!view.isInEditMode) {
        SideEffect {
            val window = (view.context as Activity).window
            window.statusBarColor = CyberDarkBg.toArgb()
            window.navigationBarColor = CyberDarkBg.toArgb()
        }
    }

    MaterialTheme(
        colorScheme = colorScheme,
        typography = Typography,
        content = content
    )
}
