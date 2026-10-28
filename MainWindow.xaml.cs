using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Collections.Concurrent;
using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Text.RegularExpressions;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
using MediaColor = System.Windows.Media.Color;
using FlareVpn.Models;
using FlareVpn.Services;
using FlareVpn.ViewModels;
using CommunityToolkit.WinUI.Notifications;

namespace FlareVpn;


public partial class MainWindow : Window
{
    private sealed record UdpProbeResult(bool? Passed, double? LatencyMs, string Detail);

    private readonly MainViewModel _vm;
    private readonly SingBoxProcessManager _proc = new();
    private readonly XrayProcessManager _xrayProc = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly CancellationTokenSource _shutdownCts = new();
    private CancellationTokenSource? _backgroundRefreshCts;
    private Task? _backgroundRefreshTask;
    private readonly Queue<DateTime> _recentVless404s = new();
    private readonly Queue<DateTime> _recentDns404s = new();
    private readonly HashSet<string> _failedServersThisSession = new(StringComparer.Ordinal);
    private const int MaxAutomaticFailovers = 3;
    private DispatcherTimer? _refreshTimer;
    private AdvancedWindow? _advanced;
    private FirebaseTelemetryService? _telemetry;
    private DispatcherTimer? _telemetryHeartbeatTimer;
    private bool _settingsReady;
    private bool _usingSystemProxy;
    private bool _automaticConnection;
    private bool _handlingAutomaticFailover;
    private int _automaticFailovers;
    private string _runtimeFailureReason = "فشل متكرر في DNS";
    private string _lastDiagnosticKey = "";
    private DateTime _diagnosticBurstStartedUtc;
    private DateTime _lastDiagnosticPrintedUtc;
    private int _diagnosticBurstCount;
    private Forms.NotifyIcon? _trayIcon;
    private bool _allowClose;
    private bool _shutdownInProgress;
    private bool _shutdownComplete;
    private string? _lastExitLocation;

