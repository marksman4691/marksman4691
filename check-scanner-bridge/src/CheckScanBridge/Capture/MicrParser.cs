using System.Text.RegularExpressions;

namespace CheckScanBridge.Capture;

/// <summary>
/// 解析美國 E-13B MICR 碼 (標準字元 T/U/A/D)。
/// 欄位由左至右：[Auxiliary On-Us] [EPC] T Routing T [On-Us] [A Amount A]
/// </summary>
public static partial class MicrParser
{
    [GeneratedRegex(@"T([0-9?D]{8,10})T")]
    private static partial Regex TransitRegex();

    [GeneratedRegex(@"U([0-9?D ]+)U")]
    private static partial Regex AuxOnUsRegex();

    [GeneratedRegex(@"A([0-9?]{10})A")]
    private static partial Regex AmountRegex();

    public static MicrData Parse(string raw)
    {
        var s = raw.Trim();
        var hasUnreadable = s.Contains('?');

        string? routing = null, aux = null, epc = null, onUs = null, account = null, serial = null;
        long? amount = null;

        var transit = TransitRegex().Match(s);
        string prefix, suffix;
        if (transit.Success)
        {
            routing = transit.Groups[1].Value.Replace("D", "");
            prefix = s[..transit.Index];
            suffix = s[(transit.Index + transit.Length)..];
        }
        else
        {
            prefix = "";
            suffix = s;
        }

        // Transit 左側：Auxiliary On-Us 與 EPC
        var auxMatch = AuxOnUsRegex().Match(prefix);
        if (auxMatch.Success)
        {
            aux = Clean(auxMatch.Groups[1].Value);
            prefix = prefix.Remove(auxMatch.Index, auxMatch.Length);
        }
        var leftover = prefix.Replace(" ", "");
        if (leftover.Length == 1 && char.IsDigit(leftover[0])) epc = leftover;

        // Transit 右側：金額與 On-Us
        var amountMatch = AmountRegex().Match(suffix);
        if (amountMatch.Success)
        {
            if (long.TryParse(amountMatch.Groups[1].Value, out var cents)) amount = cents;
            suffix = suffix.Remove(amountMatch.Index, amountMatch.Length);
        }
        var onUsRaw = suffix.Replace(" ", "");
        if (onUsRaw.Length > 0)
        {
            onUs = onUsRaw.Replace('U', '/').Replace('D', '-'); // X9 Type 25 表示法
            var tokens = onUsRaw.Split('U', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length > 0) account = tokens[0].Replace("D", "");
            // 啟發式：商業支票的票號在 Aux On-Us；個人支票通常在帳號之後
            serial = aux ?? (tokens.Length > 1 ? tokens[1].Replace("D", "") : null);
        }
        else if (aux is not null)
        {
            serial = aux;
        }

        return new MicrData(
            raw,
            routing,
            routing is not null && IsValidRouting(routing),
            aux,
            epc,
            onUs,
            account,
            serial,
            amount,
            hasUnreadable);
    }

    /// <summary>ABA routing number 檢查碼：3·(d1+d4+d7) + 7·(d2+d5+d8) + (d3+d6+d9) ≡ 0 (mod 10)。</summary>
    public static bool IsValidRouting(string routing)
    {
        if (routing.Length != 9 || !routing.All(char.IsAsciiDigit)) return false;
        int D(int i) => routing[i] - '0';
        var sum = 3 * (D(0) + D(3) + D(6)) + 7 * (D(1) + D(4) + D(7)) + (D(2) + D(5) + D(8));
        return sum % 10 == 0;
    }

    private static string Clean(string v) => v.Replace(" ", "").Replace("D", "-");
}
