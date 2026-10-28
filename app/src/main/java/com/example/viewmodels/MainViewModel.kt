package com.example.viewmodels

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.example.models.AppSettings
import com.example.models.ConnectionStats
import com.example.models.ConnectionStatus
import com.example.models.UserProfile
import com.example.models.VpnServer
import com.example.services.ConfigGeneratorService
import com.example.services.ServerScanService
import com.example.services.SettingsRepository
import com.example.services.SubscriptionRepository
import com.example.services.VpnServiceManager
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch

class MainViewModel(application: Application) : AndroidViewModel(application) {

    private val subscriptionRepo = SubscriptionRepository(application)
    private val serverScanService = ServerScanService()
    private val configGeneratorService = ConfigGeneratorService()
    private val settingsRepo = SettingsRepository(application)
    val vpnServiceManager = VpnServiceManager()

    val appSettings: StateFlow<AppSettings> = settingsRepo.settingsFlow
        .stateIn(viewModelScope, SharingStarted.Eagerly, AppSettings())

    val userProfile: StateFlow<UserProfile?> = settingsRepo.profileFlow
        .stateIn(viewModelScope, SharingStarted.Eagerly, null)

    private val _servers = MutableStateFlow<List<VpnServer>>(emptyList())
    val servers: StateFlow<List<VpnServer>> = _servers.asStateFlow()

    private val _filteredServers = MutableStateFlow<List<VpnServer>>(emptyList())
    val filteredServers: StateFlow<List<VpnServer>> = _filteredServers.asStateFlow()

    private val _selectedServer = MutableStateFlow<VpnServer?>(null)
    val selectedServer: StateFlow<VpnServer?> = _selectedServer.asStateFlow()

    private val _platformFilter = MutableStateFlow("all")
    val platformFilter: StateFlow<String> = _platformFilter.asStateFlow()

    private val _searchQuery = MutableStateFlow("")
    val searchQuery: StateFlow<String> = _searchQuery.asStateFlow()

    private val _isLoadingServers = MutableStateFlow(true)
    val isLoadingServers: StateFlow<Boolean> = _isLoadingServers.asStateFlow()

    private val _loadingMessage = MutableStateFlow("جاري جلب السيرفرات وقياس UDP عبر الأنفاق...")
    val loadingMessage: StateFlow<String> = _loadingMessage.asStateFlow()

    private val _statusMessage = MutableStateFlow("جاهز للاتصال")
    val statusMessage: StateFlow<String> = _statusMessage.asStateFlow()

    private val _scanProgress = MutableStateFlow<Pair<Int, Int>?>(null)
    val scanProgress: StateFlow<Pair<Int, Int>?> = _scanProgress.asStateFlow()

    val connectionStatus: StateFlow<ConnectionStatus> = vpnServiceManager.status
    val connectionStats: StateFlow<ConnectionStats> = vpnServiceManager.stats
    val verifiedExitLocation: StateFlow<String?> = vpnServiceManager.verifiedExitLocation

    init {
        loadServers()
    }

    fun loadServers() {
        viewModelScope.launch {
            _isLoadingServers.value = true
            _loadingMessage.value = "جاري جلب السيرفرات من المرايا المتاحة..."
            try {
                val customSubs = appSettings.value.customSubscriptions
                val (source, parsed) = subscriptionRepo.fetchServers(customSubs)

                val autoNode = VpnServer.createAutoNode()
                val fullList = mutableListOf(autoNode).apply { addAll(parsed) }

                _servers.value = fullList
                _statusMessage.value = "تم جلب ${parsed.size} سيرفر من $source"

                // Auto probe servers in background
                scanAndSort()
            } catch (e: Exception) {
                _statusMessage.value = "خطأ في الجلب: ${e.localizedMessage}"
            } finally {
                _isLoadingServers.value = false
            }
        }
    }

