namespace AssetManager.Domain.Library;

public sealed record LibraryLocation
{
    public const string ManagementDirectoryName = ".asset-manager";
    public const string DatabaseFileName = "asset-manager.db";
    public const string LogsDirectoryName = "logs";
    public const string TempDirectoryName = "temp";

    private LibraryLocation(string rootPath)
    {
        RootPath = rootPath;
        ManagementPath = Path.Combine(rootPath, ManagementDirectoryName);
        DatabasePath = Path.Combine(ManagementPath, DatabaseFileName);
        LogsPath = Path.Combine(ManagementPath, LogsDirectoryName);
        TempPath = Path.Combine(ManagementPath, TempDirectoryName);
    }

    public string RootPath { get; }

    public string ManagementPath { get; }

    public string DatabasePath { get; }

    public string LogsPath { get; }

    public string TempPath { get; }

    public static LibraryLocation Create(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new ArgumentException("Library root path is required.", nameof(rootPath));
        }

        return new LibraryLocation(Path.GetFullPath(rootPath));
    }

    public bool IsManagementPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return fullPath.Equals(ManagementPath, StringComparison.OrdinalIgnoreCase)
               || fullPath.StartsWith(ManagementPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
               || fullPath.StartsWith(ManagementPath + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
