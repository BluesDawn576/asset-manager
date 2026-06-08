using System.Collections.Specialized;
using System.IO;
using System.Windows;

namespace AssetManager.Infrastructure.Windows;

public static class WindowsFileTransferService
{
    public static IReadOnlyList<string> ExtractFilePaths(IDataObject dataObject)
    {
        if (!dataObject.GetDataPresent(DataFormats.FileDrop))
        {
            return Array.Empty<string>();
        }

        if (dataObject.GetData(DataFormats.FileDrop) is not string[] rawPaths || rawPaths.Length == 0)
        {
            return Array.Empty<string>();
        }

        return NormalizeExistingPaths(rawPaths);
    }

    public static DataObject CreateFileDropDataObject(IEnumerable<string> paths)
    {
        var fileDropList = new StringCollection();

        foreach (var path in NormalizeExistingPaths(paths))
        {
            fileDropList.Add(path);
        }

        var dataObject = new DataObject();
        dataObject.SetFileDropList(fileDropList);
        return dataObject;
    }

    public static void CopyToClipboard(IEnumerable<string> paths)
    {
        var fileDropList = new StringCollection();

        foreach (var path in NormalizeExistingPaths(paths))
        {
            fileDropList.Add(path);
        }

        Clipboard.SetFileDropList(fileDropList);
    }

    private static IReadOnlyList<string> NormalizeExistingPaths(IEnumerable<string> paths)
    {
        var uniquePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
            {
                continue;
            }

            uniquePaths.Add(fullPath);
        }

        return uniquePaths.ToArray();
    }
}
