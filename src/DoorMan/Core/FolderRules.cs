namespace DoorMan.Core;

/// <summary>Checks done first so that no folder is encrypted that would leave Windows or the app unusable.</summary>
internal static class FolderRules
{
    public static void EnsureCanProtect(string folder)
    {
        if (!Directory.Exists(folder))
            throw new UserFacingException(Strings.FolderUnavailable);

        var info = new DirectoryInfo(folder);
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new UserFacingException(Strings.CannotProtectLinks);
        if (PathUtil.AreSame(Path.GetPathRoot(folder) ?? folder, folder))
            throw new UserFacingException(Strings.CannotProtectDriveRoot);

        // System folders: neither the folder nor anything inside it can be protected.
        string[] systemTrees =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ];
        foreach (var tree in systemTrees.Where(t => t.Length > 0))
            if (PathUtil.IsSameOrInside(folder, tree))
                throw new UserFacingException(Strings.CannotProtectSystemFolders);

        // Folders that contain something essential (the user profile, Windows, this app…).
        string[] mustStayOutside =
        [
            .. systemTrees,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            AppContext.BaseDirectory,
        ];
        foreach (var path in mustStayOutside.Where(p => p.Length > 0))
            if (PathUtil.IsSameOrInside(path, folder))
                throw new UserFacingException(PathUtil.AreSame(path, AppContext.BaseDirectory)
                    ? Strings.CannotProtectInstallFolder
                    : Strings.CannotProtectContainsSystem);

        // Known personal folders: protecting them whole would break other apps.
        Environment.SpecialFolder[] known =
        [
            Environment.SpecialFolder.Desktop, Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyMusic,
            Environment.SpecialFolder.MyVideos,
        ];
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        foreach (var path in known.Select(Environment.GetFolderPath).Append(downloads).Where(p => p.Length > 0))
            if (PathUtil.AreSame(path, folder))
                throw new UserFacingException(Strings.CannotProtectPersonalFolder);

        EnsureWritable(folder);
    }

    static void EnsureWritable(string folder)
    {
        var probe = Path.Combine(folder, $".doorman-probe-{Guid.NewGuid():N}.tmp");
        try
        {
            using var _ = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (UnauthorizedAccessException)
        {
            throw new UserFacingException(Strings.NoWritePermission);
        }
        catch (IOException ex)
        {
            throw new UserFacingException(Strings.CannotWrite(ex.Message));
        }
    }
}
