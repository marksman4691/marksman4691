using CheckScanBridge.Drivers;
using CheckScanBridge.Imaging;

namespace CheckScanBridge.Capture;

/// <summary>
/// 與機型無關的處理管線：原始影像 → X9 TIFF G4、MICR 解析、基本影像品質檢查。
/// </summary>
public sealed class ItemProcessor
{
    private readonly ImageProfileOptions _image;
    private readonly RequirementOptions _req;

    public ItemProcessor(BridgeOptions options)
    {
        _image = options.Image;
        _req = options.Requirements;
    }

    public CheckItem Process(RawItem raw, int sequence, bool preview)
    {
        var warnings = new List<string>(raw.DriverWarnings ?? Array.Empty<string>());

        if (raw.Front is null)
            throw new ScannerException("FRONT_IMAGE_MISSING", "掃描機未回傳正面影像");
        if (raw.Back is null)
            throw new ScannerException("BACK_IMAGE_MISSING", "掃描機未回傳背面影像");

        var front = ToBitonal(raw.Front);
        var back = ToBitonal(raw.Back);
        CheckQuality(front, "FRONT", warnings);
        CheckQuality(back, "BACK", warnings);
        if (Math.Abs(front.Width - back.Width) > _image.Dpi / 4 || Math.Abs(front.Height - back.Height) > _image.Dpi / 4)
            warnings.Add("FRONT_BACK_SIZE_MISMATCH");

        MicrData? micr = null;
        if (string.IsNullOrWhiteSpace(raw.MicrRaw))
        {
            if (_req.RequireMicr) warnings.Add("MICR_MISSING");
        }
        else
        {
            micr = MicrParser.Parse(raw.MicrRaw);
            if (micr.HasUnreadable) warnings.Add("MICR_UNREADABLE_CHARACTERS");
            if (micr.RoutingNumber is null) warnings.Add("MICR_ROUTING_MISSING");
            else if (!micr.RoutingValid) warnings.Add("MICR_ROUTING_CHECKSUM_FAILED");
        }

        return new CheckItem(
            sequence,
            Guid.NewGuid().ToString("N"),
            Encode(front, "front", preview),
            Encode(back, "back", preview),
            micr,
            warnings);
    }

    private BitonalImage ToBitonal(RawImage raw)
    {
        GrayImage gray;
        if (raw.Gray is not null)
        {
            gray = raw.Gray;
        }
        else
        {
            var decoded = X9TiffCodec.Decode(raw.Tiff!, raw.Dpi);
            if (decoded.Bitonal is { } bit)
            {
                if (bit.Dpi == _image.Dpi) return bit;   // 掃描機原生黑白：直接使用，不重新二值化
                gray = bit.ToGray();
            }
            else
            {
                gray = decoded.Gray!;
            }
        }
        return ImageOps.Binarize(ImageOps.Resample(gray, _image.Dpi));
    }

    private void CheckQuality(BitonalImage img, string side, List<string> warnings)
    {
        var w = (double)img.Width / img.Dpi;
        var h = (double)img.Height / img.Dpi;
        if (w < _image.MinWidthInches || w > _image.MaxWidthInches || h < _image.MinHeightInches || h > _image.MaxHeightInches)
            warnings.Add($"{side}_IMAGE_SIZE_OUT_OF_RANGE");
        var ratio = img.BlackRatio();
        if (ratio < _image.MinBlackRatio) warnings.Add($"{side}_IMAGE_TOO_LIGHT");
        if (ratio > _image.MaxBlackRatio) warnings.Add($"{side}_IMAGE_TOO_DARK");
    }

    private CheckImage Encode(BitonalImage img, string side, bool preview)
    {
        var tiff = X9TiffCodec.Encode(img, _image.ByteOrder);
        var problems = X9TiffCodec.Validate(tiff, _image.Dpi, _image.ByteOrder);
        if (problems.Count > 0)
            throw new ScannerException("IMAGE_PROFILE_ERROR", $"{side} 影像不符合 X9 格式：{string.Join(", ", problems)}");

        string? png = null;
        if (preview)
            png = Convert.ToBase64String(PngEncoder.Encode(ImageOps.Preview(img, _image.PreviewMaxWidth)));

        return new CheckImage(side, "image/tiff", "x9-tiff-g4", img.Width, img.Height, img.Dpi,
            tiff.Length, Convert.ToBase64String(tiff), png);
    }
}
