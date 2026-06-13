using System.IO;

namespace AssetManager.Desktop.Preview;

/// <summary>
/// Detects animated image files by reading file headers.
/// Supports GIF (87a/89a), PNG (APNG via acTL chunk), and WebP (VP8X animation flag).
/// This handles cases where file extensions are non-standard or missing.
/// </summary>
public static class AnimatedImageDetector
{
    public static bool IsAnimated(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var header = new byte[12];
            if (stream.Read(header, 0, header.Length) < header.Length)
            {
                return false;
            }

            return IsAnimatedGif(header)
                   || IsAnimatedPng(stream, header)
                   || IsAnimatedWebP(stream, header);
        }
        catch
        {
            return false;
        }
    }

    // GIF87a or GIF89a — both can be animated.
    // GIF89a is almost always animated; GIF87a is rare and never animated.
    // For precise detection we could scan blocks, but treating GIF89a as animated
    // is the practical choice — static GIF89a files are extremely uncommon.
    private static bool IsAnimatedGif(byte[] header)
    {
        // GIF signature: "GIF87a" or "GIF89a"
        if (header[0] == 0x47 && header[1] == 0x49 && header[2] == 0x46
            && header[3] == 0x38 && header[4] == 0x39 && header[5] == 0x61)
        {
            return true; // GIF89a
        }

        return false;
    }

    // PNG: APNG files contain an acTL (animation control) chunk before the first IDAT.
    private static bool IsAnimatedPng(Stream stream, byte[] header)
    {
        // PNG signature: 89 50 4E 47 0D 0A 1A 0A
        if (header[0] != 0x89 || header[1] != 0x50 || header[2] != 0x4E || header[3] != 0x47
            || header[4] != 0x0D || header[5] != 0x0A || header[6] != 0x1A || header[7] != 0x0A)
        {
            return false;
        }

        stream.Position = 8;
        var chunkHeader = new byte[8];

        // Scan chunks until first IDAT; acTL must appear before IDAT for APNG.
        while (stream.Read(chunkHeader, 0, chunkHeader.Length) == chunkHeader.Length)
        {
            var chunkLength = (chunkHeader[0] << 24) | (chunkHeader[1] << 16) | (chunkHeader[2] << 8) | chunkHeader[3];

            // acTL chunk type: 61 63 54 4C
            if (chunkHeader[4] == 0x61 && chunkHeader[5] == 0x63
                && chunkHeader[6] == 0x54 && chunkHeader[7] == 0x4C)
            {
                return true;
            }

            // IDAT chunk: once we hit image data, no more animation control chunks.
            if (chunkHeader[4] == 0x49 && chunkHeader[5] == 0x44
                && chunkHeader[6] == 0x41 && chunkHeader[7] == 0x54)
            {
                return false;
            }

            // Skip chunk data + 4-byte CRC
            var skipBytes = chunkLength + 4;
            if (stream.Position + skipBytes > stream.Length)
            {
                break;
            }

            stream.Position += skipBytes;
        }

        return false;
    }

    // WebP: VP8X format has a feature flags byte; bit 2 (0x02) indicates animation.
    private static bool IsAnimatedWebP(Stream stream, byte[] header)
    {
        // RIFF....WEBP
        if (header[0] != 0x52 || header[1] != 0x49 || header[2] != 0x46 || header[3] != 0x46
            || header[8] != 0x57 || header[9] != 0x45 || header[10] != 0x42 || header[11] != 0x50)
        {
            return false;
        }

        // VP8X chunk: header starts at offset 12
        stream.Position = 12;
        var vp8xHeader = new byte[10];
        if (stream.Read(vp8xHeader, 0, vp8xHeader.Length) < vp8xHeader.Length)
        {
            return false;
        }

        // VP8X chunk type
        if (vp8xHeader[0] != 0x56 || vp8xHeader[1] != 0x50
            || vp8xHeader[2] != 0x38 || vp8xHeader[3] != 0x58)
        {
            return false;
        }

        // Feature flags byte: after 4-byte type + 4-byte size = offset 8 in our buffer.
        var flags = vp8xHeader[8];
        return (flags & 0x02) != 0;
    }
}