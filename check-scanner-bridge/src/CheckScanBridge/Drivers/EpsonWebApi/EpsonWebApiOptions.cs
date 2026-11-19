namespace CheckScanBridge.Drivers.EpsonWebApi;

/// <summary>Epson 網路機種 (TM-S1000II-NW 等) 內建 Scan Web API 的設定。</summary>
public sealed class EpsonWebApiOptions
{
    /// <summary>掃描機位址，例如 http://192.168.1.50 或 https://scanner01.example.local (HTTPS 需在掃描機註冊憑證)。</summary>
    public string BaseUrl { get; set; } = "http://192.168.192.168";

    /// <summary>Connect 取得的獨占時間 (秒，10–3600)；期間內無任何請求，掃描機會自動斷線並刪除影像。</summary>
    public int ExclusiveTimeoutSeconds { get; set; } = 60;

    /// <summary>Authorization header 的前綴。手冊寫「直接放 token」，若實機需要 Bearer 再改為 "Bearer"。</summary>
    public string AuthorizationScheme { get; set; } = "";

    public int HttpTimeoutMs { get; set; } = 15000;
    public int PollIntervalMs { get; set; } = 300;

    /// <summary>掃描結束 (最後一張進紙) 後等待掃描機回報完成的最長時間。</summary>
    public int CompletionTimeoutMs { get; set; } = 30000;

    /// <summary>啟用掃描機內建 IQA (X9.100-180 類別項目)，未通過的項目會成為警告。</summary>
    public bool IqaEnabled { get; set; } = true;

    /// <summary>MICR 讀取錯誤時是否停止連續掃描 (false：繼續，該張附警告)。</summary>
    public bool StopOnMicrError { get; set; }

    /// <summary>雙張進紙時停止掃描。</summary>
    public bool StopOnDoubleFeed { get; set; } = true;

    /// <summary>
    /// Epson MICR 文字中特殊符號 → 標準字元 (T/U/A/D)。
    /// 手冊範例：t = Transit、o = On-Us；Amount / Dash 依 Epson 慣例預設 a / -，請以實機確認。
    /// </summary>
    public Dictionary<string, string> MicrSymbolMap { get; set; } = new()
    {
        ["t"] = "T",
        ["o"] = "U",
        ["a"] = "A",
        ["-"] = "D",
    };

    /// <summary>選用：電子背書文字 (印在背面影像上)，例如 "FOR DEPOSIT ONLY &lt;00000000&gt;"。空白則不背書。</summary>
    public string? ElectronicEndorsement { get; set; }
}
