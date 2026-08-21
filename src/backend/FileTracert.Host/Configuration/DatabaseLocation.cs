namespace FileTracert.Host.Configuration;

/// <summary>Resolves the SQLite database file path and ensures its folder exists.</summary>
/// <remarks>
/// <para><b>Where the catalog lives, and why it is machine-wide.</b> The product ships as a
/// Windows Service (CLAUDE.md §3) running as <c>LocalSystem</c>, and a per-user folder is not a
/// place a service can find: <c>%LOCALAPPDATA%</c> for <c>LocalSystem</c> resolves to
/// <c>C:\Windows\System32\config\systemprofile\AppData\Local</c>. Keeping the old default would
/// mean the service silently starts on an <em>empty</em> catalog while the one the user built
/// from a console run sits in their own profile — two databases, no error, and the data the user
/// cares about in the one nobody is serving.</para>
/// <para>So the default is <c>%ProgramData%\FileTracert</c>: one catalog per machine, the same
/// file whether the host runs as the service, as an elevated console app, or under a different
/// account. The install script grants the folder to the users of the machine precisely because
/// the writer changes; see <c>deploy/README.md</c>.</para>
/// <para>A database written by the previous default is <em>not</em> migrated by code. Moving a
/// user's catalog is a one-off operation on their data, done deliberately with the service
/// stopped and the original left in place — not something a host does on startup, where a
/// half-copied file is a corrupted catalog.</para>
/// </remarks>
public static class DatabaseLocation
{
    /// <summary>Folder name used under <c>%ProgramData%</c> and file name of the catalog.</summary>
    private const string FolderName = "FileTracert";
    private const string FileName = "filetracert.db";

    /// <summary>
    /// The default database path, <c>%ProgramData%\FileTracert\filetracert.db</c>. Machine-wide
    /// on purpose (see the type remarks): the service and any console run must open the same
    /// catalog regardless of which account they run under. Does not touch the filesystem.
    /// </summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        FolderName,
        FileName);

    /// <summary>
    /// Returns the absolute database file path: the explicit override when set, otherwise
    /// <see cref="DefaultPath"/>. The containing directory is created if missing.
    /// </summary>
    public static string Resolve(FileTracertOptions options)
    {
        var path = string.IsNullOrWhiteSpace(options.DatabasePath)
            ? DefaultPath
            : options.DatabasePath;

        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        return path;
    }

    /// <summary>
    /// Dedicated log database path, derived from the (resolved) main database path:
    /// same directory, <c>&lt;name&gt;-logs.db</c>. Kept separate from the main DB so
    /// logging is independent of its lifecycle and write contention.
    /// </summary>
    public static string ResolveLogs(string mainDatabasePath)
    {
        var full = Path.GetFullPath(mainDatabasePath);
        var dir = Path.GetDirectoryName(full) ?? ".";
        var name = Path.GetFileNameWithoutExtension(full);
        return Path.Combine(dir, $"{name}-logs.db");
    }

    /// <summary>Builds the SQLite connection string for a resolved file path.</summary>
    public static string ConnectionString(string databasePath) => $"Data Source={databasePath}";
}
