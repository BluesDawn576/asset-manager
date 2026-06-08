using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using AssetManager.Domain.Library;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AssetManager.Infrastructure.Windows;

public sealed class WindowsThumbnailCacheService
{
    private const int ThumbnailPixelSize = 384;

    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _inFlightGenerations =
        new(StringComparer.OrdinalIgnoreCase);

    public Task<string?> GetOrCreateAsync(
        LibraryLocation location,
        AssetRecord asset,
        CancellationToken cancellationToken = default)
    {
        if (asset.TypeId != AssetTypeId.Image || asset.Status != AssetStatus.Available)
        {
            return Task.FromResult<string?>(null);
        }

        var sourcePath = asset.FullPath(location);
        if (!File.Exists(sourcePath))
        {
            return Task.FromResult<string?>(null);
        }

        var cachePath = GetCachePath(location, asset.ContentHash);
        if (IsUsableCacheFile(cachePath))
        {
            return Task.FromResult<string?>(cachePath);
        }

        var generation = _inFlightGenerations.GetOrAdd(
            cachePath,
            _ => new Lazy<Task<string?>>(
                () => GenerateThumbnailAsync(sourcePath, cachePath),
                LazyThreadSafetyMode.ExecutionAndPublication));

        return AwaitGenerationAsync(cachePath, generation, cancellationToken);
    }

    private async Task<string?> AwaitGenerationAsync(
        string cachePath,
        Lazy<Task<string?>> generation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await generation.Value.WaitAsync(cancellationToken);
        }
        finally
        {
            if (generation.IsValueCreated && generation.Value.IsCompleted)
            {
                _inFlightGenerations.TryRemove(cachePath, out _);
            }
        }
    }

    private static async Task<string?> GenerateThumbnailAsync(string sourcePath, string cachePath)
    {
        var directory = Path.GetDirectoryName(cachePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = cachePath + ".tmp";

        try
        {
            await using var sourceStream = new FileStream(
                sourcePath,
                new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.ReadWrite | FileShare.Delete,
                    Options = FileOptions.SequentialScan
                });

            var decoder = BitmapDecoder.Create(
                sourceStream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0)
            {
                return null;
            }

            var thumbnailSource = CreateThumbnailSource(decoder.Frames[0]);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(thumbnailSource));

            await using (var targetStream = new FileStream(
                             tempPath,
                             new FileStreamOptions
                             {
                                 Mode = FileMode.Create,
                                 Access = FileAccess.Write,
                                 Share = FileShare.None,
                                 Options = FileOptions.Asynchronous
                             }))
            {
                encoder.Save(targetStream);
                await targetStream.FlushAsync();
            }

            File.Move(tempPath, cachePath, overwrite: true);
            return cachePath;
        }
        catch
        {
            TryDeleteFile(tempPath);
            return IsUsableCacheFile(cachePath) ? cachePath : null;
        }
    }

    private static BitmapSource CreateThumbnailSource(BitmapSource source)
    {
        if (source.PixelWidth <= 0 || source.PixelHeight <= 0)
        {
            return source;
        }

        var longestSide = Math.Max(source.PixelWidth, source.PixelHeight);
        if (longestSide <= ThumbnailPixelSize)
        {
            if (source.CanFreeze)
            {
                source.Freeze();
            }

            return source;
        }

        var scale = (double)ThumbnailPixelSize / longestSide;
        var scaled = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        if (scaled.CanFreeze)
        {
            scaled.Freeze();
        }

        return scaled;
    }

    private static string GetCachePath(LibraryLocation location, string contentHash)
    {
        var normalizedHash = string.IsNullOrWhiteSpace(contentHash)
            ? "unknown"
            : contentHash.Trim().ToUpperInvariant();
        var prefix = normalizedHash.Length >= 2 ? normalizedHash[..2] : "00";

        return Path.Combine(location.ThumbnailsPath, prefix, normalizedHash + ".png");
    }

    private static bool IsUsableCacheFile(string path)
    {
        return File.Exists(path) && new FileInfo(path).Length > 0;
    }

    private static void TryDeleteFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
