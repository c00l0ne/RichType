using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Kazrich.App;

internal sealed class InputObserver : IDisposable
{
    private readonly Native.HookProc keyboardProc, mouseProc;
    private readonly nint keyboardHook, mouseHook;
    internal event Action<Native.KeyboardData>? KeyPressed;
    internal event Action? PointerChanged;
    internal InputObserver()
    {
        keyboardProc = OnKeyboard;
        mouseProc = OnMouse;
        keyboardHook = Native.SetWindowsHookEx(13, keyboardProc, Native.GetModuleHandle(null), 0);
        mouseHook = Native.SetWindowsHookEx(14, mouseProc, Native.GetModuleHandle(null), 0);
        if (keyboardHook == 0 || mouseHook == 0)
        {
            Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось наблюдать ввод.");
        }
    }
    private nint OnKeyboard(int code, nint message, nint data)
    {
        if (code >= 0 && (message == 0x100 || message == 0x104))
        {
            var key = Marshal.PtrToStructure<Native.KeyboardData>(data);
            // Own replacement is ignored. Other injected input invalidates the pending operation.
            if (key.Extra != Native.Marker) KeyPressed?.Invoke(key);
        }
        return Native.CallNextHookEx(keyboardHook, code, message, data);
    }
    private nint OnMouse(int code, nint message, nint data)
    {
        if (code >= 0 && message is not 0x200 and not 0x202 and not 0x205 and not 0x208)
            PointerChanged?.Invoke();
        return Native.CallNextHookEx(mouseHook, code, message, data);
    }
    public void Dispose()
    {
        if (keyboardHook != 0) Native.UnhookWindowsHookEx(keyboardHook);
        if (mouseHook != 0) Native.UnhookWindowsHookEx(mouseHook);
    }
}
