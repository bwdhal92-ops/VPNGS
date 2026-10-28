using System.Text.Json;
using System.Text.Json.Nodes;
using FlareVpn.Models;

namespace FlareVpn.Services;

public static class XrayConfigBuilder
{
    public static bool RequiresXray(VpnServer server) =>
        server.Params.GetValueOrDefault("type", "").Equals("xhttp", StringComparison.OrdinalIgnoreCase) ||
        server.Params.GetValueOrDefault("net", "").Equals("xhttp", StringComparison.OrdinalIgnoreCase);

    public static string BuildProxy(VpnServer server, int socksPort, int httpPort, string? addressOverride = null)
    {
        if (!RequiresXray(server)) throw new ArgumentException("This Xray config builder is for XHTTP nodes.", nameof(server));

        var root = new JsonObject
        {
            ["log"] = new JsonObject { ["loglevel"] = "warning" },
            ["inbounds"] = new JsonArray(
                new JsonObject
                {
                    ["tag"] = "socks-in", ["listen"] = "127.0.0.1", ["port"] = socksPort,
                    ["protocol"] = "socks", ["settings"] = new JsonObject { ["auth"] = "noauth", ["udp"] = true }
                },
                new JsonObject
                {
                    ["tag"] = "http-in", ["listen"] = "127.0.0.1", ["port"] = httpPort,
                    ["protocol"] = "http", ["settings"] = new JsonObject()
                }),
            ["outbounds"] = new JsonArray(BuildOutbound(server, addressOverride), new JsonObject
            {
                ["tag"] = "direct", ["protocol"] = "freedom", ["settings"] = new JsonObject()
            })
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonObject BuildOutbound(VpnServer server, string? addressOverride)
    {
        var p = server.Params;
        var protocol = server.Protocol switch
        {
            VpnProtocol.Vless => "vless",
            VpnProtocol.Vmess => "vmess",
            VpnProtocol.Trojan => "trojan",
            _ => throw new NotSupportedException($"XHTTP is not supported for {server.Protocol}.")
        };

        var outbound = new JsonObject { ["tag"] = "proxy", ["protocol"] = protocol };
        if (server.Protocol is VpnProtocol.Vless or VpnProtocol.Vmess)
        {
            var user = new JsonObject();
            if (server.Protocol == VpnProtocol.Vless)
            {
                user["id"] = p.GetValueOrDefault("uuid", "");
                user["encryption"] = "none";
                user["level"] = 0;
            }
            else
            {
                user["id"] = p.GetValueOrDefault("uuid", "");
                user["alterId"] = int.TryParse(p.GetValueOrDefault("aid", "0"), out var aid) ? aid : 0;
                user["security"] = p.GetValueOrDefault("security", "auto");
                user["level"] = 0;
            }

            outbound["settings"] = new JsonObject
            {
                ["vnext"] = new JsonArray(new JsonObject
                {
                    ["address"] = addressOverride ?? server.Host,
                    ["port"] = server.Port,
                    ["users"] = new JsonArray(user)
                })
            };
        }
        else
        {
            var password = p.GetValueOrDefault("password", p.GetValueOrDefault("userinfo", ""));
            outbound["settings"] = new JsonObject
            {
                ["servers"] = new JsonArray(new JsonObject
                {
                    ["address"] = addressOverride ?? server.Host, ["port"] = server.Port, ["password"] = password
                })
            };
        }

        var security = p.GetValueOrDefault("security", "").ToLowerInvariant();
        if (string.IsNullOrEmpty(security) && p.GetValueOrDefault("tls", "").Equals("tls", StringComparison.OrdinalIgnoreCase))
            security = "tls";
        if (security is not ("tls" or "reality")) security = "none";
        var sni = p.GetValueOrDefault("sni", p.GetValueOrDefault("host", server.Host));
        var fingerprint = p.GetValueOrDefault("fp", "chrome");

        var stream = new JsonObject
        {
            ["network"] = "xhttp",
            ["security"] = security,
            ["xhttpSettings"] = new JsonObject
            {
                ["path"] = p.GetValueOrDefault("path", "/"),
                ["host"] = p.GetValueOrDefault("host", ""),
                ["mode"] = NormalizeXhttpMode(p.GetValueOrDefault("mode", "auto"))
            }
        };
        if (p.TryGetValue("extra", out var extra) && !string.IsNullOrWhiteSpace(extra))
        {
            try { ((JsonObject)stream["xhttpSettings"]!)["extra"] = JsonNode.Parse(extra); }
            catch { }
        }

        if (security == "tls")
        {
            var tls = new JsonObject { ["serverName"] = sni };
            if (p.TryGetValue("alpn", out var alpn) && !string.IsNullOrWhiteSpace(alpn))
                tls["alpn"] = new JsonArray(alpn.Split(',').Select(value => JsonValue.Create(value.Trim())).ToArray());
            if (!string.IsNullOrWhiteSpace(fingerprint) && !fingerprint.Equals("unsafe", StringComparison.OrdinalIgnoreCase))
                tls["fingerprint"] = fingerprint;
            var insecure = p.GetValueOrDefault("insecure", p.GetValueOrDefault("allowInsecure", ""));
            if (insecure is "1" || insecure.Equals("true", StringComparison.OrdinalIgnoreCase))
                tls["allowInsecure"] = true;
            stream["tlsSettings"] = tls;
        }
        else if (security == "reality")
        {
            if (fingerprint.Equals("unsafe", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("REALITY requires a uTLS fingerprint; 'unsafe' cannot be used.");
            var reality = new JsonObject
            {
                ["serverName"] = sni,
                ["fingerprint"] = string.IsNullOrWhiteSpace(fingerprint) ? "chrome" : fingerprint,
                ["password"] = p.GetValueOrDefault("pbk", ""),
                ["shortId"] = p.GetValueOrDefault("sid", ""),
                ["spiderX"] = p.GetValueOrDefault("spx", p.GetValueOrDefault("spiderX", "/"))
            };
            stream["realitySettings"] = reality;
        }

        outbound["streamSettings"] = stream;
        return outbound;
    }

    private static string NormalizeXhttpMode(string mode) => mode.ToLowerInvariant() switch
    {
        "packet-up" => "packet-up",
        "stream-up" => "stream-up",
        "stream-one" => "stream-one",
        _ => "auto"
    };
}
