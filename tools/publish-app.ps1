<#
.SYNOPSIS
    Veröffentlicht NexusSSH als Native-AOT-Build (Release, self-contained) samt Rust-Kern – für x64 oder ARM64.

.DESCRIPTION
    Standard-Ziel ist %LOCALAPPDATA%\NexusSSH.build\publish\app – außerhalb des Repos, weil X: eine SMB-Freigabe
    ist. Die CI übergibt mit -Output einen Ordner im Arbeitsverzeichnis.

    Native AOT braucht den MSVC-Linker (Visual Studio Build Tools mit „Desktopentwicklung mit C++“). Der ILCompiler
    sucht ihn über vswhere.exe, das nicht im PATH liegt – deshalb wird der Installer-Ordner hier ergänzt.

    Mit -Arch arm64 entsteht die Fassung für Windows auf ARM (Ziel dann ...\publish\app-arm64). Das braucht
    zusätzlich: rustup target add aarch64-pc-windows-msvc, die ARM64-Werkzeuge von MSVC und clang (LLVM). Starten
    lässt sich das Ergebnis nur auf einem ARM-Rechner – der Selbsttest gehört dorthin.

    Nicht mit ausgeliefert werden die Debug-Symbole (.pdb) und die Bibliotheken des Windows App SDK für maschinelles
    Lernen (onnxruntime, DirectML): NexusSSH benutzt sie nicht, und sie machen ein Drittel des Ordners aus.
#>
[CmdletBinding()]
param(
    [string]$Output = '',
    [ValidateSet('x64', 'arm64')]
    [string]$Arch = 'x64'
)

$ErrorActionPreference = 'Stop'
if (-not $Output) { $Output = Join-Path $env:LOCALAPPDATA ('NexusSSH.build\publish\app' + $(if ($Arch -eq 'x64') { '' } else { "-$Arch" })) }
$installer = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if (-not (Test-Path (Join-Path $installer 'vswhere.exe'))) {
    throw 'vswhere.exe nicht gefunden – Visual Studio Build Tools mit C++ installieren.'
}

$env:PATH = "$installer;$env:PATH"
$root = Split-Path $PSScriptRoot -Parent

# Für ARM64 baut die Krypto-Bibliothek des Kerns mit clang. Das von Visual Studio („C++-Clang-Tools“) steht nicht
# im Suchpfad – ist kein anderes da, wird es hier gesucht.
if ($Arch -eq 'arm64' -and -not (Get-Command clang -ErrorAction SilentlyContinue)) {
    $llvm = & (Join-Path $installer 'vswhere.exe') -all -products * -find 'VC\Tools\Llvm\x64\bin\clang.exe' | Select-Object -First 1
    if (-not $llvm) { throw 'clang nicht gefunden – LLVM installieren oder in Visual Studio die „C++-Clang-Tools“.' }
    $env:PATH = "$(Split-Path $llvm -Parent);$env:PATH"
}

# Ein alter Stand im Ziel würde mit ausgeliefert (dotnet publish räumt nicht auf).
if (Test-Path $Output) { Get-ChildItem $Output -Force | Remove-Item -Recurse -Force }

dotnet publish (Join-Path $root 'frontend\NexusSSH.App\NexusSSH.App.csproj') -c Release -r "win-$Arch" -o $Output -nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish fehlgeschlagen ($LASTEXITCODE)." }

# Das Programm für MCP-Programme, neben der App.
dotnet publish (Join-Path $root 'frontend\NexusSSH.Mcp\NexusSSH.Mcp.csproj') -c Release -r "win-$Arch" -o $Output -nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish von nexusssh-mcp fehlgeschlagen ($LASTEXITCODE)." }

foreach ($required in 'NexusSSH.exe', 'nexusssh-mcp.exe', 'nexus_core.dll','Assets\terminal\index.html', 'Assets\terminal\vendor\xterm.js', 'Assets\NexusSSH.ico') {
    if (-not (Test-Path (Join-Path $Output $required))) { throw "$required fehlt im veröffentlichten Ordner." }
}

Get-ChildItem $Output -Recurse -Include '*.pdb' | Remove-Item -Force
foreach ($unused in 'onnxruntime.dll', 'DirectML.dll') {
    $path = Join-Path $Output $unused
    if (Test-Path $path) { Remove-Item $path -Force }
}

$exe = Join-Path $Output 'NexusSSH.exe'
'{0} – Exe {1:N1} MB, Ordner {2:N1} MB' -f $exe, ((Get-Item $exe).Length / 1MB), ((Get-ChildItem $Output -Recurse -File | Measure-Object Length -Sum).Sum / 1MB)
