# CH-71: the monthly archive refresh (Angie runs this; counts only on screen).
# Run from the repo folder, one step at a time, in this order. Close the desktop viewer first.
#   .\tools\ArchiveRefresh\Run-Refresh.ps1 -Step Pull -Destination <new folder> [-Previous <last month's destination>] [-Serial <id>]
#       (phone connected by USB, unlocked, USB debugging allowed; back up the chats in WhatsApp first. Read-only on the phone.
#        With -Previous only new and changed files come from the phone. A stopped pull: run the same command again.)
#   .\tools\ArchiveRefresh\Run-Refresh.ps1 -Step PsnFetch -PsnManifest <last game list> -PsnDefinitions <definitions file> -PsnDecisions <your decisions csv> -PsnAccountA <name> -PsnAccountB <name> -PsnLocal <a or b name> -PsnOut <new folder>
#       (only when PSN is included; needs a fresh Postman token; asks for it and the other account's id, hidden)
#   .\tools\ArchiveRefresh\Run-Refresh.ps1 -Step Plan   -Workspace <workspace.db> -Msgstore <msgstore.db> -MediaRoot <WhatsApp folder copy> -Conversation <id> -Chat <chat _id> -LocalParticipant <id> -ExpectRestored <n> -ExpectMissing <n> -PlanFolder <new private folder> [PSN parameters]
#   .\tools\ArchiveRefresh\Run-Refresh.ps1 -Step Apply  -Workspace <same> -Msgstore <same> -MediaRoot <same> -PlanFolder <same> -Sha256 <PLAN SHA-256> -BackupRoot <existing folder> -ArchiveOut <new folder> [same PSN parameters]
#   .\tools\ArchiveRefresh\Run-Refresh.ps1 -Step Images -Workspace <workspace.db>      (optional; needs the internet)
#   .\tools\ArchiveRefresh\Run-Refresh.ps1 -Step Copy   -ArchiveOut <archive folder>
# PSN parameters (all or none): -PsnTimestamps <file>[,<file>] -PsnManifest <game list> -PsnAccountA <name> -PsnAccountB <name>
#   -PsnLocal <a or b name> -PsnCurrent <current shared-activity file> -PsnDefinitions <definitions file>
#   After PsnFetch these point into the PsnOut folder: psn-trophy-timestamps.json, psn-game-list.json, psn-trophy-definitions.json.
# A stopped PsnFetch: run the same command again (same -PsnOut); finished games are skipped.
# PsnFetch adds a game only when your decisions csv marks it together; new candidates go to psn-to-review.csv in -PsnOut.
# A stopped Apply: fix the cause and run the same Apply again; finished steps are skipped.
param(
    [Parameter(Mandatory = $true)][ValidateSet('Pull', 'PsnFetch', 'Plan', 'Apply', 'Images', 'Copy')][string]$Step,
    [string]$Workspace,
    [string]$Msgstore,
    [string]$MediaRoot,
    [string]$Conversation,
    [string]$Chat,
    [string]$LocalParticipant,
    [string]$ExpectRestored,
    [string]$ExpectMissing,
    [string]$PlanFolder,
    [string]$Sha256,
    [string]$BackupRoot,
    [string]$ArchiveOut,
    [string[]]$PsnTimestamps,
    [string]$PsnManifest,
    [string]$PsnAccountA,
    [string]$PsnAccountB,
    [string]$PsnLocal,
    [string]$PsnCurrent,
    [string]$PsnDefinitions,
    [string]$PsnOut,
    [string]$PsnDecisions,
    [string]$Destination,
    [string]$Previous,
    [string]$Serial
)

$ErrorActionPreference = 'Stop'

function Build-Tool([string]$project) {
    dotnet build $project -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }
}

function Input-Args {
    if (-not $Workspace -or -not $Msgstore -or -not $MediaRoot -or -not $PlanFolder) { throw "Give -Workspace, -Msgstore, -MediaRoot and -PlanFolder." }
    if (-not (Test-Path -LiteralPath $Workspace -PathType Leaf)) { throw "Workspace not found." }
    if (-not (Test-Path -LiteralPath $Msgstore -PathType Leaf)) { throw "Msgstore not found." }
    if (-not (Test-Path -LiteralPath $MediaRoot -PathType Container)) { throw "Media root folder not found." }
    $a = @('--workspace', $Workspace, '--msgstore', $Msgstore, '--media-root', $MediaRoot, '--plan-folder', $PlanFolder)
    $psn = @($PsnTimestamps, $PsnManifest, $PsnAccountA, $PsnAccountB, $PsnLocal, $PsnCurrent, $PsnDefinitions) | Where-Object { $_ }
    if ($psn.Count -gt 0) {
        if (-not $PsnTimestamps -or -not $PsnManifest -or -not $PsnAccountA -or -not $PsnAccountB -or -not $PsnLocal -or -not $PsnCurrent -or -not $PsnDefinitions) {
            throw "Give all PSN parameters, or none to leave PSN out."
        }
        foreach ($t in $PsnTimestamps) { $a += @('--psn-timestamps', $t) }
        $a += @('--psn-manifest', $PsnManifest, '--psn-account-a', $PsnAccountA, '--psn-account-b', $PsnAccountB,
                '--psn-local', $PsnLocal, '--psn-current', $PsnCurrent, '--psn-definitions', $PsnDefinitions)
    }
    return $a
}

