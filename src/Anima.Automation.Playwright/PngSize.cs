namespace Anima.Automation.Playwright;

/// <summary>
/// Reads the pixel size out of a PNG header. Used to report screenshot dimensions
/// without taking an image-processing dependency.
/// </summary>
public static class PngSize
{
    private const int HeaderLength = 24;

    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static bool TryRead(ReadOnlySpan<byte> content, out int width, out int height)
    {
        width = 0;
        height = 0;

        if (content.Length < HeaderLength || !content[..Signature.Length].SequenceEqual(Signature))
        {
            return false;
        }

        // IHDR is the first chunk: 8-byte signature, 4-byte length, "IHDR",
        // then width and height as big-endian 32-bit integers.
        width = ReadBigEndian(content[16..20]);
        height = ReadBigEndian(content[20..24]);
        return width > 0 && height > 0;
    }

    private static int ReadBigEndian(ReadOnlySpan<byte> value)
        => (value[0] << 24) | (value[1] << 16) | (value[2] << 8) | value[3];
}
