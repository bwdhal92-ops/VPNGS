using System.Windows;
using System.Windows.Controls;
using FlareVpn.Services;
using FlareVpn.ViewModels;

namespace FlareVpn;

public partial class AdvancedWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly SingBoxProcessManager _proc;
    private readonly MainWindow _main;
    private bool _ready;

    public AdvancedWindow(MainViewModel vm, SingBoxProcessManager proc, MainWindow main)
    {
        InitializeComponent();
        _vm = vm;
        _proc = proc;
        _main = main;
        DataContext = vm;
        ShowSettingsPage();
        SocksPortBox.Text = vm.Settings.SocksPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        HttpPortBox.Text = vm.Settings.HttpPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ServerListView.SelectionChanged += (_, _) => _main.RefreshFromAdvanced();
        Loaded += (_, _) => _ready = true;
    }

    public void ShowSettingsPage()
    {
        SettingsHeader.Visibility = Visibility.Visible;
        ProxyPortsSection.Visibility = Visibility.Visible;
        PreferencesSection.Visibility = Visibility.Visible;
        ServerListSection.Visibility = Visibility.Collapsed;
        LogsSection.Visibility = Visibility.Collapsed;
        SettingsPageButton.Background = (System.Windows.Media.Brush)FindResource("AccentBrush");
        ServersPageButton.Background = (System.Windows.Media.Brush)FindResource("PanelBrush");
    }

    public void ShowServersPage()
    {
        SettingsHeader.Visibility = Visibility.Collapsed;
        ProxyPortsSection.Visibility = Visibility.Collapsed;
        PreferencesSection.Visibility = Visibility.Collapsed;
        ServerListSection.Visibility = Visibility.Visible;
        LogsSection.Visibility = Visibility.Visible;
        SettingsPageButton.Background = (System.Windows.Media.Brush)FindResource("PanelBrush");
        ServersPageButton.Background = (System.Windows.Media.Brush)FindResource("AccentBrush");
    }

    private void SettingsPage_Click(object sender, RoutedEventArgs e) => ShowSettingsPage();
    private void ServersPage_Click(object sender, RoutedEventArgs e) => ShowServersPage();

    public void AppendLog(string msg)
    {
        Dispatcher.Invoke(() =>
        {
            LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}\n");
            LogBox.ScrollToEnd();
        });
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await _main.RefreshServersPublic();
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        _vm.SearchText = SearchBox.Text;
    }

    private void SaveProxyPorts_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(SocksPortBox.Text, out var socksPort) || socksPort is < 1 or > 65535 ||
            !int.TryParse(HttpPortBox.Text, out var httpPort) || httpPort is < 1 or > 65535 ||
            socksPort == httpPort)
        {
            AppendLog("المنافذ غير صالحة. أدخل رقمين مختلفين بين 1 و65535.");
            return;
        }

        _vm.Settings.SocksPort = socksPort;
        _vm.Settings.HttpPort = httpPort;
        SettingsService.Save(_vm.Settings);
        AppendLog(_vm.IsConnected
            ? "تم حفظ المنافذ؛ ستُستخدم عند الاتصال القادم."
            : $"تم حفظ SOCKS {socksPort} وHTTP {httpPort}.");
    }

    private void OptionalSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _main.ApplyOptionalSettings();
        AppendLog(_vm.Settings.StartWithWindows
            ? "تم تفعيل بدء البرنامج مع ويندوز."
            : "تم حفظ إعدادات بدء التشغيل وشريط النظام.");
    }

    // expose refresh
}
