<#
.SYNOPSIS
    Builds the Release APK of the Chatcheology viewer and installs it on the phone connected by USB.

.DESCRIPTION
    1. Checks with "adb devices" that exactly one phone is connected and authorised (emulators are
       ignored), and says what to do if not.
    2. Builds the Release APK (signed with the key from %USERPROFILE%\.chatcheology\signing.props or the
       CHATCHEOLOGY_* environment variables when set; otherwise with the SDK's debug key).
    3. Runs tools\Check-AndroidRelease.ps1 on it and stops if any check fails.
    4. Installs it with "adb install -r", which keeps the app's data (imported archives, settings).

    It never pushes files to the phone, never reads anything from it, and never touches the archive.
    Importing the archive is done in the app itself.

.PARAMETER Serial
    The phone's serial number, when more than one phone is connected (see "adb devices").

.PARAMETER SkipBuild
    Install the APK already built, without building it again.

.EXAMPLE
    .\Chatcheology.Android\Install-OnPhone.ps1
#>
[CmdletBinding()]
param(
    [string]$Serial,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

$project = $PSScriptRoot
$repo = Split-Path -Parent $project
$apk = Join-Path $project 'bin\Release\net10.0-android\org.chatcheology.viewer-Signed.apk'

# adb from the Android SDK.
$adb = $null
foreach ($sdk in @($env:ANDROID_HOME, $env:ANDROID_SDK_ROOT, 'C:\Program Files (x86)\Android\android-sdk', "$env:LOCALAPPDATA\Android\Sdk")) {
    if ($sdk -and (Test-Path -LiteralPath (Join-Path $sdk 'platform-tools\adb.exe'))) {
        $adb = Join-Path $sdk 'platform-tools\adb.exe'
        break
    }
}

if (-not $adb) {
    Write-Host 'adb was not found. Install the Android SDK platform-tools, or set ANDROID_HOME.'
    exit 1
}

# 1. The phone.
& $adb start-server | Out-Null
$lines = & $adb devices | Select-Object -Skip 1 | Where-Object { $_ -match '\S' }
$phones = @($lines | ForEach-Object {
    $parts = ($_ -split '\s+')
    [pscustomobject]@{ Serial = $parts[0]; State = $parts[1] }
} | Where-Object { $_.Serial -notlike 'emulator-*' })

Write-Host 'adb devices:'
if ($phones.Count -eq 0) { Write-Host '  (no phone)' } else { $phones | ForEach-Object { Write-Host "  $($_.Serial)  $($_.State)" } }

if ($Serial) {
    $phones = @($phones | Where-Object { $_.Serial -eq $Serial })
}

if ($phones.Count -eq 0) {
    Write-Host ''
    Write-Host 'No phone is connected. Connect it with a USB cable, turn on USB debugging'
    Write-Host '(Settings > Developer options), allow the computer when the phone asks, then run this again.'
    exit 1
}

if ($phones.Count -gt 1) {
    Write-Host ''
    Write-Host 'More than one phone is connected. Run again with -Serial <serial> from the list above.'
    exit 1
}

$phone = $phones[0]

if ($phone.State -eq 'unauthorized') {
    Write-Host ''
    Write-Host 'The phone has not allowed this computer yet. Unlock it, accept the "Allow USB debugging?" prompt, then run this again.'
    exit 1
}

if ($phone.State -ne 'device') {
    Write-Host ''
    Write-Host "The phone is connected but not ready (state: $($phone.State)). Reconnect the cable and run this again."
    exit 1
}

# 2. Build.
if (-not $SkipBuild) {
    Write-Host ''
    Write-Host 'Building the Release APK...'
    & dotnet build (Join-Path $project 'Chatcheology.Android.csproj') -c Release
    if ($LASTEXITCODE -ne 0) {
        Write-Host 'The build failed; nothing was installed.'
        exit 1
    }
}

if (-not (Test-Path -LiteralPath $apk)) {
    Write-Host "No APK at $apk; nothing was installed."
    exit 1
}

# 3. Check.
Write-Host ''
Write-Host 'Checking the APK...'
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repo 'tools\Check-AndroidRelease.ps1') -Apk $apk
if ($LASTEXITCODE -ne 0) {
    Write-Host 'The APK did not pass its checks; nothing was installed.'
    exit 1
}

# 4. Install, keeping the app's data.
Write-Host ''
Write-Host "Installing on $($phone.Serial)..."
& $adb -s $phone.Serial install -r $apk
if ($LASTEXITCODE -ne 0) {
    Write-Host ''
    Write-Host 'The install failed. If it says the signatures do not match, the app on the phone was signed'
    Write-Host 'with a different key: uninstall it on the phone first (this deletes its imported archives).'
    exit 1
}

Write-Host ''
Write-Host 'Installed. Open Chatcheology on the phone.'
