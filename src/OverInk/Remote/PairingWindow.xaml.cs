using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QRCoder;
using OverInk.Services;

namespace OverInk.Remote;

public partial class PairingWindow : Window
{
    /// <summary>How long to wait for a first device before showing the troubleshooting checklist.</summary>
    private static readonly TimeSpan HintDelay = TimeSpan.FromSeconds(45);

    private readonly PairingService _pairing;
    private readonly RemoteSketchServer _server;
    private readonly SettingsService _settings;
    private readonly DispatcherTimer _hintTimer;
    private bool _loadingAdapters;
    private FirewallState _firewallState = FirewallState.Unknown;
    private string? _selectedAddress;

    private sealed record AdapterItem(LanAdapter Adapter, bool Recommended)
    {
        public string Label
        {
            get
            {
                var text = $"{Adapter.Name} ({Adapter.Description}) - {Adapter.Address}";
                if (Recommended)
                {
                    return text + "   [recommended]";
                }

                return Adapter.Warning is null ? text : text + "   [!]";
            }
        }
    }

    public PairingWindow(PairingService pairing, RemoteSketchServer server, SettingsService settings)
    {
        _pairing = pairing;
        _server = server;
        _settings = settings;
        InitializeComponent();
        _server.ClientCountChanged += OnClientCountChanged;
        _hintTimer = new DispatcherTimer { Interval = HintDelay };
        _hintTimer.Tick += OnHintTimerTick;
        Closed += (_, _) =>
        {
            _server.ClientCountChanged -= OnClientCountChanged;
            _hintTimer.Stop();
        };

        LoadAdapters();
        RefreshParticipants();
        _ = CheckFirewallAsync();

        if (_server.ConnectedClients == 0)
        {
            _hintTimer.Start();
        }
    }

