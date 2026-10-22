<#
.SYNOPSIS
    Checks a built Release APK of the Android viewer against the privacy and store rules of the CH-25
    design. Read only: it inspects the APK and changes nothing.

.DESCRIPTION
    Checks, on the APK's compiled manifest and resources (read with aapt2 from the Android SDK):
      1. no INTERNET permission;
      2. android:allowBackup is false;
      3. android:fullBackupContent and android:dataExtractionRules are present and their rule files
         exclude every domain (root, file, database, sharedpref, external) in every section;
      4. the launcher activity (MAIN/LAUNCHER) is the only exported component;
      5. every FileProvider is not exported, grants per-URI permissions, and its paths file allows
         only files-path "archives/";
      6. zipalign -c -P 16 passes (16 KB page alignment of native libraries).
    Prints one line per check and exits 1 if any fails.

.PARAMETER Apk
    The APK to check. Default: the signed Release APK of Chatcheology.Android in this repository.

.PARAMETER Sdk
    The Android SDK. Default: ANDROID_HOME, ANDROID_SDK_ROOT, or the Visual Studio location.

.EXAMPLE
    .\tools\Check-AndroidRelease.ps1
#>
[CmdletBinding()]
param(
    [string]$Apk,
    [string]$Sdk
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot

if (-not $Apk) {
    $Apk = Join-Path $repo 'Chatcheology.Android\bin\Release\net10.0-android\org.chatcheology.viewer-Signed.apk'
}

if (-not (Test-Path -LiteralPath $Apk)) {
    Write-Host "No APK at $Apk. Build it first: dotnet build .\Chatcheology.Android -c Release"
    exit 1
}

if (-not $Sdk) {
    foreach ($candidate in @($env:ANDROID_HOME, $env:ANDROID_SDK_ROOT, 'C:\Program Files (x86)\Android\android-sdk')) {
        if ($candidate -and (Test-Path -LiteralPath (Join-Path $candidate 'build-tools'))) { $Sdk = $candidate; break }
    }
}

if (-not $Sdk) {
    Write-Host 'The Android SDK was not found. Pass -Sdk <folder>.'
    exit 1
}

# The newest build-tools with zipalign's -P option (35 and later).
$buildTools = Get-ChildItem -LiteralPath (Join-Path $Sdk 'build-tools') -Directory |
    Where-Object { (Test-Path (Join-Path $_.FullName 'aapt2.exe')) -and (Test-Path (Join-Path $_.FullName 'zipalign.exe')) } |
    Sort-Object { [version]($_.Name -replace '[^0-9.].*$', '') } -Descending |
    Select-Object -First 1

if (-not $buildTools -or [version]($buildTools.Name -replace '[^0-9.].*$', '') -lt [version]'35.0.0') {
    Write-Host 'Android build-tools 35 or later (aapt2, zipalign -P) were not found.'
    exit 1
}

$aapt2 = Join-Path $buildTools.FullName 'aapt2.exe'
$zipalign = Join-Path $buildTools.FullName 'zipalign.exe'

# Runs a native tool and returns its exit code and its output, standard error included, as text.
# Windows PowerShell 5.1 turns a native tool's standard error into error records when it is
# redirected, which ErrorActionPreference Stop would turn into a confusing failure: so the
# preference is relaxed for the call and every line is converted to a plain string.
function Invoke-Native([string]$exe, [string[]]$arguments) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = @(& $exe @arguments 2>&1 | ForEach-Object { "$_" })
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }
    return [pscustomobject]@{ ExitCode = $code; Output = $output }
}

# ---------------------------------------------------------------------------------------------
# aapt2's xmltree dump as a tree of @{ Name; Attributes; Children }.
# ---------------------------------------------------------------------------------------------

