[CmdletBinding()]
param(
    [string]$JavaHome = $env:JAVA_HOME,
    [string]$Gradle = 'gradle'
)
$ErrorActionPreference = 'Stop'
if (-not $JavaHome -or -not (Test-Path -LiteralPath (Join-Path $JavaHome 'bin/keytool.exe'))) { throw 'Set JAVA_HOME to a JDK 17 installation.' }
$repo = Split-Path -Parent $PSScriptRoot
$keyDir = Join-Path $env:LOCALAPPDATA 'ScreenKeyBridge\signing'
$keyStore = Join-Path $keyDir 'release.jks'
$passwordFile = Join-Path $keyDir 'release-password.dpapi'
$oldJava = $env:JAVA_HOME
$oldPath = $env:PATH
$oldStore = $env:SCREENKEY_KEYSTORE
$oldPassword = $env:SCREENKEY_SIGN_PASSWORD
try {
    $env:JAVA_HOME = $JavaHome
    $env:PATH = "$(Join-Path $JavaHome 'bin');$oldPath"
    New-Item -ItemType Directory -Force -Path $keyDir, (Join-Path $repo 'dist') | Out-Null
    $hasKey = Test-Path -LiteralPath $keyStore
    $hasPassword = Test-Path -LiteralPath $passwordFile
    if ($hasKey -ne $hasPassword) { throw 'Signing key and protected password must both exist; neither was overwritten.' }
    if (-not $hasKey) {
        $env:SCREENKEY_SIGN_PASSWORD = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
        $secure = ConvertTo-SecureString $env:SCREENKEY_SIGN_PASSWORD -AsPlainText -Force
        $secure | ConvertFrom-SecureString | Set-Content -LiteralPath $passwordFile -Encoding ascii -NoNewline
        & (Join-Path $JavaHome 'bin/keytool.exe') -genkeypair -keystore $keyStore -storetype PKCS12 -storepass:env SCREENKEY_SIGN_PASSWORD -keypass:env SCREENKEY_SIGN_PASSWORD -alias screenkeybridge -keyalg RSA -keysize 3072 -validity 9125 -dname 'CN=ScreenKey Bridge, O=Deanx270' -noprompt
        if ($LASTEXITCODE -ne 0) { throw 'Signing key creation failed; inspect the signing folder before retrying.' }
        Write-Host "Created a private Android signing key in $keyDir. Back it up securely."
    } else {
        $secure = Get-Content -LiteralPath $passwordFile -Raw | ConvertTo-SecureString
        $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
        try { $env:SCREENKEY_SIGN_PASSWORD = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) }
        finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
    }
    $env:SCREENKEY_KEYSTORE = $keyStore
    & $Gradle -p (Join-Path $repo 'android') assembleDebug assembleRelease --no-daemon
    if ($LASTEXITCODE -ne 0) { throw 'Android build failed.' }
    Copy-Item -LiteralPath (Join-Path $repo 'android/app/build/outputs/apk/debug/app-debug.apk') -Destination (Join-Path $repo 'dist/ScreenKeyBridge-Android-current-debug.apk') -Force
    Copy-Item -LiteralPath (Join-Path $repo 'android/app/build/outputs/apk/release/app-release.apk') -Destination (Join-Path $repo 'dist/ScreenKeyBridge-Android-release.apk') -Force
    Write-Host 'Android APKs are in dist. The debug APK updates this machine debug install; the release APK uses the separate private release key.'
} finally {
    $env:JAVA_HOME = $oldJava
    $env:PATH = $oldPath
    $env:SCREENKEY_KEYSTORE = $oldStore
    $env:SCREENKEY_SIGN_PASSWORD = $oldPassword
}
