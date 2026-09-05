using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DeskFlow.Shell;

public sealed record HotkeyCombination(uint Modifiers, uint VirtualKey)
{
    public override string ToString() => HotkeyParser.Format(Modifiers, VirtualKey);
}

/// <summary>解析 "Ctrl+Alt+O" 形式的快捷键字符串。</summary>
public static class HotkeyParser
{
    private static readonly Dictionary<string, uint> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = 0x20,
        ["Enter"] = 0x0D,
        ["Return"] = 0x0D,
        ["Esc"] = 0x1B,
        ["Escape"] = 0x1B,
        ["Tab"] = 0x09,
        ["Back"] = 0x08,
        ["Delete"] = 0x2E,
        ["Insert"] = 0x2D,
        ["Home"] = 0x24,
        ["End"] = 0x23,
        ["PageUp"] = 0x21,
        ["PageDown"] = 0x22,
        ["Up"] = 0x26,
        ["Down"] = 0x28,
        ["Left"] = 0x25,
        ["Right"] = 0x27
    };

    public static HotkeyCombination? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        uint modifiers = 0;
        string? key = null;

        foreach (var rawPart in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var part = rawPart.Trim();
            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= NativeMethods.MOD_CONTROL;
                    continue;
                case "alt":
                    modifiers |= NativeMethods.MOD_ALT;
                    continue;
                case "shift":
                    modifiers |= NativeMethods.MOD_SHIFT;
                    continue;
                case "win":
                case "windows":
                    modifiers |= NativeMethods.MOD_WIN;
                    continue;
            }

            key = part;
        }

        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        if (key.Length == 1)
        {
            var scan = NativeMethods.VkKeyScan(key[0]);
            if (scan == -1)
            {
                return null;
            }

            var vk = (uint)(scan & 0xFF);
            if ((scan & 0x0100) != 0)
            {
                modifiers |= NativeMethods.MOD_SHIFT;
            }

            return new HotkeyCombination(modifiers, vk);
        }

        if (key.StartsWith("F", StringComparison.OrdinalIgnoreCase) && int.TryParse(key.AsSpan(1), out var fNumber) && fNumber is >= 1 and <= 24)
        {
            return new HotkeyCombination(modifiers, (uint)(0x6F + fNumber));
        }

        return NamedKeys.TryGetValue(key, out var namedVk)
            ? new HotkeyCombination(modifiers, namedVk)
            : null;
    }

    public static string Format(uint modifiers, uint virtualKey)
    {
        var parts = new List<string>();
        if ((modifiers & NativeMethods.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((modifiers & NativeMethods.MOD_ALT) != 0) parts.Add("Alt");
        if ((modifiers & NativeMethods.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((modifiers & NativeMethods.MOD_WIN) != 0) parts.Add("Win");

        var name = NamedKeys.FirstOrDefault(k => k.Value == virtualKey).Key;
        if (string.IsNullOrEmpty(name))
        {
            if (virtualKey is > 0x6F and <= 0x87)
            {
                name = "F" + (virtualKey - 0x6F);
            }
            else
            {
                name = ((Keys)virtualKey).ToString();
            }
        }

        parts.Add(name);
        return string.Join("+", parts);
    }
}

/// <summary>全局热键宿主：使用一个不可见的原生窗口接收 WM_HOTKEY 消息。</summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private sealed class MessageWindow : NativeWindow
    {
        public event EventHandler<int>? HotkeyPressed;

        public MessageWindow()
        {
            CreateHandle(new CreateParams { Caption = "DeskFlowHotkeySink" });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == NativeMethods.WM_HOTKEY)
            {
                HotkeyPressed?.Invoke(this, m.WParam.ToInt32());
            }

            base.WndProc(ref m);
        }
    }

    private readonly MessageWindow _window = new();
    private readonly Dictionary<int, HotkeyCombination> _registered = new();
    private bool _disposed;

    public const int ToggleBoxesId = 1;
    public const int OrganizeId = 2;

    public event EventHandler<int>? HotkeyPressed;

    public GlobalHotkeyService()
    {
        _window.HotkeyPressed += (_, id) => HotkeyPressed?.Invoke(this, id);
    }

    public bool Register(int id, string hotkeyText)
    {
        Unregister(id);

        var combination = HotkeyParser.Parse(hotkeyText);
        if (combination is null)
        {
            return false;
        }

        if (!NativeMethods.RegisterHotKey(_window.Handle, id, combination.Modifiers, combination.VirtualKey))
        {
            return false;
        }

        _registered[id] = combination;
        return true;
    }

    public void Unregister(int id)
    {
        if (!_registered.ContainsKey(id))
        {
            return;
        }

        NativeMethods.UnregisterHotKey(_window.Handle, id);
        _registered.Remove(id);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var id in _registered.Keys.ToList())
        {
            NativeMethods.UnregisterHotKey(_window.Handle, id);
        }

        _registered.Clear();
        _window.DestroyHandle();
        _disposed = true;
    }
}
