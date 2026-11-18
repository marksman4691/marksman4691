namespace CheckScanBridge.Imaging;

public static class ImageOps
{
    /// <summary>以面積平均法縮放到目標 DPI (支票影像通常是縮小)。</summary>
    public static GrayImage Resample(GrayImage src, int targetDpi)
    {
        if (src.Dpi == targetDpi || src.Dpi <= 0) return src;
        var scale = (double)targetDpi / src.Dpi;
        var w = Math.Max(1, (int)Math.Round(src.Width * scale));
        var h = Math.Max(1, (int)Math.Round(src.Height * scale));
        var dst = new GrayImage(w, h, targetDpi);
        var sx = (double)src.Width / w;
        var sy = (double)src.Height / h;
        for (var y = 0; y < h; y++)
        {
            var y0 = (int)(y * sy);
            var y1 = Math.Max(y0 + 1, Math.Min(src.Height, (int)Math.Ceiling((y + 1) * sy)));
            for (var x = 0; x < w; x++)
            {
                var x0 = (int)(x * sx);
                var x1 = Math.Max(x0 + 1, Math.Min(src.Width, (int)Math.Ceiling((x + 1) * sx)));
                long sum = 0;
                for (var yy = y0; yy < y1; yy++)
                {
                    var row = yy * src.Width;
                    for (var xx = x0; xx < x1; xx++) sum += src.Pixels[row + xx];
                }
                dst.Pixels[y * w + x] = (byte)(sum / ((y1 - y0) * (x1 - x0)));
            }
        }
        return dst;
    }

    /// <summary>
    /// Bradley 局部自適應二值化 (積分影像)。可容忍支票底紋與光線不均。
    /// 實務上優先使用掃描機原生的黑白輸出，這裡是灰階來源的後備方案。
    /// </summary>
    public static BitonalImage Binarize(GrayImage src, double sensitivity = 0.15)
    {
        int w = src.Width, h = src.Height;
        var integral = new long[(w + 1) * (h + 1)];
        for (var y = 0; y < h; y++)
        {
            long rowSum = 0;
            for (var x = 0; x < w; x++)
            {
                rowSum += src.Pixels[y * w + x];
                integral[(y + 1) * (w + 1) + x + 1] = integral[y * (w + 1) + x + 1] + rowSum;
            }
        }

        var half = Math.Max(4, src.Dpi / 16); // 約 1/8 英吋視窗
        var dst = new BitonalImage(w, h, src.Dpi);
        for (var y = 0; y < h; y++)
        {
            int ya = Math.Max(0, y - half), yb = Math.Min(h, y + half + 1);
            for (var x = 0; x < w; x++)
            {
                int xa = Math.Max(0, x - half), xb = Math.Min(w, x + half + 1);
                var count = (long)(xb - xa) * (yb - ya);
                var sum = integral[yb * (w + 1) + xb] - integral[ya * (w + 1) + xb]
                        - integral[yb * (w + 1) + xa] + integral[ya * (w + 1) + xa];
                var p = src.Pixels[y * w + x];
                if (p < 40 || (p * count < sum * (1.0 - sensitivity) && p < 200))
                    dst.SetBlack(x, y);
            }
        }
        return dst;
    }

    /// <summary>黑白影像 → 縮小的灰階預覽 (給瀏覽器顯示，TIFF 在多數瀏覽器無法顯示)。</summary>
    public static GrayImage Preview(BitonalImage src, int maxWidth)
    {
        var gray = src.ToGray();
        if (src.Width <= maxWidth) return gray;
        var dpi = Math.Max(1, (int)Math.Round((double)src.Dpi * maxWidth / src.Width));
        var scaled = new GrayImage(src.Width, src.Height, src.Dpi, gray.Pixels);
        return Resample(scaled, dpi);
    }
}
