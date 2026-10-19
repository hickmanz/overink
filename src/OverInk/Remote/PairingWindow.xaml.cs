using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using QRCoder;
using OverInk.Services;

namespace OverInk.Remote;

public partial class PairingWindow : Window
{
    private readonly PairingService _pairing;
    private readonly RemoteSketchServer _server;
    private readonly SettingsService _settings;

    public PairingWindow(PairingService pairing, RemoteSketchServer server, SettingsService settings)
    {
        _pairing = pairing;
        _server = server;
        _settings = settings;
        InitializeComponent();
        _server.ClientCountChanged += OnClientCountChanged;
        Closed += (_, _) => _server.ClientCountChanged -= OnClientCountChanged;
        RefreshQr();
        RefreshParticipants();
    }

    private void OnClientCountChanged(int count)
    {
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = count == 0 ? "Waiting for a remote client…" : $"{count} device(s) connected.";
            RefreshParticipants();
        });
    }

    private void RefreshQr()
    {
        var ip = RemoteNetworkHelper.GetPreferredLanIPv4() ?? "127.0.0.1";
        var port = _settings.Current.RemotePort;
        var join = _pairing.SessionJoinToken;
        var pair = _pairing.CreatePairToken();
        var url = $"http://{ip}:{port}/?join={join}&pair={pair}";
        UrlText.Text = url;

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