function Read-XmlTree([string]$file) {
    $result = Invoke-Native $aapt2 @('dump', 'xmltree', '--file', $file, $Apk)
    if ($result.ExitCode -ne 0) {
        Write-Host "aapt2 could not read $file from the APK:"
        $result.Output | Select-Object -First 5 | ForEach-Object { Write-Host "  $_" }
        exit 1
    }
    $lines = $result.Output

    $root = @{ Name = '#root'; Attributes = @{}; Children = New-Object System.Collections.ArrayList; Indent = -1 }
    $stack = New-Object System.Collections.Stack
    $stack.Push($root)

    foreach ($line in $lines) {
        $text = [string]$line
        $indent = $text.Length - $text.TrimStart().Length
        $body = $text.Trim()

        if ($body -match '^E: (?<name>[^\s]+)') {
            while ($stack.Peek().Indent -ge $indent) { [void]$stack.Pop() }
            $element = @{ Name = $Matches.name; Attributes = @{}; Children = New-Object System.Collections.ArrayList; Indent = $indent }
            [void]$stack.Peek().Children.Add($element)
            $stack.Push($element)
        }
        elseif ($body -match '^A: (?:\S*:)?(?<name>[\w-]+)(?:\(0x[0-9a-fA-F]+\))?=(?<value>.*)$') {
            while ($stack.Peek().Indent -ge $indent) { [void]$stack.Pop() }
            $attribute = $Matches.name
            $value = $Matches.value
            if ($value -match '^"(?<quoted>[^"]*)"') { $value = $Matches.quoted }
            $stack.Peek().Attributes[$attribute] = $value
        }
    }

    return $root
}

function Find-Elements($node, [string]$name) {
    $found = New-Object System.Collections.ArrayList
    foreach ($child in $node.Children) {
        if ($child.Name -eq $name) { [void]$found.Add($child) }
        foreach ($inner in (Find-Elements $child $name)) { [void]$found.Add($inner) }
    }
    return $found
}

# Resource id (0x7f......) to its file in the APK, from aapt2's resource table.
$resourceFiles = @{}
$current = $null
$resources = Invoke-Native $aapt2 @('dump', 'resources', $Apk)
if ($resources.ExitCode -ne 0) {
    Write-Host 'aapt2 could not read the resource table of the APK:'
    $resources.Output | Select-Object -First 5 | ForEach-Object { Write-Host "  $_" }
    exit 1
}
foreach ($line in $resources.Output) {
    $text = ([string]$line).Trim()
    if ($text -match '^resource (?<id>0x[0-9a-f]{8}) ') { $current = $Matches.id }
    elseif ($current -and $text -match '\(file\) (?<file>\S+)') { $resourceFiles[$current] = $Matches.file; $current = $null }
}

function Resource-File([string]$reference) {
    if ($reference -match '^@(?<id>0x[0-9a-fA-F]{8})$') { return $resourceFiles[$Matches.id.ToLowerInvariant()] }
    return $null
}

$failures = 0

function Report([bool]$passed, [string]$check, [string]$detail) {
    $state = if ($passed) { 'PASS' } else { 'FAIL' }
    if (-not $passed) { $script:failures++ }
    Write-Host ("{0}  {1}{2}" -f $state, $check, $(if ($detail) { " - $detail" } else { '' }))
}

$manifest = Read-XmlTree 'AndroidManifest.xml'
$application = @(Find-Elements $manifest 'application')[0]

# 1. No INTERNET permission.
$permissions = @(Find-Elements $manifest 'uses-permission' | ForEach-Object { $_.Attributes['name'] })
Report (-not ($permissions -contains 'android.permission.INTERNET')) 'No INTERNET permission' ("permissions: " + $(if ($permissions.Count) { $permissions -join ', ' } else { 'none' }))

# 2. allowBackup false.
Report ($application.Attributes['allowBackup'] -eq 'false') 'allowBackup is false' ("allowBackup=" + $application.Attributes['allowBackup'])

# 3. Backup and extraction rules present and excluding every domain.
$domains = @('root', 'file', 'database', 'sharedpref', 'external')

foreach ($rule in @(@{ Attribute = 'fullBackupContent'; Sections = @('full-backup-content') }, @{ Attribute = 'dataExtractionRules'; Sections = @('cloud-backup', 'device-transfer') })) {
    $file = Resource-File $application.Attributes[$rule.Attribute]

    if (-not $file) {
        Report $false "$($rule.Attribute) present" 'missing'
        continue
    }

    $tree = Read-XmlTree $file
    $missing = @()

    foreach ($section in $rule.Sections) {
        $node = (Find-Elements $tree $section) | Select-Object -First 1
        if (-not $node) { $missing += "$section (absent)"; continue }

        $excluded = @($node.Children | Where-Object { $_.Name -eq 'exclude' -and $_.Attributes['path'] -eq '.' } | ForEach-Object { $_.Attributes['domain'] })
        $includes = @($node.Children | Where-Object { $_.Name -eq 'include' })

        foreach ($domain in $domains) { if (-not ($excluded -contains $domain)) { $missing += "$section/$domain" } }
        if ($includes.Count -gt 0) { $missing += "$section has include rules" }
    }

    Report ($missing.Count -eq 0) "$($rule.Attribute) excludes everything" $(if ($missing.Count) { 'not excluded: ' + ($missing -join ', ') } else { $file })
}

