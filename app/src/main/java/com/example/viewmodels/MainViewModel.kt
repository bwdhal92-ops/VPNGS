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
import com.example.services.FirebaseTelemetryRepository
import com.example.services.ServerScanService
import com.example.services.SettingsRepository
import com.example.services.SubscriptionRepository
import com.example.services.VpnServiceManager
import kotlinx.coroutines.Job
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.isActive
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch

class MainViewModel(application: Application) : AndroidViewModel(application) {
    private val subscriptionRepo = SubscriptionRepository(application)
    private val serverScanService = ServerScanService()
    private val configGeneratorService = ConfigGeneratorService()
    private val settingsRepo = SettingsRepository(application)
    private val firebaseTelemetry = FirebaseTelemetryRepository(application)
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
    private val _loadingMessage = MutableStateFlow("جاري تجهيز قائمة الخوادم...")
    val loadingMessage: StateFlow<String> = _loadingMessage.asStateFlow()
    private val _statusMessage = MutableStateFlow("جاري تحميل القائمة المحفوظة...")
    val statusMessage: StateFlow<String> = _statusMessage.asStateFlow()
    private val _scanProgress = MutableStateFlow<Pair<Int, Int>?>(null)
    val scanProgress: StateFlow<Pair<Int, Int>?> = _scanProgress.asStateFlow()
    private val _logs = MutableStateFlow<List<String>>(listOf("[App] VPNGS Android بدأ التشغيل."))
    val logs: StateFlow<List<String>> = _logs.asStateFlow()
    private val _profileSetupRequired = MutableStateFlow(false)
    val profileSetupRequired: StateFlow<Boolean> = _profileSetupRequired.asStateFlow()
    private val _accountBlockedReason = MutableStateFlow<String?>(null)
    val accountBlockedReason: StateFlow<String?> = _accountBlockedReason.asStateFlow()
    val connectionStatus: StateFlow<ConnectionStatus> = vpnServiceManager.status
    val connectionStepMessage: StateFlow<String> = vpnServiceManager.connectionStepMessage
    val connectionStats: StateFlow<ConnectionStats> = vpnServiceManager.stats
    val verifiedExitLocation: StateFlow<String?> = vpnServiceManager.verifiedExitLocation

    private var scanJob: Job? = null
    private var sessionProfile: UserProfile? = null
    private var sessionStartingForInstallId: String? = null
    private var heartbeatJob: Job? = null
    private var showCloudflareServers = false

    init {
        viewModelScope.launch {
            settingsRepo.profileFlow.collect { profile ->
                _profileSetupRequired.value = profile?.displayName.isNullOrBlank()
                if (profile != null && profile.displayName.isNotBlank() && profile.activeSessionId == null &&
                    sessionStartingForInstallId != profile.installId) {
                    sessionStartingForInstallId = profile.installId
                    viewModelScope.launch {
                        var failures = 0
                        while (currentCoroutineContext().isActive) {
                            val latestProfile = settingsRepo.profileFlow.first() ?: break
                            try {
                                val updated = firebaseTelemetry.startSession(latestProfile)
                                sessionProfile = updated
                                settingsRepo.saveProfile(updated)
                                log("Firebase", "بدأت الجلسة للمستخدم ${updated.displayName}; uid=${updated.firebaseUid}.")
                                startFirebaseHeartbeat()
                                break
                            } catch (e: FirebaseTelemetryRepository.AccountBannedException) {
                                _accountBlockedReason.value = e.message
                                log("Firebase/BLOCKED", e.message ?: "الحساب موقوف")
                                break
                            } catch (e: Exception) {
                                failures++
                                if (failures == 1 || failures % 3 == 0) {
                                    log("Firebase/WARN", "تعذر تسجيل الجلسة؛ سيعاد المحاولة عند توفر الشبكة: ${e.message}")
                                }
                                delay(60_000L)
                            }
                        }
                        sessionStartingForInstallId = null
                    }
                }
            }
        }
        loadServers()
    }

