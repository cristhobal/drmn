using System.Diagnostics;
using DoorMan.Core;

namespace DoorMan.Shell;

internal static class ExplorerWindows
{
    static readonly Guid ShellWindowsClsid = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");

    /// <summary>
    /// Muestra la carpeta. Navega en la ventana del Explorador desde la que se intentó abrir
    /// (<paramref name="preferred"/>) o, si no, en una que esté en la carpeta padre o en la propia
    /// carpeta; si no hay ninguna, abre una nueva.
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
                    continue; // No es una ventana de carpetas (p. ej. el Panel de control).
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
            // Si la automatización del shell falla, basta con abrir una ventana nueva.
        }
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = false })?.Dispose();
    }

    /// <summary>
    /// Navega a la carpeta recién desbloqueada y espera a que la ventana llegue. El Explorador puede
    /// tener aún en caché la carpeta como bloqueada y rechazar el primer intento; en ese caso se
    /// recarga la vista (como F5) y se vuelve a intentar.
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
                    // La ventana está a mitad de navegación.
                }
            }
        }
        return false;
    }

    /// <summary>Saca a la carpeta padre las ventanas del Explorador que estén dentro de la carpeta (antes de bloquearla).</summary>
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
    /// Recarga (como F5) las ventanas que muestran la carpeta padre después de bloquear. Sin esto, el
    /// Explorador puede seguir usando la carpeta tal como era antes de bloquearla y entrar sin pedir
    /// la contraseña durante unos segundos.
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
                    // Ventanas que no muestran una carpeta del disco.
                }
            }
        }
        catch (Exception)
        {
            // Sin automatización del shell no se puede ajustar ninguna ventana; no es imprescindible.
        }
    }
}
