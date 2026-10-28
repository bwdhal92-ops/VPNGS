using System.Text.Json;
using System.Text.Json.Nodes;
using FlareVpn.Models;

namespace FlareVpn.Services;

public static class SingBoxConfigBuilder
{
    public static string BuildTunToSocks(AppSettings settings, int socksPort, string? endpointIp = null)
    {
        var routeRules = new JsonArray(
            new JsonObject { ["action"] = "sniff" },
            new JsonObject { ["protocol"] = "dns", ["action"] = "hijack-dns" });
        if (System.Net.IPAddress.TryParse(endpointIp, out var endpointAddress))
        {
            var cidr = endpointAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? "/32" : "/128";
            routeRules.Add(new JsonObject
            {
                ["ip_cidr"] = new JsonArray(endpointAddress.ToString() + cidr),
                ["outbound"] = "direct"
            });
        }
        routeRules.Add(new JsonObject { ["ip_is_private"] = true, ["outbound"] = "direct" });

        var root = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "warn", ["timestamp"] = true },
            ["dns"] = new JsonObject
            {
                ["servers"] = new JsonArray(
                    new JsonObject
                    {
                        ["tag"] = "dns-remote", ["type"] = "https", ["server"] = "8.8.8.8",
                        ["server_port"] = 443, ["path"] = "/dns-query",
                        ["tls"] = new JsonObject { ["enabled"] = true, ["server_name"] = "dns.google" },
                        ["detour"] = "proxy"
                    },
                    new JsonObject
                    {
                        ["tag"] = "dns-bootstrap", ["type"] = "https", ["server"] = "1.1.1.1",
                        ["server_port"] = 443, ["path"] = "/dns-query",
                        ["tls"] = new JsonObject { ["enabled"] = true, ["server_name"] = "cloudflare-dns.com" }
                    }),
                ["final"] = "dns-remote", ["strategy"] = "ipv4_only"
            },
            ["inbounds"] = new JsonArray(new JsonObject
            {
                ["type"] = "tun", ["tag"] = "tun-in", ["interface_name"] = "VPNGS",
                ["address"] = new JsonArray("172.19.0.1/30"), ["auto_route"] = true,
                ["strict_route"] = true, ["stack"] = "mixed"
            }),
            ["outbounds"] = new JsonArray(
                new JsonObject
                {
                    ["type"] = "socks", ["tag"] = "proxy", ["server"] = "127.0.0.1",
                    ["server_port"] = socksPort, ["version"] = "5"
                },
                new JsonObject { ["type"] = "direct", ["tag"] = "direct" },
                new JsonObject { ["type"] = "block", ["tag"] = "block" }),
            ["route"] = new JsonObject
            {
                ["rules"] = routeRules,
                ["final"] = "proxy", ["auto_detect_interface"] = true,
                ["default_domain_resolver"] = "dns-bootstrap"
            }
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public static string Build(VpnServer srv, AppSettings settings)
    {
        var root = new JsonObject();
        root["log"] = new JsonObject { ["level"] = "warn", ["timestamp"] = true };

        // DNS
        var dns = new JsonObject();
        var dnsServers = new JsonArray();
        dnsServers.Add(new JsonObject
        {
            ["tag"] = "dns-remote",
            ["type"] = "https",
            ["server"] = "8.8.8.8",
            ["server_port"] = 443,
            ["path"] = "/dns-query",
            ["tls"] = new JsonObject { ["enabled"] = true, ["server_name"] = "dns.google" },
            ["detour"] = "proxy"
        });
        // Resolve proxy server hostnames over direct DoH before the proxy exists.
        // DNS servers use the direct outbound by default; an explicit detour to
        // the empty direct outbound is rejected by sing-box.
        dnsServers.Add(new JsonObject
        {
            ["tag"] = "dns-bootstrap",
            ["type"] = "https",
            ["server"] = "1.1.1.1",
            ["server_port"] = 443,
            ["path"] = "/dns-query",
            ["tls"] = new JsonObject { ["enabled"] = true, ["server_name"] = "cloudflare-dns.com" }
        });
        dns["servers"] = dnsServers;
        dns["final"] = "dns-remote";
        dns["strategy"] = "ipv4_only";
        root["dns"] = dns;

        // Inbounds
        var inbounds = new JsonArray();
        if (settings.TunMode)
        {
            inbounds.Add(new JsonObject
            {
                ["type"] = "tun",
                ["tag"] = "tun-in",
                ["interface_name"] = "VPNGS",
                ["address"] = new JsonArray { "172.19.0.1/30" },
                ["auto_route"] = true,
                ["strict_route"] = true,
                ["stack"] = "mixed"
            });
        }
        else
        {
            inbounds.Add(new JsonObject
            {
                ["type"] = "socks",
                ["tag"] = "socks-in",
                ["listen"] = "127.0.0.1",
                ["listen_port"] = settings.SocksPort
            });
            inbounds.Add(new JsonObject
            {
                ["type"] = "http",
                ["tag"] = "http-in",
                ["listen"] = "127.0.0.1",
                ["listen_port"] = settings.HttpPort
            });
        }
        root["inbounds"] = inbounds;

        // Outbounds
        var outbounds = new JsonArray();
        outbounds.Add(BuildOutbound(srv));
        outbounds.Add(new JsonObject { ["type"] = "direct", ["tag"] = "direct" });
        outbounds.Add(new JsonObject { ["type"] = "block", ["tag"] = "block" });
        root["outbounds"] = outbounds;

        // Route
        var route = new JsonObject();
        var rules = new JsonArray();
        rules.Add(new JsonObject { ["action"] = "sniff" });
        rules.Add(new JsonObject { ["protocol"] = "dns", ["action"] = "hijack-dns" });
        rules.Add(new JsonObject { ["ip_is_private"] = true, ["outbound"] = "direct" });
        route["rules"] = rules;
        route["final"] = "proxy";
        route["auto_detect_interface"] = true;
        route["default_domain_resolver"] = "dns-bootstrap";
        root["route"] = route;

        var opts = new JsonSerializerOptions { WriteIndented = true };
        return root.ToJsonString(opts);
    }