    override fun onCleared() {
        heartbeatJob?.cancel()
        val profile = sessionProfile
        if (profile != null) {
            CoroutineScope(SupervisorJob() + Dispatchers.IO).launch {
                runCatching { firebaseTelemetry.endSession(profile) }
            }
        }
        super.onCleared()
    }

    private fun startFirebaseHeartbeat() {
        heartbeatJob?.cancel()
        heartbeatJob = viewModelScope.launch {
            while (currentCoroutineContext().isActive) {
                delay(4 * 60 * 1000L)
                val current = sessionProfile ?: break
                try {
                    val updated = firebaseTelemetry.heartbeat(current)
                    sessionProfile = updated
                    settingsRepo.saveProfile(updated)
                } catch (error: Exception) {
                    log("Firebase/WARN", "تعذر تحديث نبض الجلسة: ${error.message}")
                }
            }
        }
    }

    fun loadServers() {
        viewModelScope.launch {
            val currentSettings = settingsRepo.settingsFlow.first()
            showCloudflareServers = currentSettings.showCloudflareServers
            val previous = _servers.value.associateBy { it.id }
            val savedMeasurements = settingsRepo.loadTcpMeasurements()
            val cached = subscriptionRepo.loadCachedServers()
            if (cached.isNotEmpty()) {
                val ready = withMeasurements(cached, previous, savedMeasurements)
                publishServers(ready)
                _isLoadingServers.value = false
                _statusMessage.value = "القائمة المحفوظة جاهزة • ${cached.size} خادم"
                log("Cache", "عرض ${cached.size} خادم من الذاكرة المحلية.")
            }

            _isLoadingServers.value = _servers.value.isEmpty()
            _loadingMessage.value = "جاري تحديث قائمة الخوادم..."
            _statusMessage.value = if (cached.isEmpty()) "جاري جلب قائمة الخوادم..." else "جاري التحقق من وجود خوادم جديدة..."
            try {
                val (source, fetched) = subscriptionRepo.fetchServers(currentSettings.customSubscriptions)
                if (fetched.isEmpty()) {
                    _statusMessage.value = "تعذر جلب الخوادم؛ القائمة المحفوظة إن وُجدت ما زالت متاحة."
                    log("Servers", "لم تصل قائمة قابلة للاستخدام من المرايا أو الكاش.")
                    return@launch
                }

                val serversWithSavedValues = withMeasurements(fetched, previous, savedMeasurements)
                val existingIds = previous.keys + cached.map { it.id }
                val newServers = serversWithSavedValues.filter {
                    it.id !in existingIds && it.id != "auto" &&
                        (showCloudflareServers || !it.isCloudflare)
                }
                publishServers(serversWithSavedValues)
                _isLoadingServers.value = false
                _statusMessage.value = "تم تحديث ${fetched.size} خادم • $source"
                log("Servers", "تم جلب ${fetched.size} خادم. خوادم جديدة تحتاج الفحص: ${newServers.size}.")

                if (cached.isEmpty()) {
                    // First run: measure real TCP reachability once. Later launches only probe newly added nodes.
                    scanAndMerge(serversWithSavedValues.filter { it.id != "auto" && it.tcpReachable == null &&
                        (showCloudflareServers || !it.isCloudflare) })
                } else if (newServers.isNotEmpty()) {
                    scanAndMerge(newServers)
                }
            } catch (e: Exception) {
                _statusMessage.value = "تعذر تحديث الخوادم: ${e.localizedMessage ?: "خطأ شبكة"}"
                log("ERROR", _statusMessage.value)
                sessionProfile?.let { profile ->
                    runCatching { firebaseTelemetry.reportError(profile, "server_fetch", e) }
                }
            } finally {
                _isLoadingServers.value = false
            }
        }
    }

