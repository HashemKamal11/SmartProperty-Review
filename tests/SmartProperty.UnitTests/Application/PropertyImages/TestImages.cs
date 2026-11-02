namespace SmartProperty.UnitTests.Application.PropertyImages;

/// <summary>Tiny byte sequences that start like each supported image type, and ones that do not.</summary>
internal static class TestImages
{
    public static byte[] Jpeg(int length = 64) => WithHeader([0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01], length);

    public static byte[] Png(int length = 64) => WithHeader([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D], length);

    public static byte[] WebP(int length = 64) => WithHeader("RIFF"u8.ToArray().Concat(new byte[] { 0x24, 0x00, 0x00, 0x00 }).Concat("WEBP"u8.ToArray()).ToArray(), length);

    public static byte[] Gif(int length = 64) => WithHeader("GIF89a"u8.ToArray(), length);

    public static byte[] Html(int length = 64) => WithHeader("<html><script>alert(1)</script>"u8.ToArray(), length);

    private static byte[] WithHeader(byte[] header, int length)
    {
        var bytes = new byte[Math.Max(length, header.Length)];
        header.CopyTo(bytes, 0);

        // The body is arbitrary: only the first bytes decide what the file is called.
        for (var i = header.Length; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(i % 251);
        }

        return bytes;
    }
}
