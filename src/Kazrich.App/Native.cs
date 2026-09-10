using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Kazrich.App;

internal static class Native
{
    internal const uint Marker = 0x4B415A52;
    internal delegate nint HookProc(int code, nint message, nint data);
    [StructLayout(LayoutKind.Sequential)] internal struct KeyboardData
    { public uint Vk, Scan, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect
    { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct GuiInfo
    {
        public uint Size, Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public Rect CaretRect;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] internal struct InputUnion
    {
        [FieldOffset(0)] public KeyInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct KeyInput
    { public ushort Vk, Scan; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] internal struct MouseInput
    { public int X, Y; public uint MouseData, Flags, Time; public nuint Extra; }

    [DllImport("user32.dll", SetLastError = true)] internal static extern nint SetWindowsHookEx(int id, HookProc proc, nint module, uint thread);
    [DllImport("user32.dll")] internal static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] internal static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] internal static extern nint GetKeyboardLayout(uint thread);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] internal static extern short GetKeyState(int key);
    [DllImport("user32.dll")] internal static extern bool GetGUIThreadInfo(uint thread, ref GuiInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int ToUnicodeEx(uint key, uint scan, byte[] state, StringBuilder text, int length, uint flags, nint layout);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] internal static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(nint window, int command);

    internal static bool Down(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
    internal static bool ModifiersDown() => Down(0x10) || Down(0x11) || Down(0x12) || Down(0x5B) || Down(0x5C);
    internal static GuiInfo GetGui(nint window)
    {
        var data = new GuiInfo { Size = (uint)Marshal.SizeOf<GuiInfo>() };
        var thread = GetWindowThreadProcessId(window, out _);
        GetGUIThreadInfo(thread, ref data);
        return data;
    }
    internal static bool SameCaret(GuiInfo a, GuiInfo b) => a.Focus == b.Focus && a.Caret == b.Caret &&
        a.CaretRect.Left == b.CaretRect.Left && a.CaretRect.Top == b.CaretRect.Top &&
        a.CaretRect.Right == b.CaretRect.Right && a.CaretRect.Bottom == b.CaretRect.Bottom;

    internal static char? Translate(KeyboardData data, nint window)
    {
        var state = new byte[256];
        for (var i = 0; i < 256; i++) if (Down(i)) state[i] = 0x80;
        state[data.Vk] = 0x80;
        state[0x14] = (byte)(GetKeyState(0x14) & 1);
        var thread = GetWindowThreadProcessId(window, out _);
        var text = new StringBuilder(8);
        var count = ToUnicodeEx(data.Vk, data.Scan, state, text, 8, 4, GetKeyboardLayout(thread));
        return count == 1 ? text[0] : null;
    }
    private static Input Key(ushort key, bool up) => new()
    {
        Type = 1, Data = new InputUnion { Keyboard = new KeyInput { Vk = key, Flags = up ? 2u : 0u, Extra = Marker } }
    };
    private static Input Unicode(char c, bool up) => new()
    {
        Type = 1, Data = new InputUnion { Keyboard = new KeyInput { Scan = c, Flags = up ? 6u : 4u, Extra = Marker } }
    };
    internal static bool ReplaceSuffix(int length, string replacement)
        => ReplaceBeforeTail(length, replacement, "");

    internal static bool ReplaceBeforeTail(int length, string replacement, string tail)
    {
        // Move around the later text rather than deleting/retyping it. One SendInput batch
        // keeps navigation, replacement and caret restoration together in the input stream.
        if (replacement.Contains('\r') || replacement.Contains('\n')) return false;
        var inputs = new List<Input>();
        foreach (var unused in tail) { inputs.Add(Key(0x25, false)); inputs.Add(Key(0x25, true)); }
        for (var i = 0; i < length; i++) { inputs.Add(Key(8, false)); inputs.Add(Key(8, true)); }
        foreach (var c in replacement) { inputs.Add(Unicode(c, false)); inputs.Add(Unicode(c, true)); }
        foreach (var unused in tail) { inputs.Add(Key(0x27, false)); inputs.Add(Key(0x27, true)); }
        return SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<Input>()) == inputs.Count;
    }

    internal static bool ReplaceWordAtCaret(int length, int caretOffset, string replacement, int resultingOffset)
    {
        if (replacement.Contains('\r') || replacement.Contains('\n')) return false;
        var inputs = new List<Input>();
        void Move(int distance)
        {
            var key = (ushort)(distance < 0 ? 0x25 : 0x27);
            for (var i = 0; i < Math.Abs(distance); i++) { inputs.Add(Key(key, false)); inputs.Add(Key(key, true)); }
        }
        Move(length - caretOffset);
        for (var i = 0; i < length; i++) { inputs.Add(Key(8, false)); inputs.Add(Key(8, true)); }
        foreach (var c in replacement) { inputs.Add(Unicode(c, false)); inputs.Add(Unicode(c, true)); }
        Move(resultingOffset - replacement.Length);
        return SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<Input>()) == inputs.Count;
    }
}
