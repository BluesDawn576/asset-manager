using System.IO;

namespace AssetManager.Domain;

public sealed record AssetTransferItem
{
    public AssetTransferItem(string sourcePath)
    {
        SourcePath = Path.GetFullPath(sourcePath);
        IsDirectory = Directory.Exists(SourcePath);
        DisplayName = GetDisplayName(SourcePath);
    }

    public string SourcePath { get; }

    public string DisplayName { get; }

    public bool IsDirectory { get; }

    public string ItemKind => IsDirectory ? "Folder" : "File";

    private static string GetDisplayName(string sourcePath)
    {
        var trimmedPath = sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var displayName = Path.GetFileName(trimmedPath);
        return string.IsNullOrWhiteSpace(displayName) ? trimmedPath : displayName;
    }
}
