using AssetManager.Domain.Library;

namespace AssetManager.Application.Library;

public sealed class BuiltInAssetTypeResolver : IAssetTypeResolver
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bmp",
        ".gif",
        ".jpeg",
        ".jpg",
        ".png",
        ".tif",
        ".tiff",
        ".webp"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".avi",
        ".m4v",
        ".mkv",
        ".mov",
        ".mp4",
        ".mpeg",
        ".mpg",
        ".webm",
        ".wmv"
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".aac",
        ".flac",
        ".m4a",
        ".mp3",
        ".ogg",
        ".wav",
        ".wma"
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csv",
        ".json",
        ".log",
        ".md",
        ".rtf",
        ".text",
        ".txt",
        ".xml",
        ".yaml",
        ".yml"
    };

    public AssetTypeId Resolve(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return AssetTypeId.Unknown;
        }

        var normalizedExtension = extension.StartsWith(".", StringComparison.Ordinal)
            ? extension
            : "." + extension;

        if (ImageExtensions.Contains(normalizedExtension))
        {
            return AssetTypeId.Image;
        }

        if (VideoExtensions.Contains(normalizedExtension))
        {
            return AssetTypeId.Video;
        }

        return AudioExtensions.Contains(normalizedExtension)
            ? AssetTypeId.Audio
            : TextExtensions.Contains(normalizedExtension)
                ? AssetTypeId.Text
                : AssetTypeId.Unknown;
    }
}