    private void ShowNotification(string title, string message, bool? succeeded = null)
    {
        try
        {
            var cleanMessage = message.Length > 220 ? message[..217] + "..." : message;
            var statePrefix = succeeded switch { true => "✓ ", false => "⚠ ", _ => "" };
            var builder = new ToastContentBuilder()
                .AddText(statePrefix + title)
                .AddText(cleanMessage);
            var toast = new Windows.UI.Notifications.ToastNotification(builder.GetXml());
            ToastNotificationManagerCompat.CreateToastNotifier().Show(toast);
        }
        catch (Exception ex)
        {
            try { Console.WriteLine($"[Notifications][WARN] Windows notification could not be shown: {ex.GetBaseException().Message}"); } catch { }
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        // Recover from a previous crash that may have left the Windows proxy enabled.
        SystemProxyService.ClearProxy();
        _vm = new MainViewModel();
        DataContext = _vm;
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.IsConnected) or nameof(MainViewModel.IsConnecting) or nameof(MainViewModel.IsBusy))
                UpdateGlow();
            if (e.PropertyName == nameof(MainViewModel.SelectedServer))
                UpdateDetails();
        };
        _proc.Output += line => Dispatcher.Invoke(() =>
        {
            if (ObserveRuntimeFailure(line)) return;
            _advanced?.AppendLog(line);
            try { Console.WriteLine("[sing-box] " + line); } catch { }
        });
        _proc.Exited += () => Dispatcher.Invoke(() =>
        {
            var unexpectedExit = _vm.IsConnected && !_vm.IsBusy && !_shutdownCts.IsCancellationRequested;
            if (unexpectedExit && _xrayProc.IsRunning) _ = _xrayProc.StopAsync();
            var stoppedServer = _vm.SelectedServer;
            if (_usingSystemProxy)
            {
                SystemProxyService.ClearProxy();
                _usingSystemProxy = false;
            }
            _vm.IsConnected = false;
            _vm.IsConnecting = false;
            _advanced?.AppendLog("sing-box exited.");
            try { Console.WriteLine("[sing-box] exited."); } catch { }
            if (unexpectedExit)
            {
                if (_automaticConnection)
                {
                    _runtimeFailureReason = "انتهت عملية sing-box بشكل غير متوقع";
                    if (stoppedServer != null) _failedServersThisSession.Add(stoppedServer.Id);
                    ScheduleAutomaticFailover();
                }
                else
                {
                    _vm.StatusMessage = "انتهت عملية النفق بشكل غير متوقع. اختر سيرفرًا آخر أو أعد الاتصال.";
                }
            }
        });
        _xrayProc.Output += line => Dispatcher.Invoke(() =>
        {
            _advanced?.AppendLog("[Xray] " + line);
            try { Console.WriteLine("[xray] " + line); } catch { }
        });
        _xrayProc.Exited += () => Dispatcher.Invoke(HandleXrayExited);

        _proc.BinPath = _proc.FindBinary();
        _proc.StopOrphanedInstances();
        InitializeTrayIcon();
        SettingsService.ApplyStartupRegistration(_vm.Settings.StartWithWindows);
        _settingsReady = true;

        Loaded += async (_, _) =>
        {
            EnableDarkTitleBar();
            UpdateGlow();
            if (!await EnsureUserProfileAsync()) return;
            await InitialLoadAsync();
            if (Environment.GetCommandLineArgs().Contains("--minimized", StringComparer.OrdinalIgnoreCase) && _vm.Settings.MinimizeToTray)
                Hide();
        };
        Closing += MainWindow_Closing;
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized && _vm.Settings.MinimizeToTray) Hide();
        };
    }

    private async Task<bool> EnsureUserProfileAsync()
    {
        var profile = UserProfileService.Load();
        var dialog = new ProfileSetupWindow(profile?.DisplayName) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            _allowClose = true;
            Close();
            return false;
        }

        profile ??= new UserProfile { CreatedAtUtc = DateTime.UtcNow };
        profile.DisplayName = dialog.DisplayName;
        try { UserProfileService.Save(profile); }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"تعذر حفظ ملف المستخدم محليًا: {ex.Message}", "VPNGS", MessageBoxButton.OK, MessageBoxImage.Error);
            _allowClose = true;
            Close();
            return false;
        }
        _vm.Settings.FirebaseTelemetryEnabled = true;
        SettingsService.Save(_vm.Settings);

        try
        {
            await EnableTelemetryAsync(profile);
        }
        catch (FirebaseTelemetryService.UserBannedException ex)
        {
            _telemetry?.Dispose();
            _telemetry = null;
            System.Windows.MessageBox.Show(
                $"تم حظر هذا الحساب ولا يمكن تشغيل VPNGS.\n\nالسبب: {ex.Message}\n\nالمعرّف: {profile.FirebaseUid}",
                "تم إيقاف الحساب", MessageBoxButton.OK, MessageBoxImage.Warning);
            _allowClose = true;
            Close();
            return false;
        }
        return true;
    }

    private async Task EnableTelemetryAsync(UserProfile? profile = null)
    {
        profile ??= UserProfileService.Load();
        if (profile == null)
        {
            _vm.Settings.FirebaseTelemetryEnabled = false;
            SettingsService.Save(_vm.Settings);
            _vm.StatusMessage = "تعذر بدء التتبع: ملف المستخدم غير موجود.";
            return;
        }
        if (_telemetry != null) return;
        _telemetry = new FirebaseTelemetryService(profile);
        await _telemetry.StartSessionAsync(_shutdownCts.Token);
        _telemetryHeartbeatTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _telemetryHeartbeatTimer.Tick += async (_, _) =>
        {
            if (_telemetry != null) await _telemetry.HeartbeatAsync(_shutdownCts.Token);
        };
        _telemetryHeartbeatTimer.Start();
    }

    private void InitializeTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("فتح VPNGS", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        menu.Items.Add("خروج", null, (_, _) => Dispatcher.Invoke(() => { _allowClose = true; Close(); }));
        _trayIcon = new Forms.NotifyIcon
        {
            Text = "VPNGS",
            Icon = Drawing.SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = _vm.Settings.MinimizeToTray
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose && _vm.Settings.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            _trayIcon?.ShowBalloonTip(1800, "VPNGS", "البرنامج يعمل في شريط النظام.", Forms.ToolTipIcon.Info);
            return;
        }

        // WPF does not wait for an async void Closing handler. Cancel the first
        // close, stop the tunnel while the dispatcher is alive, then close again.
        if (_shutdownComplete) return;
        e.Cancel = true;
        if (_shutdownInProgress) return;
        _shutdownInProgress = true;

        _refreshTimer?.Stop();
        _telemetryHeartbeatTimer?.Stop();
        _shutdownCts.Cancel();
        try { Console.WriteLine("[Shutdown] Cancelling background server checks..."); } catch { }
        try
        {
            // The background UDP scan owns temporary sing-box/Xray probe
            // processes. Wait for its cancellation/finally blocks before closing.
            await StopBackgroundRefreshAsync();
        }
        catch (Exception ex)
        {
            try { Console.WriteLine("[Shutdown][WARN] Background checks did not stop cleanly: " + ex); } catch { }
        }

        try
        {
            _vm.IsConnecting = false;
            _vm.IsConnected = false;
            try { Console.WriteLine("[Shutdown] Stopping the active sing-box tunnel..."); } catch { }
            await _proc.StopAsync();
        }
        catch (Exception ex)
        {
            try { Console.WriteLine("[Shutdown][WARN] Could not stop sing-box cleanly: " + ex); } catch { }
        }

        try
        {
            try { Console.WriteLine("[Shutdown] Stopping the active Xray tunnel..."); } catch { }
            await _xrayProc.StopAsync();
        }
        catch (Exception ex)
        {
            try { Console.WriteLine("[Shutdown][WARN] Could not stop Xray cleanly: " + ex); } catch { }
        }

        try
        {
            // Remove the disconnected Wintun device after its owner exits so the
            // VPNGS adapter does not remain as a ghost network connection.
            await _proc.CleanupDisconnectedTunDevicesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            try { Console.WriteLine("[Shutdown][WARN] Could not clean the disconnected TUN adapter: " + ex); } catch { }
        }
        try
        {
            _telemetryHeartbeatTimer?.Stop();
            if (_telemetry != null)
            {
                await _telemetry.EndSessionAsync("normal_exit");
                _telemetry.Dispose();
                _telemetry = null;
            }
        }
        catch (Exception ex)
        {
            try { Console.WriteLine("[Firebase][WARN] Session shutdown reporting failed: " + ex.GetBaseException().Message); } catch { }
        }
        finally
        {
            if (_usingSystemProxy)
            {
                SystemProxyService.ClearProxy();
                _usingSystemProxy = false;
            }
            _trayIcon?.Dispose();
            _shutdownComplete = true;
            _allowClose = true;
            _shutdownInProgress = false;
            try { Console.WriteLine("[Shutdown] Cleanup finished; closing VPNGS."); } catch { }
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
    }

    public void ApplyTraySetting()
    {
        if (_trayIcon != null) _trayIcon.Visible = _vm.Settings.MinimizeToTray;
    }

    public void ApplyOptionalSettings()
    {
        SettingsService.Save(_vm.Settings);
        SettingsService.ApplyStartupRegistration(_vm.Settings.StartWithWindows);
        ApplyTraySetting();
        _vm.ApplyFilter();
        if (_vm.SelectedServer != null && !_vm.FilteredServers.Contains(_vm.SelectedServer))
        {
            _vm.SelectedServer = _vm.Servers.FirstOrDefault(server => server.Id == "auto")
                ?? _vm.FilteredServers.FirstOrDefault();
            UpdateDetails();
        }
    }

    private void HandleXrayExited()
    {
        var unexpectedExit = _vm.IsConnected && !_vm.IsBusy && !_shutdownCts.IsCancellationRequested;
        var stoppedServer = _vm.SelectedServer;
        if (_usingSystemProxy)
        {
            SystemProxyService.ClearProxy();
            _usingSystemProxy = false;
        }
        _vm.IsConnected = false;
        _vm.IsConnecting = false;
        if (_proc.IsRunning && unexpectedExit) _ = _proc.StopAsync();
        _advanced?.AppendLog("Xray exited.");
        try { Console.WriteLine("[xray] exited."); } catch { }
        if (!unexpectedExit) return;
        if (_automaticConnection)
        {
            _runtimeFailureReason = "انتهت عملية Xray بشكل غير متوقع";
            if (stoppedServer != null) _failedServersThisSession.Add(stoppedServer.Id);
            ScheduleAutomaticFailover();
        }
        else
        {
            _vm.StatusMessage = "انتهت عملية Xray بشكل غير متوقع. اختر سيرفرًا آخر أو أعد الاتصال.";
        }
    }

    private bool ObserveRuntimeFailure(string line)
    {
        var statusCode = line.Contains("unexpected HTTP response status: 429", StringComparison.OrdinalIgnoreCase) ? 429
            : line.Contains("unexpected HTTP response status: 404", StringComparison.OrdinalIgnoreCase) ? 404
            : 0;
        if (statusCode == 0) return false;

        var isDnsPacketFailure = line.Contains("process DNS packet", StringComparison.OrdinalIgnoreCase);
        var isProxyHttpFailure = line.Contains("outbound/vless[proxy]", StringComparison.OrdinalIgnoreCase) ||
                                 line.Contains("outbound/proxy", StringComparison.OrdinalIgnoreCase);
        var hasConnectionDestination = line.Contains("connection: open connection to", StringComparison.OrdinalIgnoreCase);
        if (!isDnsPacketFailure && !isProxyHttpFailure && !hasConnectionDestination) return false;

        var server = _vm.SelectedServer;
        var serverLabel = server == null ? "غير محدد" : server.DisplayName;
        var protocol = server?.Protocol.ToString() ?? "غير معروف";
        var endpoint = server == null ? "غير معروف" : $"{server.Host}:{server.Port}";
        var category = isDnsPacketFailure ? "DNS/DoH" : isProxyHttpFailure ? "استجابة عبر نفق البروكسي" : "اتصال HTTP صادر";
        var key = $"{server?.Id}|{category}|{statusCode}";
        var now = DateTime.UtcNow;
        if (key != _lastDiagnosticKey || now - _lastDiagnosticPrintedUtc > TimeSpan.FromSeconds(15))
        {
            _lastDiagnosticKey = key;
            _diagnosticBurstStartedUtc = now;
            _lastDiagnosticPrintedUtc = now;
            _diagnosticBurstCount = 1;
            PrintRuntimeDiagnostic(line, category, statusCode, serverLabel, protocol, endpoint, firstInBurst: true);
            return true;
        }

        _diagnosticBurstCount++;
        // Coalesce noisy repeated sing-box lines into one periodic count summary.
        if (now - _lastDiagnosticPrintedUtc >= TimeSpan.FromSeconds(5))
        {
            _lastDiagnosticPrintedUtc = now;
            var elapsed = now - _diagnosticBurstStartedUtc;
            Console.WriteLine($"[VPNGS][LOG SUMMARY] category={category}; HTTP={statusCode}; server={serverLabel}; protocol={protocol}; endpoint={endpoint}; repeated={_diagnosticBurstCount} times in {elapsed.TotalSeconds:0}s; auto-switch=disabled.");
        }
        return true;
    }

    private static void PrintRuntimeDiagnostic(string line, string category, int statusCode,
        string serverLabel, string protocol, string endpoint, bool firstInBurst)
    {
        var meaning = statusCode switch
        {
            429 => "Too Many Requests: الجهة التي ردّت فرضت حدًا مؤقتًا على الطلبات.",
            404 => "Not Found: الجهة التي ردّت لم تجد المسار/المورد المطلوب.",
            _ => "استجابة HTTP غير ناجحة."
        };
        var interpretation = statusCode == 429
            ? "الاحتمالات: حدّ مزود DNS/DoH أو عنوان الخروج المشترك للسيرفر أو سياسة في المسار. الرمز وحده لا يحدد أيها السبب ولا يعني أن بروتوكول النفق غير مدعوم."
            : "الاحتمالات: مسار DoH غير صحيح، أو رد من وسيط/مسار السيرفر. الرمز وحده لا يثبت أن بروتوكول النفق غير مدعوم."
              + (category == "استجابة عبر نفق البروكسي" ? " وإذا كان الرد على VLESS outbound فقد تكون بيانات النقل أو إعداد VLESS غير مقبولة لدى الطرف البعيد." : "");
        var resolver = category == "DNS/DoH"
            ? "إعداد التطبيق: DNS الافتراضي Google DoH على 8.8.8.8:443/dns-query عبر البروكسي؛ Bootstrap Cloudflare على 1.1.1.1:443/dns-query مباشر. سطر process DNS packet لا يحدد أي الوجهتين أصدرت الرد، لذلك لا أجزم بالمصدر."
            : "الوجهة الظاهرة في سجل sing-box أدناه تساعد على تحديد الطرف الذي أعاد الرد.";
        var cleanLine = Regex.Replace(line, @"\x1B\[[0-?]*[ -/]*[@-~]", "").Trim();

        Console.WriteLine("[VPNGS][RUNTIME DIAGNOSTIC] sing-box received an HTTP error");
        Console.WriteLine($"  Server: {serverLabel}");
        Console.WriteLine($"  Tunnel protocol / endpoint: {protocol} / {endpoint}");
        Console.WriteLine($"  Error: HTTP {statusCode} ({(statusCode == 429 ? "rate limit" : "not found")}) in {category}");
        Console.WriteLine($"  Meaning: {meaning}");
        Console.WriteLine($"  Assessment: {interpretation}");
        Console.WriteLine($"  DNS context: {resolver}");
        Console.WriteLine("  Action: بقي التطبيق على نفس السيرفر؛ لا يوجد تبديل تلقائي بسبب هذا الخطأ.");
        Console.WriteLine($"  sing-box detail: {cleanLine}");
        Console.WriteLine("[VPNGS][RUNTIME DIAGNOSTIC END]");
    }

    private void ScheduleAutomaticFailover()
    {
        if (_handlingAutomaticFailover || _shutdownCts.IsCancellationRequested) return;
        _handlingAutomaticFailover = true;
        Dispatcher.BeginInvoke(new Action(async () => await AutomaticFailoverAsync()));
    }

    private async Task AutomaticFailoverAsync()
    {
        try
        {
            while (!_shutdownCts.IsCancellationRequested && _automaticConnection &&
                   _automaticFailovers < MaxAutomaticFailovers)
            {
                var previous = _vm.SelectedServer;
                if (previous != null && previous.Id != "auto") _failedServersThisSession.Add(previous.Id);

                // Automatic failover only retries servers that passed the UDP tunnel probe.
                var next = _vm.Servers.FirstOrDefault(server => server.Id != "auto" &&
                    server.UdpProbePassed == true && !_failedServersThisSession.Contains(server.Id));
                if (next == null) break;

                _automaticFailovers++;
                _recentVless404s.Clear();
                _recentDns404s.Clear();
                _vm.SelectedServer = next;
                UpdateDetails();
                _vm.IsConnected = false;
                _vm.StatusMessage = $"{_runtimeFailureReason}؛ جارٍ تجربة {next.DisplayName} ({_automaticFailovers}/{MaxAutomaticFailovers})...";
                Console.WriteLine($"[Failover] Trying {next.DisplayName} after {_runtimeFailureReason}.");

                await ConnectAsync();
                if (!_vm.IsConnected)
                {
                    _failedServersThisSession.Add(next.Id);
                    continue;
                }

                // Give the replacement a brief observation window before declaring it stable.
                await Task.Delay(TimeSpan.FromSeconds(3), _shutdownCts.Token);
                if (_recentVless404s.Count < 3 && _recentDns404s.Count < 3) return;
            }

            if (_vm.IsConnected) await DisconnectAsync();
            _automaticConnection = false;
            _vm.StatusMessage = $"لم أجد سيرفرًا بديلًا سليمًا بعد {_runtimeFailureReason}. حدّث القائمة أو اختر سيرفرًا آخر يدويًا.";
            Console.WriteLine($"[Failover] No healthy alternative remained after {_runtimeFailureReason}.");
        }
        catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
        {
        }
        finally
        {
            _handlingAutomaticFailover = false;
        }
    }

    private async Task InitialLoadAsync()
    {
        var scanCache = await ServerScanCacheService.LoadAsync(_shutdownCts.Token);
        if (scanCache?.Servers.Count > 0)
        {
            ReplaceVisibleServers(scanCache.Servers.Where(server => !server.IsCloudflare), keepSelection: false);
            RestoreSelectedServer();
            var age = DateTime.UtcNow - scanCache.SavedAtUtc;
            _vm.StatusMessage = scanCache.RequiresUdpRescan
                ? $"تم تحميل {scanCache.Servers.Count} سيرفر؛ تُحدّث الآن قياسات UDP القديمة بالخلفية."
                : $"تم تحميل {scanCache.Servers.Count} سيرفر من الكاش ({age.TotalHours:0.#} ساعة)؛ التحديث يعمل بالخلفية.";
            Console.WriteLine(scanCache.RequiresUdpRescan
                ? $"[Cache] Loaded {scanCache.Servers.Count} servers; old single-sample UDP results require a fresh scan. App is ready."
                : $"[Cache] Loaded {scanCache.Servers.Count} servers and UDP results ({age.TotalHours:0.#}h old). App is ready.");
            _vm.IsLoadingServers = false;

            // Keep the saved results and ordering. The background refresh only
            // checks servers that are genuinely new in the updated feed.
            _backgroundRefreshTask = RefreshServersAsync(
                keepSelection: true, showLoading: false, fullScan: scanCache.RequiresUdpRescan, isBackground: true);
        }
        else
        {
            var rawCache = await SubscriptionService.LoadCachedListAsync(_shutdownCts.Token);
            var cachedServers = rawCache.HasValue
                ? SubscriptionService.Parse(rawCache.Value.Text).Where(server => !server.IsCloudflare).ToList()
                : new List<VpnServer>();
            if (cachedServers.Count > 0)
            {
                ReplaceVisibleServers(cachedServers, keepSelection: false);
                RestoreSelectedServer();
                _vm.StatusMessage = "تم تحميل قائمة السيرفرات المحلية؛ يجري فحص UDP لأفضل المرشحين بالخلفية.";
                Console.WriteLine($"[Cache] Loaded {cachedServers.Count} servers from feed cache; UDP results are not cached yet.");
                _vm.IsLoadingServers = false;
                _backgroundRefreshTask = RefreshServersAsync(
                    keepSelection: true, showLoading: true, fullScan: true, isBackground: true);
            }
            else
            {
                await RefreshServersAsync(showLoading: true, fullScan: true);
            }
        }

        UpdateDetails();

        if (_vm.Settings.AutoRefresh)
        {
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(_vm.Settings.RefreshMinutes) };
            _refreshTimer.Tick += async (_, _) => await AutoRefreshServersAsync();
            _refreshTimer.Start();
        }
    }

    private void RestoreSelectedServer()
    {
        if (!string.IsNullOrEmpty(_vm.Settings.SelectedServerId))
        {
            var saved = _vm.Servers.FirstOrDefault(server => server.Id == _vm.Settings.SelectedServerId)
                ?? _vm.Servers.FirstOrDefault(server => server.Id != "auto" &&
                    SubscriptionService.LegacyStableId(server.RawLink) == _vm.Settings.SelectedServerId);
            if (saved != null)
            {
                _vm.SelectedServer = saved;
                // Migrate the selected-server preference from the old link-hash ID.
                if (_vm.Settings.SelectedServerId != saved.Id)
                {
                    _vm.Settings.SelectedServerId = saved.Id;
                    SettingsService.Save(_vm.Settings);
                }
            }
        }
        _vm.SelectedServer ??= _vm.FilteredServers.FirstOrDefault() ?? _vm.Servers.FirstOrDefault();
    }

    private void ReplaceVisibleServers(IEnumerable<VpnServer> servers, bool keepSelection)
    {
        var previousId = keepSelection ? _vm.SelectedServer?.Id : null;
        var entries = servers.Where(server => server.Id != "auto").ToList();
        _vm.Servers.ReplaceAll(entries);
        _vm.Servers.Insert(0, new VpnServer { Id = "auto", Remark = _vm.RecommendationLabel, Country = "Auto" });
        _vm.SortByPerformance();
        _vm.ApplyFilter();
        if (previousId != null)
            _vm.SelectedServer = _vm.Servers.FirstOrDefault(server => server.Id == previousId);
        if (_vm.SelectedServer == null)
            _vm.SelectedServer = _vm.FilteredServers.FirstOrDefault() ?? _vm.Servers.FirstOrDefault();
    }

    private async Task AutoRefreshServersAsync()
    {
        await RefreshServersAsync(keepSelection: true, showLoading: false, fullScan: false, isBackground: true);
    }

    private async Task RefreshServersAsync(
        bool keepSelection = false,
        bool showLoading = true,
        bool fullScan = true,
        bool isBackground = false)
    {
        if (_vm.IsBusy) return;
        if (_vm.IsConnected)
        {
            if (!isBackground) _vm.StatusMessage = "افصل النفق قبل إعادة فحص UDP للسيرفرات.";
            return;
        }
        if (!await _refreshGate.WaitAsync(0)) return;

        var previousId = _vm.SelectedServer?.Id;
        CancellationTokenSource? backgroundCts = null;
        var refreshToken = _shutdownCts.Token;
        if (isBackground)
        {
            backgroundCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
            _backgroundRefreshCts = backgroundCts;
            refreshToken = backgroundCts.Token;
        }

        if (showLoading)
        {
            _vm.IsBusy = true;
            _vm.IsLoadingServers = true;
            ServerCombo.IsDropDownOpen = false;
            if (_advanced?.IsLoaded == true) _advanced.IsEnabled = false;
            _vm.LoadingMessage = "جاري جلب قائمة السيرفرات من المصدر...";
            _vm.StatusMessage = "جلب قائمة السيرفرات...";
        }
        try
        {
            Console.WriteLine("[Servers] Fetching server list...");
            var (url, text) = await SubscriptionService.FetchAsync(refreshToken);
            var parsed = SubscriptionService.Parse(text);
            var fetchedCount = parsed.Count;
            parsed = parsed.Where(server => !server.IsCloudflare).ToList();
            Console.WriteLine($"[Servers] Fetched {parsed.Count} non-Cloudflare servers from {url}; excluded {fetchedCount - parsed.Count} Cloudflare entries.");

            var previousById = _vm.Servers.Where(server => server.Id != "auto")
                .GroupBy(server => server.Id).ToDictionary(group => group.Key, group => group.First());
            foreach (var server in parsed)
            {
                if (!previousById.TryGetValue(server.Id, out var cached)) continue;
                server.UdpProbePassed = cached.UdpProbePassed;
                server.UdpLatencyMs = cached.UdpLatencyMs;
                server.WebProbePassed = cached.WebProbePassed;
                server.DownloadMbps = cached.DownloadMbps;
                server.SetVerifiedExitLocation(cached.VerifiedExitCountry, cached.VerifiedExitCountryCode);
            }

            ReplaceVisibleServers(parsed, keepSelection || previousId != null);
            var testTotal = parsed.Count;
            var newServers = parsed
                .Where(server => !previousById.ContainsKey(server.Id))
                .ToList();
            var candidates = fullScan ? parsed : newServers;

            if (showLoading)
            {
                _vm.StatusMessage = $"تم جلب {testTotal} سيرفر؛ بدء فحص UDP لـ {candidates.Count}...";
                _vm.LoadingMessage = $"جاري فحص UDP عبر الأنفاق... 0/{candidates.Count}";
            }
            Console.WriteLine(fullScan
                ? $"[UDP test] Starting full scan for {candidates.Count}/{testTotal} servers."
                : $"[UDP test] Incremental update: checking {candidates.Count} new servers; keeping saved results for {testTotal - candidates.Count} existing servers.");
            await TestGameUdpCandidatesAsync(candidates, refreshToken);

            _vm.SortByPerformance();
            await ServerScanCacheService.SaveAsync(_vm.Servers, fullScan, refreshToken);
            var udpPassedCount = parsed.Count(server => server.UdpProbePassed == true);
            var unclassifiedCount = parsed.Count(server => server.UdpProbePassed == null);
            var summary = udpPassedCount > 0
                ? $"{_vm.RecommendationLabel}؛ اجتاز UDP {udpPassedCount} سيرفر."
                : $"لم يثبت نجاح UDP لأي سيرفر حتى الآن.";
            if (unclassifiedCount > 0)
                summary += $" تعذر إكمال الفحص المحلي لـ {unclassifiedCount} سيرفر؛ لم تُحسب كفشل، ويمكن إعادة فحصها يدويًا من تحديث القائمة.";
            if (!isBackground) _vm.StatusMessage = summary;
            else _vm.StatusMessage = $"تم تحديث القائمة بالخلفية. {summary}";
            Console.WriteLine($"[UDP test] Finished: {udpPassedCount} passed; tested={candidates.Count}/{testTotal}; full={fullScan}.");
            if (!isBackground)
                ShowNotification("اكتمل فحص السيرفرات", $"اجتاز UDP: {udpPassedCount} من {testTotal}.", udpPassedCount > 0);

            if (previousId != null)
            {
                var s = _vm.Servers.FirstOrDefault(x => x.Id == previousId);
                if (s != null) _vm.SelectedServer = s;
            }
            if (_vm.SelectedServer == null && _vm.Servers.Count > 0)
            {
                _vm.SelectedServer = _vm.FilteredServers.FirstOrDefault() ?? _vm.Servers.FirstOrDefault();
            }
            UpdateDetails();
        }
        catch (OperationCanceledException) when (refreshToken.IsCancellationRequested)
        {
            Console.WriteLine(isBackground
                ? "[Servers] Background update cancelled (connection start or app shutdown)."
                : "[Servers] Server loading/tests cancelled during app shutdown.");
        }
        catch (Exception ex)
        {
            var cause = GetServerFetchCause(ex.GetBaseException().Message);
            if (_telemetry != null) _ = _telemetry.ReportExceptionAsync(ex, "server-fetch");
            if (!isBackground) _vm.StatusMessage = $"تعذر جلب قائمة السيرفرات: {cause} اضغط تحديث بعد إصلاح الاتصال.";
            if (!isBackground) ShowNotification("فشل تحديث السيرفرات", cause, false);
            Console.WriteLine("[Servers][ERROR] Failed to fetch or parse the server list.");
            Console.WriteLine($"[Servers][CAUSE] {cause}");
            WriteExceptionDetails("Servers", ex);
            _advanced?.AppendLog("فشل جلب السيرفرات: " + cause);
        }
        finally
        {
            if (showLoading)
            {
                if (_advanced?.IsLoaded == true) _advanced.IsEnabled = true;
                _vm.IsBusy = false;
                _vm.IsLoadingServers = false;
            }
            if (backgroundCts != null)
            {
                if (ReferenceEquals(_backgroundRefreshCts, backgroundCts)) _backgroundRefreshCts = null;
                backgroundCts.Dispose();
            }
            _refreshGate.Release();
        }
    }

    private async Task TestGameUdpCandidatesAsync(IReadOnlyCollection<VpnServer> servers, CancellationToken ct)
    {
        // Each probe launches its own proxy process. A small bounded pool avoids
        // starving Windows and causing false failures from local startup timeouts.
        const int parallelChecks = 4;
        var candidates = servers.Where(server => server.Id != "auto").ToList();
        if (candidates.Count == 0) return;
        if (_vm.IsConnected)
        {
            Console.WriteLine("[UDP test][WARN] Full UDP scan skipped while a VPN tunnel is active, to avoid routing probe processes through the active tunnel.");
            return;
        }

        IPAddress? stunAddress;
        try
        {
            stunAddress = (await Dns.GetHostAddressesAsync("stun.l.google.com", ct))
                .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            Console.WriteLine($"[Game UDP][WARN] Could not resolve STUN test host; automatic game-UDP scan skipped: {ex.GetBaseException().Message}");
            return;
        }
        if (stunAddress == null)
        {
            Console.WriteLine("[Game UDP][WARN] STUN test host returned no IPv4 address; automatic scan skipped.");
            return;
        }

        Console.WriteLine($"[UDP test] Starting real UDP tunnel checks for {candidates.Count} servers; concurrency={parallelChecks}.");
        var queue = new ConcurrentQueue<VpnServer>(candidates);
        var startedCount = 0;
        var checkedCount = 0;
        var successCount = 0;
        if (_vm.IsLoadingServers)
        {
            _vm.LoadingMessage = $"جاري فحص UDP لأفضل {candidates.Count} سيرفر...";
            _vm.StatusMessage = "اختبار UDP الحقيقي للسيرفرات...";
        }

        async Task WorkerAsync()
        {
            while (!ct.IsCancellationRequested && queue.TryDequeue(out var server))
            {
                var current = Interlocked.Increment(ref startedCount);
                Console.WriteLine($"[UDP test] Testing {server.DisplayName} ({current}/{candidates.Count})...");
                var result = await ProbeServerUdpThroughSocksAsync(server, stunAddress, ct);
                // A local sing-box/Xray startup failure says nothing about the
                // server's UDP capability. Keep any previously verified result.
                if (result.Passed.HasValue)
                {
                    server.UdpProbePassed = result.Passed.Value;
                    server.UdpLatencyMs = result.LatencyMs;
                }
                if (result.Passed == true) Interlocked.Increment(ref successCount);
                var completed = Interlocked.Increment(ref checkedCount);
                Console.WriteLine(result.Passed switch
                {
                    true => $"[UDP test] PASS {server.DisplayName}: median STUN RTT {result.LatencyMs:0.##} ms.",
                    false => $"[UDP test] FAIL {server.DisplayName}: {result.Detail}",
                    _ => $"[UDP test] RETRY {server.DisplayName}: {result.Detail}; not marked as an UDP failure."
                });
                if (completed % 10 == 0 || completed == candidates.Count)
                {
                    if (_vm.IsLoadingServers)
                        _vm.LoadingMessage = $"فحص UDP عبر الأنفاق... {completed}/{candidates.Count} | نجح {successCount}";
                    else
                        _vm.StatusMessage = $"فحص السيرفرات بالخلفية: {completed}/{candidates.Count}؛ اجتاز UDP {successCount}.";
                    _vm.SortByPerformance();
                    Console.WriteLine($"[UDP test] {completed}/{candidates.Count} servers checked; {successCount} passed.");
                }
            }
        }

        try
        {
            await Task.WhenAll(Enumerable.Range(0, Math.Min(parallelChecks, candidates.Count)).Select(_ => WorkerAsync()));
            Console.WriteLine($"[UDP test] Finished: {successCount}/{checkedCount} servers passed the real UDP tunnel probe.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Game UDP][WARN] Automatic scan stopped: {ex.GetBaseException().Message}");
        }
    }

    private async Task<UdpProbeResult> ProbeServerUdpThroughSocksAsync(VpnServer server, IPAddress stunAddress, CancellationToken ct)
    {
        const int stunPort = 19302;
        var (socksPort, httpPort) = GetFreeProxyPorts();
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(12)); // Includes local engine startup and three STUN samples.
        SingBoxProcessManager? singBoxProbe = null;
        XrayProcessManager? xrayProbe = null;
        var proxyStarted = false;
        try
        {
            if (XrayConfigBuilder.RequiresXray(server))
            {
                xrayProbe = new XrayProcessManager { BinPath = _xrayProc.BinPath };
                xrayProbe.Output += line => Console.WriteLine($"[Game UDP][{server.DisplayName}][xray] {line}");
                var config = XrayConfigBuilder.BuildProxy(server, socksPort, httpPort);
                if (!await xrayProbe.StartAsync(config, httpPort, timeout.Token))
                {
                    Console.WriteLine($"[Game UDP] Proxy startup failed for {server.DisplayName}: {xrayProbe.LastOutput}");
                    return new UdpProbeResult(null, null, "تعذر تشغيل Xray محليًا");
                }
            }
            else
            {
                singBoxProbe = new SingBoxProcessManager { BinPath = _proc.BinPath };
                singBoxProbe.Output += line => Console.WriteLine($"[Game UDP][{server.DisplayName}][sing-box] {line}");
                var settings = new AppSettings { TunMode = false, SocksPort = socksPort, HttpPort = httpPort };
                var config = SingBoxConfigBuilder.Build(server, settings);
                if (!await singBoxProbe.StartAsync(config, timeout.Token))
                {
                    Console.WriteLine($"[Game UDP] Proxy startup failed for {server.DisplayName}: {singBoxProbe.LastOutput}");
                    return new UdpProbeResult(null, null, "تعذر تشغيل sing-box محليًا");
                }
            }

            proxyStarted = true;
            var measurement = await SocksUdpStunProbeAsync(socksPort, stunAddress, stunPort, timeout.Token);
            return measurement.MedianMs.HasValue
                ? new UdpProbeResult(true, measurement.MedianMs, $"وسيط {measurement.SuccessfulSamples}/3 عينات STUN عبر UDP")
                : new UdpProbeResult(null, null, $"وصلت {measurement.SuccessfulSamples}/3 عينات STUN فقط؛ النتيجة غير كافية للترتيب");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            var detail = proxyStarted ? "انتهت مهلة فحص عينات STUN" : "انتهت مهلة تشغيل الفاحص المحلي";
            Console.WriteLine($"[Game UDP][TIMEOUT] {server.DisplayName}: {detail}.");
            return proxyStarted
                ? new UdpProbeResult(false, null, detail)
                : new UdpProbeResult(null, null, detail);
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException or InvalidDataException or HttpRequestException)
        {
            Console.WriteLine($"[Game UDP] Probe detail for {server.DisplayName}: {ex.GetBaseException().Message}");
            // A local socket/process exception is inconclusive; only a validated
            // STUN response counts as PASS, and a completed STUN timeout/rejection
            // is handled above as FAIL.
            return new UdpProbeResult(null, null, ex.GetBaseException().Message);
        }
        finally
        {
            timeout.Dispose();
            if (singBoxProbe != null) await singBoxProbe.StopAsync();
            if (xrayProbe != null) await xrayProbe.StopAsync();
        }
    }

    private async Task MeasureSelectedDownloadAsync(VpnServer server, int? localHttpProxyPort, CancellationToken ct)
    {
        server.DownloadMbps = null;
        Console.WriteLine($"[SpeedTest] START selected server {server.DisplayName}; downloading at most 1 MiB.");
        var handler = new HttpClientHandler { UseProxy = localHttpProxyPort.HasValue };
        if (localHttpProxyPort.HasValue)
        {
            handler.UseProxy = true;
            handler.Proxy = new WebProxy($"http://127.0.0.1:{localHttpProxyPort.Value}");
        }
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var downloadTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        downloadTimeout.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://bom.proof.ovh.net/files/1Mb.dat");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, downloadTimeout.Token);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(downloadTimeout.Token);
            var buffer = new byte[32 * 1024];
            const int measurementBytes = 1024 * 1024;
            var totalBytes = 0L;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            while (totalBytes < measurementBytes)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, measurementBytes - totalBytes)), downloadTimeout.Token);
                if (read == 0) break;
                totalBytes += read;
            }
            stopwatch.Stop();
            if (totalBytes > 0 && stopwatch.Elapsed.TotalSeconds > 0)
            {
                server.DownloadMbps = totalBytes * 8d / stopwatch.Elapsed.TotalSeconds / 1_000_000d;
                Console.WriteLine($"[SpeedTest] PASS {server.DisplayName}: {server.DownloadMbps:0.##} Mbps over a capped 1 MiB sample.");
            }
            else
            {
                Console.WriteLine($"[SpeedTest] FAIL {server.DisplayName}: empty download response.");
            }
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested) throw;
            Console.WriteLine($"[SpeedTest] FAIL {server.DisplayName}: {ex.GetBaseException().Message}");
        }
    }

    private sealed record UdpLatencyMeasurement(double? MedianMs, int SuccessfulSamples);

    private static async Task<UdpLatencyMeasurement> SocksUdpStunProbeAsync(int socksPort, IPAddress stunAddress, int stunPort, CancellationToken ct)
    {
        using var control = new TcpClient(AddressFamily.InterNetwork);
        await control.ConnectAsync(IPAddress.Loopback, socksPort, ct);
        var stream = control.GetStream();
        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, ct);
        var greeting = new byte[2];
        await stream.ReadExactlyAsync(greeting, ct);
        if (greeting[0] != 0x05 || greeting[1] != 0x00)
            throw new InvalidDataException("SOCKS5 no-auth negotiation failed.");

        await stream.WriteAsync(new byte[] { 0x05, 0x03, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, ct);
        var reply = new byte[4];
        await stream.ReadExactlyAsync(reply, ct);
        if (reply[0] != 0x05 || reply[1] != 0x00)
            throw new InvalidDataException($"SOCKS5 UDP ASSOCIATE rejected ({reply[1]}).");

        IPAddress relayAddress;
        switch (reply[3])
        {
            case 0x01:
                var ipv4 = new byte[4];
                await stream.ReadExactlyAsync(ipv4, ct);
                relayAddress = new IPAddress(ipv4);
                break;
            case 0x04:
                var ipv6 = new byte[16];
                await stream.ReadExactlyAsync(ipv6, ct);
                relayAddress = new IPAddress(ipv6);
                break;
            case 0x03:
                var length = new byte[1];
                await stream.ReadExactlyAsync(length, ct);
                var domainBytes = new byte[length[0]];
                await stream.ReadExactlyAsync(domainBytes, ct);
                var domain = System.Text.Encoding.ASCII.GetString(domainBytes);
                relayAddress = (await Dns.GetHostAddressesAsync(domain, ct)).First();
                break;
            default:
                throw new InvalidDataException("SOCKS5 returned an unknown UDP relay address type.");
        }

        var relayPortBytes = new byte[2];
        await stream.ReadExactlyAsync(relayPortBytes, ct);
        var relayPort = (relayPortBytes[0] << 8) | relayPortBytes[1];
        if (relayPort == 0) throw new InvalidDataException("SOCKS5 returned an empty UDP relay port.");
        if (relayAddress.Equals(IPAddress.Any) || relayAddress.Equals(IPAddress.IPv6Any))
            relayAddress = IPAddress.Loopback;
        if (relayAddress.AddressFamily != AddressFamily.InterNetwork)
            throw new InvalidDataException("SOCKS5 UDP relay did not bind to IPv4.");

        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var timings = new List<double>(capacity: 3);
        for (var sample = 0; sample < 3; sample++)
        {
            ct.ThrowIfCancellationRequested();
            var transactionId = new byte[12];
            System.Security.Cryptography.RandomNumberGenerator.Fill(transactionId);
            var stunRequest = new byte[20];
            stunRequest[1] = 0x01;
            stunRequest[4] = 0x21; stunRequest[5] = 0x12; stunRequest[6] = 0xA4; stunRequest[7] = 0x42;
            Buffer.BlockCopy(transactionId, 0, stunRequest, 8, transactionId.Length);

            var packet = new byte[10 + stunRequest.Length];
            packet[3] = 0x01;
            Buffer.BlockCopy(stunAddress.GetAddressBytes(), 0, packet, 4, 4);
            packet[8] = (byte)(stunPort >> 8);
            packet[9] = (byte)stunPort;
            Buffer.BlockCopy(stunRequest, 0, packet, 10, stunRequest.Length);

            using var udpTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            udpTimeout.CancelAfter(TimeSpan.FromMilliseconds(1500));
            try
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                await udp.SendAsync(packet, new IPEndPoint(relayAddress, relayPort), udpTimeout.Token);
                var received = await udp.ReceiveAsync(udpTimeout.Token);
                stopwatch.Stop();
                var response = received.Buffer;
                if (response.Length < 10 || response[0] != 0 || response[1] != 0 || response[2] != 0)
                    continue;
                var payloadOffset = response[3] switch
                {
                    0x01 => 10,
                    0x04 => 22,
                    0x03 when response.Length > 4 => 7 + response[4],
                    _ => -1
                };
                if (payloadOffset < 0 || response.Length < payloadOffset + 20) continue;
                var stun = response.AsSpan(payloadOffset);
                var valid = stun[0] == 0x01 && stun[1] == 0x01 &&
                            stun[4] == 0x21 && stun[5] == 0x12 && stun[6] == 0xA4 && stun[7] == 0x42 &&
                            stun.Slice(8, 12).SequenceEqual(transactionId);
                if (valid) timings.Add(stopwatch.Elapsed.TotalMilliseconds);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // One lost sample is tolerated; require two replies for a stable ranking value.
            }
        }

        if (timings.Count < 2) return new UdpLatencyMeasurement(null, timings.Count);
        timings.Sort();
        var median = timings.Count == 3 ? timings[1] : (timings[0] + timings[1]) / 2d;
        Console.WriteLine($"[UDP test] STUN samples: {timings.Count}/3; median={median:0.##} ms; range={timings[0]:0.##}-{timings[^1]:0.##} ms.");
        return new UdpLatencyMeasurement(median, timings.Count);
    }

    private void UpdateDetails()
    {
        var s = _vm.SelectedServer;
        PingRun.Text = s?.Id == "auto" ? "تلقائي" : s?.UdpLatencyMs.HasValue == true ? $"{s.UdpLatencyMs:0.#} ms STUN/UDP" : "-";
        ProtoRun.Text = s?.Protocol.ToString() ?? "-";
        var warningServer = s?.Protocol == VpnProtocol.Hysteria2
            ? s
            : s?.Id == "auto" ? _vm.GetRecommendedServer() : null;
        HysteriaWarningPanel.Visibility = warningServer?.Protocol == VpnProtocol.Hysteria2
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void SettingsToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_settingsReady) return;
        SettingsService.Save(_vm.Settings);
        if (ReferenceEquals(sender, AutoRefreshToggle) && _vm.Settings.AutoRefresh)
        {
            if (_refreshTimer == null)
            {
                _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(_vm.Settings.RefreshMinutes) };
                _refreshTimer.Tick += async (_, _) => await AutoRefreshServersAsync();
            }
            _refreshTimer.Start();
        }
        else if (ReferenceEquals(sender, AutoRefreshToggle))
        {
            _refreshTimer?.Stop();
            _refreshTimer = null;
        }

        if (ReferenceEquals(sender, TunToggle) && _vm.IsConnected && !_vm.IsBusy)
            _ = RestartForTunSettingAsync();
    }

    private async Task RestartForTunSettingAsync()
    {
        if (_vm.IsBusy || !_vm.IsConnected) return;
        TunToggle.IsEnabled = false;
        _vm.IsBusy = true;
        _vm.IsConnecting = true;
        _vm.IsConnected = false;
        _vm.StatusMessage = "جارٍ إعادة الاتصال لتطبيق وضع TUN...";
        try
        {
            await _proc.StopAsync();
            await _xrayProc.StopAsync();
            SystemProxyService.ClearProxy();
            await ConnectAsync();
        }
        finally
        {
            TunToggle.IsEnabled = true;
            _vm.IsBusy = false;
        }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.IsBusy) return;
        if (_vm.IsConnected) { await DisconnectAsync(); return; }
        await ConnectAsync();
    }

    private async Task ConnectAsync()
    {
        var selectedBeforeScan = _vm.SelectedServer;
        if (selectedBeforeScan?.Id == "auto" && _backgroundRefreshTask is { IsCompleted: false } scanTask)
        {
            _vm.StatusMessage = "جارٍ إكمال فحص UDP لاختيار أفضل سيرفر من القائمة كلها...";
            try { await scanTask; }
            catch (OperationCanceledException) { }
            _backgroundRefreshTask = null;
        }
        else
        {
            await StopBackgroundRefreshAsync();
        }

        var srv = _vm.SelectedServer;
        if (srv == null) { _vm.StatusMessage = "اختر سيرفراً أولاً."; return; }
        ClearExitLocationLabels();
        _lastExitLocation = null;
        var useAutoSelection = srv.Id == "auto";
        if (!_handlingAutomaticFailover)
        {
            var preserveAutomaticMode = _vm.IsConnecting && _automaticConnection;
            _automaticConnection = useAutoSelection || preserveAutomaticMode;
            if (!preserveAutomaticMode)
            {
                _automaticFailovers = 0;
                _failedServersThisSession.Clear();
                _recentVless404s.Clear();
                _recentDns404s.Clear();
            }
        }
        if (useAutoSelection)
        {
            srv = _vm.GetRecommendedServer();
            if (srv == null)
            {
                _vm.StatusMessage = _vm.PlatformFilter switch
                {
                    "games" => "لا يوجد سيرفر اجتاز فحص UDP للألعاب بعد؛ اختر كل الاستخدامات أو حدّث القائمة.",
                    "browse" => "لا يوجد سيرفر اجتاز فحص الويب بعد؛ اختر كل الاستخدامات أو حدّث القائمة.",
                    "stream" => "لا توجد نتيجة تنزيل للمشاهدة بعد؛ حدّث القائمة لإعادة الفحص.",
                    _ => "لم ينجح فحص UDP لأي سيرفر؛ حدّث القائمة أو تحقق من الشبكة."
                };
                return;
            }
            _vm.SelectedServer = srv;
            UpdateDetails();
        }

        _vm.IsBusy = true;
        _vm.IsConnecting = true;
        _vm.StatusMessage = "جاري الاتصال...";
        ShowNotification("جارٍ الاتصال", $"يتم اختبار الاتصال عبر {srv.DisplayName}...");
        try
        {
            Console.WriteLine(useAutoSelection
                ? $"[Connect] Auto-selected {srv.DisplayName} from measured UDP tunnel results."
                : $"[Connect] Using manually selected server {srv.DisplayName}.");
            Console.WriteLine("[Connect] selected=" + srv.DisplayName + " host=" + srv.Host + ":" + srv.Port + " tun=" + _vm.Settings.TunMode);
            if (XrayConfigBuilder.RequiresXray(srv))
            {
                await ConnectXhttpAsync(srv, useAutoSelection);
                return;
            }
            var config = SingBoxConfigBuilder.Build(srv, _vm.Settings);
            Console.WriteLine("[Connect] config built, starting sing-box...");
            var ok = await _proc.StartAsync(config);
            Console.WriteLine("[Connect] sing-box start result=" + ok);
            var fellBackToProxy = false;
            if (!ok && _vm.Settings.TunMode)
            {
                _advanced?.AppendLog("تعذر إنشاء واجهة TUN؛ جارٍ تنظيف المحول وإعادة المحاولة مرة واحدة.");
                await _proc.StopAsync();
                _vm.IsConnecting = true;
                await Task.Delay(1500);
                config = SingBoxConfigBuilder.Build(srv, _vm.Settings);
                ok = await _proc.StartAsync(config);
                Console.WriteLine("[Connect] TUN retry result=" + ok);
            }

            var activeHttpPort = _vm.Settings.HttpPort;
            var activeSocksPort = _vm.Settings.SocksPort;
            if (!ok && _vm.Settings.TunMode)
            {
                _advanced?.AppendLog("فشلت إعادة TUN؛ جارٍ تشغيل Proxy احتياطي بمنافذ متاحة.");
                _vm.IsConnecting = true;
                var proxyFallbackSettings = new AppSettings
                {
                    TunMode = false,
                    SocksPort = _vm.Settings.SocksPort,
                    HttpPort = _vm.Settings.HttpPort
                };
                if (proxyFallbackSettings.SocksPort == proxyFallbackSettings.HttpPort ||
                    !CanBindLoopbackPort(proxyFallbackSettings.SocksPort) ||
                    !CanBindLoopbackPort(proxyFallbackSettings.HttpPort))
                {
                    (proxyFallbackSettings.SocksPort, proxyFallbackSettings.HttpPort) = GetFreeProxyPorts();
                    _advanced?.AppendLog($"المنافذ الافتراضية محجوزة؛ تم اختيار {proxyFallbackSettings.SocksPort}/{proxyFallbackSettings.HttpPort}.");
                }
                config = SingBoxConfigBuilder.Build(srv, proxyFallbackSettings);
                ok = await _proc.StartAsync(config);
                if (!ok && (_proc.LastOutput.Contains("bind:", StringComparison.OrdinalIgnoreCase) ||
                            _proc.LastOutput.Contains("Only one usage", StringComparison.OrdinalIgnoreCase)))
                {
                    await _proc.StopAsync();
                    (proxyFallbackSettings.SocksPort, proxyFallbackSettings.HttpPort) = GetFreeProxyPorts();
                    config = SingBoxConfigBuilder.Build(srv, proxyFallbackSettings);
                    ok = await _proc.StartAsync(config);
                }
                if (ok)
                {
                    fellBackToProxy = true;
                    activeHttpPort = proxyFallbackSettings.HttpPort;
                    activeSocksPort = proxyFallbackSettings.SocksPort;
                    Console.WriteLine("[Connect] TUN unavailable; system proxy fallback started.");
                }
                else
                {
                    _vm.StatusMessage = "تعذر تشغيل TUN وProxy. راجع السجل.";
                    ShowNotification("فشل الاتصال", $"تعذر تشغيل النفق مع {srv.DisplayName}.", false);
                    Console.WriteLine("[Connect] FAILED to start TUN and proxy fallback");
                    return;
                }
            }
            if (!ok) { _vm.StatusMessage = "فشل الاتصال."; ShowNotification("فشل الاتصال", $"لم يبدأ sing-box للسيرفر {srv.DisplayName}.", false); Console.WriteLine("[Connect] FAILED to start sing-box"); return; }

            if (_vm.Settings.TunMode && !fellBackToProxy)
            {
                Console.WriteLine("[Connect] TUN mode enabled");
                _advanced?.AppendLog("TUN mode: يتطلب صلاحيات Administrator.");
                if (!IsAdmin()) { Console.WriteLine("[Connect] WARNING: not running as admin"); _advanced?.AppendLog("تحذير: التطبيق لا يعمل كمسؤول."); }
            }
            else if (_vm.Settings.SetSystemProxy)
            {
                SystemProxyService.SetProxy("127.0.0.1", activeHttpPort);
                _usingSystemProxy = true;
                Console.WriteLine("[Connect] system proxy set to 127.0.0.1:" + activeHttpPort);
            }

            int? healthProxyPort = _vm.Settings.TunMode && !fellBackToProxy ? null : activeHttpPort;
            if (!await VerifyInternetPathAsync(srv, healthProxyPort, _shutdownCts.Token))
            {
                await HandleUnhealthyTunnelAsync(srv, "بدأ sing-box لكن فشل اختبار مرور الإنترنت عبر النفق.");
                return;
            }

            bool? udpProbePassed = null;
            if (_vm.Settings.TunMode && !fellBackToProxy)
            {
                udpProbePassed = await VerifyUdpRelayAsync(srv, _shutdownCts.Token);
                srv.UdpProbePassed = udpProbePassed;
                if (!udpProbePassed.Value) srv.UdpLatencyMs = null;
                _vm.SortByPerformance();
                UpdateDetails();
            }
            else
            {
                Console.WriteLine("[Connect][UDP] SKIP: system-proxy mode cannot validate Roblox UDP traffic.");
            }

            // Download measurement is intentionally limited to the server the user is connecting to.
            await MeasureSelectedDownloadAsync(srv, healthProxyPort, _shutdownCts.Token);
            _vm.SortByPerformance();
            UpdateDetails();

            _vm.IsConnected = true;
            _vm.Settings.SelectedServerId = srv.Id;
            SettingsService.Save(_vm.Settings);
            var connectionSummary = fellBackToProxy
                ? (_vm.Settings.SetSystemProxy
                    ? "تم تشغيل Proxy النظام؛ واجهة TUN غير متاحة."
                    : $"تم تشغيل Proxy محلي على 127.0.0.1:{activeSocksPort} (SOCKS) و127.0.0.1:{activeHttpPort} (HTTP).")
                : "بدأ النفق.";
            if (!string.IsNullOrWhiteSpace(_lastExitLocation))
                connectionSummary += $" موقع الخروج حسب IP (تقريبي): {_lastExitLocation}. اسم القائمة: {srv.DisplayName}.";
            connectionSummary += udpProbePassed switch
            {
                true => " فحص UDP عبر النفق نجح (مؤشر للألعاب، وليس ضمانًا لكل تجربة).",
                false => " فشل فحص UDP العام؛ قد يعمل الويب بينما لا تعمل بعض الألعاب عبر هذا السيرفر.",
                null => " لم يُفحص UDP لأن الاتصال يعمل عبر Proxy وليس TUN."
            };
            _vm.StatusMessage = useAutoSelection
                ? $"{connectionSummary} أقل قياس STUN/UDP حاليًا: {srv.DisplayName} ({srv.UdpLatencyMs:0.#} ms)؛ هذا مؤشر عام وليس قياسًا لخادم Roblox نفسه."
                : connectionSummary;
            await ServerScanCacheService.SaveAsync(_vm.Servers, fullScan: false, _shutdownCts.Token);
            Console.WriteLine("[Connect] SUCCESS");
            ShowNotification("تم الاتصال", $"اتصالك يعمل عبر {srv.DisplayName}.", true);
        }
        catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
        {
            _vm.StatusMessage = "تم إلغاء الاتصال عند إغلاق البرنامج.";
        }
        catch (Exception ex)
        {
            if (_telemetry != null) _ = _telemetry.ReportExceptionAsync(ex, "connect");
            var server = _vm.SelectedServer;
            Console.WriteLine($"[Connect][ERROR] Failed to connect; server={server?.DisplayName ?? "unknown"}; protocol={server?.Protocol.ToString() ?? "unknown"}; endpoint={server?.Host ?? "unknown"}:{server?.Port.ToString() ?? "unknown"}.");
            WriteExceptionDetails("Connect", ex);
            _vm.StatusMessage = "فشل الاتصال. راجع CMD أو سجل الإعدادات المتقدمة لمعرفة السبب.";
            ShowNotification("فشل الاتصال", ex.GetBaseException().Message, false);
            _advanced?.AppendLog("فشل الاتصال؛ التفاصيل الكاملة في CMD.");
        }
        finally
        {
            _vm.IsLoadingServers = false;
            _vm.IsConnecting = false;
            _vm.IsBusy = false;
        }
    }

    private async Task StopBackgroundRefreshAsync()
    {
        var task = _backgroundRefreshTask;
        if (task == null || task.IsCompleted) return;
        _backgroundRefreshCts?.Cancel();
        try { await task; }
        catch (OperationCanceledException) { }
        _backgroundRefreshTask = null;
    }

    private static void WriteExceptionDetails(string category, Exception exception)
    {
        var details = exception.GetBaseException().Message;
        foreach (var line in details.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            Console.WriteLine($"[{category}][ERROR] {line.Trim()}");
    }

    private async Task<bool> VerifyInternetPathAsync(VpnServer server, int? localHttpProxyPort, CancellationToken ct)
    {
        const string tag = "[Connect][HEALTH]";
        var path = localHttpProxyPort.HasValue
            ? $"HTTP proxy 127.0.0.1:{localHttpProxyPort.Value}"
            : "direct through TUN";
        Console.WriteLine($"{tag} Checking real internet traffic for {server.DisplayName} via {path}...");
        _vm.StatusMessage = "تم تشغيل النفق؛ جارٍ التحقق من مرور الإنترنت...";

        // Allow Windows to finish applying the Wintun routes before probing.
        await Task.Delay(TimeSpan.FromSeconds(1), ct);
        var endpoints = new[]
        {
            "https://www.gstatic.com/generate_204",
            "https://cp.cloudflare.com/generate_204"
        };
        var proxy = localHttpProxyPort.HasValue
            ? new WebProxy($"http://127.0.0.1:{localHttpProxyPort.Value}")
            : null;
        using var handler = new HttpClientHandler { UseProxy = proxy != null, Proxy = proxy };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var failures = new List<string>();

        foreach (var endpoint in endpoints)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var response = await client.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.IsSuccessStatusCode)
                {
                    server.WebProbePassed = true;
                    Console.WriteLine($"{tag} PASS {server.DisplayName}: {new Uri(endpoint).Host} returned HTTP {(int)response.StatusCode}.");
                    await LookupExitLocationAsync(client, server, ct);
                    return true;
                }
                failures.Add($"{new Uri(endpoint).Host}=HTTP {(int)response.StatusCode}");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                failures.Add($"{new Uri(endpoint).Host}={ex.GetBaseException().Message}");
            }
        }

        Console.WriteLine($"{tag} FAIL server={server.DisplayName}; protocol={server.Protocol}; endpoint={server.Host}:{server.Port}; path={path}.");
        server.WebProbePassed = false;
        foreach (var failure in failures) Console.WriteLine($"{tag} Probe: {failure}");
        return false;
    }

    private async Task<bool> VerifyUdpRelayAsync(VpnServer server, CancellationToken ct)
    {
        const string stunHost = "stun.l.google.com";
        const int stunPort = 19302;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(stunHost, timeout.Token);
            var address = addresses.FirstOrDefault(candidate => candidate.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            if (address == null) throw new HttpRequestException("STUN host returned no IPv4 address.");

            using var udp = new System.Net.Sockets.UdpClient(System.Net.Sockets.AddressFamily.InterNetwork);
            udp.Connect(new IPEndPoint(address, stunPort));

            // RFC 5389 Binding Request: type 0x0001, magic cookie and random transaction ID.
            var transactionId = new byte[12];
            System.Security.Cryptography.RandomNumberGenerator.Fill(transactionId);
            var request = new byte[20];
            request[1] = 0x01;
            request[4] = 0x21;
            request[5] = 0x12;
            request[6] = 0xA4;
            request[7] = 0x42;
            Buffer.BlockCopy(transactionId, 0, request, 8, transactionId.Length);

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            await udp.SendAsync(request, timeout.Token);
            var response = await udp.ReceiveAsync(timeout.Token);
            stopwatch.Stop();
            var packet = response.Buffer;
            var valid = packet.Length >= 20 && packet[0] == 0x01 && packet[1] == 0x01 &&
                        packet[4] == 0x21 && packet[5] == 0x12 && packet[6] == 0xA4 && packet[7] == 0x42 &&
                        packet.AsSpan(8, 12).SequenceEqual(transactionId);
            if (valid) server.UdpLatencyMs = stopwatch.Elapsed.TotalMilliseconds;
            Console.WriteLine(valid
                ? $"[Connect][UDP] PASS server={server.DisplayName}; STUN UDP response received through TUN from {stunHost}:{stunPort}."
                : $"[Connect][UDP] FAIL server={server.DisplayName}; STUN returned an invalid response.");
            return valid;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or HttpRequestException)
        {
            Console.WriteLine($"[Connect][UDP] FAIL server={server.DisplayName}; {ex.GetBaseException().Message}");
            return false;
        }
    }

    private async Task LookupExitLocationAsync(HttpClient client, VpnServer server, CancellationToken ct)
    {
        var providers = new (string Name, string Url, bool IpWho)[]
        {
            ("ipwho.is", "https://ipwho.is/", true),
            ("ipapi.co", "https://ipapi.co/json/", false)
        };
        var providerErrors = new List<string>();
        foreach (var provider in providers)
        {
            try
            {
                using var response = await client.GetAsync(provider.Url, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var document = await System.Text.Json.JsonDocument.ParseAsync(stream, cancellationToken: ct);
                var root = document.RootElement;
                if (provider.IpWho && (!root.TryGetProperty("success", out var success) || !success.GetBoolean()))
                    throw new HttpRequestException("Provider returned success=false.");

                var ip = ReadJsonString(root, "ip");
                var country = provider.IpWho ? ReadJsonString(root, "country") : ReadJsonString(root, "country_name");
                if (string.IsNullOrWhiteSpace(country)) country = ReadJsonString(root, "country");
                var countryCode = ReadJsonString(root, "country_code");
                var city = ReadJsonString(root, "city");
                var region = ReadJsonString(root, "region");
                var isp = root.TryGetProperty("connection", out var connection)
                    ? ReadJsonString(connection, "isp")
                    : ReadJsonString(root, "org");
                var place = string.Join(", ", new[] { city, country }.Where(value => !string.IsNullOrWhiteSpace(value)));
                if (string.IsNullOrWhiteSpace(place)) place = region;
                _vm.VerifiedExitLocation = string.Join(", ", new[] { country, city }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
                server.SetVerifiedExitLocation(country, countryCode);
                _lastExitLocation = string.Join(" • ", new[] { place, string.IsNullOrWhiteSpace(ip) ? "" : $"IP {ip}" }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
                Console.WriteLine($"[Connect][GEO] Exit IP location (approximate): server-label={server.DisplayName}; egress={_lastExitLocation}; ISP={isp}; source={provider.Name}.");
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested &&
                                       ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
            {
                var detail = ex.GetBaseException().Message;
                providerErrors.Add($"{provider.Name}: {detail}");
                Console.WriteLine($"[Connect][GEO] {provider.Name} lookup failed: {detail}; trying the backup provider.");
            }
        }

        // Some exits reset connections to JSON geolocation providers. Cloudflare
        // trace is a lightweight fallback on the same CDN used by the health probe.
        try
        {
            using var response = await client.GetAsync(
                "https://www.cloudflare.com/cdn-cgi/trace",
                HttpCompletionOption.ResponseHeadersRead,
                ct);
            response.EnsureSuccessStatusCode();
            var trace = await response.Content.ReadAsStringAsync(ct);
            var fields = trace.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split('=', 2))
                .Where(parts => parts.Length == 2)
                .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.OrdinalIgnoreCase);
            if (!fields.TryGetValue("loc", out var countryCode) || countryCode.Length != 2)
                throw new HttpRequestException("Cloudflare trace did not return a country code.");

            var country = GetCountryName(countryCode);
            fields.TryGetValue("ip", out var ip);
            _vm.VerifiedExitLocation = country;
            server.SetVerifiedExitLocation(country, countryCode);
            _lastExitLocation = string.Join(" • ", new[] { country, string.IsNullOrWhiteSpace(ip) ? "" : $"IP {ip}" }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
            Console.WriteLine($"[Connect][GEO] Exit IP location (approximate): server-label={server.DisplayName}; egress={_lastExitLocation}; source=Cloudflare trace.");
            return;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested &&
                                   ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            var detail = ex.GetBaseException().Message;
            providerErrors.Add($"Cloudflare trace: {detail}");
            Console.WriteLine($"[Connect][GEO] Cloudflare trace lookup failed: {detail}.");
        }

        Console.WriteLine("[Connect][GEO] All IP location providers failed; connection stays active. " + string.Join(" | ", providerErrors));
    }

    private static string GetCountryName(string countryCode)
    {
        try { return new RegionInfo(countryCode.ToUpperInvariant()).EnglishName; }
        catch { return countryCode.ToUpperInvariant(); }
    }

    private static string ReadJsonString(System.Text.Json.JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private void ClearExitLocationLabels()
    {
        foreach (var server in _vm.Servers)
            server.SetVerifiedExitLocation(null, null);
        _vm.VerifiedExitLocation = null;
    }

    private async Task HandleUnhealthyTunnelAsync(VpnServer server, string reason)
    {
        if (_telemetry != null) _ = _telemetry.ReportExceptionAsync(new InvalidOperationException(reason), "tunnel-health");
        _runtimeFailureReason = reason;
        Console.WriteLine($"[Connect][HEALTH][ERROR] {reason} server={server.DisplayName}; protocol={server.Protocol}; endpoint={server.Host}:{server.Port}.");
        _advanced?.AppendLog($"فشل فحص الإنترنت عبر {server.DisplayName}؛ جارٍ إيقاف النفق لمنع بقاء اتصال بلا إنترنت.");
        await _proc.StopAsync();
        await _xrayProc.StopAsync();
        if (_usingSystemProxy)
        {
            SystemProxyService.ClearProxy();
            _usingSystemProxy = false;
        }
        _vm.IsConnected = false;
        _vm.StatusMessage = $"فشل مرور الإنترنت عبر {server.DisplayName}. تم إيقاف النفق؛ راجع تفاصيل الفحص في CMD أو اختر سيرفرًا آخر.";
        ShowNotification("فشل التحقق من الاتصال", $"النفق بدأ لكن الإنترنت لم يعمل عبر {server.DisplayName}.", false);

        if (_automaticConnection)
        {
            _failedServersThisSession.Add(server.Id);
            ScheduleAutomaticFailover();
        }
    }

    private static string GetServerFetchCause(string message)
    {
        if (message.Contains("No such host is known", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("name resolution", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Name or service not known", StringComparison.OrdinalIgnoreCase))
            return "تعذر DNS: ويندوز لم يستطع تحويل أسماء نطاقات مصادر القوائم إلى عناوين IP. افحص DNS واتصال الإنترنت.";

        if (message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("timed out", StringComparison.OrdinalIgnoreCase))
            return "انتهت مهلة الاتصال بمصادر القوائم قبل وصول رد؛ افحص الإنترنت أو الجدار الناري.";

        if (message.Contains("HTTP ", StringComparison.OrdinalIgnoreCase))
            return "وصل التطبيق إلى مصدر قائمة، لكن المصدر أعاد استجابة HTTP غير ناجحة؛ راجع تفاصيل كل مرآة في CMD.";

        return "تعذر الاتصال بمصادر القوائم؛ راجع تفاصيل المرايا في CMD.";
    }

    private async Task ConnectXhttpAsync(VpnServer server, bool useAutoSelection)
    {
        var socksPort = _vm.Settings.SocksPort;
        var httpPort = _vm.Settings.HttpPort;
        if (socksPort == httpPort || !CanBindLoopbackPort(socksPort) || !CanBindLoopbackPort(httpPort))
        {
            (socksPort, httpPort) = GetFreeProxyPorts();
            _vm.Settings.SocksPort = socksPort;
            _vm.Settings.HttpPort = httpPort;
            SettingsService.Save(_vm.Settings);
            _advanced?.AppendLog($"منافذ Xray الافتراضية كانت محجوزة؛ تم اختيار {socksPort}/{httpPort}.");
        }

        try
        {
            await _proc.StopAsync();
            await _xrayProc.StopAsync();
            IPAddress? xrayEndpointIp = null;
            if (_vm.Settings.TunMode)
            {
                try
                {
                    if (IPAddress.TryParse(server.Host, out var literalAddress))
                        xrayEndpointIp = literalAddress;
                    else
                    {
                        var resolvedAddresses = await Dns.GetHostAddressesAsync(server.Host, _shutdownCts.Token);
                        xrayEndpointIp = resolvedAddresses.FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork)
                            ?? resolvedAddresses.FirstOrDefault();
                    }
                    if (xrayEndpointIp != null)
                        Console.WriteLine($"[Connect] XHTTP endpoint resolved to {xrayEndpointIp}; excluding it from TUN to prevent an Xray/SOCKS routing loop.");
                }
                catch (Exception ex) when (ex is SocketException or OperationCanceledException && !_shutdownCts.IsCancellationRequested)
                {
                    Console.WriteLine($"[Connect][WARN] Could not resolve XHTTP endpoint before TUN startup: {ex.GetBaseException().Message}");
                }
            }
            Console.WriteLine("[Connect] XHTTP transport detected; starting Xray engine...");
            var xrayConfig = XrayConfigBuilder.BuildProxy(server, socksPort, httpPort, xrayEndpointIp?.ToString());
            var xrayStarted = await _xrayProc.StartAsync(xrayConfig, httpPort, _shutdownCts.Token);
            Console.WriteLine("[Connect] Xray start result=" + xrayStarted);
            if (!xrayStarted)
            {
                _vm.StatusMessage = "فشل تشغيل Xray لهذا السيرفر. راجع سجل Xray.";
                ShowNotification("فشل الاتصال", $"تعذر تشغيل Xray للسيرفر {server.DisplayName}.", false);
                Console.WriteLine("[Connect] FAILED to start Xray: " + _xrayProc.LastOutput);
                return;
            }

            var tunStarted = false;
            var fellBackToProxy = false;
            if (_vm.Settings.TunMode && xrayEndpointIp != null)
            {
                var tunConfig = SingBoxConfigBuilder.BuildTunToSocks(_vm.Settings, socksPort, xrayEndpointIp.ToString());
                Console.WriteLine("[Connect] Starting TUN bridge to the Xray SOCKS inbound...");
                tunStarted = await _proc.StartAsync(tunConfig, _shutdownCts.Token);
                if (!tunStarted)
                {
                    Console.WriteLine("[Connect] TUN bridge failed; cleaning the adapter and retrying once.");
                    await _proc.StopAsync();
                    await Task.Delay(1200, _shutdownCts.Token);
                    tunStarted = await _proc.StartAsync(tunConfig, _shutdownCts.Token);
                    Console.WriteLine("[Connect] TUN bridge retry result=" + tunStarted);
                }
                fellBackToProxy = !tunStarted;
            }
            else if (_vm.Settings.TunMode)
            {
                Console.WriteLine("[Connect][WARN] TUN bridge skipped because the XHTTP endpoint could not be resolved safely; using the local proxy path.");
            }

            if (!tunStarted && _vm.Settings.SetSystemProxy)
            {
                SystemProxyService.SetProxy("127.0.0.1", httpPort);
                _usingSystemProxy = true;
                Console.WriteLine($"[Connect] Xray system proxy set to 127.0.0.1:{httpPort}");
            }

            int? healthProxyPort = tunStarted ? null : httpPort;
            if (!await VerifyInternetPathAsync(server, healthProxyPort, _shutdownCts.Token))
            {
                await HandleUnhealthyTunnelAsync(server, "بدأ Xray/TUN لكن فشل اختبار مرور الإنترنت عبر النفق.");
                return;
            }

            await MeasureSelectedDownloadAsync(server, healthProxyPort, _shutdownCts.Token);

            _vm.IsConnected = true;
            _vm.Settings.SelectedServerId = server.Id;
            SettingsService.Save(_vm.Settings);

            var mode = tunStarted
                ? "تم تشغيل XHTTP عبر Xray وTUN."
                : _vm.Settings.SetSystemProxy
                    ? "تم تشغيل XHTTP عبر Xray؛ تعذر TUN وتم تفعيل Proxy النظام."
                    : $"تم تشغيل XHTTP عبر Xray على 127.0.0.1:{socksPort} (SOCKS) و127.0.0.1:{httpPort} (HTTP).";
            if (!string.IsNullOrWhiteSpace(_lastExitLocation))
                mode += $" موقع الخروج حسب IP (تقريبي): {_lastExitLocation}. اسم القائمة: {server.DisplayName}.";
            _vm.StatusMessage = useAutoSelection ? $"{mode} التوصية: {server.DisplayName}." : mode;
            await ServerScanCacheService.SaveAsync(_vm.Servers, fullScan: false, _shutdownCts.Token);
            Console.WriteLine("[Connect] XHTTP SUCCESS; TUN=" + tunStarted + ", proxyFallback=" + fellBackToProxy);
            ShowNotification("تم الاتصال", $"اتصالك يعمل عبر {server.DisplayName}.", true);
        }
        catch
        {
            await _proc.StopAsync();
            await _xrayProc.StopAsync();
            if (_usingSystemProxy)
            {
                SystemProxyService.ClearProxy();
                _usingSystemProxy = false;
            }
            throw;
        }
    }

    private static bool CanBindLoopbackPort(int port)
    {
        try
        {
            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return false;
        }
    }

    private static (int SocksPort, int HttpPort) GetFreeProxyPorts()
    {
        using var socksListener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        using var httpListener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        socksListener.Start();
        httpListener.Start();
        return (((System.Net.IPEndPoint)socksListener.LocalEndpoint).Port,
            ((System.Net.IPEndPoint)httpListener.LocalEndpoint).Port);
    }

    private async Task DisconnectAsync()
    {
        _vm.IsBusy = true;
        try
        {
            await _proc.StopAsync();
            await _xrayProc.StopAsync();
            if (_usingSystemProxy)
            {
                SystemProxyService.ClearProxy();
                _usingSystemProxy = false;
            }
            _vm.IsConnected = false;
            _automaticConnection = false;
            _vm.StatusMessage = "تم الفصل.";
            ShowNotification("تم قطع الاتصال", "تم إيقاف النفق وإعادة إعدادات الشبكة.", true);
        }
        finally { _vm.IsBusy = false; }
    }

    private void Advanced_Click(object sender, RoutedEventArgs e)
    {
        ShowAdvancedPage(showServers: false);
    }

    private void Servers_Click(object sender, RoutedEventArgs e) => ShowAdvancedPage(showServers: true);

    private void ShowAdvancedPage(bool showServers)
    {
        if (_advanced == null || !_advanced.IsLoaded)
        {
            _advanced = new AdvancedWindow(_vm, _proc, this);
            _advanced.Closed += (_, _) => _advanced = null;
        }
        if (showServers) _advanced.ShowServersPage();
        else _advanced.ShowSettingsPage();
        _advanced.Show();
        _advanced.Activate();
    }

    private void UpdateGlow()
    {
        if (GlowEffect == null) return;
        GlowEffect.Color = _vm.IsConnected ? MediaColor.FromRgb(0x22, 0xC5, 0x5E) :
            _vm.IsConnecting ? MediaColor.FromRgb(0xFF, 0xB5, 0x47) : MediaColor.FromRgb(0xFF, 0x6B, 0x35);
        if (_vm.IsConnecting)
        {
            GlowEffect.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty,
                new DoubleAnimation(0.25, 0.85, new Duration(TimeSpan.FromSeconds(0.8))) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            PowerScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(1, 1.06, new Duration(TimeSpan.FromSeconds(0.8))) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            PowerScale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(1, 1.06, new Duration(TimeSpan.FromSeconds(0.8))) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        }
        else
        {
            GlowEffect.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, null);
            PowerScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            PowerScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            PowerScale.ScaleX = PowerScale.ScaleY = 1;
            GlowEffect.Opacity = 0.55;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private void EnableDarkTitleBar()
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int dark = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        }
        catch { }
    }

    private bool IsAdmin()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            var pr = new WindowsPrincipal(id);
            return pr.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    public async Task RefreshServersPublic()
    {
        await StopBackgroundRefreshAsync();
        // An explicit user-requested refresh remains the opt-in way to recheck
        // every server; automatic startup/timer updates are incremental only.
        await RefreshServersAsync(keepSelection: true, fullScan: true);
        UpdateDetails();
        _advanced?.AppendLog("تم تحديث القائمة.");
    }
    public void RefreshFromAdvanced() => UpdateDetails();
}