    fun scanAndSort() {
        viewModelScope.launch {
            val list = _servers.value
            if (list.isEmpty()) return@launch

            _statusMessage.value = "جاري فحص وقت الاستجابة وقياس UDP..."
            val scanned = serverScanService.scanServers(list) { current, total ->
                _scanProgress.value = Pair(current, total)
            }
            _scanProgress.value = null

            _servers.value = scanned
            val sorted = serverScanService.sortServersByPerformance(scanned, _platformFilter.value)
            _servers.value = sorted
            applyFilter()

            if (_selectedServer.value == null) {
                _selectedServer.value = sorted.firstOrNull()
            }
            _statusMessage.value = "اكتمل فحص السيرفرات وترتيب الأداء"
        }
    }

    fun selectServer(server: VpnServer) {
        _selectedServer.value = server
    }

    fun setPlatformFilter(filter: String) {
        _platformFilter.value = filter
        val sorted = serverScanService.sortServersByPerformance(_servers.value, filter)
        _servers.value = sorted
        applyFilter()
    }

    fun setSearchQuery(query: String) {
        _searchQuery.value = query
        applyFilter()
    }

    private fun applyFilter() {
        val q = _searchQuery.value.trim().lowercase()
        val filterType = _platformFilter.value
        val showCloudflare = appSettings.value.showCloudflareServers

        val result = _servers.value.filter { s ->
            if (s.id == "auto") return@filter true
            if (!showCloudflare && s.isCloudflare) return@filter false

            val passesPlatform = when (filterType) {
                "games" -> s.udpProbePassed == true
                "browse" -> s.webProbePassed == true
                "stream" -> s.downloadMbps != null
                else -> true
            }
            if (!passesPlatform) return@filter false

            if (q.isBlank()) true
            else s.remark.lowercase().contains(q) ||
                    s.country.lowercase().contains(q) ||
                    s.host.lowercase().contains(q)
        }

        _filteredServers.value = result
    }

    fun toggleConnection() {
        val server = _selectedServer.value ?: _servers.value.firstOrNull()
        if (server == null) {
            _statusMessage.value = "الرجاء اختيار سيرفر أولاً"
            return
        }

        val targetServer = if (server.id == "auto") {
            serverScanService.getRecommendedServer(_servers.value, _platformFilter.value) ?: server
        } else server

        if (connectionStatus.value == ConnectionStatus.CONNECTED || connectionStatus.value == ConnectionStatus.CONNECTING) {
            vpnServiceManager.disconnect()
            _statusMessage.value = "تم إيقاف الاتصال"
        } else {
            vpnServiceManager.connect(targetServer)
            _statusMessage.value = "جاري الاتصال بـ ${targetServer.displayName}"
        }
    }

    fun parseAndAddCustomLink(rawLink: String): Boolean {
        val server = subscriptionRepo.parseLink(rawLink) ?: return false
        val current = _servers.value.toMutableList()
        current.add(1, server)
        _servers.value = current
        applyFilter()
        _selectedServer.value = server
        _statusMessage.value = "تمت إضافة السيرفر: ${server.displayName}"
        return true
    }

    fun generateSingBoxConfigForServer(server: VpnServer): String {
        val settings = appSettings.value
        return configGeneratorService.generateSingBoxConfig(
            server = server,
            socksPort = settings.socksPort,
            httpPort = settings.httpPort,
            tunMode = settings.tunMode
        )
    }

    fun addCustomSubscription(url: String) {
        viewModelScope.launch {
            val currentSettings = appSettings.value
            val updatedUrls = currentSettings.customSubscriptions.toMutableList()
            if (!updatedUrls.contains(url)) {
                updatedUrls.add(url)
                val newSettings = currentSettings.copy(customSubscriptions = updatedUrls)
                settingsRepo.saveSettings(newSettings)
                loadServers()
            }
        }
    }

    fun saveUserProfile(name: String) {
        viewModelScope.launch {
            val profile = UserProfile(displayName = name)
            settingsRepo.saveProfile(profile)
        }
    }

    fun updateSettings(newSettings: AppSettings) {
        viewModelScope.launch {
            settingsRepo.saveSettings(newSettings)
            applyFilter()
        }
    }
}
