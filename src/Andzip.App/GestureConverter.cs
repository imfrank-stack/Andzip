using Andzip.Core.Menu;
using Avalonia.Input;

namespace Andzip.App;

/// <summary>把与平台无关的 <see cref="ShortcutSpec"/> 转成 Avalonia 手势（Windows 为 Ctrl；macOS 映射 Command 由 Avalonia 处理）。</summary>
internal static class GestureConverter
{
    public static KeyGesture? ToGesture(ShortcutSpec? spec)
    {
        if (spec is null || !TryParseKey(spec.Key, out var key))
        {
            return null;
        }

        var modifiers = KeyModifiers.None;
        if (spec.HasModifier("Ctrl"))
        {
            modifiers |= KeyModifiers.Control;
        }

        if (spec.HasModifier("Shift"))
        {
            modifiers |= KeyModifiers.Shift;
        }

        if (spec.HasModifier("Alt"))
        {
            modifiers |= KeyModifiers.Alt;
        }

        return new KeyGesture(key, modifiers);
    }

    private static bool TryParseKey(string token, out Key key)
    {
        switch (token.ToUpperInvariant())
        {
            case "INS":
                key = Key.Insert;
                return true;
            case "DEL":
                key = Key.Delete;
                return true;
            case "ENTER":
                key = Key.Enter;
                return true;
            case "BACKSPACE":
                key = Key.Back;
                return true;
            case "ESC":
            case "ESCAPE":
                key = Key.Escape;
                return true;
            default:
                return Enum.TryParse(token, ignoreCase: true, out key);
        }
    }
}