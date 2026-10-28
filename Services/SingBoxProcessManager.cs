using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using FlareVpn.Services;

namespace FlareVpn.Services;

public class SingBoxProcessManager : IDisposable
{
    private Process? _proc;
    private string _configPath = "";
    public bool IsRunning => _proc != null && !_proc.HasExited;
    public string BinPath { get; set; } = "";
    public string LastOutput { get; private set; } = "";
    public event Action<string>? Output;
    public event Action? Exited;

    public async Task<bool> StartAsync(string configJson, CancellationToken ct = default)
    {
        await StopAsync();
        LastOutput = "";
        try
        {
            // Each manager/session gets its own config so speed probes cannot overwrite
            // the active connection's credentials or config while sing-box is running.
            var dir = Path.Combine(Path.GetTempPath(), "VPNGS", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            _configPath = Path.Combine(dir, "config.json");
            await File.WriteAllTextAsync(_configPath, configJson, ct);

            if (string.IsNullOrEmpty(BinPath) || !File.Exists(BinPath))
            {
                // try find
                BinPath = FindBinary();
                if (string.IsNullOrEmpty(BinPath)) throw new FileNotFoundException("sing-box.exe not found");
            }

            if (ContainsTunInbound(configJson))
                await RemoveDisconnectedSingTunDevicesAsync(ct, "before TUN startup");

            if (ContainsTunInbound(configJson) && HasTunInterface(configJson))
            {
                Output?.Invoke("A previous VPNGS TUN adapter is still present; waiting for Windows to remove it before starting sing-box.");
                var cleanupDeadline = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < cleanupDeadline && HasTunInterface(configJson))
                    await Task.Delay(200, ct);
                if (HasTunInterface(configJson))
                {
                    Output?.Invoke("TUN startup aborted: the previous VPNGS adapter did not disappear. Disconnect/restart Windows networking, then try again.");
                    return false;
                }
            }

            var psi = new ProcessStartInfo
            {
                FileName = BinPath,
                Arguments = $"run -c \"{_configPath}\"",
                WorkingDirectory = Path.GetDirectoryName(BinPath),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _proc = process;
            process.OutputDataReceived += (_, e) => { if (e.Data != null) RecordOutput(e.Data); };
            // sing-box writes normal INFO lines to stderr too. Preserve its own
            // severity instead of labeling every stderr line as an error.
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) RecordOutput(e.Data); };
            process.Exited += (_, _) =>
            {
                if (ReferenceEquals(_proc, process)) Exited?.Invoke();
            };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (!ContainsTunInbound(configJson))
            {
                var httpPort = GetHttpListenPort(configJson);
                if (httpPort.HasValue)
                {
                    var ready = await WaitForLocalPortAsync(httpPort.Value, process, TimeSpan.FromSeconds(3), ct);
                    if (ready) return true;
                    Output?.Invoke($"Proxy startup timed out: HTTP listener on 127.0.0.1:{httpPort.Value} did not become ready.");
                    await StopAsync();
                    return false;
                }

                // Unknown non-TUN inbound: retain a short process-liveness check.
                await Task.Delay(250, ct);
                return !process.HasExited;
            }

            // A running process is not proof that TUN is usable. On Windows,
            // wait until Wintun creates the configured adapter or sing-box exits.
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (process.HasExited) return false;
                var tun = FindTunInterface(configJson);
                if (tun != null)
                {
                    // The adapter can be left behind briefly after a previous
                    // sing-box instance exits. Do not report success solely
                    // because an interface with the expected name exists; make
                    // sure this process survives startup before accepting it.
                    await Task.Delay(750, ct);
                    if (process.HasExited)
                    {
                        Output?.Invoke("TUN adapter was present, but sing-box exited during startup; treating the attempt as failed.");
                        return false;
                    }
                    Output?.Invoke("TUN interface is ready: " + tun.Name);
                    return true;
                }
                await Task.Delay(250, ct);
            }

