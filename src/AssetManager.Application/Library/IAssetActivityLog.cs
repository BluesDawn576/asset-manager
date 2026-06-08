using AssetManager.Domain.Library;

namespace AssetManager.Application.Library;

public interface IAssetActivityLog
{
    Task AppendAsync(LibraryLocation location, string message, CancellationToken cancellationToken = default);
}
