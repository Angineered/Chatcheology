<#
.SYNOPSIS
    Read-only search of a drive or folder for WhatsApp backups, exports and media.

.DESCRIPTION
    Matches on file names and folder paths only. It never opens, copies, moves or
    changes any file. By default it prints counts only: no file names or paths.
    Add -ShowPaths to also list where each match is (for your eyes only).

    Looks for:
      Database   msgstore*.db / msgstore*.db.crypt*, wa.db, ChatStorage.sqlite,
                 and a 'key' file inside a WhatsApp folder
      Export     WhatsApp Chat*.txt / .zip
      Media      IMG/VID/AUD/PTT/DOC/STK-YYYYMMDD-WA* (date taken from the name)
      iPhone     Manifest.db / Manifest.mbdb / Manifest.plist under MobileSync\Backup
      Samsung    *.sbu, or anything under a SmartSwitch / Kies backup folder
      Container  archives, disk images, backup sets and mailboxes (.pst/.ost/.mbox),
                 which can hold an export without showing its name

    Media dates are checked against two windows:
      start gap  on or before 2013-06-14
      2015 gap   2015-03-22 to 2015-04-10

.PARAMETER Root
    Drive or folder to search, e.g. F:\

.PARAMETER ShowPaths
    Also list the full path of every database, export, iPhone and Samsung match,
    and of media dated inside either window.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\tools\Find-WhatsAppArtifacts.ps1 -Root F:\

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\tools\Find-WhatsAppArtifacts.ps1 -Root F:\ -ShowPaths
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Root,

    [switch]$ShowPaths
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
    Write-Error "Not a folder or drive: $Root"
    exit 1
}

$StartGapEnd = 20130614
$Gap2015From = 20150322
$Gap2015To = 20150410

$mediaPattern = '^(IMG|VID|AUD|PTT|DOC|STK)-(\d{8})-WA'
$dbPattern = '^msgstore.*\.db(\.crypt\d*)?$'

$databases = New-Object System.Collections.Generic.List[object]
$exports = New-Object System.Collections.Generic.List[string]
$iphone = New-Object System.Collections.Generic.List[string]
$samsung = New-Object System.Collections.Generic.List[string]
$mediaInWindow = New-Object System.Collections.Generic.List[object]
$mediaByMonth = @{}
$mediaByType = @{}
$scanned = 0

# Containers that can hold a phone backup without exposing its file names.
$containerExts = @('.zip', '.rar', '.7z', '.tar', '.gz', '.tgz', '.ab', '.bbb', '.ipd', '.img', '.iso',
    '.bin', '.dd', '.raw', '.vhd', '.vhdx', '.vmdk', '.dmg', '.tib', '.mrimg', '.wim', '.bkf', '.bak',
    '.spbm', '.nbu', '.nbf', '.pst', '.ost', '.mbox')
$containers = New-Object System.Collections.Generic.List[object]
$bigFileBytes = 500MB
$bigByExt = @{}

Write-Host "Searching $Root (read-only, names only) ..."

$walkErrors = @()
Get-ChildItem -LiteralPath $Root -Recurse -File -Force -ErrorAction SilentlyContinue -ErrorVariable +walkErrors |
    ForEach-Object {
        $scanned++
        if ($scanned % 50000 -eq 0) { Write-Host "  ... $scanned files checked" }

        $name = $_.Name
        $path = $_.FullName
        $ext = $_.Extension.ToLowerInvariant()
        if (-not $ext) { $ext = '(none)' }

        if ($_.Length -ge $bigFileBytes) {
            if (-not $bigByExt.ContainsKey($ext)) { $bigByExt[$ext] = [pscustomobject]@{ Count = 0; Bytes = [long]0 } }
            $bigByExt[$ext].Count++
            $bigByExt[$ext].Bytes += $_.Length
        }
        if ($containerExts -contains $ext -or $path -match '\\WindowsImageBackup\\') {
            $containers.Add([pscustomobject]@{
                Ext      = $ext
                Modified = $_.LastWriteTime.ToString('yyyy-MM')
                Bytes    = $_.Length
                Path     = $path
            })
        }

        if ($name -match $mediaPattern) {
            $kind = $Matches[1].ToUpperInvariant()
            $stamp = $Matches[2]
            $day = [int]$stamp
            $year = [int]$stamp.Substring(0, 4)
            if ($year -lt 2009 -or $year -gt 2030) { return }

            $month = $stamp.Substring(0, 4) + '-' + $stamp.Substring(4, 2)
            $mediaByMonth[$month] = 1 + [int]$mediaByMonth[$month]
            $mediaByType[$kind] = 1 + [int]$mediaByType[$kind]

            if ($day -le $StartGapEnd) {
                $mediaInWindow.Add([pscustomobject]@{ Window = 'start gap'; Day = $day; Path = $path })
            }
            elseif ($day -ge $Gap2015From -and $day -le $Gap2015To) {
                $mediaInWindow.Add([pscustomobject]@{ Window = '2015 gap'; Day = $day; Path = $path })
            }
            return
        }

        if ($name -match $dbPattern -or $name -eq 'wa.db' -or $name -eq 'ChatStorage.sqlite' -or
            ($name -eq 'key' -and $path -match 'whatsapp')) {
            $format = if ($name -match '\.(crypt\d*)$') { $Matches[1] }
                      elseif ($name -eq 'key') { 'key file' }
                      else { 'plain' }
            $dated = if ($name -match '(\d{4})-(\d{2})-(\d{2})') { "$($Matches[1])-$($Matches[2])-$($Matches[3])" }
                     else { '(no date in name)' }
            $databases.Add([pscustomobject]@{
                Kind     = if ($name -eq 'ChatStorage.sqlite') { 'iPhone ChatStorage' } elseif ($name -eq 'key') { 'key' } elseif ($name -eq 'wa.db') { 'wa.db (contacts)' } else { 'msgstore' }
                Format   = $format
                NameDate = $dated
                Modified = $_.LastWriteTime.ToString('yyyy-MM-dd')
                SizeMB   = [math]::Round($_.Length / 1MB, 2)
                Path     = $path
            })
            return
        }

        if ($name -match '^WhatsApp Chat.*\.(txt|zip)$') { $exports.Add($path); return }

        if ($path -match '\\MobileSync\\Backup\\' -and $name -match '^Manifest\.(db|mbdb|plist)$') { $iphone.Add($path); return }

        if ($name -match '\.sbu$' -or $path -match '\\(SmartSwitch|Kies)\\') { $samsung.Add($path); return }
    }

