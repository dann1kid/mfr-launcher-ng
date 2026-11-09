# Publishes the launcher as a single self-contained executable (no .NET runtime needed).
# Usage: powershell -File publish.ps1 [-OutputDir publish]
param(
    [string]$OutputDir = "publish",
    [string]$Contour = "production"
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

Rename-Item (Join-Path $repo "$OutputDir/Mfr.Launcher.exe") "M[FR] Launcher.exe" -Force

# адреса контура: копия секции из файла адресов рядом с exe (переопределяется конвейером)
$contour = if ($Contour) { $Contour } else { "production" }
$addresses = Get-Content (Join-Path $repo "addresses.json") -Raw | ConvertFrom-Json
$addresses.$contour | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $repo "$OutputDir/launcher.json") -Encoding UTF8
Get-ChildItem (Join-Path $repo $OutputDir) | Where-Object { $_.Name -notlike "M[FR]*" -and $_.Extension -ne ".exe" } | Remove-Item -Force

Write-Host "published to ${OutputDir}:"
Get-ChildItem (Join-Path $repo $OutputDir) | Select-Object Name, @{N="MB";E={[math]::Round($_.Length/1MB,1)}}
