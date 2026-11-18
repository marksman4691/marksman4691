using BitMiracle.LibTiff.Classic;
using CheckScanBridge.Capture;
using CheckScanBridge.Drivers;
using CheckScanBridge.Imaging;

namespace CheckScanBridge.Tests;

public class ImagingTests
{
    private static BitonalImage Pattern(int w, int h, int dpi)
    {
        var img = new BitonalImage(w, h, dpi);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                if ((x / 7 + y / 5) % 3 == 0 || x == y) img.SetBlack(x, y);
        return img;
    }

    [Theory]
    [InlineData(TiffByteOrder.LittleEndian)]
    [InlineData(TiffByteOrder.BigEndian)]
    public void G4RoundTripIsLosslessAndMeetsProfile(TiffByteOrder order)
    {
        var src = Pattern(1203, 551, 200); // 寬度非 8 的倍數
        var tiff = X9TiffCodec.Encode(src, order);
        Assert.Empty(X9TiffCodec.Validate(tiff, 200, order));
        Assert.Equal(order == TiffByteOrder.LittleEndian ? (byte)'I' : (byte)'M', tiff[0]);

        var back = X9TiffCodec.Decode(tiff, 0).Bitonal!;
        Assert.Equal(src.Width, back.Width);
        Assert.Equal(src.Height, back.Height);
        Assert.Equal(200, back.Dpi);
        Assert.Equal(src.Bits, back.Bits);
    }

    [Fact]
    public void ValidateReportsWrongDpiAndOrder()
    {
        var tiff = X9TiffCodec.Encode(Pattern(100, 50, 240), TiffByteOrder.LittleEndian);
        var problems = X9TiffCodec.Validate(tiff, 200, TiffByteOrder.BigEndian);
        Assert.Contains("200 dpi", problems);
        Assert.Contains("byte order", problems);
    }

    [Fact]
    public void ResampleScalesToTargetDpi()
    {
        var g = new GrayImage(1800, 825, 300);
        var r = ImageOps.Resample(g, 200);
        Assert.Equal((1200, 550, 200), (r.Width, r.Height, r.Dpi));
    }

    [Fact]
    public void BinarizeRemovesLightBackgroundKeepsInk()
    {
        var g = new GrayImage(400, 200, 200);
        for (var i = 0; i < g.Pixels.Length; i++) g.Pixels[i] = (byte)(220 + (i % 7) * 3); // 淺色底紋
        for (var y = 90; y < 110; y++)
            for (var x = 50; x < 350; x++) g.Pixels[y * 400 + x] = 30; // 墨水線
        var b = ImageOps.Binarize(g);
        Assert.True(b.IsBlack(200, 100));
        Assert.False(b.IsBlack(10, 10));
        Assert.InRange(b.BlackRatio(), 0.07, 0.09);
    }

    [Fact]
    public void PngPreviewHasSignature()
    {
        var png = PngEncoder.Encode(ImageOps.Preview(Pattern(1200, 550, 200), 600));
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png[..4]);
    }

    private static ItemProcessor Processor(Action<BridgeOptions>? configure = null)
    {
        var o = new BridgeOptions();
        configure?.Invoke(o);
        return new ItemProcessor(o);
    }

    [Fact]
    public void ProcessorPassesThroughNativeBitonalTiff()
    {
        var src = Pattern(1200, 550, 200);
        var tiff = X9TiffCodec.Encode(src, TiffByteOrder.BigEndian); // 不同 byte order 也會被正規化
        var item = Processor().Process(new RawItem(RawImage.FromTiff(tiff, 200), RawImage.FromTiff(tiff, 200), "T021000021T 1U"), 1, false);
        var outBits = X9TiffCodec.Decode(Convert.FromBase64String(item.Front.Base64), 0).Bitonal!;
        Assert.Equal(src.Bits, outBits.Bits);
        Assert.Equal('I', (char)Convert.FromBase64String(item.Front.Base64)[0]);
        Assert.Null(item.Front.PreviewPngBase64);
    }

    [Fact]
    public void ProcessorConvertsGrayTiffAt300Dpi()
    {
        var gray = WriteGrayTiff(1800, 825, 300);
        var item = Processor().Process(new RawItem(RawImage.FromTiff(gray, 0), RawImage.FromTiff(gray, 0), "T021000021T 1U"), 1, true);
        Assert.Equal((1200, 550, 200), (item.Front.Width, item.Front.Height, item.Front.Dpi));
        Assert.Equal("x9-tiff-g4", item.Front.Format);
        Assert.NotNull(item.Front.PreviewPngBase64);
    }

    [Fact]
    public void ProcessorRejectsMissingBack()
    {
        var tiff = X9TiffCodec.Encode(Pattern(1200, 550, 200), TiffByteOrder.LittleEndian);
        var ex = Assert.Throws<ScannerException>(() =>
            Processor().Process(new RawItem(RawImage.FromTiff(tiff, 200), null, null), 1, false));
        Assert.Equal("BACK_IMAGE_MISSING", ex.Code);
    }

    [Fact]
    public void ProcessorWarnsOnMicrAndQualityProblems()
    {
        var blank = X9TiffCodec.Encode(new BitonalImage(1200, 550, 200), TiffByteOrder.LittleEndian);
        var small = X9TiffCodec.Encode(Pattern(400, 200, 200), TiffByteOrder.LittleEndian);
        var item = Processor().Process(new RawItem(RawImage.FromTiff(small, 200), RawImage.FromTiff(blank, 200), "T021000022T 1U?"), 1, false);
        Assert.Contains("FRONT_IMAGE_SIZE_OUT_OF_RANGE", item.Warnings);
        Assert.Contains("BACK_IMAGE_TOO_LIGHT", item.Warnings);
        Assert.Contains("FRONT_BACK_SIZE_MISMATCH", item.Warnings);
        Assert.Contains("MICR_ROUTING_CHECKSUM_FAILED", item.Warnings);
        Assert.Contains("MICR_UNREADABLE_CHARACTERS", item.Warnings);

        var noMicr = Processor().Process(new RawItem(RawImage.FromTiff(blank, 200), RawImage.FromTiff(blank, 200), null), 1, false);
        Assert.Contains("MICR_MISSING", noMicr.Warnings);
    }

    private static byte[] WriteGrayTiff(int w, int h, int dpi)
    {
        var ms = new MemoryStream();
        using (var t = Tiff.ClientOpen("g", "w", ms, new TiffStream())!)
        {
            t.SetField(TiffTag.IMAGEWIDTH, w);
            t.SetField(TiffTag.IMAGELENGTH, h);
            t.SetField(TiffTag.BITSPERSAMPLE, 8);
            t.SetField(TiffTag.SAMPLESPERPIXEL, 1);
            t.SetField(TiffTag.PHOTOMETRIC, Photometric.MINISBLACK);
            t.SetField(TiffTag.COMPRESSION, Compression.LZW);
            t.SetField(TiffTag.ROWSPERSTRIP, 64);
            t.SetField(TiffTag.XRESOLUTION, (float)dpi);
            t.SetField(TiffTag.YRESOLUTION, (float)dpi);
            t.SetField(TiffTag.RESOLUTIONUNIT, ResUnit.INCH);
            var row = new byte[w];
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++) row[x] = (byte)((y / 40 + x / 60) % 4 == 0 ? 25 : 235);
                t.WriteScanline(row, y);
            }
        }
        return ms.ToArray();
    }
}
