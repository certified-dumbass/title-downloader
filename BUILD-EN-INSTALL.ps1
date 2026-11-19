$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src/Jellyfin.Plugin.SubtitleGuardian.csproj'
Write-Host 'Building Dreamstreaming Subtitle Guardian for Jellyfin 12.4 (.NET SDK 10 required)...' -ForegroundColor Cyan
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'dotnet is not installed. Install the .NET 10 SDK from https://dotnet.microsoft.com/download/dotnet/10.0'
}
& dotnet build $project -c Release
if ($LASTEXITCODE -ne 0) { throw 'Build failed; plugin has NOT been installed.' }
$releaseFolder = Join-Path $PSScriptRoot 'Dreamstreaming Subtitle Guardian_1.1.0.0'
New-Item -ItemType Directory -Force -Path $releaseFolder | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'src/bin/Release/net10.0/Jellyfin.Plugin.SubtitleGuardian.dll') $releaseFolder -Force
Copy-Item (Join-Path $PSScriptRoot 'src/meta.json') $releaseFolder -Force
$packagePath = Join-Path $PSScriptRoot 'Dreamstreaming-Subtitle-Guardian-install.zip'
if (Test-Path $packagePath) { Remove-Item $packagePath }
Compress-Archive -Path (Join-Path $releaseFolder '*') -DestinationPath $packagePath
Write-Host "Build complete. Installable plugin ZIP: $packagePath" -ForegroundColor Green
Write-Host 'Stop Jellyfin, copy the plugin folder into the Jellyfin data/plugins directory and restart Jellyfin.'
