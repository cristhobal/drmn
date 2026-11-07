using System.Runtime.InteropServices;

namespace DoorMan.Shell;

/// <summary>Brings a window to the front even when the process is in the background.</summary>
internal static class ForegroundHelper
{
    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hwnd, IntPtr processId);

    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern bool BringWindowToTop(IntPtr hwnd);

    public static void Bring(IntPtr hwnd)
    {
        uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
        uint current = GetCurrentThreadId();
        if (foregroundThread == 0 || foregroundThread == current)
        {
            SetForegroundWindow(hwnd);
            return;
        }
        // Windows only lets a thread come to the front if it shares input with the active window.
        AttachThreadInput(current, foregroundThread, true);
        BringWindowToTop(hwnd);
        SetForegroundWindow(hwnd);
        AttachThreadInput(current, foregroundThread, false);
    }
}
