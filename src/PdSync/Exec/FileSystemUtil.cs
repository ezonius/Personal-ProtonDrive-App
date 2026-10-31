namespace PdSync.Exec;

/// <summary>Small move helpers with a cross-volume fallback.</summary>
public static class FileSystemUtil
{
    public static void MoveFile(string source, string destination, bool overwrite)
    {
        try
        {
            File.Move(source, destination, overwrite);
        }
        catch (IOException)
        {
            File.Copy(source, destination, overwrite);
            File.Delete(source);
        }
    }

    public static void MoveDirectory(string source, string destination)
    {
        try
        {
            Directory.Move(source, destination);
        }
        catch (IOException)
        {
            CopyDirectory(source, destination);
            Directory.Delete(source, recursive: true);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var target = Path.Combine(destination, Path.GetFileName(file));
            File.Copy(file, target, overwrite: false);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            var target = Path.Combine(destination, Path.GetFileName(directory));
            CopyDirectory(directory, target);
        }
    }
}
