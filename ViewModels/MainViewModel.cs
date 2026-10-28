using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using FlareVpn.Models;
using FlareVpn.Services;

namespace FlareVpn.ViewModels;

public class MainViewModel : INotifyPropertyChanged
{
    public BulkObservableCollection<VpnServer> Servers { get; } = new();
    public BulkObservableCollection<VpnServer> FilteredServers { get; } = new();

    private VpnServer? _selectedServer;
    public VpnServer? SelectedServer
    {
        get => _selectedServer;
        set { _selectedServer = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusText)); }
    }

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        set { _isConnected = value; OnPropertyChanged(); OnPropertyChanged(nameof(ConnectButtonText)); OnPropertyChanged(nameof(StatusText)); }
    }

    private string? _verifiedExitLocation;
    public string? VerifiedExitLocation
    {
        get => _verifiedExitLocation;
        set { _verifiedExitLocation = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusText)); }
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set { _isBusy = value; OnPropertyChanged(); }
    }

    private bool _isLoadingServers = true;
    public bool IsLoadingServers
    {
        get => _isLoadingServers;
        set { _isLoadingServers = value; OnPropertyChanged(); }
    }

    private bool _isConnecting;
    public bool IsConnecting
    {
        get => _isConnecting;
        set { _isConnecting = value; OnPropertyChanged(); OnPropertyChanged(nameof(ConnectButtonText)); OnPropertyChanged(nameof(StatusText)); }
    }

    private string _loadingMessage = "جاري جلب السيرفرات وقياس UDP عبر الأنفاق...";
    public string LoadingMessage
    {
        get => _loadingMessage;
        set { _loadingMessage = value; OnPropertyChanged(); }
    }

    private string _statusMessage = "جاهز";
    public string StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; OnPropertyChanged(); }
    }

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set { _searchText = value; OnPropertyChanged(); ApplyFilter(); }
    }

    private string _platformFilter = "all";
    public string PlatformFilter
    {
        get => _platformFilter;
        set
        {
            if (_platformFilter == value) return;
            _platformFilter = value;
            OnPropertyChanged();
            ApplyFilter();
            var auto = Servers.FirstOrDefault(server => server.Id == "auto");
            if (auto != null) auto.Remark = RecommendationLabel;
        }
    }

    public string ConnectButtonText => IsConnecting ? "جاري الاتصال..." : IsConnected ? "إيقاف الاتصال" : "اتصال";
    public string StatusText => IsConnecting ? "جاري الاتصال..." : IsConnected
        ? !string.IsNullOrWhiteSpace(VerifiedExitLocation)
            ? $"متصل • الخروج: {VerifiedExitLocation}"
            : $"متصل • موقع الخروج غير متاح • الخادم: {SelectedServer?.DisplayName ?? "غير محدد"}"
        : "غير متصل";

    public AppSettings Settings { get; set; } = SettingsService.Load();

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void ApplyFilter()
    {
        var q = SearchText.Trim().ToLowerInvariant();
        var filtered = new List<VpnServer>();
        foreach (var s in Servers)
        {
            if (s.Id != "auto" && !MatchesPlatformFilter(s)) continue;
            // Failed UDP probes are not selectable in the normal server list.
            // Unknown (not yet tested) results remain visible as unclassified.
            if (s.Id != "auto" && s.UdpProbePassed == false) continue;
            if (s.Id != "auto" && s.IsCloudflare)
                continue;
            if (string.IsNullOrEmpty(q) ||
                (s.Remark?.ToLowerInvariant().Contains(q) ?? false) ||
                (s.Country?.ToLowerInvariant().Contains(q) ?? false) ||
                s.Host.ToLowerInvariant().Contains(q))
                filtered.Add(s);
        }
        FilteredServers.ReplaceAll(filtered);
        if (SelectedServer is { Id: not "auto", UdpProbePassed: false })
            SelectedServer = Servers.FirstOrDefault(server => server.Id == "auto");
    }

    private bool MatchesPlatformFilter(VpnServer server) => PlatformFilter switch
    {
        "games" => server.UdpProbePassed == true,
        "browse" => server.WebProbePassed == true,
        "stream" => server.DownloadMbps.HasValue,
        _ => true
    };

    public void SortByPerformance()
    {
        var auto = Servers.FirstOrDefault(s => s.Id == "auto");
        var ordered = Servers
            .Where(s => s.Id != "auto")
            .OrderBy(s => PlatformFilter switch
            {
                "games" => s.UdpProbePassed == true ? 0 : 1,
                "browse" => s.WebProbePassed == true ? 0 : 1,
                "stream" => s.DownloadMbps.HasValue ? 0 : 1,
                _ => s.UdpProbePassed == true ? 0 : s.UdpProbePassed == false ? 2 : 1
            })
            .ThenBy(s => PlatformFilter == "stream" ? -(s.DownloadMbps ?? 0) : s.UdpLatencyMs ?? double.MaxValue)
            .ThenBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Id, StringComparer.Ordinal)
            .ToList();
        if (auto != null)
        {
            auto.Remark = RecommendationLabel;
            ordered.Insert(0, auto);
        }
        Servers.ReplaceAll(ordered);
        ApplyFilter();
    }

    public VpnServer? GetRecommendedServer()
    {
        var available = Servers.Where(s => s.Id != "auto" && s.UdpProbePassed == true && s.UdpLatencyMs.HasValue);
        var candidates = PlatformFilter switch
        {
            "games" => available.Where(s => s.UdpProbePassed == true),
            "browse" => available.Where(s => s.WebProbePassed == true),
            "stream" => available.Where(s => s.DownloadMbps.HasValue),
            _ => available
        };
        return candidates
            .OrderBy(s => PlatformFilter == "stream" ? -(s.DownloadMbps ?? 0) : s.UdpLatencyMs ?? double.MaxValue)
            .ThenBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Id, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    public string RecommendationLabel
    {
        get
        {
            var best = GetRecommendedServer();
            if (best == null) return PlatformFilter switch
            {
                "games" => "لا يوجد سيرفر اجتاز UDP بعد",
                "browse" => "لا يوجد سيرفر اجتاز اختبار الويب بعد",
                "stream" => "لا توجد نتيجة تنزيل بعد",
                _ => "لا يوجد سيرفر اجتاز فحص UDP بعد"
            };
            return PlatformFilter switch
            {
                "games" => $"أقل قياس STUN/UDP: {best.DisplayName} (مؤشر عام للألعاب)",
                "browse" => $"أفضل سيرفر متاح للتصفح: {best.DisplayName}",
                "stream" => $"أفضل تنزيل للمشاهدة: {best.DisplayName} ({best.DownloadMbps:0.#} Mbps)",
                _ => best.UdpProbePassed == true
                    ? $"أقل قياس STUN/UDP: {best.DisplayName} ({best.UdpLatencyMs:0.#} ms)"
                    : $"أقل قياس STUN/UDP: {best.DisplayName} ({best.UdpLatencyMs:0.#} ms)"
            };
        }
    }
}
