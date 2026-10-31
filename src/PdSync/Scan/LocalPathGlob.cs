namespace PdSync.Scan;

/// <summary>
/// The CLI expands local path arguments as glob patterns (S14). These helpers
/// escape a literal path before it is handed to <c>fs upload</c>. A path that
/// cannot be represented safely (brace expansion) is staged under a simple
/// temporary name by the executor instead.
/// </summary>
public static class LocalPathGlob
{
    public static string Escape(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;

        // Escape the characters that are special to the CLI's glob matcher.
        // '[' must be escaped first; ']' alone is literal.
        return path
            .Replace("[", "[[]", StringComparison.Ordinal)
            .Replace("*", "[*]", StringComparison.Ordinal)
            .Replace("?", "[?]", StringComparison.Ordinal);
    }

    public static bool RequiresStaging(string path)
    {
        // A brace expression such as "{a,b}" is expanded by the CLI and cannot
        // be escaped reliably. Stage the file under a plain temporary name.
        var name = Path.GetFileName(path);
        return name.Contains('{', StringComparison.Ordinal) &&
               name.Contains('}', StringComparison.Ordinal) &&
               name.Contains(',', StringComparison.Ordinal);
    }
}
