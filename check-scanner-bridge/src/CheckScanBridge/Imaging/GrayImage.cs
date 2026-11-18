namespace CheckScanBridge.Imaging;

/// <summary>8-bit 灰階影像，0 = 黑、255 = 白，由上而下逐列存放。</summary>
public sealed class GrayImage
{
    public GrayImage(int width, int height, int dpi, byte[]? pixels = null)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        Width = width;
        Height = height;
        Dpi = dpi;
        Pixels = pixels ?? new byte[width * height];
        if (Pixels.Length != width * height) throw new ArgumentException("pixel buffer size mismatch");
    }

    public int Width { get; }
    public int Height { get; }
    public int Dpi { get; }
    public byte[] Pixels { get; }
}

/// <summary>1-bit 黑白影像，每列 (Width+7)/8 bytes，MSB 在前，bit 1 = 黑。</summary>
public sealed class BitonalImage
{
    public BitonalImage(int width, int height, int dpi)
    {
        Width = width;
        Height = height;
        Dpi = dpi;
        Stride = (width + 7) / 8;
        Bits = new byte[Stride * height];
    }

    public int Width { get; }
    public int Height { get; }
    public int Dpi { get; }
    public int Stride { get; }
    public byte[] Bits { get; }

    public bool IsBlack(int x, int y) => (Bits[y * Stride + (x >> 3)] & (0x80 >> (x & 7))) != 0;

    public void SetBlack(int x, int y) => Bits[y * Stride + (x >> 3)] |= (byte)(0x80 >> (x & 7));

    public GrayImage ToGray()
    {
        var g = new GrayImage(Width, Height, Dpi);
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
                g.Pixels[y * Width + x] = IsBlack(x, y) ? (byte)0 : (byte)255;
        return g;
    }

    public double BlackRatio()
    {
        long black = 0;
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
                if (IsBlack(x, y)) black++;
        return (double)black / ((long)Width * Height);
    }
}
