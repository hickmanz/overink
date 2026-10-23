using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace OverInk.Services;

/// <summary>
/// Gives a borderless, non-layered window an acrylic blur-behind (real blur of what is behind it).
/// Uses the accent-policy API (Windows 10 1803+). The blur is only clipped by Windows' own corner rounding
/// (about 8 physical pixels, see <see cref="SystemCornerRadiusPixels"/>); a custom window region does not clip it.
/// </summary>
public static class AcrylicBackdrop
{
    /// <summary>Corner radius Windows 11 applies for <c>DWMWCP_ROUND</c>, in physical pixels.</summary>
    public const double SystemCornerRadiusPixels = 8;

    private const int WcaAccentPolicy = 19;
    private const int AccentEnableAcrylicBlurBehind = 4;
    private const int DwmaWindowCornerPreference = 33;
    private const int DwmaBorderColor = 34;
    private const int DwmwcpRound = 2;
    private const uint DwmColorNone = 0xFFFFFFFE;

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor; // AABBGGRR; alpha is the strength of the tint over the blur
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
    private static extern int DwmSetWindowAttributeUInt(IntPtr hwnd, int attribute, ref uint value, int size);

    public static bool IsSupported => Environment.OSVersion.Version.Build >= 17134;

    /// <summary>The system corner radius expressed in DIPs for this window's current scale.</summary>
    public static double SystemCornerRadiusDips(Window window) =>
        SystemCornerRadiusPixels / VisualTreeHelper.GetDpi(window).DpiScaleX;

    /// <summary>
    /// Call from SourceInitialized (the window handle must exist). <paramref name="tint"/> is laid over the blur;
    /// its alpha sets how dark/opaque the glass is.
    /// </summary>
    public static bool Apply(Window window, Color tint)
    {
        if (!IsSupported)
        {
            return false;
        }

        var accentPtr = IntPtr.Zero;
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                return false;
            }

            // Let transparent WPF pixels show the blur instead of painting black.
            if (HwndSource.FromHwnd(handle)?.CompositionTarget is { } target)
            {
                target.BackgroundColor = Colors.Transparent;
            }

            var corner = DwmwcpRound;
            DwmSetWindowAttribute(handle, DwmaWindowCornerPreference, ref corner, sizeof(int));

            // Drop the system's own 1px window border; the toolbar draws its own rim.
            var noBorder = DwmColorNone;
            DwmSetWindowAttributeUInt(handle, DwmaBorderColor, ref noBorder, sizeof(uint));

            var accent = new AccentPolicy
            {
                AccentState = AccentEnableAcrylicBlurBehind,
                AccentFlags = 2,
                GradientColor = (uint)((tint.A << 24) | (tint.B << 16) | (tint.G << 8) | tint.R),
            };

            var size = Marshal.SizeOf<AccentPolicy>();
            accentPtr = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(accent, accentPtr, false);

            var data = new WindowCompositionAttributeData
            {
                Attribute = WcaAccentPolicy,
                Data = accentPtr,
                SizeOfData = size,
            };

            return SetWindowCompositionAttribute(handle, ref data) != 0;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (accentPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(accentPtr);
            }
        }
    }
}
