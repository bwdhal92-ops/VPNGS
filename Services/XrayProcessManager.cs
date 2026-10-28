using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;

namespace FlareVpn.Services;

public sealed class XrayProcessManager
{
    private Process? _process;
    private string _configDirectory = "";
    public string BinPath { get; set; } = FindBinary();
    public string LastOutput { get; private set; } = "";
    public bool IsRunning => _process != null && !_process.HasExited;
    public event Action<string>? Output;
    public event Action? Exited;

    public static string FindBinary()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "bin", "xray.exe"),
            Path.Combine(AppContext.BaseDirectory, "xray.exe"),
            Path.Combine(Environment.CurrentDirectory, "bin", "xray.exe")
        };
        return candidates.FirstOrDefault(File.Exists) ?? "";
    }

    public async Task<bool> StartAsync(string configJson, int readyPort, CancellationToken ct = default)
    {
        await StopAsync();
        LastOutput = "";
        try
        {
            if (string.IsNullOrWhiteSpace(BinPath) || !File.Exists(BinPath))
                throw new FileNotFoundException("xray.exe not found", BinPath);

            _configDirectory = Path.Combine(Path.GetTempPath(), "VPNGS", "xray-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_configDirectory);
            var configPath = Path.Combine(_configDirectory, "config.json");
            await File.WriteAllTextAsync(configPath, configJson, ct);

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = BinPath,
                    Arguments = $"run -c \"{configPath}\"",
                    WorkingDirectory = Path.GetDirectoryName(BinPath),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                },
                EnableRaisingEvents = true
            };
            _process = process;
            process.OutputDataReceived += (_, e) => { if (e.Data != null) RecordOutput(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) RecordOutput(e.Data); };
            process.Exited += (_, _) => { if (ReferenceEquals(_process, process)) Exited?.Invoke(); };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (process.HasExited)
                {
                    await StopAsync();
                    return false;
                }
                if (IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == readyPort))
                    return !process.HasExited;
                await Task.Delay(50, ct);
            }

            Output?.Invoke($"Xray HTTP listener on 127.0.0.1:{readyPort} did not become ready.");
            await StopAsync();
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await StopAsync();
            throw;
        }
        catch (Exception ex)
        {
            Output?.Invoke("Xray start failed: " + ex.GetBaseException().Message);
            await StopAsync();
            return false;
        }
    }

    private void RecordOutput(string line)
    {
        LastOutput = line;
        Output?.Invoke(line);
    }

    public async Task StopAsync()
    {
        var process = _process;
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
                    Output?.Invoke("Xray did not exit after termination; sending a second stop request.");
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
            Output?.Invoke("Could not stop Xray within the shutdown timeout: " + ex.GetBaseException().Message);
            throw;
        }
        finally
        {
            if (process == null || process.HasExited)
            {
                if (ReferenceEquals(_process, process)) _process = null;
                process?.Dispose();
                try
                {
                    if (!string.IsNullOrWhiteSpace(_configDirectory) && Directory.Exists(_configDirectory))
                        Directory.Delete(_configDirectory, true);
                }
                catch { }
                _configDirectory = "";
            }
        }
    }
}