    private static JsonObject BuildOutbound(VpnServer srv)
    {
        var p = srv.Params;
        JsonObject o = new() { ["tag"] = "proxy" };
        switch (srv.Protocol)
        {
            case VpnProtocol.Vless:
                o["type"] = "vless";
                o["server"] = srv.Host;
                o["server_port"] = srv.Port;
                o["uuid"] = p.GetValueOrDefault("uuid", "");
                if (p.TryGetValue("flow", out var flow) && !string.IsNullOrEmpty(flow))
                    o["flow"] = flow;
                o["packet_encoding"] = "xudp";
                break;
            case VpnProtocol.Vmess:
                o["type"] = "vmess";
                o["server"] = srv.Host;
                o["server_port"] = srv.Port;
                o["uuid"] = p.GetValueOrDefault("uuid", "");
                var aid = p.GetValueOrDefault("aid", "0");
                if (int.TryParse(aid, out var a)) o["alter_id"] = a; else o["alter_id"] = 0;
                o["security"] = string.IsNullOrEmpty(p.GetValueOrDefault("security", "")) ? "auto" : p["security"];
                break;
            case VpnProtocol.Trojan:
                o["type"] = "trojan";
                o["server"] = srv.Host;
                o["server_port"] = srv.Port;
                o["password"] = p.GetValueOrDefault("password", "");
                break;
            case VpnProtocol.Hysteria2:
                o["type"] = "hysteria2";
                o["server"] = srv.Host;
                o["server_port"] = srv.Port;
                var pw = p.GetValueOrDefault("userinfo", "");
                if (pw.Contains(':')) pw = pw.Split(':', 2)[1];
                if (string.IsNullOrEmpty(pw)) pw = p.GetValueOrDefault("password", "");
                o["password"] = pw;
                // obfs
                if (p.TryGetValue("obfs", out var obfs) && !string.IsNullOrEmpty(obfs))
                {
                    var ob = new JsonObject { ["type"] = obfs };
                    if (p.TryGetValue("obfs-password", out var obp)) ob["password"] = obp;
                    o["obfs"] = ob;
                }
                break;
            case VpnProtocol.Tuic:
                o["type"] = "tuic";
                o["server"] = srv.Host;
                o["server_port"] = srv.Port;
                var ui = p.GetValueOrDefault("userinfo", "");
                string uuid = ui, pass = "";
                if (ui.Contains(':')) { var parts = ui.Split(':', 2); uuid = parts[0]; pass = parts[1]; }
                if (p.ContainsKey("uuid")) uuid = p["uuid"];
                if (p.ContainsKey("password")) pass = p["password"];
                o["uuid"] = uuid;
                o["password"] = pass;
                o["congestion_control"] = "bbr";
                break;
        }

        // TLS
        var security = p.GetValueOrDefault("security", "").ToLowerInvariant();
        // For hysteria2/tuic/trojan default tls
        bool needsTls = srv.Protocol is VpnProtocol.Hysteria2 or VpnProtocol.Tuic or VpnProtocol.Trojan;
        if (security is "tls" or "reality") needsTls = true;
        if (needsTls)
        {
            var tls = new JsonObject { ["enabled"] = true };
            var sni = p.GetValueOrDefault("sni", "");
            if (string.IsNullOrEmpty(sni)) sni = p.GetValueOrDefault("host", "");
            if (string.IsNullOrEmpty(sni)) sni = srv.Host;
            tls["server_name"] = sni;

            if (security == "reality")
            {
                var requestedFp = p.GetValueOrDefault("fp", "chrome").Trim();
                if (requestedFp.Equals("unsafe", StringComparison.OrdinalIgnoreCase))
                    throw new NotSupportedException("The 'unsafe' fingerprint disables uTLS, but sing-box requires uTLS for REALITY. Use a supported fingerprint such as chrome.");
                var fp = string.IsNullOrWhiteSpace(requestedFp) ? "chrome" : requestedFp.ToLowerInvariant();
                tls["utls"] = new JsonObject { ["enabled"] = true, ["fingerprint"] = fp };
                var reality = new JsonObject { ["enabled"] = true };
                if (p.TryGetValue("pbk", out var pbk)) reality["public_key"] = pbk;
                if (p.TryGetValue("sid", out var sid)) reality["short_id"] = sid;
                tls["reality"] = reality;
            }
            else
            {
                // insecure
                var insecure = p.GetValueOrDefault("insecure", "");
                if (string.IsNullOrEmpty(insecure)) insecure = p.GetValueOrDefault("allowInsecure", "");
                if (insecure == "1" || insecure.Equals("true", StringComparison.OrdinalIgnoreCase))
                    tls["insecure"] = true;
                // alpn
                if (p.TryGetValue("alpn", out var alpn) && !string.IsNullOrEmpty(alpn))
                    tls["alpn"] = new JsonArray(alpn.Split(',').Select(x => JsonValue.Create(x.Trim())).ToArray());
                var rawFingerprint = p.GetValueOrDefault("fp", "");
                if (!string.IsNullOrWhiteSpace(rawFingerprint))
                {
                    // Xray's "unsafe" means using the native TLS stack without uTLS.
                    // Do not pass it as a uTLS fingerprint; sing-box doesn't define it.
                    if (!rawFingerprint.Equals("unsafe", StringComparison.OrdinalIgnoreCase))
                        tls["utls"] = new JsonObject { ["enabled"] = true, ["fingerprint"] = rawFingerprint.Trim().ToLowerInvariant() };
                }
            }
            o["tls"] = tls;
        }

        // Transport
        var net = p.GetValueOrDefault("type", p.GetValueOrDefault("net", "")).ToLowerInvariant();
        if (string.IsNullOrEmpty(net)) net = "tcp";
        JsonObject? transport = null;
        switch (net)
        {
            case "ws":
                transport = new JsonObject { ["type"] = "ws" };
                var path = p.GetValueOrDefault("path", "/");
                transport["path"] = string.IsNullOrEmpty(path) ? "/" : path;
                var hostH = p.GetValueOrDefault("host", "");
                if (string.IsNullOrEmpty(hostH)) hostH = p.GetValueOrDefault("sni", srv.Host);
                transport["headers"] = new JsonObject { ["Host"] = hostH };
                break;
            case "grpc":
                transport = new JsonObject { ["type"] = "grpc" };
                var sn = p.GetValueOrDefault("serviceName", p.GetValueOrDefault("service_name", ""));
                transport["service_name"] = sn;
                break;
            case "xhttp":
                transport = new JsonObject { ["type"] = "xhttp" };
                transport["path"] = p.GetValueOrDefault("path", "/");
                var hx = p.GetValueOrDefault("host", "");
                if (!string.IsNullOrEmpty(hx)) transport["host"] = hx;
                transport["mode"] = "auto";
                break;
            case "httpupgrade":
                transport = new JsonObject { ["type"] = "httpupgrade" };
                transport["path"] = p.GetValueOrDefault("path", "/");
                var hu = p.GetValueOrDefault("host", "");
                if (!string.IsNullOrEmpty(hu)) transport["host"] = hu;
                break;
        }
        if (transport != null) o["transport"] = transport;

        return o;
    }

}