# 4. Only the launcher activity is exported.
$exported = @()
$launchers = 0

foreach ($kind in @('activity', 'activity-alias', 'service', 'receiver', 'provider')) {
    foreach ($component in (Find-Elements $application $kind)) {
        $filters = @(Find-Elements $component 'intent-filter')
        $flag = $component.Attributes['exported']
        $isExported = ($flag -eq 'true') -or (($null -eq $flag) -and $filters.Count -gt 0)

        $isLauncher = $false
        foreach ($filter in $filters) {
            $actions = @(Find-Elements $filter 'action' | ForEach-Object { $_.Attributes['name'] })
            $categories = @(Find-Elements $filter 'category' | ForEach-Object { $_.Attributes['name'] })
            if (($actions -contains 'android.intent.action.MAIN') -and ($categories -contains 'android.intent.category.LAUNCHER')) { $isLauncher = $true }
        }

        if ($isExported -and -not ($kind -eq 'activity' -and $isLauncher)) { $exported += "$kind $($component.Attributes['name'])" }
        if ($isExported -and $kind -eq 'activity' -and $isLauncher) { $launchers++ }
    }
}

Report (($exported.Count -eq 0) -and ($launchers -eq 1)) 'Only the launcher activity is exported' $(if ($exported.Count) { 'also exported: ' + ($exported -join ', ') } else { "$launchers launcher activity" })

# 5. Every FileProvider: not exported, per-URI grants, files/archives/ only.
$providers = @(Find-Elements $application 'provider' | Where-Object {
    @(Find-Elements $_ 'meta-data' | Where-Object { $_.Attributes['name'] -eq 'android.support.FILE_PROVIDER_PATHS' }).Count -gt 0
})

if ($providers.Count -eq 0) {
    Report $false 'FileProviders limited to files/archives/' 'no FileProvider found'
}

foreach ($provider in $providers) {
    $name = $provider.Attributes['name']
    $meta = @(Find-Elements $provider 'meta-data' | Where-Object { $_.Attributes['name'] -eq 'android.support.FILE_PROVIDER_PATHS' })[0]
    $file = Resource-File $meta.Attributes['resource']
    $problems = @()

    if ($provider.Attributes['exported'] -ne 'false') { $problems += 'exported' }
    if ($provider.Attributes['grantUriPermissions'] -ne 'true') { $problems += 'no per-URI grants' }

    if (-not $file) {
        $problems += 'paths file missing'
    }
    else {
        $paths = (Find-Elements (Read-XmlTree $file) 'paths') | Select-Object -First 1
        $entries = @(if ($paths) { $paths.Children } else { @() })
        if ($entries.Count -eq 0) { $problems += 'no paths' }

        foreach ($entry in $entries) {
            $path = ([string]$entry.Attributes['path']).TrimStart('/')
            if ($entry.Name -ne 'files-path' -or ($path -ne 'archives/' -and $path -ne 'archives')) {
                $problems += "$($entry.Name) path=""$($entry.Attributes['path'])"""
            }
        }
    }

    Report ($problems.Count -eq 0) "FileProvider $name limited to files/archives/" $(if ($problems.Count) { $problems -join ', ' } else { "not exported, $file" })
}

# 6. 16 KB page alignment.
$align = Invoke-Native $zipalign @('-c', '-P', '16', '-v', '4', $Apk)
Report ($align.ExitCode -eq 0) 'zipalign -c -P 16 passes' (($align.Output | Select-Object -Last 1) -join '')

if ($failures -gt 0) {
    Write-Host "$failures check(s) failed."
    exit 1
}

Write-Host 'All checks passed.'
exit 0
