[CmdletBinding()]
param(
    [string]$Dotnet = 'dotnet',
    [string]$Iscc = 'ISCC.exe',
    [string]$PlatformTools = ''
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$output = Join-Path $repo 'dist'
$publish = Join-Path $env:TEMP 'screenkey-installer-publish'
if (-not $PlatformTools) {
    $adb = Get-Command adb -ErrorAction Stop
    $PlatformTools = Split-Path -Parent $adb.Source
}
foreach ($file in @('adb.exe', 'AdbWinApi.dll', 'AdbWinUsbApi.dll', 'NOTICE.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $PlatformTools $file))) { throw "Missing platform-tools file: $file" }
}
New-Item -ItemType Directory -Force -Path $publish, $output | Out-Null
& $Dotnet publish (Join-Path $repo 'windows/ScreenKeyBridge/ScreenKeyBridge.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $publish
if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed.' }
& $Iscc "/DPublishDir=$publish" "/DPlatformToolsDir=$PlatformTools" "/O$output" (Join-Path $repo 'packaging/ScreenKeyBridge.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
Write-Host "Installer: $(Join-Path $output 'ScreenKeyBridge-Windows-Setup.exe')"
