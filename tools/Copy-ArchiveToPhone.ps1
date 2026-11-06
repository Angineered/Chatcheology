<#
.SYNOPSIS
    Copies an exported Chatcheology archive folder to the phone's Download folder, ready for the app's
    "Import archive...".

.DESCRIPTION
    1. Checks the folder is an exported archive (it has CONTENTS.sha256).
    2. Checks with "adb devices" that exactly one phone is connected and authorised (emulators ignored).
    3. Stops if a folder with the same name is already in the phone's Download folder (never overwrites).
    4. Copies the folder with "adb push" and adds an empty .nomedia file so the gallery ignores its photos.

    It only reads the archive folder and only writes the new folder on the phone. Output is counts only:
    no file names or paths. After importing in the app, the copied folder may be deleted on the phone.

.PARAMETER ArchiveFolder
    The exported archive folder on this computer.

.PARAMETER Serial
    The phone's serial number, when more than one phone is connected (see "adb devices").

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\tools\Copy-ArchiveToPhone.ps1 -ArchiveFolder 'D:\Exports\archive-2026-11'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ArchiveFolder,
    [string]$Serial
)

$ErrorActionPreference = 'Stop'

# 1. The archive.
if (-not (Test-Path -LiteralPath $ArchiveFolder -PathType Container)) {
    Write-Host 'The archive folder was not found.'
    exit 1
}

$source = (Resolve-Path -LiteralPath $ArchiveFolder).ProviderPath.TrimEnd('\')

if (-not (Test-Path -LiteralPath (Join-Path $source 'CONTENTS.sha256'))) {
    Write-Host 'That folder has no CONTENTS.sha256, so it is not an exported archive. Nothing was copied.'
    exit 1
}

$name = Split-Path -Leaf $source

# Spaces are fine (the export names folders "Chatcheology archive <date> <time>"); quotes and shell
# characters are not, because the name is used inside single-quoted phone shell commands.
if ($name -notmatch '^[A-Za-z0-9 ._()-]+$') {
    Write-Host 'Rename the archive folder to letters, digits, spaces, dot, dash, underscore or brackets only, then run this again.'
    exit 1
}

$files = @(Get-ChildItem -LiteralPath $source -Recurse -File -Force)
$bytes = ($files | Measure-Object -Property Length -Sum).Sum
Write-Host ("Archive: {0} files, {1:N0} MB" -f $files.Count, ($bytes / 1MB))

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

function Invoke-Adb([string[]]$arguments) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = @(& $adb @arguments 2>&1 | ForEach-Object { "$_" })
        return [pscustomobject]@{ Code = $LASTEXITCODE; Output = $output }
    }
    finally {
        $ErrorActionPreference = $previous
    }
}

function Test-Emulator([string]$serial) {
    if ($serial -like 'emulator-*') { return $true }
    foreach ($property in @('ro.kernel.qemu', 'ro.boot.qemu')) {
        if (((Invoke-Adb @('-s', $serial, 'shell', 'getprop', $property)).Output -join '').Trim() -eq '1') { return $true }
    }
    return $false
}

# 2. The phone.
Invoke-Adb @('start-server') | Out-Null
$lines = (Invoke-Adb @('devices')).Output | Select-Object -Skip 1 | Where-Object { $_ -match '^\S+\s+\S+$' }
$devices = @($lines | ForEach-Object {
    $parts = ($_ -split '\s+')
    [pscustomobject]@{ Serial = $parts[0]; State = $parts[1] }
})
$phones = @($devices | Where-Object { $_.Serial -notlike 'emulator-*' -and -not ($_.State -eq 'device' -and (Test-Emulator $_.Serial)) })

Write-Host 'adb devices:'
if ($phones.Count -eq 0) { Write-Host '  (no phone)' } else { $phones | ForEach-Object { Write-Host "  $($_.Serial)  $($_.State)" } }

if ($Serial) { $phones = @($phones | Where-Object { $_.Serial -eq $Serial }) }

if ($phones.Count -eq 0) {
    Write-Host 'No phone is connected. Connect it by USB, turn on USB debugging, allow the computer, then run this again.'
    exit 1
}
if ($phones.Count -gt 1) {
    Write-Host 'More than one phone is connected. Run again with -Serial <serial> from the list above.'
    exit 1
}
$phone = $phones[0]
if ($phone.State -eq 'unauthorized') {
    Write-Host 'The phone has not allowed this computer yet. Unlock it, accept "Allow USB debugging?", then run this again.'
    exit 1
}
if ($phone.State -ne 'device') {
    Write-Host "The phone is not ready (state: $($phone.State)). Reconnect the cable and run this again."
    exit 1
}

# 3. Never overwrite.
$target = "/sdcard/Download/$name"
$exists = Invoke-Adb @('-s', $phone.Serial, 'shell', "if [ -e '$target' ]; then echo yes; else echo no; fi")
if (($exists.Output -join '').Trim() -ne 'no') {
    Write-Host 'A folder with this name is already in the phone''s Download folder (or the phone could not be checked).'
    Write-Host 'Delete it on the phone, or rename the archive folder here, then run this again. Nothing was copied.'
    exit 1
}

# 4. Copy, then .nomedia.
Write-Host 'Copying to the phone''s Download folder...'
$push = Invoke-Adb @('-s', $phone.Serial, 'push', $source, '/sdcard/Download/')
if ($push.Code -ne 0) {
    Write-Host 'The copy failed. Delete the partly copied folder on the phone, then run this again.'
    exit 1
}

$touch = Invoke-Adb @('-s', $phone.Serial, 'shell', "touch '$target/.nomedia'")
if ($touch.Code -ne 0) {
    Write-Host 'Copied, but the .nomedia file could not be added; the gallery may show the archive''s photos.'
    exit 1
}

$count = Invoke-Adb @('-s', $phone.Serial, 'shell', "find '$target' -type f ! -name .nomedia | wc -l")
Write-Host ("Done: {0} files on the phone (expected {1})." -f ($count.Output -join '').Trim(), $files.Count)
Write-Host 'On the phone: Chatcheology > Import archive... > pick the folder in Download.'
