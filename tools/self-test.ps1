<#
.SYNOPSIS
    Startet eine veröffentlichte NexusSSH.exe im Selbsttest (--self-test) und meldet, ob sie läuft.

.DESCRIPTION
    Unter Native AOT fallen manche Fehler erst zur Laufzeit auf (z. B. eine Liste, die XAML nicht anzeigen kann).
    Der Selbsttest startet die App mit Beispieldaten in einem Fenster außerhalb des Bildschirms, lädt die
    Ansichten und beendet sie nach ein paar Sekunden: Exit 0 = keine Ausnahme, 3 = Ausnahmen (stehen in app.log).

    Die App schreibt dabei in einen eigenen Datenordner im Temp-Verzeichnis, nie in die Daten des Benutzers.

        ./tools/self-test.ps1                       # prüft %LOCALAPPDATA%\NexusSSH.build\publish\app\NexusSSH.exe
        ./tools/self-test.ps1 -Exe pfad\NexusSSH.exe

    Mit -TestServer (Pfad zu nexus-testserver.exe) folgt ein zweiter Durchgang mit einer echten Verbindung: Die
    App verbindet sich mit dem Testserver auf 127.0.0.1, liest die Dateiliste und schickt eine Datei hin und
    zurück (--self-test-live). So ist auch der Weg durch den Rust-Kern unter Native AOT geprüft.

        ./tools/self-test.ps1 -TestServer "$env:LOCALAPPDATA\NexusSSH.build\cargo\debug\nexus-testserver.exe"
#>
[CmdletBinding()]
param(
    [string]$Exe = (Join-Path $env:LOCALAPPDATA 'NexusSSH.build\publish\app\NexusSSH.exe'),
    # Ein Dienst ohne Bildschirm (Sitzung 0) kann kein WinUI-Fenster öffnen. Mit diesem Schalter gilt das als
    # „nicht prüfbar“ statt als Fehler.
    [switch]$AllowNoDesktop,
    # nexus-testserver.exe: Dann prüft ein zweiter Durchgang Verbindung, Dateiliste und einen Transfer.
    [string]$TestServer = '',
    # So viele Zeilen schreibt der Testserver für die Messung des Terminals (flood).
    [int]$FloodLines = 200000
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Exe)) { throw "$Exe nicht gefunden." }

