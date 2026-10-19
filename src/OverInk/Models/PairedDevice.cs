namespace OverInk.Models;

public sealed class PairedDevice
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "iPad";
    public string KeyHash { get; set; } = string.Empty;
    public DateTime LastSeenUtc { get; set; }
    public bool AutoAllow { get; set; }

    /// <summary>
    /// Whether this device may browse, edit and create captures. False for devices that joined through a
    /// session link; true for devices paired by QR code. Devices stored before this setting existed keep access.
    /// </summary>
    public bool CanUseLibrary { get; set; } = true;
}
