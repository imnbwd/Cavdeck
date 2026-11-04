using System.Windows.Input;

namespace Cavdeck.App.Services;

/// <summary>Deliberately limited to non-system Ctrl+Alt / Ctrl+Shift combinations.</summary>
public readonly record struct GlobalHotkeyGesture(uint Modifiers, uint VirtualKey, string Text)
{
    public const string Default = "Ctrl+Alt+C";
    private const uint Alt = 0x0001;
    private const uint Control = 0x0002;
    private const uint Shift = 0x0004;

    public static bool TryParse(string? value, out GlobalHotkeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var parts = value.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length is < 3 or > 4 || parts.Any(string.IsNullOrEmpty))
            return false;
        uint modifiers = 0;
        foreach (var token in parts[..^1])
        {
            var bit = token.ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => Control,
                "ALT" => Alt,
                "SHIFT" => Shift,
                _ => 0u
            };
            if (bit == 0 || (modifiers & bit) != 0) return false;
            modifiers |= bit;
        }
        // Two modifiers make accidental activation unlikely. Windows shortcuts remain reserved.
        if ((modifiers & Control) == 0 || (modifiers & (Alt | Shift)) == 0)
            return false;
        var key = parts[^1].ToUpperInvariant();
        uint vk;
        if (key.Length == 1 && key[0] is >= 'A' and <= 'Z' or >= '0' and <= '9')
            vk = key[0];
        else if (key.StartsWith('F') && int.TryParse(key.AsSpan(1), out var function)
                 && function is >= 1 and <= 11)
            vk = (uint)(0x70 + function - 1);
        else
            return false;
        // ShellWindow handles Ctrl+T/W/B and Ctrl+1..9 regardless of Alt/Shift.
        // Registering those chords globally would silently steal existing commands.
        if (key is "T" or "W" or "B" or "1" or "2" or "3" or "4" or "5" or "6" or "7" or "8" or "9"
            or "F2" or "F4" or "F5")
            return false;
        var name = "Ctrl" + ((modifiers & Alt) != 0 ? "+Alt" : "")
                          + ((modifiers & Shift) != 0 ? "+Shift" : "") + "+" + key;
        gesture = new GlobalHotkeyGesture(modifiers, vk, name);
        return true;
    }

    public static bool TryCapture(Key key, ModifierKeys modifiers, out GlobalHotkeyGesture gesture)
    {
        gesture = default;
        if ((modifiers & ModifierKeys.Windows) != 0) return false;
        var name = key switch
        {
            >= Key.A and <= Key.Z => key.ToString(),
            >= Key.D0 and <= Key.D9 => (key - Key.D0).ToString(),
            >= Key.F1 and <= Key.F11 => key.ToString(),
            _ => null
        };
        if (name is null) return false;
        var input = ((modifiers & ModifierKeys.Control) != 0 ? "Ctrl+" : "") +
                    ((modifiers & ModifierKeys.Alt) != 0 ? "Alt+" : "") +
                    ((modifiers & ModifierKeys.Shift) != 0 ? "Shift+" : "") + name;
        return TryParse(input, out gesture);
    }
}
