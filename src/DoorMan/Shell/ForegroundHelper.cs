using System.Runtime.InteropServices;

namespace DoorMan.Shell;

/// <summary>Trae una ventana al frente aunque el proceso esté en segundo plano.</summary>
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
        // Windows solo deja pasar al frente a quien comparte la entrada con la ventana activa.
        AttachThreadInput(current, foregroundThread, true);
        BringWindowToTop(hwnd);
        SetForegroundWindow(hwnd);
        AttachThreadInput(current, foregroundThread, false);
    }
}
