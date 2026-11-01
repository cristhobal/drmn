namespace CarpetaProtegida.Core;

/// <summary>Comprobaciones previas para no cifrar carpetas que dejarían Windows o la aplicación inservibles.</summary>
internal static class FolderRules
{
    public static void EnsureCanProtect(string folder)
    {
        if (!Directory.Exists(folder))
            throw new UserFacingException("La carpeta no existe o no está disponible.");

        var info = new DirectoryInfo(folder);
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new UserFacingException("No se pueden proteger enlaces simbólicos ni puntos de unión.");
        if (PathUtil.AreSame(Path.GetPathRoot(folder) ?? folder, folder))
            throw new UserFacingException("No se puede proteger la raíz de una unidad.");

        // Carpetas del sistema: no se puede proteger ni la carpeta ni nada que esté dentro.
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
                throw new UserFacingException("No se pueden proteger carpetas del sistema ni de configuración de programas.");

        // Carpetas que contienen algo imprescindible (el perfil de usuario, Windows, esta aplicación…).
        string[] mustStayOutside =
        [
            .. systemTrees,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            AppContext.BaseDirectory,
        ];
        foreach (var path in mustStayOutside.Where(p => p.Length > 0))
            if (PathUtil.IsSameOrInside(path, folder))
                throw new UserFacingException(PathUtil.AreSame(path, AppContext.BaseDirectory)
                    ? "No se puede proteger la carpeta donde está instalada Carpeta Protegida."
                    : "No se puede proteger esta carpeta porque contiene carpetas del sistema o tu perfil de usuario.");

        // Carpetas personales conocidas: protegerlas enteras rompería otros programas.
        Environment.SpecialFolder[] known =
        [
            Environment.SpecialFolder.Desktop, Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyMusic,
            Environment.SpecialFolder.MyVideos,
        ];
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        foreach (var path in known.Select(Environment.GetFolderPath).Append(downloads).Where(p => p.Length > 0))
            if (PathUtil.AreSame(path, folder))
                throw new UserFacingException("No se puede proteger una carpeta personal de Windows completa. Protege una subcarpeta en su lugar.");

        EnsureWritable(folder);
    }

    static void EnsureWritable(string folder)
    {
        var probe = Path.Combine(folder, $".cp-prueba-{Guid.NewGuid():N}.tmp");
        try
        {
            using var _ = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (UnauthorizedAccessException)
        {
            throw new UserFacingException("No tienes permiso de escritura en esta carpeta.");
        }
        catch (IOException ex)
        {
            throw new UserFacingException($"No se puede escribir en esta carpeta: {ex.Message}");
        }
    }
}
