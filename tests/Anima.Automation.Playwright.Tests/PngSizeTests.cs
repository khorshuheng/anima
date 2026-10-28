namespace Anima.Automation.Playwright.Tests;

public sealed class PngSizeTests
{
    private static byte[] Png(int width, int height)
    {
        byte[] header = new byte[24];
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(header, 0);

        // IHDR chunk length and type, then width/height big-endian.
        header[11] = 0x0D;
        header[12] = (byte)'I';
        header[13] = (byte)'H';
        header[14] = (byte)'D';
        header[15] = (byte)'R';

        byte[] dims = [.. BitConverter.GetBytes(width).Reverse(), .. BitConverter.GetBytes(height).Reverse()];
        dims.CopyTo(header, 16);
        return header;
    }

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1, 1)]
    [InlineData(1568, 4000)]
    public void Reads_dimensions_from_a_png_header(int width, int height)
    {
        Assert.True(PngSize.TryRead(Png(width, height), out int readWidth, out int readHeight));
        Assert.Equal(width, readWidth);
        Assert.Equal(height, readHeight);
    }

    [Fact]
    public void Rejects_content_that_is_not_a_png()
    {
        Assert.False(PngSize.TryRead("not a png at all, definitely not"u8, out _, out _));
        Assert.False(PngSize.TryRead([], out _, out _));
        Assert.False(PngSize.TryRead([0x89, 0x50], out _, out _));
    }

    [Fact]
    public void Rejects_a_png_with_zero_dimensions() => Assert.False(PngSize.TryRead(Png(0, 0), out _, out _));
}
