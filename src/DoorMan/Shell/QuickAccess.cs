using DoorMan.Core;

namespace DoorMan.Shell;

/// <summary>
/// Quick Access keeps its own reference to each folder, taken when it was added. If it was added while unlocked,
/// the reference does not go through DoorMan's extension and Explorer would enter the locked folder without asking
/// for the password. When locking, the folder is pinned again (so that Windows renews the reference) or, if it only
/// showed up as a frequent folder, removed from the list.
/// </summary>
internal static class QuickAccess
{
    const string Namespace = "shell:::{679f85cb-0220-4080-b29b-5540cc05aab6}";

    public static void Refresh(string folder)
    {
        // Shell verbs are asynchronous and need a live STA thread while they finish.
        var thread = new Thread(() => RefreshNow(folder)) { IsBackground = true, Name = "DoorMan Quick Access" };
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
            // Quick Access is a convenience: if this fails, the folder is encrypted anyway.
        }
    }
}