$tool = "tools\ArchiveRefresh\bin\Release\net10.0\ArchiveRefresh.exe"

switch ($Step) {
    'Pull' {
        if (-not $Destination) { throw "Give -Destination (a new folder; with -Previous, best on the same drive as the previous copy)." }
        if ($Previous -and -not (Test-Path -LiteralPath (Join-Path $Previous 'WhatsApp') -PathType Container)) {
            throw "Previous copy not found (it must hold the WhatsApp folder)."
        }
        $adb = 'C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe'
        if (-not (Test-Path -LiteralPath $adb -PathType Leaf)) { throw "adb not found at $adb" }
        $a = @('pull', '--adb', $adb, '--destination', $Destination)
        if ($Previous) { $a += @('--previous', $Previous) }
        if ($Serial) { $a += @('--serial', $Serial) }
        Build-Tool tools\ArchiveRefresh\ArchiveRefresh.csproj
        & $tool @a
        if ($LASTEXITCODE -eq 0) {
            Write-Host ""
            Write-Host "Next: decrypt $(Join-Path $Destination 'Databases\msgstore.db.crypt15'), then Plan with -MediaRoot '$(Join-Path $Destination 'WhatsApp')'."
            Write-Host "Next month: -Previous '$Destination'."
        }
    }
    'PsnFetch' {
        if (-not $PsnManifest -or -not $PsnDefinitions -or -not $PsnDecisions -or -not $PsnAccountA -or -not $PsnAccountB -or -not $PsnLocal -or -not $PsnOut) {
            throw "Give -PsnManifest (last game list), -PsnDefinitions, -PsnDecisions (your decisions csv), -PsnAccountA, -PsnAccountB, -PsnLocal and -PsnOut (a new folder)."
        }
        if (-not (Test-Path -LiteralPath $PsnManifest -PathType Leaf)) { throw "Game list not found." }
        if (-not (Test-Path -LiteralPath $PsnDefinitions -PathType Leaf)) { throw "Definitions file not found." }
        if (-not (Test-Path -LiteralPath $PsnDecisions -PathType Leaf)) { throw "Decisions file not found." }
        Build-Tool tools\MemoryImport\MemoryImport.csproj
        & "tools\MemoryImport\bin\Release\net10.0\MemoryImport.exe" psn-fetch --previous-list $PsnManifest --definitions $PsnDefinitions `
            --decisions $PsnDecisions --account-a $PsnAccountA --account-b $PsnAccountB --local $PsnLocal --out $PsnOut
        if ($LASTEXITCODE -eq 0) {
            Write-Host ""
            Write-Host "Next: Plan with these PSN parameters (and -PsnAccountA, -PsnAccountB, -PsnLocal as above, -PsnCurrent the last plan folder's psn-shared-activity.json):"
            Write-Host ("-PsnTimestamps '{0}' -PsnManifest '{1}' -PsnDefinitions '{2}'" -f `
                (Join-Path $PsnOut 'psn-trophy-timestamps.json'), (Join-Path $PsnOut 'psn-game-list.json'), (Join-Path $PsnOut 'psn-trophy-definitions.json'))
        }
    }
    'Plan' {
        if (-not $Conversation -or -not $Chat -or -not $LocalParticipant -or -not $ExpectRestored -or -not $ExpectMissing) {
            throw "Give -Conversation, -Chat, -LocalParticipant, -ExpectRestored and -ExpectMissing."
        }
        if ($PlanFolder -and (Test-Path -LiteralPath $PlanFolder)) { throw "The plan folder already exists; give a new one." }
        $a = Input-Args
        Build-Tool tools\ArchiveRefresh\ArchiveRefresh.csproj
        & $tool plan @a --conversation $Conversation --chat $Chat --local-participant $LocalParticipant --expect-restored $ExpectRestored --expect-missing $ExpectMissing
    }
    'Apply' {
        if (-not $Sha256 -or -not $BackupRoot -or -not $ArchiveOut) { throw "Give -Sha256, -BackupRoot and -ArchiveOut." }
        if (-not (Test-Path -LiteralPath $BackupRoot -PathType Container)) { throw "Backup root folder not found." }
        $a = Input-Args
        Build-Tool tools\ArchiveRefresh\ArchiveRefresh.csproj
        & $tool apply @a --plan-sha256 $Sha256 --backup-root $BackupRoot --archive-out $ArchiveOut
        if ($LASTEXITCODE -eq 0) {
            Write-Host ""
            Write-Host "Next (copies the archive to the phone):"
            Write-Host ".\tools\ArchiveRefresh\Run-Refresh.ps1 -Step Copy -ArchiveOut '$ArchiveOut'"
        }
    }
    'Images' {
        if (-not $Workspace -or -not (Test-Path -LiteralPath $Workspace -PathType Leaf)) { throw "Give -Workspace (the workspace file)." }
        $folder = Split-Path -Parent (Resolve-Path -LiteralPath $Workspace)
        Build-Tool tools\MemoryImport\MemoryImport.csproj
        & "tools\MemoryImport\bin\Release\net10.0\MemoryImport.exe" fetch-images --workspace-folder $folder
    }
    'Copy' {
        if (-not $ArchiveOut) { throw "Give -ArchiveOut (the exported archive folder)." }
        & powershell -ExecutionPolicy Bypass -File .\tools\Copy-ArchiveToPhone.ps1 -ArchiveFolder $ArchiveOut
    }
}
