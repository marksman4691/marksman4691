#Requires -RunAsAdministrator
# 將 publish 目錄複製到安裝目錄並註冊為 Windows 服務 (開機自動啟動)
param(
    [string]$Source = "$PSScriptRoot\..\publish",
    [string]$InstallDir = "$env:ProgramFiles\CheckScanBridge",
    [string]$ServiceName = "CheckScanBridge"
)
$ErrorActionPreference = "Stop"

if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
    Stop-Service $ServiceName -ErrorAction SilentlyContinue
}

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
# 保留現有設定檔
$keepConfig = Test-Path "$InstallDir\appsettings.json"
Copy-Item "$Source\*" $InstallDir -Recurse -Force -Exclude $(if ($keepConfig) { "appsettings.json" } else { @() })

$exe = Join-Path $InstallDir "CheckScanBridge.exe"
if (-not (Get-Service $ServiceName -ErrorAction SilentlyContinue)) {
    New-Service -Name $ServiceName -BinaryPathName "`"$exe`"" `
        -DisplayName "Epson Check Scanner Bridge" `
        -Description "讓網頁透過 http://127.0.0.1 操作 Epson 票據掃描機" `
        -StartupType Automatic | Out-Null
    sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null
}
Start-Service $ServiceName
Get-Service $ServiceName
