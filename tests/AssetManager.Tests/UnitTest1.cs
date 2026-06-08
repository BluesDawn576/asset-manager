using AssetManager.Domain;

namespace AssetManager.Tests;

public class UnitTest1
{
    [Fact]
    public void AssetTransferItem_UsesFileNameForDisplay()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var tempFile = Path.Combine(tempRoot, "demo", "image.png");
        Directory.CreateDirectory(Path.GetDirectoryName(tempFile)!);
        File.WriteAllText(tempFile, "demo");

        try
        {
            var item = new AssetTransferItem(tempFile);

            Assert.Equal(Path.GetFullPath(tempFile), item.SourcePath);
            Assert.Equal("image.png", item.DisplayName);
            Assert.False(item.IsDirectory);
            Assert.Equal("File", item.ItemKind);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void AssetTransferItem_RecognizesDirectories()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var item = new AssetTransferItem(tempRoot);

            Assert.Equal(Path.GetFullPath(tempRoot), item.SourcePath);
            Assert.True(item.IsDirectory);
            Assert.Equal("Folder", item.ItemKind);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }
}
