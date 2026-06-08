using AssetManager.Domain.Library;

namespace AssetManager.Application.Library;

public interface IAssetTypeResolver
{
    AssetTypeId Resolve(string? extension);
}
