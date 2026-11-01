using DoorMan.Core;

namespace DoorMan.Shell;

/// <summary>
/// Acceso rápido guarda su propia referencia a cada carpeta, tomada cuando se añadió. Si se añadió
/// estando desbloqueada, la referencia no pasa por la extensión de DoorMan y el Explorador entraría
/// en la carpeta bloqueada sin pedir la contraseña. Al bloquear, la carpeta se vuelve a anclar (para
/// que Windows renueve la referencia) o, si solo aparecía como frecuente, se quita de la lista.
/// </summary>
internal static class QuickAccess
{
    const string Namespace = "shell:::{679f85cb-0220-4080-b29b-5540cc05aab6}";

    public static void Refresh(string folder)
    {
        // Las órdenes del shell son asíncronas y necesitan un hilo STA vivo mientras terminan.
        var thread = new Thread(() => RefreshNow(folder)) { IsBackground = true, Name = "DoorMan Acceso rápido" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    static void RefreshNow(string folder)
    {
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
            dynamic? entry = null;
            foreach (dynamic item in shell.Namespace(Namespace).Items())
            {
                string? path = item.Path;
                if ((bool)item.IsFolder && path is not null && PathUtil.AreSame(path, folder))
                {
                    entry = item;
                    break;
                }
            }
            if (entry is null)
                return;

            if (entry.ExtendedProperty("System.Home.IsPinned") is true)
            {
                entry.InvokeVerb("unpinfromhome");
                Thread.Sleep(1500);
                dynamic parent = shell.Namespace(Path.GetDirectoryName(folder));
                parent.ParseName(Path.GetFileName(folder)).InvokeVerb("pintohome");
            }
            else
            {
                entry.InvokeVerb("removefromhome");
            }
            Thread.Sleep(3000);
        }
        catch (Exception)
        {
            // Acceso rápido es una comodidad: si falla, la carpeta sigue cifrada igualmente.
        }
    }
}
