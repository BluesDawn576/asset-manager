using AssetManager.Application.Library;
using AssetManager.Domain.Library;

namespace AssetManager.Infrastructure.Storage.Library;

public sealed class FileAssetActivityLog : IAssetActivityLog
{
    public async Task AppendAsync(
        LibraryLocation location,
        string message,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(location.LogsPath);

        var logLine = $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}";
        var logPath = Path.Combine(location.LogsPath, "activity.log");
        await File.AppendAllTextAsync(logPath, logLine, cancellationToken);
    }
}
