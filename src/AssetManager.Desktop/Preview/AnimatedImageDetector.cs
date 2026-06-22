using System.IO;

namespace AssetManager.Desktop.Preview;

/// <summary>
/// Detects animated GIF files by reading image descriptors instead of trusting the GIF version header.
/// </summary>
public static class AnimatedImageDetector
{
    public static bool IsAnimatedGif(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var header = new byte[13];
            if (stream.Read(header, 0, header.Length) < header.Length)
            {
                return false;
            }

            return IsGif(header) && HasMultipleImageFrames(stream, header);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsGif(byte[] header)
    {
        return header[0] == 0x47 && header[1] == 0x49 && header[2] == 0x46
               && header[3] == 0x38
               && (header[4] == 0x37 || header[4] == 0x39)
               && header[5] == 0x61;
    }

    private static bool HasMultipleImageFrames(Stream stream, byte[] header)
    {
        if ((header[10] & 0x80) != 0)
        {
            var globalColorTableSize = 3 * (1 << ((header[10] & 0x07) + 1));
            stream.Position += globalColorTableSize;
        }

        var frameCount = 0;
        while (stream.Position < stream.Length)
        {
            var blockType = stream.ReadByte();
            if (blockType < 0 || blockType == 0x3B)
            {
                return false;
            }

            if (blockType == 0x2C)
            {
                frameCount++;
                if (frameCount > 1)
                {
                    return true;
                }

                if (!SkipImageData(stream))
                {
                    return false;
                }

                continue;
            }

            if (blockType == 0x21)
            {
                if (stream.ReadByte() < 0 || !SkipSubBlocks(stream))
                {
                    return false;
                }

                continue;
            }

            return false;
        }

        return false;
    }

    private static bool SkipImageData(Stream stream)
    {
        Span<byte> imageDescriptor = stackalloc byte[9];
        if (stream.Read(imageDescriptor) < imageDescriptor.Length)
        {
            return false;
        }

        if ((imageDescriptor[8] & 0x80) != 0)
        {
            var localColorTableSize = 3 * (1 << ((imageDescriptor[8] & 0x07) + 1));
            stream.Position += localColorTableSize;
        }

        return stream.ReadByte() >= 0 && SkipSubBlocks(stream);
    }

    private static bool SkipSubBlocks(Stream stream)
    {
        while (stream.Position < stream.Length)
        {
            var blockLength = stream.ReadByte();
            if (blockLength < 0)
            {
                return false;
            }

            if (blockLength == 0)
            {
                return true;
            }

            if (stream.Position + blockLength > stream.Length)
            {
                return false;
            }

            stream.Position += blockLength;
        }

        return false;
    }
}
