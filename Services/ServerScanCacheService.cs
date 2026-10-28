using System.IO;
using System.Text.Json;
using FlareVpn.Models;

namespace FlareVpn.Services;

public sealed record ServerScanCache(DateTime SavedAtUtc, DateTime? LastFullScanAtUtc, List<VpnServer> Servers, bool RequiresUdpRescan);

public static class ServerScanCacheService
{
    private static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VPNGS", "server-scan-cache.json");

    private sealed class CacheDocument
    {
        public DateTime SavedAtUtc { get; set; }
        public DateTime? LastFullScanAtUtc { get; set; }
        public int UdpMeasurementVersion { get; set; }
        public List<CacheEntry> Servers { get; set; } = new();
    }

    private sealed class CacheEntry
    {
        public string RawLink { get; set; } = "";
        public bool? UdpProbePassed { get; set; }
        public double? UdpLatencyMs { get; set; }
        public bool? WebProbePassed { get; set; }
        public double? DownloadMbps { get; set; }
        public string? ExitCountry { get; set; }
        public string? ExitCountryCode { get; set; }
    }

    public static async Task<ServerScanCache?> LoadAsync(CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(CachePath)) return null;
            await using var stream = File.OpenRead(CachePath);
            var document = await JsonSerializer.DeserializeAsync<CacheDocument>(stream, cancellationToken: ct);
            if (document?.Servers.Count is null or 0) return null;

            var servers = new List<VpnServer>(document.Servers.Count);
            var requiresUdpRescan = document.UdpMeasurementVersion < 2;
            foreach (var entry in document.Servers)
            {
                var server = SubscriptionService.ParseLink(entry.RawLink);
                if (server == null) continue;
                if (server.IsCloudflare) continue;
                if (!requiresUdpRescan)
                {
                    server.UdpProbePassed = entry.UdpProbePassed;
                    server.UdpLatencyMs = entry.UdpLatencyMs;
                }
                server.WebProbePassed = entry.WebProbePassed;
                server.DownloadMbps = entry.DownloadMbps;
                server.SetVerifiedExitLocation(entry.ExitCountry, entry.ExitCountryCode);
                servers.Add(server);
            }
            return servers.Count == 0 ? null : new ServerScanCache(document.SavedAtUtc, document.LastFullScanAtUtc, servers, requiresUdpRescan);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Cache][WARN] Could not read server scan cache: {ex.GetBaseException().Message}");
            return null;
        }
    }

    public static async Task SaveAsync(IEnumerable<VpnServer> servers, bool fullScan, CancellationToken ct = default)
    {
        try
        {
            var previous = await LoadAsync(ct);
            var document = new CacheDocument
            {
                SavedAtUtc = DateTime.UtcNow,
                UdpMeasurementVersion = 2,
                // Keep the time of the last actual full scan; incremental updates and
                // connection checks must not claim that all servers were rechecked.
                LastFullScanAtUtc = fullScan ? DateTime.UtcNow : previous?.LastFullScanAtUtc,
                Servers = servers
                    .Where(server => server.Id != "auto" && !server.IsCloudflare && !string.IsNullOrWhiteSpace(server.RawLink))
                    .Select(server => new CacheEntry
                    {
                        RawLink = server.RawLink,
                        UdpProbePassed = server.UdpProbePassed,
                        UdpLatencyMs = server.UdpLatencyMs,
                        WebProbePassed = server.WebProbePassed,
                        DownloadMbps = server.DownloadMbps,
                        ExitCountry = server.VerifiedExitCountry,
                        ExitCountryCode = server.VerifiedExitCountryCode
                    })
                    .ToList()
            };

            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            var temporaryPath = CachePath + ".tmp";
            await using (var stream = File.Create(temporaryPath))
                await JsonSerializer.SerializeAsync(stream, document, new JsonSerializerOptions { WriteIndented = false }, ct);
            File.Move(temporaryPath, CachePath, overwrite: true);
            Console.WriteLine($"[Cache] Saved UDP results for {document.Servers.Count} servers.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Cache][WARN] Could not save server scan cache: {ex.GetBaseException().Message}");
        }
    }
}
