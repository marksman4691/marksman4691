#Requires -RunAsAdministrator
param(
    [string]$InstallDir = "$env:ProgramFiles\CheckScanBridge",
    [string]$ServiceName = "CheckScanBridge",
    [switch]$RemoveFiles
)
$ErrorActionPreference = "Stop"
if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
    Stop-Service $ServiceName -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
}
if ($RemoveFiles -and (Test-Path $InstallDir)) { Remove-Item $InstallDir -Recurse -Force }
Write-Host "Removed $ServiceName"
