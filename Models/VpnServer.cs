using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FlareVpn.Models;

public enum VpnProtocol { Vless, Vmess, Trojan, Hysteria2, Tuic, Unknown }

public class VpnServer : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public VpnProtocol Protocol { get; set; }
    public string RawLink { get; set; } = "";
    private string _remark = "";
    public string Remark
    {
        get => _remark;
        set
        {
            _remark = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(UiDisplayName));
            OnPropertyChanged(nameof(CountryFlagCode));
            OnPropertyChanged(nameof(CountryFlagUri));
        }
    }
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string Country { get; set; } = "";
    private string? _verifiedExitCountry;
    public string? VerifiedExitCountry
    {
        get => _verifiedExitCountry;
        private set
        {
            _verifiedExitCountry = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(UiDisplayName));
        }
    }
    private string? _verifiedExitCountryCode;
    public string? VerifiedExitCountryCode
    {
        get => _verifiedExitCountryCode;
        private set
        {
            _verifiedExitCountryCode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CountryFlagCode));
            OnPropertyChanged(nameof(CountryFlagUri));
        }
    }
    public string? CountryFlagCode => VerifiedExitCountryCode ?? GetCountryCode(Country, Remark, Id);
    public bool IsCloudflare => string.Equals(Country?.Trim(), "Cloudflare", StringComparison.OrdinalIgnoreCase)
        || (Remark?.Contains("Cloudflare", StringComparison.OrdinalIgnoreCase) ?? false)
        || (Host?.Contains("cloudflare", StringComparison.OrdinalIgnoreCase) ?? false);
    public string? CountryFlagUri => CountryFlagCode is { } code
        ? $"pack://application:,,,/VPNGS;component/Assets/Flags/{code}.png"
        : null;
    public string PingLabel
    {
        get
        {
            if (Id == "auto") return "الأفضل";
            var labels = new List<string> { UdpLatencyMs.HasValue ? $"{UdpLatencyMs.Value:0.#} ms STUN/UDP" : "— STUN/UDP" };
            if (UdpProbePassed.HasValue) labels.Add(UdpProbePassed.Value ? "U✓" : "U✕");
            if (WebProbePassed.HasValue) labels.Add(WebProbePassed.Value ? "W✓" : "W✕");
            if (DownloadMbps.HasValue) labels.Add($"↓{DownloadMbps.Value:0.#}");
            return string.Join(" · ", labels);
        }
    }
    private bool? _udpProbePassed;
    public bool? UdpProbePassed
    {
        get => _udpProbePassed;
        set { _udpProbePassed = value; OnPropertyChanged(); OnPropertyChanged(nameof(PingLabel)); }
    }
    private bool? _webProbePassed;
    public bool? WebProbePassed
    {
        get => _webProbePassed;
        set { _webProbePassed = value; OnPropertyChanged(); OnPropertyChanged(nameof(PingLabel)); }
    }
    private double? _downloadMbps;
    public double? DownloadMbps
    {
        get => _downloadMbps;
        set { _downloadMbps = value; OnPropertyChanged(); OnPropertyChanged(nameof(PingLabel)); }
    }
    private double? _udpLatencyMs;
    public double? UdpLatencyMs
    {
        get => _udpLatencyMs;
        set { _udpLatencyMs = value; OnPropertyChanged(); OnPropertyChanged(nameof(PingLabel)); }
    }
    public Dictionary<string, string> Params { get; set; } = new();
    public string DisplayName => string.IsNullOrWhiteSpace(Remark) ? $"{Protocol} {Host}:{Port}" : Remark;
    public string UiDisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(VerifiedExitCountry))
                return $"{DisplayName} · خروج {VerifiedExitCountry}";

            return !string.IsNullOrWhiteSpace(VerifiedExitCountry)
                ? $"{VerifiedExitCountry} • {DisplayName}"
                : DisplayName;
        }
    }
    public override string ToString() => DisplayName;

    public void SetVerifiedExitLocation(string? country, string? countryCode)
    {
        VerifiedExitCountry = string.IsNullOrWhiteSpace(country) ? null : country;
        var normalizedCode = countryCode?.Trim().ToLowerInvariant();
        VerifiedExitCountryCode = normalizedCode is { Length: 2 } && SupportedFlagCodes.Contains(normalizedCode)
            ? normalizedCode
            : null;
    }

    private static readonly HashSet<string> SupportedFlagCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "ae","ar","at","au","be","bh","br","ca","ch","cn","de","dk","dz","ee","eg","es",
        "fi","fr","gb","hk","il","in","iq","ir","it","jo","jp","kr","kw","lb","ma","mx",
        "nl","no","om","pl","qa","ro","ru","sa","se","sg","sy","tn","tr","ua","us","ye","za"
    };

    private static string? GetCountryCode(string country, string remark, string id)
    {
        if (id == "auto" || country.Contains("Cloudflare", StringComparison.OrdinalIgnoreCase) ||
            remark.Contains("Cloudflare", StringComparison.OrdinalIgnoreCase)) return null;

        var codes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Finland"] = "fi", ["Sweden"] = "se", ["Netherlands"] = "nl", ["Germany"] = "de",
            ["Ukraine"] = "ua",
            ["Estonia"] = "ee", ["France"] = "fr", ["United Kingdom"] = "gb", ["UK"] = "gb",
            ["USA"] = "us", ["United States"] = "us", ["Canada"] = "ca", ["Singapore"] = "sg",
            ["Japan"] = "jp", ["Hong Kong"] = "hk", ["Israel"] = "il", ["Poland"] = "pl",
            ["Romania"] = "ro", ["Turkey"] = "tr", ["Türkiye"] = "tr", ["Italy"] = "it",
            ["Spain"] = "es", ["Russia"] = "ru", ["Switzerland"] = "ch", ["Austria"] = "at",
            ["Belgium"] = "be", ["Norway"] = "no", ["Denmark"] = "dk", ["Australia"] = "au",
            ["India"] = "in", ["South Korea"] = "kr", ["China"] = "cn", ["Brazil"] = "br",
            ["Argentina"] = "ar", ["Mexico"] = "mx", ["South Africa"] = "za", ["UAE"] = "ae",
            ["Saudi Arabia"] = "sa", ["Iran"] = "ir", ["Iraq"] = "iq", ["Egypt"] = "eg",
            ["Morocco"] = "ma", ["Algeria"] = "dz", ["Tunisia"] = "tn", ["Jordan"] = "jo",
            ["Lebanon"] = "lb", ["Syria"] = "sy", ["Yemen"] = "ye", ["Kuwait"] = "kw",
            ["Qatar"] = "qa", ["Bahrain"] = "bh", ["Oman"] = "om"
        };

        if (!string.IsNullOrWhiteSpace(country) && codes.TryGetValue(country.Trim(), out var code)) return code;
        if (country.Length == 2 && codes.Values.Contains(country.ToLowerInvariant())) return country.ToLowerInvariant();
        foreach (var entry in codes.OrderByDescending(entry => entry.Key.Length))
        {
            var wholeCountryName = $@"(?<![\p{{L}}]){System.Text.RegularExpressions.Regex.Escape(entry.Key)}(?![\p{{L}}])";
            if (System.Text.RegularExpressions.Regex.IsMatch(remark, wholeCountryName,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return entry.Value;
        }
        return null;
    }
}