    /** Explicit full refresh, initiated by the user. */
    fun scanAndSort() {
        if (scanJob?.isActive == true) return
        scanJob = viewModelScope.launch {
            val targets = _servers.value.filter { it.id != "auto" }
            if (targets.isEmpty()) return@launch
            log("TCP", "بدء فحص الوصول TCP يدويًا لـ ${targets.size} خادم.")
            scanAndMerge(targets)
        }
    }

    private suspend fun scanAndMerge(targets: List<VpnServer>) {
        if (targets.isEmpty()) return
        _statusMessage.value = "فحص اتصال TCP الحقيقي • ${targets.size} خادم"
        _loadingMessage.value = "جاري فحص اتصال TCP..."
        val scanned = serverScanService.scanServers(targets) { current, total ->
            _scanProgress.value = current to total
        }
        _scanProgress.value = null
        val byId = scanned.associateBy { it.id }
        val merged = _servers.value.map { byId[it.id] ?: it }
        publishServers(merged)
        val measurements = merged.mapNotNull { server ->
            if (server.tcpReachable == true) server.tcpLatencyMs?.let { server.id to it }
            else if (server.tcpReachable == false) server.id to -1.0
            else null
        }.toMap()
        settingsRepo.saveTcpMeasurements(measurements)
        val reachable = scanned.count { it.tcpReachable == true }
        log("TCP", "اكتمل الفحص: $reachable/${scanned.size} متاح TCP. لم يتم إجراء فحص UDP أو تنزيل.")
        _statusMessage.value = "اكتمل فحص TCP: $reachable/${scanned.size} متاح • UDP والسرعة غير مختبرين"
    }

    private fun withMeasurements(
        incoming: List<VpnServer>,
        previous: Map<String, VpnServer>,
        measurements: Map<String, Double>
    ): List<VpnServer> = incoming.map { server ->
        val old = previous[server.id]
        val hasSavedMeasurement = measurements.containsKey(server.id)
        val savedLatency = measurements[server.id]
        val tcp = if (hasSavedMeasurement && savedLatency != null && savedLatency >= 0) savedLatency else old?.tcpLatencyMs
        server.copy(
            tcpLatencyMs = tcp,
            tcpReachable = when {
                hasSavedMeasurement -> savedLatency != null && savedLatency >= 0
                tcp != null -> true
                else -> old?.tcpReachable
            },
            udpLatencyMs = old?.udpLatencyMs,
            udpProbePassed = old?.udpProbePassed,
            webProbePassed = old?.webProbePassed,
            downloadMbps = old?.downloadMbps
        )
    }

    private fun publishServers(list: List<VpnServer>) {
        val auto = list.firstOrNull { it.id == "auto" } ?: VpnServer.createAutoNode()
        val ordinary = list.filter { it.id != "auto" }
        val filtered = if (showCloudflareServers) ordinary else ordinary.filterNot { it.isCloudflare }
        val sorted = serverScanService.sortServersByPerformance(listOf(auto) + filtered, _platformFilter.value)
        _servers.value = sorted
        applyFilter()
        val selectedId = _selectedServer.value?.id
        _selectedServer.value = sorted.firstOrNull { it.id == selectedId } ?: sorted.firstOrNull()
    }

    fun selectServer(server: VpnServer) { _selectedServer.value = server }

    fun setPlatformFilter(filter: String) {
        _platformFilter.value = filter
        if (filter == "games") _statusMessage.value = "قياس UDP يتطلب محرك النفق، وغير متاح في نسخة Android الحالية."
        if (filter == "browse") _statusMessage.value = "فحص التصفح عبر النفق غير متاح في نسخة Android الحالية."
        if (filter == "stream") _statusMessage.value = "اختبار سرعة التنزيل غير متاح بعد؛ لن نعرض أرقامًا تقديرية."
        publishServers(_servers.value)
    }

    fun setSearchQuery(query: String) { _searchQuery.value = query; applyFilter() }

