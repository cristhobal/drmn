namespace DoorMan.Core;

/// <summary>Error cuyo mensaje está pensado para mostrarse tal cual al usuario.</summary>
internal sealed class UserFacingException(string message) : Exception(message);

internal readonly record struct ProgressInfo(long Done, long Total, string? CurrentItem);

/// <summary>Acumula el avance y lo notifica como mucho cada 50 ms.</summary>
internal sealed class ProgressTracker(IProgress<ProgressInfo>? sink, long total)
{
    long _done;
    long _lastReport = long.MinValue;
    string? _item;

    public void Advance(long bytes) => Report(bytes, null);

    public void SetItem(string item) => Report(0, item);

    void Report(long bytes, string? item)
    {
        if (sink is null)
            return;
        _done += bytes;
        bool itemChanged = item is not null && item != _item;
        if (item is not null)
            _item = item;
        long now = Environment.TickCount64;
        if (itemChanged || now - _lastReport >= 50)
        {
            _lastReport = now;
            sink.Report(new ProgressInfo(Math.Min(_done, total), total, _item));
        }
    }
}

internal static class PathUtil
{
    public static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim().Trim('"')));

    public static bool AreSame(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>¿Es <paramref name="path"/> igual a <paramref name="ancestor"/> o está dentro de ella?</summary>
    public static bool IsSameOrInside(string path, string ancestor)
    {
        path = Normalize(path);
        ancestor = Normalize(ancestor);
        if (string.Equals(path, ancestor, StringComparison.OrdinalIgnoreCase))
            return true;
        var prefix = Path.EndsInDirectorySeparator(ancestor) ? ancestor : ancestor + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
