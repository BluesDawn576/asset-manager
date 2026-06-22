using System.IO;
using System.Text;

namespace AssetManager.Desktop.Preview;

public static class FileFormatDetector
{
    private const int MaxHeaderLength = 64;

    public static string? Detect(string filePath)
    {
        Span<byte> header = stackalloc byte[MaxHeaderLength];
        int bytesRead;
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            bytesRead = stream.Read(header);
        }
        catch
        {
            return null;
        }

        return Detect(header[..bytesRead]);
    }

    private static string? Detect(ReadOnlySpan<byte> header)
    {
        if (StartsWith(header, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return "PNG";
        }

        if (StartsWith(header, [0xFF, 0xD8, 0xFF]))
        {
            return "JPG";
        }

        if (StartsWithAscii(header, "GIF87a") || StartsWithAscii(header, "GIF89a"))
        {
            return "GIF";
        }

        if (StartsWithAscii(header, "BM"))
        {
            return "BMP";
        }

        if (StartsWith(header, [0x49, 0x49, 0x2A, 0x00]) || StartsWith(header, [0x4D, 0x4D, 0x00, 0x2A]))
        {
            return "TIFF";
        }

        if (header.Length >= 12 && StartsWithAscii(header, "RIFF"))
        {
            if (StartsWithAscii(header[8..], "WEBP"))
            {
                return "WEBP";
            }

            if (StartsWithAscii(header[8..], "WAVE"))
            {
                return "WAV";
            }

            if (StartsWithAscii(header[8..], "AVI "))
            {
                return "AVI";
            }
        }

        if (header.Length >= 12 && StartsWithAscii(header[4..], "ftyp"))
        {
            return DetectIsoBaseMediaFormat(header[8..12]);
        }

        if (StartsWith(header, [0x1A, 0x45, 0xDF, 0xA3]))
        {
            return "WEBM";
        }

        if (StartsWithAscii(header, "ID3") || StartsWith(header, [0xFF, 0xFB]) || StartsWith(header, [0xFF, 0xF3]) || StartsWith(header, [0xFF, 0xF2]))
        {
            return "MP3";
        }

        if (StartsWithAscii(header, "fLaC"))
        {
            return "FLAC";
        }

        if (StartsWithAscii(header, "OggS"))
        {
            return "OGG";
        }

        if (StartsWith(header, [0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9]))
        {
            return "ASF";
        }

        return null;
    }

    private static string DetectIsoBaseMediaFormat(ReadOnlySpan<byte> brand)
    {
        if (StartsWithAscii(brand, "M4A") || StartsWithAscii(brand, "M4B"))
        {
            return "M4A";
        }

        if (StartsWithAscii(brand, "M4V"))
        {
            return "M4V";
        }

        if (StartsWithAscii(brand, "qt  "))
        {
            return "MOV";
        }

        return "MP4";
    }

    private static bool StartsWithAscii(ReadOnlySpan<byte> header, string value)
    {
        return header.StartsWith(Encoding.ASCII.GetBytes(value));
    }

    private static bool StartsWith(ReadOnlySpan<byte> header, ReadOnlySpan<byte> value)
    {
        return header.StartsWith(value);
    }
}
