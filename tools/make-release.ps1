<#
.SYNOPSIS
    Baut das Auslieferungs-Artefakt für NexusSSH: setup.exe (Installer je Benutzer).

.DESCRIPTION
    setup.exe – Inno Setup, installiert ohne Adminrechte nach %LOCALAPPDATA%\Programs

    Das Artefakt landet in %LOCALAPPDATA%\NexusSSH.build\dist (außerhalb des Repos, X: ist eine SMB-Freigabe).
    Voraussetzung: Inno Setup 6 (ISCC.exe).

        ./tools/make-release.ps1              # Version aus Directory.Build.props
        ./tools/make-release.ps1 -Version 0.6.0
        ./tools/make-release.ps1 -Arch arm64   # Windows auf ARM: die Dateien tragen dann „-arm64“ im Namen
#>
[CmdletBinding()]
param(
    [string]$Version = '',
    [ValidateSet('x64', 'arm64')]
    [string]$Arch = 'x64',
    [string]$Dist = (Join-Path $env:LOCALAPPDATA 'NexusSSH.build\dist')
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent

# --- Version: Parameter oder Directory.Build.props ---
if (-not $Version) {
    $props = Get-Content (Join-Path $repo 'Directory.Build.props') -Raw
    if ($props -notmatch '<Version>([^<]+)</Version>') { throw 'Version nicht in Directory.Build.props gefunden.' }
    $Version = $Matches[1].Trim()
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version '$Version' hat nicht die Form 1.2.3." }
Write-Host "Version: $Version" -ForegroundColor Cyan

# --- Ordner vorbereiten ---
$stageRoot = Join-Path ([IO.Path]::GetDirectoryName($Dist)) 'release-stage'
$appDir = Join-Path $stageRoot 'app'
# Die x64-Dateien heißen wie immer; alles andere trägt die Architektur im Namen.
$suffix = if ($Arch -eq 'x64') { '' } else { "-$Arch" }
foreach ($directory in $stageRoot, $Dist) {
    if (Test-Path $directory) { Get-ChildItem $directory -Force | Remove-Item -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
}

# --- 1. App veröffentlichen (Native AOT, samt Rust-Kern) ---
Write-Host 'Veröffentliche App (Native AOT) ...' -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'publish-app.ps1') -Output $appDir -Arch $Arch

# --- 2. Installer (Inno Setup) ---
$iscc = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
    'C:\Program Files\Inno Setup 6\ISCC.exe'
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { $iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source }

if (-not $iscc) { throw 'Inno Setup (ISCC.exe) nicht gefunden. Installieren mit:  winget install JRSoftware.InnoSetup' }

Write-Host 'Baue setup.exe (Inno Setup) ...' -ForegroundColor Cyan
& $iscc /Q "/DAppVersion=$Version" "/DArch=$Arch" "/DSourceDir=$appDir" "/DRepoDir=$repo" "/DOutputDir=$Dist" (Join-Path $repo 'installer\NexusSSH.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC fehlgeschlagen ($LASTEXITCODE)." }
$setup = Join-Path $Dist "NexusSSH-Setup-$Version$suffix.exe"
Write-Host ('  -> {0} ({1:N1} MB)' -f $setup, ((Get-Item $setup).Length / 1MB)) -ForegroundColor Green

Write-Host "`nFertig. Artefakte in: $Dist" -ForegroundColor Cyan
Get-ChildItem $Dist | ForEach-Object { '{0,8:N1} MB  {1}  sha256 {2}' -f ($_.Length / 1MB), $_.Name, (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower() }
