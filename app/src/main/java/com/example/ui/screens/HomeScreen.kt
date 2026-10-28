package com.example.ui.screens

import android.widget.Toast
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.ArrowDownward
import androidx.compose.material.icons.filled.ArrowUpward
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Search
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.OutlinedTextFieldDefaults
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.example.models.ConnectionStatus
import com.example.models.VpnServer
import com.example.ui.components.ConnectionPulseButton
import com.example.ui.components.PlatformFilterChip
import com.example.ui.components.ServerCard
import com.example.ui.theme.CyberCardBorder
import com.example.ui.theme.CyberDarkBg
import com.example.ui.theme.CyberSurface
import com.example.ui.theme.FlameCyan
import com.example.ui.theme.FlameOrange
import com.example.ui.theme.SuccessGreen
import com.example.ui.theme.TextMuted
import com.example.ui.theme.TextPrimary
import com.example.ui.theme.TextSecondary
import com.example.viewmodels.MainViewModel

@Composable
fun HomeScreen(
    viewModel: MainViewModel,
    onOpenServerDetail: (VpnServer) -> Unit,
    modifier: Modifier = Modifier
) {
    val context = LocalContext.current
    val servers by viewModel.filteredServers.collectAsState()
    val selectedServer by viewModel.selectedServer.collectAsState()
    val platformFilter by viewModel.platformFilter.collectAsState()
    val searchQuery by viewModel.searchQuery.collectAsState()
    val isLoading by viewModel.isLoadingServers.collectAsState()
    val statusMessage by viewModel.statusMessage.collectAsState()
    val connectionStatus by viewModel.connectionStatus.collectAsState()
    val connectionStats by viewModel.connectionStats.collectAsState()
    val scanProgress by viewModel.scanProgress.collectAsState()

    var showAddLinkDialog by remember { mutableStateOf(false) }
    var rawLinkInput by remember { mutableStateOf("") }

    val formattedDuration = viewModel.vpnServiceManager.formatDuration(connectionStats.durationSeconds)

    Column(
        modifier = modifier
            .fillMaxSize()
            .background(CyberDarkBg)
            .padding(horizontal = 16.dp)
            .testTag("home_screen")
    ) {
        Spacer(modifier = Modifier.height(12.dp))

        // Top Status Header Banner
        Box(
            modifier = Modifier
                .fillMaxWidth()
                .clip(RoundedCornerShape(12.dp))
                .background(CyberSurface)
                .border(1.dp, CyberCardBorder, RoundedCornerShape(12.dp))
                .padding(horizontal = 14.dp, vertical = 10.dp)
        ) {
            Row(
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.SpaceBetween,
                modifier = Modifier.fillMaxWidth()
            ) {
                Column(modifier = Modifier.weight(1f)) {
                    Text(
                        text = "VPNGS • FlareVPN",
                        color = FlameCyan,
                        fontSize = 15.sp,
                        fontWeight = FontWeight.Bold
                    )
                    Text(
                        text = statusMessage,
                        color = TextSecondary,
                        fontSize = 12.sp,
                        maxLines = 1
                    )
                }

                Row(verticalAlignment = Alignment.CenterVertically) {
                    IconButton(
                        onClick = { showAddLinkDialog = true },
                        modifier = Modifier.testTag("add_server_button")
                    ) {
                        Icon(
                            imageVector = Icons.Default.Add,
                            contentDescription = "إضافة سيرفر",
                            tint = FlameCyan
                        )
                    }

                    IconButton(
                        onClick = { viewModel.scanAndSort() },
                        modifier = Modifier.testTag("refresh_scan_button")
                    ) {
                        if (scanProgress != null) {
                            CircularProgressIndicator(
                                modifier = Modifier.size(20.dp),
                                color = FlameCyan,
                                strokeWidth = 2.dp
                            )
                        } else {
                            Icon(
                                imageVector = Icons.Default.Refresh,
                                contentDescription = "تحديث الفحص",
                                tint = TextPrimary
                            )
                        }
                    }
                }
            }
        }

        Spacer(modifier = Modifier.height(16.dp))

        // Connection Pulse Central Button
        Box(
            contentAlignment = Alignment.Center,
            modifier = Modifier.fillMaxWidth()
        ) {
            ConnectionPulseButton(
                status = connectionStatus,
                durationText = formattedDuration,
                onClick = { viewModel.toggleConnection() }
            )
        }

        Spacer(modifier = Modifier.height(16.dp))

        // Live Speed Telemetry Bar when connected
        AnimatedVisibility(visible = connectionStatus == ConnectionStatus.CONNECTED) {
            Box(
                modifier = Modifier
                    .fillMaxWidth()
                    .clip(RoundedCornerShape(10.dp))
                    .background(CyberSurface)
                    .border(1.dp, SuccessGreen.copy(alpha = 0.5f), RoundedCornerShape(10.dp))
                    .padding(10.dp)
            ) {
                Row(
                    horizontalArrangement = Arrangement.SpaceAround,
                    modifier = Modifier.fillMaxWidth()
                ) {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Icon(Icons.Default.ArrowDownward, contentDescription = null, tint = SuccessGreen, modifier = Modifier.size(16.dp))
                        Spacer(modifier = Modifier.width(4.dp))
                        Text("${connectionStats.downloadSpeedMbps} Mbps", color = TextPrimary, fontSize = 12.sp, fontWeight = FontWeight.Bold)
                    }

                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Icon(Icons.Default.ArrowUpward, contentDescription = null, tint = FlameCyan, modifier = Modifier.size(16.dp))
                        Spacer(modifier = Modifier.width(4.dp))
                        Text("${connectionStats.uploadSpeedMbps} Mbps", color = TextPrimary, fontSize = 12.sp, fontWeight = FontWeight.Bold)
                    }

                    Text(
                        text = "المحول: ${viewModel.vpnServiceManager.formatDataSize(connectionStats.totalBytesReceived)}",
                        color = TextMuted,
                        fontSize = 12.sp
                    )
                }
            }
        }

        Spacer(modifier = Modifier.height(14.dp))

        // Category Filter Chips (All, Games, Browse, Stream)
        Row(
            horizontalArrangement = Arrangement.spacedBy(8.dp),
            modifier = Modifier.fillMaxWidth()
        ) {
            PlatformFilterChip(filterKey = "all", label = "الكل", isSelected = platformFilter == "all", onSelect = { viewModel.setPlatformFilter("all") })
            PlatformFilterChip(filterKey = "games", label = "الألعاب UDP", isSelected = platformFilter == "games", onSelect = { viewModel.setPlatformFilter("games") })
            PlatformFilterChip(filterKey = "browse", label = "التصفح", isSelected = platformFilter == "browse", onSelect = { viewModel.setPlatformFilter("browse") })
            PlatformFilterChip(filterKey = "stream", label = "المشاهدة", isSelected = platformFilter == "stream", onSelect = { viewModel.setPlatformFilter("stream") })
        }

        Spacer(modifier = Modifier.height(12.dp))

        // Search Bar
        OutlinedTextField(
            value = searchQuery,
            onValueChange = { viewModel.setSearchQuery(it) },
            placeholder = { Text("بحث باسم السيرفر أو الدولة...", color = TextMuted, fontSize = 13.sp) },
            leadingIcon = { Icon(Icons.Default.Search, contentDescription = "بحث", tint = TextMuted) },
            singleLine = true,
            colors = OutlinedTextFieldDefaults.colors(
                focusedContainerColor = CyberSurface,
                unfocusedContainerColor = CyberSurface,
                focusedBorderColor = FlameCyan,
                unfocusedBorderColor = CyberCardBorder,
                focusedTextColor = TextPrimary,
                unfocusedTextColor = TextPrimary
            ),
            shape = RoundedCornerShape(12.dp),
            modifier = Modifier
                .fillMaxWidth()
                .testTag("search_servers_input")
        )

        Spacer(modifier = Modifier.height(10.dp))

        // Servers List
        if (isLoading) {
            Box(
                contentAlignment = Alignment.Center,
                modifier = Modifier
                    .fillMaxSize()
                    .weight(1f)
            ) {
                Column(horizontalAlignment = Alignment.CenterHorizontally) {
                    CircularProgressIndicator(color = FlameCyan)
                    Spacer(modifier = Modifier.height(12.dp))
                    Text(viewModel.loadingMessage.value, color = TextMuted, fontSize = 13.sp)
                }
            }
        } else {
            LazyColumn(
                verticalArrangement = Arrangement.spacedBy(8.dp),
                modifier = Modifier
                    .fillMaxSize()
                    .weight(1f)
                    .testTag("server_list")
            ) {
                items(
                    items = servers,
                    key = { it.id }
                ) { server ->
                    ServerCard(
                        server = server,
                        isSelected = selectedServer?.id == server.id,
                        onSelect = { viewModel.selectServer(server) },
                        onDetailClick = { onOpenServerDetail(server) }
                    )
                }
            }
        }
    }

    // Dialog for adding custom VLESS/VMess link
    if (showAddLinkDialog) {
        AlertDialog(
            onDismissRequest = { showAddLinkDialog = false },
            title = { Text("إضافة رابط سيرفر يدوي", color = TextPrimary, fontWeight = FontWeight.Bold) },
            text = {
                Column {
                    Text("أدخل رابط VLESS, VMess, Trojan, Hysteria2, أو TUIC:", color = TextSecondary, fontSize = 13.sp)
                    Spacer(modifier = Modifier.height(8.dp))
                    OutlinedTextField(
                        value = rawLinkInput,
                        onValueChange = { rawLinkInput = it },
                        placeholder = { Text("vless://... or vmess://...", color = TextMuted) },
                        colors = OutlinedTextFieldDefaults.colors(
                            focusedContainerColor = CyberDarkBg,
                            unfocusedContainerColor = CyberDarkBg,
                            focusedBorderColor = FlameCyan,
                            unfocusedBorderColor = CyberCardBorder,
                            focusedTextColor = TextPrimary,
                            unfocusedTextColor = TextPrimary
                        ),
                        modifier = Modifier
                            .fillMaxWidth()
                            .testTag("custom_link_text_input")
                    )
                }
            },
            confirmButton = {
                Button(
                    onClick = {
                        if (rawLinkInput.isNotBlank()) {
                            val success = viewModel.parseAndAddCustomLink(rawLinkInput)
                            if (success) {
                                Toast.makeText(context, "تمت إضافة السيرفر بنجاح", Toast.LENGTH_SHORT).show()
                                rawLinkInput = ""
                                showAddLinkDialog = false
                            } else {
                                Toast.makeText(context, "الرابط غير صالح", Toast.LENGTH_SHORT).show()
                            }
                        }
                    },
                    colors = ButtonDefaults.buttonColors(containerColor = FlameCyan, contentColor = CyberDarkBg),
                    modifier = Modifier.testTag("submit_custom_link_button")
                ) {
                    Text("إضافة")
                }
            },
            dismissButton = {
                TextButton(onClick = { showAddLinkDialog = false }) {
                    Text("إلغاء", color = TextMuted)
                }
            },
            containerColor = CyberSurface
        )
    }
}