$data = Join-Path ([IO.Path]::GetTempPath()) ("nexusssh-selftest-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $data | Out-Null
$log = Join-Path $data 'logs\app.log'
$previous = $env:NEXUSSSH_DATA_DIR
$env:NEXUSSSH_DATA_DIR = $data
try {
    # Ein Dienst läuft in Sitzung 0: Dort entsteht kein Fenster, und die App beendet sich womöglich gar nicht.
    $interactive = (Get-Process -Id $PID).SessionId -ne 0
    Write-Output "Selbsttest in Sitzung $((Get-Process -Id $PID).SessionId) ($(if ($interactive) { 'mit' } else { 'ohne' }) Bildschirm)."
    $process = Start-Process $Exe -ArgumentList '--self-test' -PassThru
    if (-not $process.WaitForExit(60000)) {
        Stop-Process -Id $process.Id -Force
        if (Test-Path $log) { Write-Output '--- app.log'; Get-Content $log -Tail 40 }
        if ($AllowNoDesktop -and -not $interactive) {
            Write-Output '::warning::Selbsttest nicht möglich: Dieser Runner läuft als Dienst ohne Bildschirm (Sitzung 0), die App kam nicht zum Ende.'
            exit 0
        }

        Write-Output "::error::Selbsttest: Die App hat sich nach 60 s nicht beendet (Sitzung $((Get-Process -Id $PID).SessionId))."
        exit 1
    }

    $code = $process.ExitCode
    if (Test-Path $log) { Write-Output '--- app.log'; Get-Content $log -Tail 40 }
    if ($code -eq 0) {
        Write-Output 'Selbsttest bestanden: Die App startet, lädt ihre Ansichten und beendet sich ohne Ausnahme.'
        if (-not $TestServer) { exit 0 }

        # Zweiter Durchgang: echte Verbindung zum Testserver auf einem freien Port, mit eigenem Datenordner,
        # eigenem ~/.ssh und einem eigenen Ordner als Dateisystem des Servers.
        if (-not (Test-Path $TestServer)) { Write-Output "::error::$TestServer nicht gefunden."; exit 1 }
        $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0); $listener.Start()
        $port = $listener.LocalEndpoint.Port; $listener.Stop()
        $files = Join-Path $data 'server-files'; $ssh = Join-Path $data 'ssh'
        New-Item -ItemType Directory -Force $files, $ssh | Out-Null
        Set-Content (Join-Path $files 'readme.txt') 'self-test'
        Remove-Item $log -ErrorAction SilentlyContinue
        $previousSsh = $env:NEXUSSSH_SSH_DIR
        $env:NEXUSSSH_SSH_DIR = $ssh
        $server = Start-Process $TestServer -ArgumentList "$port", 'demo', 'demo', "`"$files`"" -PassThru -WindowStyle Hidden
        try {
            Start-Sleep -Seconds 2
            $live = Start-Process $Exe -ArgumentList '--self-test-live', "$port", '--self-test-flood', "$FloodLines" -PassThru
            if (-not $live.WaitForExit(90000)) {
                Stop-Process -Id $live.Id -Force
                if (Test-Path $log) { Write-Output '--- app.log'; Get-Content $log -Tail 40 }
                Write-Output '::error::Selbsttest mit Verbindung: Die App hat sich nach 90 s nicht beendet.'
                exit 1
            }

            if (Test-Path $log) { Write-Output '--- app.log'; Get-Content $log -Tail 40 }
            if ($live.ExitCode -ne 0) { Write-Output "::error::Selbsttest mit Verbindung fehlgeschlagen (Exit $($live.ExitCode))."; exit 1 }
            Write-Output 'Selbsttest mit Verbindung bestanden: verbunden, Dateiliste gelesen, eine Datei hin- und zurückgeschickt.'

            # Das Tempo des Terminals: so viele Zeilen je Sekunde kamen vom Testserver (flood) bis ins Terminal.
            # Die Marke in tools/budgets.json ist eine Untergrenze – weit unter dem Üblichen, damit sie nur ein
            # echtes Einbrechen meldet.
            $flood = Get-Content (Join-Path $data 'self-test\flood.json') -Raw | ConvertFrom-Json
            Write-Output ("Terminal: {0} Zeilen ({1:0.0} MB) in {2} ms – {3} Zeilen/s, {4} MB/s" -f $flood.lines, ($flood.bytes / 1MB), $flood.ms, $flood.linesPerSecond, $flood.mbPerSecond)
            $least = (Get-Content (Join-Path $PSScriptRoot 'budgets.json') -Raw | ConvertFrom-Json).floodLinesPerSecond
            if ($flood.linesPerSecond -lt $least) {
                Write-Output "::error::Das Terminal ist langsamer als die Marke ($($flood.linesPerSecond) < $least Zeilen/s, tools/budgets.json)."
                exit 1
            }

            exit 0
        }
        finally {
            Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
            $env:NEXUSSSH_SSH_DIR = $previousSsh
        }
    }

    if ($AllowNoDesktop -and -not $interactive) {
        Write-Output "::warning::Selbsttest nicht möglich: Dieser Runner läuft als Dienst ohne Bildschirm (Exit $code). Die Probe läuft im Windows-Job der CI."
        exit 0
    }

    Write-Output "::error::Selbsttest fehlgeschlagen (Exit $code)."
    exit 1
}
finally {
    $env:NEXUSSSH_DATA_DIR = $previous
    Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
}
