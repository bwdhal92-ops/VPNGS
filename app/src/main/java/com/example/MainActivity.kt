package com.example

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.viewModels
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.BugReport
import androidx.compose.material.icons.filled.Home
import androidx.compose.material.icons.filled.Person
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material3.Icon
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.NavigationBarItemDefaults
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.example.models.VpnServer
import com.example.ui.screens.AdvancedScreen
import com.example.ui.screens.HomeScreen
import com.example.ui.screens.ProfilesScreen
import com.example.ui.screens.ServerDetailSheet
import com.example.ui.screens.SettingsScreen
import com.example.ui.theme.CyberCardBorder
import com.example.ui.theme.CyberDarkBg
import com.example.ui.theme.CyberSurface
import com.example.ui.theme.FlameCyan
import com.example.ui.theme.TextMuted
import com.example.ui.theme.TextPrimary
import com.example.ui.theme.VPNGSTheme
import com.example.viewmodels.MainViewModel

class MainActivity : ComponentActivity() {

    private val viewModel: MainViewModel by viewModels()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()

        setContent {
            VPNGSTheme {
                MainAppScreen(viewModel = viewModel)
            }
        }
    }
}

enum class NavTab(val label: String, val icon: ImageVector) {
    HOME("الرئيسية", Icons.Default.Home),
    PROFILES("الحساب", Icons.Default.Person),
    SETTINGS("الإعدادات", Icons.Default.Settings),
    ADVANCED("المتقدم", Icons.Default.BugReport)
}

@Composable
fun MainAppScreen(viewModel: MainViewModel) {
    var selectedTab by remember { mutableStateOf(NavTab.HOME) }
    var activeDetailServer by remember { mutableStateOf<VpnServer?>(null) }

    Scaffold(
        bottomBar = {
            NavigationBar(
                containerColor = CyberSurface,
                tonalElevation = 8.dp,
                modifier = Modifier.testTag("bottom_navigation_bar")
            ) {
                NavTab.entries.forEach { tab ->
                    val isSelected = selectedTab == tab
                    NavigationBarItem(
                        selected = isSelected,
                        onClick = { selectedTab = tab },
                        icon = {
                            Icon(
                                imageVector = tab.icon,
                                contentDescription = tab.label,
                                tint = if (isSelected) FlameCyan else TextMuted
                            )
                        },
                        label = {
                            Text(
                                text = tab.label,
                                color = if (isSelected) FlameCyan else TextMuted,
                                fontSize = 11.sp
                            )
                        },
                        colors = NavigationBarItemDefaults.colors(
                            indicatorColor = CyberCardBorder
                        ),
                        modifier = Modifier.testTag("nav_tab_${tab.name.lowercase()}")
                    )
                }
            }
        },
        containerColor = CyberDarkBg,
        modifier = Modifier.fillMaxSize()
    ) { paddingValues ->
        Box(
            modifier = Modifier
                .fillMaxSize()
                .padding(paddingValues)
        ) {
            when (selectedTab) {
                NavTab.HOME -> HomeScreen(
                    viewModel = viewModel,
                    onOpenServerDetail = { server -> activeDetailServer = server }
                )
                NavTab.PROFILES -> ProfilesScreen(viewModel = viewModel)
                NavTab.SETTINGS -> SettingsScreen(viewModel = viewModel)
                NavTab.ADVANCED -> AdvancedScreen(viewModel = viewModel)
            }

            activeDetailServer?.let { server ->
                val jsonConfig = viewModel.generateSingBoxConfigForServer(server)
                ServerDetailSheet(
                    server = server,
                    generatedConfigJson = jsonConfig,
                    onDismiss = { activeDetailServer = null }
                )
            }
        }
    }
}
