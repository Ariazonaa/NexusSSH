<#
.SYNOPSIS
    Misst Startzeit und Speicher einer veröffentlichten NexusSSH.exe und vergleicht sie mit festen Obergrenzen.

.DESCRIPTION
    Die App startet mit Beispieldaten (--demo), ohne den Fokus zu nehmen (--no-activate) und mit einem eigenen
    Datenordner im Temp-Verzeichnis. Gemessen wird

      - die Zeit vom Start des Prozesses bis das Hauptfenster da ist (Median aus mehreren Läufen),
      - der Speicher, den nur dieser Prozess belegt (Private Bytes), ein paar Sekunden danach – ohne die
        Prozesse des WebView2, die Windows getrennt führt.

    Die Zahlen stehen in der Ausgabe (und mit -Json in einer Datei); liegen sie über den Obergrenzen in
    tools/budgets.json, endet das Skript mit 1. Die Grenzen sind bewusst weit: Sie sollen ein unbemerktes Wachsen
    auffangen, nicht die Schwankungen eines Rechners.

        ./tools/measure-start.ps1                      # prüft %LOCALAPPDATA%\NexusSSH.build\publish\app\NexusSSH.exe
        ./tools/measure-start.ps1 -Exe pfad\NexusSSH.exe -Runs 5
#>
[CmdletBinding()]
param(
    [string]$Exe = (Join-Path $env:LOCALAPPDATA 'NexusSSH.build\publish\app\NexusSSH.exe'),
    [int]$Runs = 5,
    [string]$Budgets = (Join-Path $PSScriptRoot 'budgets.json'),
    # Die Zahlen zusätzlich als JSON in diese Datei schreiben.
    [string]$Json = '',
    # Ein Dienst ohne Bildschirm (Sitzung 0) kann kein Fenster öffnen: dann nichts messen statt scheitern.
    [switch]$AllowNoDesktop
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Exe)) { throw "$Exe nicht gefunden." }
if ($AllowNoDesktop -and (Get-Process -Id $PID).SessionId -eq 0) {
    Write-Output '::warning::Messung nicht möglich: Dieser Runner läuft als Dienst ohne Bildschirm.'
    exit 0
}

$data = Join-Path ([IO.Path]::GetTempPath()) ("nexusssh-measure-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $data, (Join-Path $data 'ssh') | Out-Null
$previous = $env:NEXUSSSH_DATA_DIR, $env:NEXUSSSH_SSH_DIR
$env:NEXUSSSH_DATA_DIR = $data
$env:NEXUSSSH_SSH_DIR = Join-Path $data 'ssh'
$starts = @(); $memory = @()
try {
    for ($run = 1; $run -le $Runs; $run++) {
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $process = Start-Process $Exe -ArgumentList '--demo', '--no-activate' -PassThru
        try {
            # Bis das Hauptfenster da ist – höchstens 30 s.
            while ($watch.Elapsed.TotalSeconds -lt 30) {
                $process.Refresh()
                if ($process.HasExited) { throw "Die App hat sich beim Start beendet (Exit $($process.ExitCode))." }
                if ($process.MainWindowHandle -ne 0) { break }
                Start-Sleep -Milliseconds 20
            }

            if ($process.MainWindowHandle -eq 0) { throw 'Nach 30 s war kein Fenster da.' }
            $starts += [int]$watch.ElapsedMilliseconds

            # Kurz zur Ruhe kommen lassen (Ansichten, Terminal-Seite), dann den Speicher lesen.
            Start-Sleep -Seconds 5
            $process.Refresh()
            $memory += [int]($process.PrivateMemorySize64 / 1MB)
        }
        finally {
            if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
            $process.WaitForExit(10000) | Out-Null
        }
    }
}
finally {
    $env:NEXUSSSH_DATA_DIR, $env:NEXUSSSH_SSH_DIR = $previous
    Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
}

function Median([int[]]$values) { $sorted = $values | Sort-Object; $sorted[[int][Math]::Floor(($sorted.Count - 1) / 2)] }
$result = [ordered]@{
    startMs      = Median $starts
    startMsAll   = $starts
    privateMb    = Median $memory
    privateMbAll = $memory
    exeMb        = [Math]::Round((Get-Item $Exe).Length / 1MB, 1)
    folderMb     = [Math]::Round(((Get-ChildItem (Split-Path $Exe) -Recurse -File | Measure-Object Length -Sum).Sum) / 1MB, 1)
}
Write-Output ("Start bis Fenster: {0} ms (Läufe: {1})" -f $result.startMs, ($starts -join ', '))
Write-Output ("Speicher (Private Bytes): {0} MB (Läufe: {1})" -f $result.privateMb, ($memory -join ', '))
Write-Output ("Exe {0} MB, Ordner {1} MB" -f $result.exeMb, $result.folderMb)
if ($Json) { $result | ConvertTo-Json | Set-Content $Json -Encoding utf8 }

$limits = Get-Content $Budgets -Raw | ConvertFrom-Json
$over = @()
foreach ($name in 'startMs', 'privateMb', 'exeMb', 'folderMb') {
    if ($result[$name] -gt $limits.$name) { $over += "$name = $($result[$name]) (Obergrenze $($limits.$name))" }
}

if ($over.Count -gt 0) {
    Write-Output ('::error::Über der Obergrenze: ' + ($over -join '; ') + '. Ist das Wachstum gewollt, die Grenze in tools/budgets.json anheben.')
    exit 1
}

Write-Output 'Innerhalb der Obergrenzen (tools/budgets.json).'
exit 0
