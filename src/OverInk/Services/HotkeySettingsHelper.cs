using System.Windows.Input;
using OverInk.Interop;

namespace OverInk.Services;

public static class HotkeySettingsHelper
{
    public const uint DefaultModifiers = NativeMethods.ModControl | NativeMethods.ModShift;
    public const uint DefaultVirtualKey = 0x44; // D

    public static string Format(uint modifiers, uint virtualKey)
    {
        var parts = new List<string>();
        if ((modifiers & NativeMethods.ModControl) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((modifiers & NativeMethods.ModShift) != 0)
        {
            parts.Add("Shift");
        }

        if ((modifiers & NativeMethods.ModAlt) != 0)
        {
            parts.Add("Alt");
        }

        if ((modifiers & NativeMethods.ModWin) != 0)
        {
            parts.Add("Win");
        }

        var key = KeyInterop.KeyFromVirtualKey((int)virtualKey);
        parts.Add(key == Key.None ? $"0x{virtualKey:X}" : KeyName(key));
        return string.Join(" + ", parts);
    }

    public static uint ModifiersFromKeyboard()
    {
        uint modifiers = 0;
        if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl))
        {
            modifiers |= NativeMethods.ModControl;
        }

        if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
        {
            modifiers |= NativeMethods.ModShift;
        }

        if (Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt))
        {
            modifiers |= NativeMethods.ModAlt;
        }

        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin))
        {
            modifiers |= NativeMethods.ModWin;
        }

        return modifiers;
    }

    /// <summary>Keys safe to bind without a modifier because they are never used for typing.</summary>
    public static bool IsStandaloneKey(Key key) =>
        key is Key.Snapshot or Key.Pause or Key.Scroll || key is >= Key.F1 and <= Key.F24;

    private static string KeyName(Key key) => key switch
    {
        Key.Snapshot => "Print Screen",
        Key.Scroll => "Scroll Lock",
        _ => key.ToString()
    };
    public static bool IsModifierKey(Key key) =>
        key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.System;

    public static bool TryCreateFromKey(Key key, out uint modifiers, out uint virtualKey)
    {
        modifiers = ModifiersFromKeyboard();
        virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);

        if (virtualKey == 0 || IsModifierKey(key))
        {
            return false;
        }

        // Keys that never type anything may be bound on their own (Print Screen, F-keys, ...).
        return modifiers != 0 || IsStandaloneKey(key);
    }
}
