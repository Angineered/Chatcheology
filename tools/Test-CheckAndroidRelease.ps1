<#
.SYNOPSIS
    Shows that tools\Check-AndroidRelease.ps1 fails on what it must refuse (CH-47): builds small
    synthetic APKs (a manifest only, linked with the SDK's aapt2) and checks the result line of the
    permission, INTERNET and exported-component checks for each.

.DESCRIPTION
    Each case is a manifest with the allowed shape (POST_NOTIFICATIONS, RECEIVE_BOOT_COMPLETED,
    AndroidX's signature-level guard, an exported launcher activity, two receivers not exported, the
    boot one with a BOOT_COMPLETED filter) and one change. The other checks (backup rules, FileProvider)
    fail on these bare APKs by design and are not looked at. Nothing outside a temporary folder is
    written; the folder is removed at the end. Prints one line per expectation and exits 1 if any
    is not met.

.PARAMETER Sdk
    The Android SDK. Default: ANDROID_HOME, ANDROID_SDK_ROOT, or the Visual Studio location.

.EXAMPLE
    .\tools\Test-CheckAndroidRelease.ps1
#>
[CmdletBinding()]
param(
    [string]$Sdk
)

$ErrorActionPreference = 'Stop'

if (-not $Sdk) {
    foreach ($candidate in @($env:ANDROID_HOME, $env:ANDROID_SDK_ROOT, 'C:\Program Files (x86)\Android\android-sdk')) {
        if ($candidate -and (Test-Path -LiteralPath (Join-Path $candidate 'build-tools'))) { $Sdk = $candidate; break }
    }
}

if (-not $Sdk) {
    Write-Host 'The Android SDK was not found. Pass -Sdk <folder>.'
    exit 1
}

$aapt2 = Get-ChildItem -LiteralPath (Join-Path $Sdk 'build-tools') -Directory |
    Sort-Object { [version]($_.Name -replace '[^0-9.].*$', '') } -Descending |
    ForEach-Object { Join-Path $_.FullName 'aapt2.exe' } |
    Where-Object { Test-Path -LiteralPath $_ } |
    Select-Object -First 1

$androidJar = Get-ChildItem -LiteralPath (Join-Path $Sdk 'platforms') -Directory |
    Sort-Object { [int]($_.Name -replace '[^0-9]', '') } -Descending |
    ForEach-Object { Join-Path $_.FullName 'android.jar' } |
    Where-Object { Test-Path -LiteralPath $_ } |
    Select-Object -First 1

if (-not $aapt2 -or -not $androidJar) {
    Write-Host 'aapt2 or a platform android.jar was not found in the SDK.'
    exit 1
}

$check = Join-Path $PSScriptRoot 'Check-AndroidRelease.ps1'
$work = Join-Path ([IO.Path]::GetTempPath()) ('chatcheology-release-check-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null

$package = 'org.chatcheology.viewer'
$allowedPermissions = @(
    '<uses-permission android:name="android.permission.POST_NOTIFICATIONS" />',
    '<uses-permission android:name="android.permission.RECEIVE_BOOT_COMPLETED" />',
    "<permission android:name=""$package.DYNAMIC_RECEIVER_NOT_EXPORTED_PERMISSION"" android:protectionLevel=""signature"" />",
    "<uses-permission android:name=""$package.DYNAMIC_RECEIVER_NOT_EXPORTED_PERMISSION"" />"
)
$launcher = '<activity android:name=".MainActivity" android:exported="true"><intent-filter><action android:name="android.intent.action.MAIN" /><category android:name="android.intent.category.LAUNCHER" /></intent-filter></activity>'
$alarm = '<receiver android:name=".NotedDateAlarmReceiver" android:exported="false" />'
$boot = '<receiver android:name=".NotedDateBootReceiver" android:exported="false"><intent-filter><action android:name="android.intent.action.BOOT_COMPLETED" /></intent-filter></receiver>'

function New-Manifest([string[]]$permissions, [string[]]$components) {
    return @"
<?xml version="1.0" encoding="utf-8"?>
<manifest xmlns:android="http://schemas.android.com/apk/res/android" package="$package">
  $($permissions -join "`n  ")
  <application android:allowBackup="false">
    $($components -join "`n    ")
  </application>
</manifest>
"@
}

$allowed = @($launcher, $alarm, $boot)

# Each case: a name, the manifest, and the result each named check must give.
$cases = @(
    @{ Name = 'allowed shape'; Manifest = (New-Manifest $allowedPermissions $allowed)
       Expect = @{ 'No INTERNET permission' = 'PASS'; 'Only the launcher activity is exported' = 'PASS'; 'Permissions are exactly the allowed ones' = 'PASS' } },
    @{ Name = 'allowed without the AndroidX guard'; Manifest = (New-Manifest $allowedPermissions[0..1] $allowed)
       Expect = @{ 'Permissions are exactly the allowed ones' = 'PASS' } },
    @{ Name = 'INTERNET'; Manifest = (New-Manifest ($allowedPermissions + '<uses-permission android:name="android.permission.INTERNET" />') $allowed)
       Expect = @{ 'No INTERNET permission' = 'FAIL'; 'Permissions are exactly the allowed ones' = 'FAIL' } },
    @{ Name = 'SCHEDULE_EXACT_ALARM'; Manifest = (New-Manifest ($allowedPermissions + '<uses-permission android:name="android.permission.SCHEDULE_EXACT_ALARM" />') $allowed)
       Expect = @{ 'No INTERNET permission' = 'PASS'; 'Permissions are exactly the allowed ones' = 'FAIL' } },
    @{ Name = 'USE_EXACT_ALARM'; Manifest = (New-Manifest ($allowedPermissions + '<uses-permission android:name="android.permission.USE_EXACT_ALARM" />') $allowed)
       Expect = @{ 'Permissions are exactly the allowed ones' = 'FAIL' } },
    @{ Name = 'FOREGROUND_SERVICE'; Manifest = (New-Manifest ($allowedPermissions + '<uses-permission android:name="android.permission.FOREGROUND_SERVICE" />') $allowed)
       Expect = @{ 'Permissions are exactly the allowed ones' = 'FAIL' } },
    @{ Name = 'WAKE_LOCK'; Manifest = (New-Manifest ($allowedPermissions + '<uses-permission android:name="android.permission.WAKE_LOCK" />') $allowed)
       Expect = @{ 'Permissions are exactly the allowed ones' = 'FAIL' } },
    @{ Name = 'uses-permission-sdk-23 CAMERA'; Manifest = (New-Manifest ($allowedPermissions + '<uses-permission-sdk-23 android:name="android.permission.CAMERA" />') $allowed)
       Expect = @{ 'Permissions are exactly the allowed ones' = 'FAIL' } },
    @{ Name = 'another declared permission'; Manifest = (New-Manifest ($allowedPermissions + "<permission android:name=""$package.OTHER"" android:protectionLevel=""normal"" />") $allowed)
       Expect = @{ 'Permissions are exactly the allowed ones' = 'FAIL' } },
    @{ Name = 'POST_NOTIFICATIONS missing'; Manifest = (New-Manifest $allowedPermissions[1..3] $allowed)
       Expect = @{ 'Permissions are exactly the allowed ones' = 'FAIL' } },
    @{ Name = 'RECEIVE_BOOT_COMPLETED missing'; Manifest = (New-Manifest @($allowedPermissions[0]) $allowed)
       Expect = @{ 'Permissions are exactly the allowed ones' = 'FAIL' } },
    @{ Name = 'no permissions at all'; Manifest = (New-Manifest @() $allowed)
       Expect = @{ 'No INTERNET permission' = 'PASS'; 'Permissions are exactly the allowed ones' = 'FAIL' } },
    @{ Name = 'boot receiver exported'; Manifest = (New-Manifest $allowedPermissions @($launcher, $alarm, ($boot -replace 'exported="false"', 'exported="true"')))
       Expect = @{ 'Only the launcher activity is exported' = 'FAIL'; 'Permissions are exactly the allowed ones' = 'PASS' } },
    @{ Name = 'boot receiver without exported (filter implies exported)'; Manifest = (New-Manifest $allowedPermissions @($launcher, $alarm, ($boot -replace ' android:exported="false"', '')))
       Expect = @{ 'Only the launcher activity is exported' = 'FAIL' } },
    @{ Name = 'alarm receiver exported'; Manifest = (New-Manifest $allowedPermissions @($launcher, ($alarm -replace 'exported="false"', 'exported="true"'), $boot))
       Expect = @{ 'Only the launcher activity is exported' = 'FAIL' } },
    @{ Name = 'a foreground service (needs FOREGROUND_SERVICE)'; Manifest = (New-Manifest ($allowedPermissions + '<uses-permission android:name="android.permission.FOREGROUND_SERVICE" />', '<uses-permission android:name="android.permission.FOREGROUND_SERVICE_SHORT_SERVICE" />') ($allowed + '<service android:name=".Reminder" android:exported="false" android:foregroundServiceType="shortService" />'))
       Expect = @{ 'Permissions are exactly the allowed ones' = 'FAIL' } }
)

$failed = 0
$index = 0

try {
    foreach ($case in $cases) {
        $index++
        $folder = Join-Path $work $index
        New-Item -ItemType Directory -Path $folder | Out-Null
        $manifestPath = Join-Path $folder 'AndroidManifest.xml'
        $apk = Join-Path $folder 'case.apk'
        [IO.File]::WriteAllText($manifestPath, $case.Manifest, (New-Object Text.UTF8Encoding $false))

        $previous = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            $link = @(& $aapt2 link -o $apk -I $androidJar --manifest $manifestPath 2>&1 | ForEach-Object { "$_" })
            $linked = $LASTEXITCODE -eq 0
            $output = @(& powershell -NoProfile -ExecutionPolicy Bypass -File $check -Apk $apk -Sdk $Sdk 2>&1 | ForEach-Object { "$_" })
            $code = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $previous
        }

        if (-not $linked) {
            $failed++
            Write-Host "FAIL  $($case.Name): aapt2 link failed: $($link | Select-Object -First 3)"
            continue
        }

        # The bare APKs have no backup rules or FileProvider, so the whole check always exits 1.
        if ($code -ne 1) {
            $failed++
            Write-Host "FAIL  $($case.Name): the release check exited $code, expected 1"
        }

        foreach ($name in $case.Expect.Keys) {
            $want = $case.Expect[$name]
            $line = $output | Where-Object { $_ -match ('^(PASS|FAIL)  ' + [regex]::Escape($name) + '( - |$)') } | Select-Object -First 1
            $got = if ($line) { $line.Substring(0, 4) } else { 'absent' }

            if ($got -eq $want) {
                Write-Host "ok    $($case.Name): $name -> $got"
            }
            else {
                $failed++
                Write-Host "FAIL  $($case.Name): $name -> $got, expected $want ($line)"
            }
        }
    }
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failed -gt 0) {
    Write-Host "$failed expectation(s) not met."
    exit 1
}

Write-Host "All $($cases.Count) cases behaved as expected."
exit 0
