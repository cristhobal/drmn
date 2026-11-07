using System.Diagnostics;
using DoorMan.Core;

namespace DoorMan.Shell;

internal static class ExplorerWindows
{
    static readonly Guid ShellWindowsClsid = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");

    /// <summary>
    /// Shows the folder. It navigates in the Explorer window where the user tried to open it
    /// (<paramref name="preferred"/>) or else in one showing the parent folder or the folder itself; if there is
    /// none, it opens a new one.
    /// </summary>
    public static void Show(string folder, IntPtr preferred = default)
    {
        try
        {
            var parent = Path.GetDirectoryName(folder);
            dynamic windows = Activator.CreateInstance(Type.GetTypeFromCLSID(ShellWindowsClsid)!)!;
            dynamic? match = null;
            int count = windows.Count;
            for (int i = 0; i < count; i++)
            {
                dynamic? window = windows.Item(i);
                if (window is null)
                    continue;
                string current;
                long hwnd;
                try
                {
                    current = window.Document.Folder.Self.Path;
                    hwnd = (long)window.HWND;
                }
                catch (Exception)
                {
                    continue; // Not a folder window (for example, Control Panel).
                }
                bool here = PathUtil.AreSame(current, folder) || (parent is not null && PathUtil.AreSame(current, parent));
                if (here && (match is null || hwnd == (long)preferred))
                    match = window;
            }
            if (match is not null)
            {
                ForegroundHelper.Bring(new IntPtr((long)match.HWND));
                if (NavigateAndWait(match, folder))
                    return;
            }
        }
        catch (Exception)
        {
            // If shell automation fails, opening a new window is enough.
        }
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = false })?.Dispose();
    }

    /// <summary>
    /// Navigates to the folder just unlocked and waits for the window to get there. Explorer may still have the
    /// folder cached as locked and reject the first attempt; in that case the view is reloaded (like F5) and the
    /// navigation is tried again.
    /// </summary>
    static bool NavigateAndWait(dynamic window, string folder)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0)
            {
                ShellNotify.ItemChanged(folder);
                try { window.Refresh(); } catch (Exception) { }
            }
            window.Navigate2(folder);
            for (int wait = 0; wait < 15; wait++)
            {
                Thread.Sleep(100);
                try
                {
                    if (PathUtil.AreSame((string)window.Document.Folder.Self.Path, folder))
                        return true;
                }
                catch (Exception)
                {
                    // The window is in the middle of navigating.
                }
            }
        }
        return false;
    }

    /// <summary>Moves Explorer windows that are inside the folder to its parent (before locking it).</summary>
    public static void LeaveFolder(string folder)
    {
        var parent = Path.GetDirectoryName(folder);
        if (parent is not null)
            ForEachWindow((window, current) =>
            {
                if (PathUtil.IsSameOrInside(current, folder))
                    window.Navigate2(parent);
            });
    }

    /// <summary>
    /// Reloads (like F5) the windows showing the parent folder after locking. Without this, Explorer may keep using
    /// the folder as it was before it was locked and enter it without asking for the password for a few seconds.
    /// </summary>
    public static void RefreshParentViews(string folder)
    {
        var parent = Path.GetDirectoryName(folder);
        if (parent is not null)
            ForEachWindow((window, current) =>
            {
                if (PathUtil.AreSame(current, parent))
                    window.Refresh();
            });
    }

    static void ForEachWindow(Action<dynamic, string> action)
    {
        try
        {
            dynamic windows = Activator.CreateInstance(Type.GetTypeFromCLSID(ShellWindowsClsid)!)!;
            int count = windows.Count;
            for (int i = 0; i < count; i++)
            {
                try
                {
                    dynamic? window = windows.Item(i);
                    string? current = window?.Document.Folder.Self.Path;
                    if (current is not null && Path.IsPathFullyQualified(current))
                        action(window!, current);
                }
                catch (Exception)
                {
                    // Windows that do not show a folder on disk.
                }
            }
        }
        catch (Exception)
        {
            // Without shell automation no window can be adjusted; it is not essential.
        }
    }
}
