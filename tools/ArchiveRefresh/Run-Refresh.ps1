# CH-71: the monthly archive refresh (Angie runs this; counts only on screen).
# Run from the repo folder, one step at a time, in this order. Close the desktop viewer first.
#   .\tools\ArchiveRefresh\Run-Refresh.ps1 -Step Plan   -Workspace <workspace.db> -Msgstore <msgstore.db> -MediaRoot <WhatsApp folder copy> -Conversation <id> -Chat <chat _id> -LocalParticipant <id> -ExpectRestored <n> -ExpectMissing <n> -PlanFolder <new private folder> [PSN parameters]
#   .\tools\ArchiveRefresh\Run-Refresh.ps1 -Step Apply  -Workspace <same> -Msgstore <same> -MediaRoot <same> -PlanFolder <same> -Sha256 <PLAN SHA-256> -BackupRoot <existing folder> -ArchiveOut <new folder> [same PSN parameters]
#   .\tools\ArchiveRefresh\Run-Refresh.ps1 -Step Images -Workspace <workspace.db>      (optional; needs the internet)
#   .\tools\ArchiveRefresh\Run-Refresh.ps1 -Step Copy   -ArchiveOut <archive folder>
# PSN parameters (all or none): -PsnTimestamps <file>[,<file>] -PsnManifest <game list> -PsnAccountA <name> -PsnAccountB <name>
#   -PsnLocal <a or b name> -PsnCurrent <current shared-activity file> -PsnDefinitions <definitions file>
# A stopped Apply: fix the cause and run the same Apply again; finished steps are skipped.
param(
    [Parameter(Mandatory = $true)][ValidateSet('Plan', 'Apply', 'Images', 'Copy')][string]$Step,
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
    [string]$PsnDefinitions
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
