namespace FileTracert.Host.Configuration;

/// <summary>Resolves the SQLite database file path and ensures its folder exists.</summary>
public static class DatabaseLocation
{
    /// <summary>
    /// Returns the absolute database file path. Uses the explicit override when
    /// set, otherwise <c>%LOCALAPPDATA%\FileTracert\filetracert.db</c>. The
    /// containing directory is created if missing.
    /// </summary>
    public static string Resolve(FileTracertOptions options)
    {
        var path = string.IsNullOrWhiteSpace(options.DatabasePath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FileTracert",
                "filetracert.db")
            : options.DatabasePath;

        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        return path;
    }

    /// <summary>Builds the SQLite connection string for a resolved file path.</summary>
    public static string ConnectionString(string databasePath) => $"Data Source={databasePath}";
}
