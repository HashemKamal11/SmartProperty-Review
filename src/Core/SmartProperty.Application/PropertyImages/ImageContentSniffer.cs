using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Application.PropertyImages;

/// <summary>
/// Decides what a file really is from its first bytes. The type a client declares, and the extension of its file
/// name, are never trusted: a script renamed to <c>photo.jpg</c> must not be stored and served as an image.
/// </summary>
internal static class ImageContentSniffer
{
    /// <summary>How many leading bytes are needed to tell the supported types apart.</summary>
    public const int HeaderLength = 12;

    /// <summary>The content type the bytes say the file is, or null when it is not a supported image.</summary>
    public static string? Detect(ReadOnlySpan<byte> header)
    {
        // JPEG: FF D8 FF
        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
        {
            return PropertyImage.Jpeg;
        }

        // PNG: 89 'P' 'N' 'G' 0D 0A 1A 0A
        if (header.Length >= 8
            && header[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            return PropertyImage.Png;
        }

        // WebP: "RIFF" <4-byte size> "WEBP"
        if (header.Length >= 12
            && header[..4].SequenceEqual("RIFF"u8)
            && header[8..12].SequenceEqual("WEBP"u8))
        {
            return PropertyImage.WebP;
        }

        return null;
    }
}