            Output?.Invoke("TUN startup timed out: the Wintun interface did not appear.");
            await StopAsync();
            return false;
        }
        catch (Exception ex)
        {
            Output?.Invoke("Start failed: " + ex.Message);
            return false;
        }
    }

    private void RecordOutput(string line)
    {
        LastOutput = line;
        Output?.Invoke(line);
    }

    private static bool ContainsTunInbound(string configJson)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(configJson);
            return document.RootElement.GetProperty("inbounds").EnumerateArray()
                .Any(inbound => inbound.TryGetProperty("type", out var type) && type.GetString() == "tun");
        }
        catch { return false; }
    }

    private static int? GetHttpListenPort(string configJson)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(configJson);
            foreach (var inbound in document.RootElement.GetProperty("inbounds").EnumerateArray())
            {
                if (!inbound.TryGetProperty("type", out var type) || type.GetString() != "http") continue;
                if (inbound.TryGetProperty("listen_port", out var port) && port.TryGetInt32(out var value))
                    return value;
            }
        }
        catch { }
        return null;
    }

    private static async Task<bool> WaitForLocalPortAsync(int port, Process process, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (process.HasExited) return false;
            // Inspect the OS listener table instead of opening a TCP connection; a
            // half-open readiness probe is logged by sing-box as an HTTP EOF.
            if (IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == port))
                return !process.HasExited;
            await Task.Delay(50, ct);
        }
        return false;
    }

    private static NetworkInterface? FindTunInterface(string configJson)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(configJson);
            var inbound = document.RootElement.GetProperty("inbounds").EnumerateArray()
                .FirstOrDefault(item => item.TryGetProperty("type", out var type) && type.GetString() == "tun");
            if (inbound.ValueKind == System.Text.Json.JsonValueKind.Undefined) return null;
            var expectedName = inbound.TryGetProperty("interface_name", out var name) ? name.GetString() : null;
            return NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(network =>
                network.OperationalStatus == OperationalStatus.Up &&
                (!string.IsNullOrWhiteSpace(expectedName) &&
                 (network.Name.Contains(expectedName, StringComparison.OrdinalIgnoreCase) ||
                  network.Description.Contains(expectedName, StringComparison.OrdinalIgnoreCase))));
        }
        catch { return null; }
    }

    private static bool HasTunInterface(string configJson)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(configJson);
            var inbound = document.RootElement.GetProperty("inbounds").EnumerateArray()
                .FirstOrDefault(item => item.TryGetProperty("type", out var type) && type.GetString() == "tun");
            if (inbound.ValueKind == System.Text.Json.JsonValueKind.Undefined) return false;
            var expectedName = inbound.TryGetProperty("interface_name", out var name) ? name.GetString() : null;
            if (string.IsNullOrWhiteSpace(expectedName)) return false;
            // After pnputil removes a phantom Wintun device, .NET can still
            // enumerate its stale, disconnected NetworkInterface briefly.
            // Only an active adapter can conflict with a fresh TUN start.
            return NetworkInterface.GetAllNetworkInterfaces().Any(network =>
                network.OperationalStatus == OperationalStatus.Up &&
                (network.Name.Contains(expectedName, StringComparison.OrdinalIgnoreCase) ||
                 network.Description.Contains(expectedName, StringComparison.OrdinalIgnoreCase)));
        }
        catch { return false; }
    }

    public Task CleanupDisconnectedTunDevicesAsync(CancellationToken cancellationToken = default)
        => RemoveDisconnectedSingTunDevicesAsync(cancellationToken, "application shutdown");

    private async Task RemoveDisconnectedSingTunDevicesAsync(CancellationToken cancellationToken, string context)
    {
        try
        {
            var enumerate = new ProcessStartInfo("pnputil.exe", "/enum-devices /disconnected /class Net")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(enumerate);
            if (process == null) return;
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0) return;

            foreach (var block in System.Text.RegularExpressions.Regex.Split(output, @"\r?\n\s*\r?\n"))
            {
                if (!block.Contains("sing-tun Tunnel", StringComparison.OrdinalIgnoreCase) ||
                    !block.Contains("Disconnected", StringComparison.OrdinalIgnoreCase))
                    continue;

                var match = System.Text.RegularExpressions.Regex.Match(
                    block,
                    @"(?im)^\s*Instance ID:\s*(SWD\\Wintun\\\{[0-9a-f-]{36}\})\s*$");
                if (!match.Success) continue;

                var instanceId = match.Groups[1].Value;
                var remove = new ProcessStartInfo("pnputil.exe", $"/remove-device \"{instanceId}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var removal = Process.Start(remove);
                if (removal == null) continue;
                var removalOutputTask = removal.StandardOutput.ReadToEndAsync(cancellationToken);
                var removalErrorTask = removal.StandardError.ReadToEndAsync(cancellationToken);
                await removal.WaitForExitAsync(cancellationToken);
                var result = await removalOutputTask;
                var error = await removalErrorTask;
                if (removal.ExitCode == 0)
                    Output?.Invoke($"Removed stale disconnected Wintun device {instanceId} during {context}.");
                else
                    Output?.Invoke($"Could not remove stale Wintun device {instanceId}: {(string.IsNullOrWhiteSpace(error) ? result.Trim() : error.Trim())}");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Output?.Invoke("Could not inspect stale Wintun devices: " + ex.GetBaseException().Message);
        }
    }

    public async Task StopAsync()
    {
        var process = _proc;
        try
        {
            if (process != null && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                try
                {
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (TimeoutException)
                {
                    Output?.Invoke("sing-box did not exit after termination; sending a second stop request.");
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
                }
            }
        }
        catch (Exception) when (process?.HasExited == true)
        {
            // Process exit can race the kill request; its cleanup is still safe.
        }
        catch (Exception ex)
        {
            Output?.Invoke("Could not stop sing-box within the shutdown timeout: " + ex.GetBaseException().Message);
            throw;
        }
        finally
        {
            if (process == null || process.HasExited)
            {
                if (ReferenceEquals(_proc, process)) _proc = null;
                process?.Dispose();
                try
                {
                    if (!string.IsNullOrWhiteSpace(_configPath))
                    {
                        var directory = Path.GetDirectoryName(_configPath);
                        if (directory != null && Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
                        _configPath = "";
                    }
                }
                catch { }
            }
        }
    }

    public void StopOrphanedInstances()
    {
        if (string.IsNullOrWhiteSpace(BinPath) || !File.Exists(BinPath)) return;
        string expectedPath;
        try { expectedPath = Path.GetFullPath(BinPath); }
        catch { return; }

        foreach (var candidate in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(BinPath)))
        {
            try
            {
                if (candidate.Id == Environment.ProcessId) continue;
                var imagePath = candidate.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(imagePath) ||
                    !string.Equals(Path.GetFullPath(imagePath), expectedPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                candidate.Kill(entireProcessTree: true);
                candidate.WaitForExit(3000);
                Output?.Invoke($"Stopped leftover sing-box process ({candidate.Id}).");
            }
            catch (Exception ex)
            {
                Output?.Invoke($"Could not stop leftover sing-box process: {ex.Message}");
            }
            finally { candidate.Dispose(); }
        }
    }

    public string FindBinary()
    {
        // check app dir / bin
        var candidates = new List<string>();
        var baseDir = AppContext.BaseDirectory;
        candidates.Add(Path.Combine(baseDir, "bin", "sing-box.exe"));
        candidates.Add(Path.Combine(baseDir, "sing-box.exe"));
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "bin", "sing-box.exe"));
        // search up
        try
        {
            var dir = new DirectoryInfo(baseDir);
            for (int i = 0; i < 5 && dir != null; i++)
            {
                candidates.Add(Path.Combine(dir.FullName, "bin", "sing-box.exe"));
                dir = dir.Parent;
            }
        }
        catch { }
        foreach (var c in candidates)
            if (File.Exists(c)) return c;
        // PATH
        try
        {
            var psi = new ProcessStartInfo("where", "sing-box.exe") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi);
            if (p != null) { var o = p.StandardOutput.ReadToEnd(); p.WaitForExit(); var first = o.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(); if (first != null && File.Exists(first)) return first; }
        }
        catch { }
        return "";
    }

    public void Dispose() => StopAsync().GetAwaiter().GetResult();
}
