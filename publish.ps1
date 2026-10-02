# Publishes the launcher as a single self-contained executable (no .NET runtime needed).
# Usage: powershell -File publish.ps1 [-OutputDir publish]
param(
    [string]$OutputDir = "publish"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $MyInvocation.MyCommand.Path

dotnet test (Join-Path $repo "Mfr.Protocol.Tests") | Out-Null
if ($LASTEXITCODE -ne 0) { throw "tests failed" }

dotnet publish (Join-Path $repo "Mfr.Launcher") `
    -c Release -r win-x64 --self-contained -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o (Join-Path $repo $OutputDir)
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

# ship it under the name the old updater expects, so existing Java clients
# update seamlessly to the new native launcher
Rename-Item (Join-Path $repo "$OutputDir/Mfr.Launcher.exe") "M[FR] Launcher.exe" -Force
Get-ChildItem (Join-Path $repo $OutputDir) | Where-Object { $_.Name -notlike "M[FR]*" -and $_.Extension -ne ".exe" } | Remove-Item -Force

Write-Host "published to ${OutputDir}:"
Get-ChildItem (Join-Path $repo $OutputDir) | Select-Object Name, @{N="MB";E={[math]::Round($_.Length/1MB,1)}}
