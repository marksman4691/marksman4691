using BitMiracle.LibTiff.Classic;

namespace CheckScanBridge.Imaging;

public enum TiffByteOrder { LittleEndian, BigEndian }

/// <summary>
/// 依 X9.37 / X9.100-187 (影像格式 X9.100-181) 的一般要求產生支票影像：
/// TIFF 6.0、單頁、單一 strip、1-bit 黑白、CCITT Group 4、WhiteIsZero、指定 DPI。
/// 實際 DPI / byte order 等細節請以 JPM Host-to-Host 影像規格為準 (可設定)。
/// </summary>
public static class X9TiffCodec
{
    static X9TiffCodec()
    {
        Tiff.SetErrorHandler(new QuietHandler()); // 避免 LibTiff 寫 stderr
    }

    public static byte[] Encode(BitonalImage img, TiffByteOrder order)
    {
        var ms = new MemoryStream();
        using (var tif = Tiff.ClientOpen("x9", order == TiffByteOrder.BigEndian ? "wb" : "wl", ms, new TiffStream())
                         ?? throw new InvalidOperationException("無法建立 TIFF"))
        {
            tif.SetField(TiffTag.SUBFILETYPE, 0);
            tif.SetField(TiffTag.IMAGEWIDTH, img.Width);
            tif.SetField(TiffTag.IMAGELENGTH, img.Height);
            tif.SetField(TiffTag.BITSPERSAMPLE, 1);
            tif.SetField(TiffTag.SAMPLESPERPIXEL, 1);
            tif.SetField(TiffTag.COMPRESSION, Compression.CCITTFAX4);
            tif.SetField(TiffTag.PHOTOMETRIC, Photometric.MINISWHITE);
            tif.SetField(TiffTag.FILLORDER, FillOrder.MSB2LSB);
            tif.SetField(TiffTag.ROWSPERSTRIP, img.Height);
            tif.SetField(TiffTag.XRESOLUTION, (float)img.Dpi);
            tif.SetField(TiffTag.YRESOLUTION, (float)img.Dpi);
            tif.SetField(TiffTag.RESOLUTIONUNIT, ResUnit.INCH);

            var row = new byte[img.Stride];
            for (var y = 0; y < img.Height; y++)
            {
                Buffer.BlockCopy(img.Bits, y * img.Stride, row, 0, img.Stride);
                if (!tif.WriteScanline(row, y)) throw new InvalidOperationException("TIFF 寫入失敗");
            }
            tif.WriteDirectory();
        }
        return ms.ToArray();
    }

    /// <summary>解碼任意 TIFF (G4/LZW/JPEG/未壓縮，黑白/灰階/彩色)。</summary>
    public static DecodedTiff Decode(byte[] data, int fallbackDpi)
    {
        using var tif = Tiff.ClientOpen("in", "r", new MemoryStream(data), new TiffStream())
                        ?? throw new InvalidDataException("不是有效的 TIFF");
        var w = tif.GetField(TiffTag.IMAGEWIDTH)[0].ToInt();
        var h = tif.GetField(TiffTag.IMAGELENGTH)[0].ToInt();
        var bps = tif.GetField(TiffTag.BITSPERSAMPLE)?[0].ToInt() ?? 1;
        var spp = tif.GetField(TiffTag.SAMPLESPERPIXEL)?[0].ToInt() ?? 1;
        var photometric = (Photometric)(tif.GetField(TiffTag.PHOTOMETRIC)?[0].ToInt() ?? 0);
        var dpi = ReadDpi(tif) ?? fallbackDpi;

        if (bps == 1 && spp == 1)
        {
            var img = new BitonalImage(w, h, dpi);
            var row = new byte[tif.ScanlineSize()];
            var invert = photometric == Photometric.MINISBLACK; // MinIsBlack 時 bit 1 = 白
            for (var y = 0; y < h; y++)
            {
                tif.ReadScanline(row, y);
                for (var i = 0; i < img.Stride; i++)
                    img.Bits[y * img.Stride + i] = invert ? (byte)~row[i] : row[i];
            }
            // 清除每列最後一個 byte 多出的 padding bits
            var pad = img.Stride * 8 - w;
            if (pad > 0)
                for (var y = 0; y < h; y++)
                    img.Bits[y * img.Stride + img.Stride - 1] &= (byte)(0xFF << pad);
            return new DecodedTiff(img, null);
        }

        var raster = new int[w * h];
        if (!tif.ReadRGBAImageOriented(w, h, raster, Orientation.TOPLEFT))
            throw new InvalidDataException("無法解碼 TIFF 影像");
        var gray = new GrayImage(w, h, dpi);
        for (var i = 0; i < raster.Length; i++)
        {
            var p = raster[i];
            gray.Pixels[i] = (byte)((Tiff.GetR(p) * 299 + Tiff.GetG(p) * 587 + Tiff.GetB(p) * 114) / 1000);
        }
        return new DecodedTiff(null, gray);
    }

    /// <summary>檢查 TIFF 是否符合 X9 影像格式設定，回傳不符合的項目。</summary>
    public static List<string> Validate(byte[] data, int expectedDpi, TiffByteOrder expectedOrder)
    {
        var problems = new List<string>();
        var bigEndian = data.Length >= 2 && data[0] == (byte)'M' && data[1] == (byte)'M';
        if (bigEndian != (expectedOrder == TiffByteOrder.BigEndian)) problems.Add("byte order");

        using var tif = Tiff.ClientOpen("v", "r", new MemoryStream(data), new TiffStream());
        if (tif is null) { problems.Add("not a TIFF"); return problems; }
        int Get(TiffTag t) => tif.GetField(t)?[0].ToInt() ?? -1;

        if (tif.NumberOfDirectories() != 1) problems.Add("single page");
        if (Get(TiffTag.BITSPERSAMPLE) != 1 || Get(TiffTag.SAMPLESPERPIXEL) != 1) problems.Add("bitonal");
        if (Get(TiffTag.COMPRESSION) != (int)Compression.CCITTFAX4) problems.Add("CCITT G4");
        if (Get(TiffTag.PHOTOMETRIC) != (int)Photometric.MINISWHITE) problems.Add("WhiteIsZero");
        if (tif.NumberOfStrips() != 1) problems.Add("single strip");
        if (ReadDpi(tif) != expectedDpi) problems.Add($"{expectedDpi} dpi");
        if (Get(TiffTag.RESOLUTIONUNIT) != (int)ResUnit.INCH) problems.Add("resolution unit inch");
        return problems;
    }

    private static int? ReadDpi(Tiff tif)
    {
        var x = tif.GetField(TiffTag.XRESOLUTION);
        if (x is null) return null;
        var v = x[0].ToFloat();
        if ((ResUnit)(tif.GetField(TiffTag.RESOLUTIONUNIT)?[0].ToInt() ?? 2) == ResUnit.CENTIMETER) v *= 2.54f;
        return v > 0 ? (int)Math.Round(v) : null;
    }

    private sealed class QuietHandler : TiffErrorHandler
    {
        public override void WarningHandler(Tiff tif, string method, string format, params object[] args) { }
        public override void WarningHandlerExt(Tiff tif, object clientData, string method, string format, params object[] args) { }
    }
}

public sealed record DecodedTiff(BitonalImage? Bitonal, GrayImage? Gray);