    private fun applyFilter() {
        val q = _searchQuery.value.trim().lowercase()
        val filterType = _platformFilter.value
        _filteredServers.value = _servers.value.filter { server ->
            if (server.id == "auto") return@filter q.isBlank() || "auto الأفضل".contains(q)
            if (!showCloudflareServers && server.isCloudflare) return@filter false
            if (server.tcpReachable == false) return@filter false
            val passes = when (filterType) {
                "games" -> server.udpProbePassed == true
                "browse" -> server.webProbePassed == true
                "stream" -> server.downloadMbps != null
                else -> true
            }
            passes && (q.isBlank() || server.remark.lowercase().contains(q) ||
                server.country.lowercase().contains(q) || server.host.lowercase().contains(q))
        }
    }

    fun toggleConnection() {
        val server = _selectedServer.value ?: run { _statusMessage.value = "اختر خادمًا أولًا"; return }
        if (connectionStatus.value == ConnectionStatus.CONNECTED || connectionStatus.value == ConnectionStatus.CONNECTING) {
            vpnServiceManager.disconnect()
            _statusMessage.value = "تم إيقاف الاتصال"
        } else if (server.id == "auto") {
            val target = serverScanService.getRecommendedServer(_servers.value, _platformFilter.value) ?: server
            vpnServiceManager.connect(target)
        } else {
            vpnServiceManager.connect(server)
        }
        _statusMessage.value = vpnServiceManager.connectionStepMessage.value
    }

    fun parseAndAddCustomLink(rawLink: String): Boolean {
        val server = subscriptionRepo.parseLink(rawLink) ?: return false
        if (_servers.value.any { it.id == server.id }) return false
        publishServers(_servers.value.filterNot { it.id == "auto" } + server)
        _selectedServer.value = server
        _statusMessage.value = "تمت إضافة الخادم: ${server.displayName}"
        log("Servers", "تمت إضافة رابط خادم يدويًا.")
        return true
    }

    fun generateSingBoxConfigForServer(server: VpnServer): String = configGeneratorService.generateSingBoxConfig(
        server = server,
        socksPort = appSettings.value.socksPort,
        httpPort = appSettings.value.httpPort,
        tunMode = appSettings.value.tunMode
    )

    fun addCustomSubscription(url: String) {
        val normalized = url.trim()
        if (!normalized.startsWith("https://") && !normalized.startsWith("http://")) {
            _statusMessage.value = "رابط الاشتراك يجب أن يبدأ بـ https:// أو http://"
            return
        }
        viewModelScope.launch {
            val settings = appSettings.value
            if (normalized !in settings.customSubscriptions) {
                settingsRepo.saveSettings(settings.copy(customSubscriptions = settings.customSubscriptions + normalized))
                log("Subscriptions", "تمت إضافة اشتراك مخصص.")
                loadServers()
            }
        }
    }

    fun saveUserProfile(name: String) {
        val trimmed = name.trim().take(40)
        if (trimmed.isBlank()) return
        viewModelScope.launch {
            val existing = userProfile.value ?: UserProfile()
            settingsRepo.saveProfile(existing.copy(displayName = trimmed))
            log("Profile", "تم حفظ اسم الملف محليًا على الجهاز.")
            _profileSetupRequired.value = false
        }
    }

    fun updateSettings(newSettings: AppSettings) {
        val safe = newSettings.copy(
            socksPort = newSettings.socksPort.coerceIn(1024, 65535),
            httpPort = newSettings.httpPort.coerceIn(1024, 65535),
            customSubscriptions = newSettings.customSubscriptions.distinct().take(20)
        )
        viewModelScope.launch {
            val cloudflareVisibilityChanged = safe.showCloudflareServers != appSettings.value.showCloudflareServers
            settingsRepo.saveSettings(safe)
            showCloudflareServers = safe.showCloudflareServers
            if (cloudflareVisibilityChanged) loadServers() else applyFilter()
        }
    }

    private fun log(category: String, message: String) {
        _logs.value = (_logs.value + "[$category] $message").takeLast(300)
    }
}