    private void OnClientCountChanged(int count)
    {
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = count == 0 ? "Waiting for a remote client…" : $"{count} device(s) connected.";
            if (count > 0)
            {
                _hintTimer.Stop();
                HintPanel.Visibility = Visibility.Collapsed;
            }

            RefreshParticipants();
        });
    }

    private void OnHintTimerTick(object? sender, EventArgs e)
    {
        _hintTimer.Stop();
        if (_server.ConnectedClients > 0)
        {
            return;
        }

        var port = _settings.Current.RemotePort;
        var health = _selectedAddress is null ? $"http://<this PC's address>:{port}/health" : $"http://{_selectedAddress}:{port}/health";
        HintText.Text =
            "Still waiting for a device. Check that:\n" +
            "• the iPad is on the same Wi-Fi / network as this PC (not a guest network)\n" +
            "• any VPN on this PC or the iPad is disconnected\n" +
            "• Windows Firewall allows OverInk (use \"Fix firewall\" above if it shows)\n" +
            "• the router doesn't have \"client isolation\" / \"AP isolation\" turned on\n\n" +
            $"Quick test: open {health} in Safari on the iPad. If you see {{\"ok\":true}}, the network is fine.";
        HintPanel.Visibility = Visibility.Visible;
    }

    private void LoadAdapters()
    {
        _loadingAdapters = true;
        try
        {
            var adapters = LanAdapters.GetAdapters(ShowAllCheck.IsChecked == true);
            var recommended = LanAdapters.Choose(adapters, null);
            var items = adapters
                .Select(a => new AdapterItem(a, recommended is { CanReachLan: true } && a == recommended))
                .ToList();

            AdapterCombo.ItemsSource = items;

            var chosen = LanAdapters.Choose(adapters, _settings.Current.RemoteAdapterId);
            AdapterCombo.SelectedItem = items.FirstOrDefault(i => i.Adapter == chosen);
            AdapterCombo.IsEnabled = items.Count > 0;
            UpdateVpnNotice();
        }
        finally
        {
            _loadingAdapters = false;
        }

        RefreshQr();
    }

    private void UpdateVpnNotice()
    {
        var vpn = LanAdapters.GetAdapters(includeAll: false)
            .FirstOrDefault(a => a.Kind == LanAdapterKind.Vpn && a.IsUp);
        if (vpn is null)
        {
            VpnPanel.Visibility = Visibility.Collapsed;
            return;
        }

        VpnText.Text =
            $"A VPN looks connected ({vpn.Name}). VPNs can block devices on your home or office network from " +
            "reaching this PC. If your iPad can't connect, try disconnecting the VPN.";
        VpnPanel.Visibility = Visibility.Visible;
    }

    private void RefreshQr()
    {
        var adapter = (AdapterCombo.SelectedItem as AdapterItem)?.Adapter;
        _selectedAddress = adapter?.Address.ToString();

        if (adapter is null)
        {
            UrlText.Text = "No network connection found. Connect this PC to Wi-Fi or Ethernet, then press Refresh.";
            QrImage.Source = null;
            QrImage.Visibility = Visibility.Collapsed;
            AltUrlText.Visibility = Visibility.Collapsed;
            AdapterWarningText.Visibility = Visibility.Collapsed;
            return;
        }

        AdapterWarningText.Text = adapter.Warning ?? string.Empty;
        AdapterWarningText.Visibility = adapter.Warning is null ? Visibility.Collapsed : Visibility.Visible;

        var port = _settings.Current.RemotePort;
        var query = $"?join={_pairing.SessionJoinToken}&pair={_pairing.CreatePairToken()}";
        var url = $"http://{adapter.Address}:{port}/{query}";
        UrlText.Text = url;

        var host = Environment.MachineName.ToLowerInvariant();
        AltUrlText.Text = $"Or try the PC's name (doesn't work on every network): http://{host}.local:{port}/{query}";
        AltUrlText.Visibility = Visibility.Visible;

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.Q);
        var png = new PngByteQRCode(data);
        var bytes = png.GetGraphic(12);
        using var ms = new MemoryStream(bytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = ms;
        image.EndInit();
        QrImage.Source = image;
        QrImage.Visibility = Visibility.Visible;
    }

    private async Task CheckFirewallAsync()
    {
        var port = _settings.Current.RemotePort;
        var exe = Environment.ProcessPath;
        var result = await Task.Run(() => FirewallHelper.Check(port, exe));
        _firewallState = result.State;
        if (result.State is FirewallState.NoRule or FirewallState.Blocked)
        {
            FirewallText.Text = result.Message;
            FixFirewallButton.IsEnabled = true;
            FirewallPanel.Visibility = Visibility.Visible;
        }
        else
        {
            FirewallPanel.Visibility = Visibility.Collapsed;
        }
    }

    private async void FixFirewall_Click(object sender, RoutedEventArgs e)
    {
        FixFirewallButton.IsEnabled = false;
        var port = _settings.Current.RemotePort;
        var exe = Environment.ProcessPath;
        var replace = _firewallState == FirewallState.Blocked;
        FirewallText.Text = "Waiting for the Windows admin prompt…";

        var (ok, message) = await FirewallHelper.AddAllowRuleAsync(port, exe, replace);
        if (ok)
        {
            await CheckFirewallAsync();
            if (FirewallPanel.Visibility == Visibility.Collapsed)
            {
                StatusText.Text = message;
            }
            else
            {
                FirewallText.Text = message + " " + FirewallText.Text;
                FixFirewallButton.IsEnabled = true;
            }
        }
        else
        {
            FirewallText.Text = message;
            FixFirewallButton.IsEnabled = true;
        }
    }

    private void AdapterCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loadingAdapters || AdapterCombo.SelectedItem is not AdapterItem item)
        {
            return;
        }

        _settings.Current.RemoteAdapterId = item.Adapter.Id;
        _settings.Save();
        RefreshQr();
    }

    private void ShowAll_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            LoadAdapters();
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        LoadAdapters();
        _ = CheckFirewallAsync();
    }

    private void RefreshParticipants()
    {
        ParticipantsList.Items.Clear();
        foreach (var device in _settings.Current.PairedDevices)
        {
            ParticipantsList.Items.Add($"{device.Name} · last seen {device.LastSeenUtc.ToLocalTime():g}");
        }
    }

    private void RotateJoinLink_Click(object sender, RoutedEventArgs e)
    {
        _pairing.RotateSessionJoinToken();
        RefreshQr();
    }

    private void RevokeAll_Click(object sender, RoutedEventArgs e)
    {
        _pairing.RevokeAll();
        RefreshParticipants();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
