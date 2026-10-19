namespace OverInk.Models;

public sealed class PairedDevice
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "iPad";
    public string KeyHash { get; set; } = string.Empty;
    public DateTime LastSeenUtc { get; set; }
    public bool AutoAllow { get; set; }
}
