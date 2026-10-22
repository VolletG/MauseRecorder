using System.Windows.Input;
using MauseRecorder.Interop;

namespace MauseRecorder.Models;

/// <summary>
/// A global hotkey: modifier flags (MOD_*) plus a virtual-key code.
/// Serialized in settings as a friendly string like "Ctrl + Alt + R".
/// </summary>
public readonly record struct Hotkey(uint Modifiers, uint VirtualKey)
{
    public const uint Ctrl = NativeMethods.MOD_CONTROL;
    public const uint Alt = NativeMethods.MOD_ALT;
    public const uint Shift = NativeMethods.MOD_SHIFT;
    public const uint Win = NativeMethods.MOD_WIN;

    public static readonly Hotkey DefaultRecord = new(Ctrl | Alt, 0x52); // R
    public static readonly Hotkey DefaultPlay = new(Ctrl | Alt, 0x50);   // P
    public static readonly Hotkey DefaultShow = new(Ctrl | Alt, 0x48);   // H

    public bool IsValid => VirtualKey != 0 && (Modifiers & (Ctrl | Alt | Shift | Win)) != 0;

    public string Format()
    {
        if (!IsValid) return "—";

        var parts = new List<string>(5);
        if ((Modifiers & Ctrl) != 0) parts.Add("Ctrl");
        if ((Modifiers & Alt) != 0) parts.Add("Alt");
        if ((Modifiers & Shift) != 0) parts.Add("Shift");
        if ((Modifiers & Win) != 0) parts.Add("Win");
        parts.Add(KeyName(VirtualKey));
        return string.Join(" + ", parts);
    }

    public static string KeyName(uint virtualKey)
    {
        var key = KeyInterop.KeyFromVirtualKey((int)virtualKey);
        return key switch
        {
            >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
            Key.OemPlus => "+",
            Key.OemMinus => "-",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemQuestion => "/",
            Key.OemSemicolon => ";",
            Key.OemQuotes => "'",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            Key.OemPipe => "\\",
            Key.OemTilde => "`",
            Key.Add => "+",
            Key.Subtract => "-",
            Key.Multiply => "*",
            Key.Divide => "/",
            Key.Decimal => ".",
            Key.Space => "Space",
            Key.Escape => "Esc",
            Key.Return => "Enter",
            Key.Back => "Backspace",
            Key.Next => "PageDown",
            Key.Prior => "PageUp",
            Key.Capital => "CapsLock",
            _ => key.ToString()
        };
    }

    public static Hotkey? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        uint modifiers = 0;
        uint virtualKey = 0;

        var parts = text.Contains(" + ", StringComparison.Ordinal)
            ? text.Split(" + ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var raw in parts)
        {
            var token = raw.Trim();
            switch (token.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= Ctrl;
                    continue;
                case "alt":
                    modifiers |= Alt;
                    continue;
                case "shift":
                    modifiers |= Shift;
                    continue;
                case "win":
                case "windows":
                    modifiers |= Win;
                    continue;
            }

            var parsed = ParseKey(token);
            if (parsed is null) return null;
            virtualKey = parsed.Value;
        }

        var hotkey = new Hotkey(modifiers, virtualKey);
        return hotkey.IsValid ? hotkey : null;
    }

    public static Hotkey FromKey(Key key, ModifierKeys modifiers)
    {
        uint flags = 0;
        if ((modifiers & ModifierKeys.Control) != 0) flags |= Ctrl;
        if ((modifiers & ModifierKeys.Alt) != 0) flags |= Alt;
        if ((modifiers & ModifierKeys.Shift) != 0) flags |= Shift;
        if ((modifiers & ModifierKeys.Windows) != 0) flags |= Win;

        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        return new Hotkey(flags, (uint)virtualKey);
    }

    private static uint? ParseKey(string token)
    {
        if (token.Length == 1)
        {
            var ch = char.ToUpperInvariant(token[0]);
            if (ch is >= 'A' and <= 'Z') return ch;
            if (ch is >= '0' and <= '9') return ch;
            switch (ch)
            {
                case '+': return (uint)Key.OemPlus;
                case '-': return (uint)Key.OemMinus;
                case ',': return (uint)Key.OemComma;
                case '.': return (uint)Key.OemPeriod;
                case '/': return (uint)Key.OemQuestion;
                case ';': return (uint)Key.OemSemicolon;
                case '\'': return (uint)Key.OemQuotes;
                case '[': return (uint)Key.OemOpenBrackets;
                case ']': return (uint)Key.OemCloseBrackets;
                case '\\': return (uint)Key.OemPipe;
                case '`': return (uint)Key.OemTilde;
            }
        }

        var name = token.ToLowerInvariant() switch
        {
            "esc" => "Escape",
            "return" => "Return",
            "space" => "Space",
            "backspace" => "Back",
            "pagedown" => "Next",
            "pageup" => "Prior",
            "capslock" => "Capital",
            _ => token
        };

        if (Enum.TryParse<Key>(name, true, out var key) && key != Key.None)
        {
            var virtualKey = KeyInterop.VirtualKeyFromKey(key);
            if (virtualKey != 0) return (uint)virtualKey;
        }

        return null;
    }

    public override string ToString() => Format();
}
