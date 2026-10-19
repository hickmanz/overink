using System.Security.Cryptography;
using System.Text;
using OverInk.Models;
using OverInk.Services;

namespace OverInk.Remote;

public sealed class PairingService
{
    private readonly SettingsService _settings;
    private readonly object _gate = new();
    private string? _activePairToken;
    private DateTime _pairTokenExpiresUtc;
    private string _sessionJoinToken = Guid.NewGuid().ToString("N");

    public PairingService(SettingsService settings)
    {
        _settings = settings;
    }

    public string SessionJoinToken
    {
        get
        {
            lock (_gate)
            {
                return _sessionJoinToken;
            }
        }
    }

    public string RotateSessionJoinToken()
    {
        lock (_gate)
        {
            _sessionJoinToken = Guid.NewGuid().ToString("N");
            return _sessionJoinToken;
        }
    }

    public string CreatePairToken()
    {
        lock (_gate)
        {
            _activePairToken = CreateToken();
            _pairTokenExpiresUtc = DateTime.UtcNow.AddMinutes(10);
            return _activePairToken;
        }
    }

    public bool TryConsumePairToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        lock (_gate)
        {
            if (_activePairToken is null || DateTime.UtcNow > _pairTokenExpiresUtc)
            {
                return false;
            }

            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(_activePairToken),
                    Encoding.UTF8.GetBytes(token)))
            {
                return false;
            }

            _activePairToken = null;
            return true;
        }
    }

    public bool TryValidateJoinToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        lock (_gate)
        {
            return string.Equals(_sessionJoinToken, token, StringComparison.Ordinal);
        }
    }

    public string IssueDeviceKey(string deviceName)
    {
        var key = CreateToken();
        var device = new PairedDevice
        {
            Name = string.IsNullOrWhiteSpace(deviceName) ? "iPad" : deviceName.Trim(),
            KeyHash = HashKey(key),
            LastSeenUtc = DateTime.UtcNow
        };
        _settings.Current.PairedDevices.Add(device);
        _settings.Save();
        return key;
    }

    public PairedDevice? FindByDeviceKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var hash = HashKey(key);
        var device = _settings.Current.PairedDevices.FirstOrDefault(d => d.KeyHash == hash);
        if (device is not null)
        {
            device.LastSeenUtc = DateTime.UtcNow;
            _settings.Save();
        }

        return device;
    }

    public void RevokeAll()
    {
        _settings.Current.PairedDevices.Clear();
        _settings.Save();
    }

    public void Revoke(PairedDevice device)
    {
        _settings.Current.PairedDevices.RemoveAll(d => d.Id == device.Id);
        _settings.Save();
    }

    public static string HashKey(string key)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(bytes);
    }

    private static string CreateToken()
    {
        Span<byte> buffer = stackalloc byte[16];
        RandomNumberGenerator.Fill(buffer);
        return Convert.ToBase64String(buffer).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
