# 產生 Windows 發佈版 (預設 x86，因多數 OPOS ActiveX 控制項為 32 位元)
param(
    [ValidateSet("win-x86", "win-x64")] [string]$Runtime = "win-x86",
    [string]$Output = "$PSScriptRoot\..\publish"
)
$ErrorActionPreference = "Stop"
dotnet publish "$PSScriptRoot\..\src\CheckScanBridge" -c Release -r $Runtime `
    --self-contained true -p:PublishSingleFile=true -o $Output
Write-Host "Published to $Output"
