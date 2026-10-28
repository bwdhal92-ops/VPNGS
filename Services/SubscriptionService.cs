using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.IO;
using FlareVpn.Models;

namespace FlareVpn.Services;

public static class SubscriptionService
{
    public static readonly string[] MirrorUrls = new[]
    {
        "https://vless.svinakraft.workers.dev/fastest.txt",
        "https://raw.githubusercontent.com/svinakraft-maker/FlareFeed/main/fastest.txt",
        "https://cdn.jsdelivr.net/gh/svinakraft-maker/FlareFeed@main/fastest.txt",
        "https://gh-proxy.com/https://raw.githubusercontent.com/svinakraft-maker/FlareFeed/main/fastest.txt",
        "https://vless.svinakraft.workers.dev/Top500.txt",
        "https://raw.githubusercontent.com/svinakraft-maker/FlareFeed/main/Top500.txt",
    };

    private static readonly HttpClientHandler Handler = new()
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
    };
    private static readonly HttpClient Http = new(Handler) { Timeout = TimeSpan.FromSeconds(25) };
    private static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VPNGS", "server-list-cache.txt");

    static SubscriptionService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
    }

    public static async Task<(string Text, TimeSpan Age)?> LoadCachedListAsync(CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(CachePath)) return null;
            var text = await File.ReadAllTextAsync(CachePath, ct);
            if (string.IsNullOrWhiteSpace(text) || !text.Contains("://")) return null;
            return (text, DateTime.UtcNow - File.GetLastWriteTimeUtc(CachePath));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Servers][WARN] Could not read cached server list: {ex.GetBaseException().Message}");
            return null;
        }
    }

    public static async Task<(string url, string text)> FetchAsync(CancellationToken ct = default)
    {
        var errors = new List<string>();
        var fastestTask = FetchGroupAsync(MirrorUrls.Take(4), ct);
        var fullListTask = FetchGroupAsync(MirrorUrls.Skip(4), ct);
        var groups = await Task.WhenAll(fastestTask, fullListTask);
        var fastestGroup = groups[0];
        var fullListGroup = groups[1];

        string? fetchedText = null;
        string? fetchedSource = null;
        if (fastestGroup.Text != null && fullListGroup.Text != null)
        {
            fetchedText = fastestGroup.Text + Environment.NewLine + fullListGroup.Text;
            fetchedSource = $"{fastestGroup.Url} + {fullListGroup.Url}";
        }
        else if (fastestGroup.Text != null)
        {
            fetchedText = fastestGroup.Text;
            fetchedSource = fastestGroup.Url;
        }
        else if (fullListGroup.Text != null)
        {
            fetchedText = fullListGroup.Text;
            fetchedSource = fullListGroup.Url;
        }

        if (fetchedText != null)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
                await File.WriteAllTextAsync(CachePath, fetchedText, Encoding.UTF8, ct);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Servers][WARN] Could not update local server-list cache: {ex.Message}");
            }
            return (fetchedSource!, fetchedText);
        }

        errors.AddRange(fastestGroup.Errors);
        errors.AddRange(fullListGroup.Errors);
        try
        {
            if (File.Exists(CachePath))
            {
                var cachedText = await File.ReadAllTextAsync(CachePath, ct);
                if (!string.IsNullOrWhiteSpace(cachedText) && cachedText.Contains("://"))
                {
                    var cacheAge = DateTime.UtcNow - File.GetLastWriteTimeUtc(CachePath);
                    Console.WriteLine($"[Servers][WARN] All live mirrors failed; using cached server list ({cacheAge.TotalHours:0.#} hours old).");
                    return ($"local cache ({cacheAge.TotalHours:0.#}h old)", cachedText);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Servers][WARN] Could not read local server-list cache: {ex.Message}");
        }
        throw new Exception("تعذر جلب قائمة السيرفرات من جميع المرايا.\n" + string.Join("\n", errors));
    }

    private static async Task<(string? Url, string? Text, List<string> Errors)> FetchGroupAsync(
        IEnumerable<string> urls, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(22));
        var pending = urls.Select(url => FetchMirrorAsync(url, timeout.Token)).ToList();
        var errors = new List<string>();

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var completed = await Task.WhenAny(pending);
            pending.Remove(completed);
            var attempt = await completed;
            if (attempt.Text != null)
            {
                timeout.Cancel();
                return (attempt.Url, attempt.Text, errors);
            }
            if (attempt.Error != null) errors.Add(attempt.Error);
        }

        ct.ThrowIfCancellationRequested();
        return (null, null, errors);
    }

    private static async Task<(string Url, string? Text, string? Error)> FetchMirrorAsync(string url, CancellationToken ct)
    {
        try
        {
            using var response = await Http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
                return (url, null, $"{url} -> HTTP {(int)response.StatusCode}");

            var text = await response.Content.ReadAsStringAsync(ct);
            return !string.IsNullOrWhiteSpace(text) && text.Contains("://")
                ? (url, text, null)
                : (url, null, $"{url} -> response contained no server links");
        }
        catch (OperationCanceledException)
        {
            return (url, null, $"{url} -> timeout");
        }
        catch (Exception ex)
        {
            return (url, null, $"{url} -> {ex.GetType().Name}: {ex.Message}");
        }
    }

    public static List<VpnServer> Parse(string text)
    {
        var list = new List<VpnServer>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawLine in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;
            // Keep full line (URL + fragment with country name); encode spaces for Uri parsing
            var token = line.Replace(" ", "%20");
            if (!token.Contains("://")) continue;
            try
            {
                var srv = ParseLink(token);
                if (srv != null && seen.Add(srv.Id)) list.Add(srv);
            }
            catch
            {
                // skip malformed entry
            }
        }
        return list;
    }

    public static VpnServer? ParseLink(string link)
    {
        link = link.Trim();
        if (string.IsNullOrEmpty(link)) return null;
        try
        {
            if (link.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase))
                return ParseVmess(link);
            if (link.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
                return ParseGeneric(link, VpnProtocol.Vless);
            if (link.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase))
                return ParseGeneric(link, VpnProtocol.Trojan);
            if (link.StartsWith("hysteria2://", StringComparison.OrdinalIgnoreCase) || link.StartsWith("hy2://", StringComparison.OrdinalIgnoreCase))
                return ParseGeneric(link, VpnProtocol.Hysteria2);
            if (link.StartsWith("tuic://", StringComparison.OrdinalIgnoreCase))
                return ParseGeneric(link, VpnProtocol.Tuic);
        }
        catch { }
        return null;
    }

    private static VpnServer? ParseGeneric(string link, VpnProtocol proto)
    {
        // Normalize hy2
        if (link.StartsWith("hy2://", StringComparison.OrdinalIgnoreCase))
            link = "hysteria2://" + link.Substring(6);

        Uri uri;
        try { uri = new Uri(link); }
        catch { return null; }

        var srv = new VpnServer
        {
            Protocol = proto,
            RawLink = link,
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : DefaultPort(proto),
            Remark = FormatRemark(Uri.UnescapeDataString(uri.Fragment.TrimStart('#')).Trim(), uri.Host),
        };

        // userinfo password for trojan
        if (proto == VpnProtocol.Trojan)
        {
            srv.Params["password"] = Uri.UnescapeDataString(uri.UserInfo);
        }
        else if (proto == VpnProtocol.Hysteria2 || proto == VpnProtocol.Tuic)
        {
            // userinfo may be user:pass or just password
            srv.Params["userinfo"] = Uri.UnescapeDataString(uri.UserInfo);
        }
        else
        {
            srv.Params["uuid"] = Uri.UnescapeDataString(uri.UserInfo);
        }

        var q = ParseQuery(uri.Query);
        foreach (var kv in q) srv.Params[kv.Key] = kv.Value;
        srv.Id = StableId(srv);

        if (string.IsNullOrWhiteSpace(srv.Remark))
            srv.Remark = $"{proto} {srv.Host}";

        // country guess from remark
        srv.Country = GuessCountry(srv.Remark);
        return srv;
    }

    private static VpnServer? ParseVmess(string link)
    {
        var b64 = link.Substring("vmess://".Length).Trim();
        // pad
        while (b64.Length % 4 != 0) b64 += "=";
        string json;
        try { json = Encoding.UTF8.GetString(Convert.FromBase64String(b64)); }
        catch { return null; }

        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        string Get(string k) => r.TryGetProperty(k, out var v) ? v.ToString() : "";

        var srv = new VpnServer
        {
            Protocol = VpnProtocol.Vmess,
            RawLink = link,
            Host = Get("add"),
            Port = int.TryParse(Get("port"), out var p) ? p : 443,
            Remark = FormatRemark(Get("ps"), Get("add")),
        };
        srv.Params["uuid"] = Get("id");
        srv.Params["aid"] = Get("aid");
        srv.Params["security"] = Get("scy");
        srv.Params["net"] = Get("net");
        srv.Params["type"] = Get("type");
        srv.Params["host"] = Get("host");
        srv.Params["path"] = Get("path");
        srv.Params["tls"] = Get("tls");
        srv.Params["sni"] = Get("sni");
        srv.Params["fp"] = Get("fp");
        srv.Params["alpn"] = Get("alpn");
        srv.Id = StableId(srv);
        if (string.IsNullOrWhiteSpace(srv.Remark)) srv.Remark = $"VMess {srv.Host}";
        srv.Country = GuessCountry(srv.Remark);
        return srv;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(query)) return dict;
        query = query.TrimStart('?');
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = part.IndexOf('=');
            if (idx < 0)
            {
                dict[Uri.UnescapeDataString(part)] = "";
            }
            else
            {
                var k = Uri.UnescapeDataString(part.Substring(0, idx));
                var v = Uri.UnescapeDataString(part.Substring(idx + 1));
                dict[k] = v;
            }
        }
        return dict;
    }

    private static int DefaultPort(VpnProtocol p) => p switch
    {
        VpnProtocol.Vless => 443,
        VpnProtocol.Vmess => 443,
        VpnProtocol.Trojan => 443,
        VpnProtocol.Hysteria2 => 443,
        VpnProtocol.Tuic => 443,
        _ => 443,
    };

    private static string StableId(VpnServer server)
    {
        // Identity is based on connection settings, not the display label/fragment.
        // This preserves scan history when a feed renames or renumbers a node.
        var canonical = string.Join("\n", new[]
        {
            server.Protocol.ToString(),
            server.Host.Trim().TrimEnd('.').ToLowerInvariant(),
            server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            string.Join("\n", server.Params
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key.ToLowerInvariant()}={pair.Value}"))
        });
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes.AsSpan(0, 12)).ToLowerInvariant();
    }

    public static string LegacyStableId(string link)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(link.Trim()));
        return Convert.ToHexString(bytes.AsSpan(0, 12)).ToLowerInvariant();
    }

    private static readonly string[] Countries = new[]
    {
        "Finland","Sweden","Netherlands","Germany","Estonia","France","United Kingdom","UK","USA","Canada",
        "Singapore","Japan","Hong Kong","Cloudflare","Israel","Poland","Romania","Turkey","Italy","Spain","Ukraine"
    };

    private static string FormatRemark(string raw, string host = "")
    {
        try
        {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        raw = System.Net.WebUtility.UrlDecode(raw).Trim();
        // FlareFeed format: "0001 | Country | [ping...] | ..."
        if (raw.Contains("|"))
        {
            var parts = raw.Split('|', StringSplitOptions.TrimEntries);
            var id = parts.Length > 0 ? parts[0] : "";
            var country = parts.Length > 1 ? parts[1] : "";
            // remove bracketed speed/ping segments from country part
            if (country.Contains('[')) country = country.Split('[')[0].Trim();
            country = CleanServerLabel(country);
            if (string.IsNullOrWhiteSpace(country))
            {
                var hc = CountryFromHost(host);
                if (hc != null && !string.IsNullOrWhiteSpace(id)) return $"{hc.Value.name} • {id}";
                if (hc != null) return hc.Value.name;
                return string.IsNullOrWhiteSpace(id) ? raw : $"سيرفر {id}";
            }
            return string.IsNullOrWhiteSpace(id) ? country : $"{country} • {id}";
        }
        // If just a number, try to get country from host
        if (int.TryParse(raw, out _))
        {
            var hc = CountryFromHost(host);
            if (hc != null) return $"{hc.Value.name} • {raw}";
            return $"سيرفر {raw}";
        }
        // If raw has no country info, try host
        if (!HasCountry(raw))
        {
            var hc = CountryFromHost(host);
            if (hc != null) return hc.Value.name;
        }
        return CleanServerLabel(raw);
        }
        catch
        {
            return "سيرفر عام";
        }
    }

    private static string CleanServerLabel(string value)
    {
        value = System.Text.RegularExpressions.Regex.Replace(value, @"^(?:\uD83C[\uDDE6-\uDDFF]){1,2}\s*", "");
        value = System.Text.RegularExpressions.Regex.Replace(value, @"^(?:☁\uFE0F?|⚡|🌐|🌍|🌎|🌏)\s*", "");
        value = System.Text.RegularExpressions.Regex.Replace(value, @"\s*\(([^)]*)\)", " • $1");
        value = value.Replace("(", " ").Replace(")", "").Replace("\u200E", "").Replace("\u200F", "").Trim();

        var codePrefix = System.Text.RegularExpressions.Regex.Match(value, @"^([A-Za-z]{2})\s+(.+)$");
        if (codePrefix.Success && CountryMap.ContainsKey(codePrefix.Groups[1].Value))
        {
            var countryName = CountryMap[codePrefix.Groups[1].Value].name;
            if (codePrefix.Groups[2].Value.StartsWith(countryName, StringComparison.OrdinalIgnoreCase))
                value = codePrefix.Groups[2].Value.Trim();
        }
        return value;
    }

    private static readonly Dictionary<string, (string emoji, string name)> CountryMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["FI"] = ("🇫🇮", "Finland"),
        ["SE"] = ("🇸🇪", "Sweden"),
        ["NL"] = ("🇳🇱", "Netherlands"),
        ["DE"] = ("🇩🇪", "Germany"),
        ["EE"] = ("🇪🇪", "Estonia"),
        ["UA"] = ("🇺🇦", "Ukraine"),
        ["FR"] = ("🇫🇷", "France"),
        ["GB"] = ("🇬🇧", "United Kingdom"),
        ["UK"] = ("🇬🇧", "United Kingdom"),
        ["US"] = ("🇺🇸", "USA"),
        ["CA"] = ("🇨🇦", "Canada"),
        ["SG"] = ("🇸🇬", "Singapore"),
        ["JP"] = ("🇯🇵", "Japan"),
        ["HK"] = ("🇭🇰", "Hong Kong"),
        ["IL"] = ("🇮🇱", "Israel"),
        ["PL"] = ("🇵🇱", "Poland"),
        ["RO"] = ("🇷🇴", "Romania"),
        ["TR"] = ("🇹🇷", "Turkey"),
        ["IT"] = ("🇮🇹", "Italy"),
        ["ES"] = ("🇪🇸", "Spain"),
        ["RU"] = ("🇷🇺", "Russia"),
        ["CH"] = ("🇨🇭", "Switzerland"),
        ["AT"] = ("🇦🇹", "Austria"),
        ["BE"] = ("🇧🇪", "Belgium"),
        ["NO"] = ("🇳🇴", "Norway"),
        ["DK"] = ("🇩🇰", "Denmark"),
        ["AU"] = ("🇦🇺", "Australia"),
        ["IN"] = ("🇮🇳", "India"),
        ["KR"] = ("🇰🇷", "South Korea"),
        ["CN"] = ("🇨🇳", "China"),
        ["BR"] = ("🇧🇷", "Brazil"),
        ["AR"] = ("🇦🇷", "Argentina"),
        ["MX"] = ("🇲🇽", "Mexico"),
        ["ZA"] = ("🇿🇦", "South Africa"),
        ["AE"] = ("🇦🇪", "UAE"),
        ["SA"] = ("🇸🇦", "Saudi Arabia"),
        ["IR"] = ("🇮🇷", "Iran"),
        ["IQ"] = ("🇮🇶", "Iraq"),
        ["EG"] = ("🇪🇬", "Egypt"),
        ["MA"] = ("🇲🇦", "Morocco"),
        ["DZ"] = ("🇩🇿", "Algeria"),
        ["TN"] = ("🇹🇳", "Tunisia"),
        ["JO"] = ("🇯🇴", "Jordan"),
        ["LB"] = ("🇱🇧", "Lebanon"),
        ["SY"] = ("🇸🇾", "Syria"),
        ["YE"] = ("🇾🇪", "Yemen"),
        ["KW"] = ("🇰🇼", "Kuwait"),
        ["QA"] = ("🇶🇦", "Qatar"),
        ["BH"] = ("🇧🇭", "Bahrain"),
        ["OM"] = ("🇴🇲", "Oman"),
    };

    private static bool HasCountry(string s)
    {
        // check for flag emoji or known country name
        foreach (var kv in CountryMap)
            if (s.Contains(kv.Value.name, StringComparison.OrdinalIgnoreCase)) return true;
        // flag emoji range
        foreach (var ch in s) if (ch >= 0x1F1E6 && ch <= 0x1F1FF) return true;
        return false;
    }

    private static (string emoji, string name)? CountryFromHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;
        host = host.ToLowerInvariant();
        // TLD
        var tld = "";
        var lastDot = host.LastIndexOf('.');
        if (lastDot >= 0 && lastDot < host.Length - 1) tld = host.Substring(lastDot + 1);
        if (tld.Length == 2 && CountryMap.TryGetValue(tld.ToUpperInvariant(), out var c)) return c;
        // hostname contains country code before digit, e.g. fi3, nl1, de2
        // search for 2-letter code followed by digit
        for (int i = 0; i + 2 < host.Length; i++)
        {
            if (char.IsLetter(host[i]) && char.IsLetter(host[i + 1]) && char.IsDigit(host[i + 2]))
            {
                var code = host.Substring(i, 2).ToUpperInvariant();
                if (CountryMap.TryGetValue(code, out var c2)) return c2;
            }
        }
        return null;
    }

    private static string GuessCountry(string remark)
    {
        if (string.IsNullOrWhiteSpace(remark)) return "";

        // Match whole country tokens. Substring matching made "Ukraine" match
        // the alias "UK", which assigned the British flag to Ukrainian nodes.
        foreach (var country in Countries.OrderByDescending(c => c.Length))
        {
            var pattern = $@"(?<![\p{{L}}]){System.Text.RegularExpressions.Regex.Escape(country)}(?![\p{{L}}])";
            if (System.Text.RegularExpressions.Regex.IsMatch(remark, pattern,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return country;
        }
        return "";
    }
}