Write-Host ''
Write-Host "Files checked:        $scanned"
Write-Host "Folders not readable: $($walkErrors.Count)"
Write-Host ''

Write-Host "== Database backups: $($databases.Count)"
if ($databases.Count -gt 0) {
    $databases | Group-Object Kind, Format | Sort-Object Name |
        ForEach-Object { Write-Host ("   {0,-35} {1}" -f $_.Name, $_.Count) }
    Write-Host '   By date in name (or last-modified when none):'
    $databases | ForEach-Object {
        $d = if ($_.NameDate -ne '(no date in name)') { $_.NameDate } else { "$($_.Modified) (modified)" }
        [pscustomobject]@{ Kind = $_.Kind; Format = $_.Format; Date = $d; SizeMB = $_.SizeMB }
    } | Sort-Object Date | Format-Table -AutoSize | Out-String | Write-Host
}

Write-Host "== Chat exports (.txt / .zip): $($exports.Count)"
Write-Host "== iPhone backups (manifest files): $($iphone.Count)"
Write-Host "== Samsung backup files: $($samsung.Count)"
Write-Host ''

$mediaTotal = ($mediaByMonth.Values | Measure-Object -Sum).Sum
if (-not $mediaTotal) { $mediaTotal = 0 }
Write-Host "== Dated WhatsApp media: $mediaTotal"
if ($mediaTotal -gt 0) {
    $mediaByType.GetEnumerator() | Sort-Object Name |
        ForEach-Object { Write-Host ("   {0,-5} {1}" -f $_.Name, $_.Value) }
    $startCount = @($mediaInWindow | Where-Object Window -eq 'start gap').Count
    $gapCount = @($mediaInWindow | Where-Object Window -eq '2015 gap').Count
    Write-Host ''
    Write-Host "   On or before 2013-06-14:   $startCount"
    Write-Host "   2015-03-22 to 2015-04-10:  $gapCount"
    Write-Host ''
    Write-Host '   Per month:'
    $mediaByMonth.GetEnumerator() | Sort-Object Name |
        ForEach-Object { Write-Host ("   {0}  {1}" -f $_.Name, $_.Value) }
}

Write-Host ''
Write-Host "== Archives, images and backup containers: $($containers.Count)"
if ($containers.Count -gt 0) {
    $containers | Group-Object Ext | Sort-Object Name | ForEach-Object {
        $gb = [math]::Round((($_.Group | Measure-Object Bytes -Sum).Sum) / 1GB, 2)
        $years = ($_.Group | ForEach-Object { $_.Modified.Substring(0, 4) } | Sort-Object -Unique) -join ', '
        Write-Host ("   {0,-8} {1,5} files  {2,9} GB   modified: {3}" -f $_.Name, $_.Count, $gb, $years)
    }
}

Write-Host ''
Write-Host '== Files of 500 MB or more, by extension:'
if ($bigByExt.Count -eq 0) { Write-Host '   none' }
$bigByExt.GetEnumerator() | Sort-Object { $_.Value.Bytes } -Descending | ForEach-Object {
    Write-Host ("   {0,-8} {1,5} files  {2,9} GB" -f $_.Name, $_.Value.Count, [math]::Round($_.Value.Bytes / 1GB, 2))
}

if ($ShowPaths) {
    Write-Host ''
    Write-Host '==== Paths (-ShowPaths) ===='
    if ($containers.Count) {
        Write-Host '-- Archives, images and backup containers'
        $containers | Sort-Object Path | ForEach-Object {
            Write-Host ("   {0,9} MB  {1}  {2}" -f [math]::Round($_.Bytes / 1MB), $_.Modified, $_.Path)
        }
    }
    if ($databases.Count) {
        Write-Host '-- Databases'
        $databases | Sort-Object NameDate | ForEach-Object { Write-Host "   $($_.Path)" }
    }
    if ($exports.Count) { Write-Host '-- Exports'; $exports | Sort-Object | ForEach-Object { Write-Host "   $_" } }
    if ($iphone.Count) { Write-Host '-- iPhone backups'; $iphone | Sort-Object | ForEach-Object { Write-Host "   $_" } }
    if ($samsung.Count) { Write-Host '-- Samsung'; $samsung | Sort-Object | ForEach-Object { Write-Host "   $_" } }
    if ($mediaInWindow.Count) {
        Write-Host '-- Media inside a gap window'
        $mediaInWindow | Sort-Object Day | ForEach-Object { Write-Host "   [$($_.Window)] $($_.Path)" }
    }
}
