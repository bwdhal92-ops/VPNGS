using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using FlareVpn.Services;

namespace FlareVpn;

public partial class App : System.Windows.Application
{
    private Mutex? _instanceMutex;
    private bool _ownsInstanceMutex;

    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);
    private const int ATTACH_PARENT_PROCESS = -1;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Acquire the per-user-session lock before creating any window. This avoids
        // a second process creating MainWindow or touching the active tunnel.
        try
        {
            _instanceMutex = new Mutex(initiallyOwned: true, name: @"Local\VPNGS_SingleInstance", createdNew: out var createdNew);
            _ownsInstanceMutex = createdNew;
            if (!createdNew)
            {
                _instanceMutex.Dispose();
                _instanceMutex = null;
                System.Windows.MessageBox.Show(
                    "برنامج VPNGS مفتوح بالفعل. أغلق النافذة الحالية قبل تشغيله مرة أخرى.",
                    "VPNGS مفتوح بالفعل",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Shutdown(0);
                return;
            }
        }
        catch (Exception ex)
        {
            try { Console.Error.WriteLine("[Startup] Single-instance guard failed: " + ex); } catch { }
            System.Windows.MessageBox.Show(
                "تعذر التأكد مما إذا كان VPNGS مفتوحًا بالفعل. أغلق أي نسخة أخرى ثم حاول مجددًا.",
                "تعذر بدء VPNGS",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // Only show a console for explicit diagnostic launches (e.g. VPNGS.bat).
        // Published desktop builds stay windowed even when launched by a build script.
        if (string.Equals(Environment.GetEnvironmentVariable("FLARE_CONSOLE"), "1", StringComparison.Ordinal))
        {
            var attached = false;
            try { attached = AttachConsole(ATTACH_PARENT_PROCESS); } catch { }
            if (!attached)
            {
                try { AllocConsole(); } catch { }
            }
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
        }

        // Prevent Arabic-Indic digit conversion and mirroring issues for IPs/numbers
        Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
        Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

        // Global exception handler
        DispatcherUnhandledException += (_, ex) =>
        {
            try { Console.WriteLine("[FATAL] " + ex.Exception); } catch { }
            try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "VPNGS.log"), DateTime.Now + " " + ex.Exception + Environment.NewLine); } catch { }
            if (FirebaseTelemetryService.Current is { } telemetry)
                _ = telemetry.ReportExceptionAsync(ex.Exception, "unhandled-ui");
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            try { Console.WriteLine("[FATAL] " + ex.ExceptionObject); } catch { }
            try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "VPNGS.log"), DateTime.Now + " " + ex.ExceptionObject + Environment.NewLine); } catch { }
            if (ex.ExceptionObject is Exception exception && FirebaseTelemetryService.Current is { } telemetry)
                _ = telemetry.ReportExceptionAsync(exception, "unhandled-process");
        };

        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsInstanceMutex && _instanceMutex is not null)
        {
            try { _instanceMutex.ReleaseMutex(); } catch (ApplicationException) { }
        }

        _instanceMutex?.Dispose();
        _instanceMutex = null;
        _ownsInstanceMutex = false;
        base.OnExit(e);
    }
}
