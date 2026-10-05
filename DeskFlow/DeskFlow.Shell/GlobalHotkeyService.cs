using System.Runtime.InteropServices;
using System.Windows.Forms;
using DeskFlow.Core.Services;

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

        foreach (var rawPart in text.Split('+', StringSplitOptions.TrimEntries))
        {
            var part = rawPart.Trim();
            if (part.Length == 0) return null;
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

            if (key is not null) return null;
            key = part;
        }

        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        if (key.Length == 1)
        {
            // Uppercase letters describe a key, not an implicit Shift modifier.
            if (char.IsAsciiLetter(key[0]))
                return new HotkeyCombination(modifiers, char.ToUpperInvariant(key[0]));
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
            if ((scan & 0x0200) != 0) modifiers |= NativeMethods.MOD_CONTROL;
            if ((scan & 0x0400) != 0) modifiers |= NativeMethods.MOD_ALT;

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
    private sealed record Binding(int NativeId, HotkeyCombination Combination);
    private Dictionary<int, Binding> _registered = new();
    private int _nextNativeId = 100;
    private bool _disposed;

    public const int ToggleBoxesId = 1;
    public const int OrganizeId = 2;

    public event EventHandler<int>? HotkeyPressed;

    public GlobalHotkeyService()
    {
        _window.HotkeyPressed += (_, id) =>
        {
            foreach (var pair in _registered)
                if (pair.Value.NativeId == id) { HotkeyPressed?.Invoke(this, pair.Key); break; }
        };
    }

    public bool Register(int id, string hotkeyText)
    {
        return TryApplyInternal(new Dictionary<int, string> { [id] = hotkeyText }, out _, merge: true);
    }

    public IReadOnlyDictionary<int, HotkeyCombination> Active =>
        _registered.ToDictionary(p => p.Key, p => p.Value.Combination);

    public bool TryApply(IReadOnlyDictionary<int, string> desired, out string error)
        => TryApplyInternal(desired, out error, merge: false);

    private bool TryApplyInternal(IReadOnlyDictionary<int, string> desired, out string error, bool merge)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        error = string.Empty;
        var parsed = merge ? Active.Where(p => !desired.ContainsKey(p.Key)).ToDictionary(p => p.Key, p => p.Value)
            : new Dictionary<int, HotkeyCombination>();
        foreach (var pair in desired)
        {
            if (string.IsNullOrWhiteSpace(pair.Value)) continue; // blank explicitly disables this action
            var combination = HotkeyParser.Parse(pair.Value);
            var action = pair.Key == ToggleBoxesId ? "显示 / 隐藏盒子" : "一键整理";
            if (combination is null) { error = $"{action}的快捷键格式无效。示例：Ctrl+Alt+O；留空可禁用。"; return false; }
            if (combination.VirtualKey == 0x7B) { error = "F12 由系统保留，请选择其他按键。"; return false; }
            if (parsed.ContainsValue(combination)) { error = $"快捷键 {combination} 被分配给多个操作，请使用不同组合。"; return false; }
            parsed.Add(pair.Key, combination);
        }

        var staged = new List<Binding>();
        var next = new Dictionary<int, Binding>();
        foreach (var pair in parsed)
        {
            var binding = _registered.Values.FirstOrDefault(b => b.Combination == pair.Value);
            if (binding is null)
            {
                // Use a fresh native ID while retaining every previously working registration.
                do { _nextNativeId = _nextNativeId >= 0xBFFF ? 100 : _nextNativeId + 1; }
                while (_registered.Values.Concat(staged).Any(b => b.NativeId == _nextNativeId));
                if (!NativeMethods.RegisterHotKey(_window.Handle, _nextNativeId,
                        pair.Value.Modifiers | 0x4000, pair.Value.VirtualKey))
                {
                    var code = Marshal.GetLastWin32Error();
                    foreach (var added in staged) NativeMethods.UnregisterHotKey(_window.Handle, added.NativeId);
                    error = $"快捷键 {pair.Value} 无法注册（可能被其他程序或系统占用，错误码 {code}）。请选择其他组合，原有热键仍然有效。";
                    DiagnosticLog.Error("Hotkeys", error);
                    return false;
                }
                binding = new Binding(_nextNativeId, pair.Value);
                staged.Add(binding);
            }
            next.Add(pair.Key, binding);
        }
        foreach (var old in _registered.Values)
            if (!next.Values.Contains(old)) NativeMethods.UnregisterHotKey(_window.Handle, old.NativeId);
        _registered = next;
        return true;
    }

    public void Unregister(int id)
    {
        if (!_registered.ContainsKey(id))
        {
            return;
        }

        NativeMethods.UnregisterHotKey(_window.Handle, _registered[id].NativeId);
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
            NativeMethods.UnregisterHotKey(_window.Handle, _registered[id].NativeId);
        }

        _registered.Clear();
        _window.DestroyHandle();
        _disposed = true;
    }
}
