<#
.SYNOPSIS
    Holt xterm.js mit dem Fit- und dem Such-Addon ohne npm ins Repo.

.DESCRIPTION
    Laedt die Pakete als Tarball direkt von registry.npmjs.org, prueft die SHA-512-Summe gegen die hier
    festgehaltenen Werte und legt nur die Laufzeitdateien nach frontend/NexusSSH.App/Assets/terminal/vendor.
    Die Dateien sind eingecheckt; das Skript braucht man nur fuer ein Update.

    Update: Version und Integrity unten aendern (Wert aus https://registry.npmjs.org/<paket>/<version>,
    Feld dist.integrity), Skript ausfuehren, Terminal in der App pruefen, committen.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$packages = @(
    @{
        Name      = '@xterm/xterm'
        Version   = '6.0.0'
        Tarball   = 'https://registry.npmjs.org/@xterm/xterm/-/xterm-6.0.0.tgz'
        Integrity = 'sha512-TQwDdQGtwwDt+2cgKDLn0IRaSxYu1tSUjgKarSDkUM0ZNiSRXFpjxEsvc/Zgc5kq5omJ+V0a8/kIM2WD3sMOYg=='
        Files     = @{ 'lib/xterm.js' = 'xterm.js'; 'css/xterm.css' = 'xterm.css'; 'LICENSE' = 'xterm.LICENSE.txt' }
    },
    @{
        Name      = '@xterm/addon-fit'
        Version   = '0.11.0'
        Tarball   = 'https://registry.npmjs.org/@xterm/addon-fit/-/addon-fit-0.11.0.tgz'
        Integrity = 'sha512-jYcgT6xtVYhnhgxh3QgYDnnNMYTcf8ElbxxFzX0IZo+vabQqSPAjC3c1wJrKB5E19VwQei89QCiZZP86DCPF7g=='
        Files     = @{ 'lib/addon-fit.js' = 'addon-fit.js'; 'LICENSE' = 'addon-fit.LICENSE.txt' }
    },
    @{
        Name      = '@xterm/addon-search'
        Version   = '0.16.0'
        Tarball   = 'https://registry.npmjs.org/@xterm/addon-search/-/addon-search-0.16.0.tgz'
        Integrity = 'sha512-9OeuBFu0/uZJPu+9AHKY6g/w0Czyb/Ut0A5t79I4ULoU4IfU5BEpPFVGQxP4zTTMdfZEYkVIRYbHBX1xWwjeSA=='
        Files     = @{ 'lib/addon-search.js' = 'addon-search.js'; 'LICENSE' = 'addon-search.LICENSE.txt' }
    },
    @{
        # Bilder im Terminal (Sixel, iTerm2) - nur geladen, wenn der Benutzer es in den Einstellungen einschaltet.
        Name      = '@xterm/addon-image'
        Version   = '0.9.0'
        Tarball   = 'https://registry.npmjs.org/@xterm/addon-image/-/addon-image-0.9.0.tgz'
        Integrity = 'sha512-oYWA8/QAr5/Emwl1xL7WCoOqeG3IZfpzEz/OVq1j4Oi9934TQmHiyubClikRf0D/jL3JNiNuz/Lsqx0kXQ02BA=='
        Files     = @{ 'lib/addon-image.js' = 'addon-image.js'; 'LICENSE' = 'addon-image.LICENSE.txt' }
    },
    @{
        # Den Inhalt eines Terminals samt Farben als Text - damit er mitwandert, wenn ein Tab in ein eigenes
        # Fenster umzieht (dort laeuft eine zweite Terminal-Seite). Das Paket bringt keine eigene LICENSE-Datei mit:
        # Es stammt aus demselben Repository wie xterm.js (MIT, "The xterm.js authors"), dessen Lizenztext als
        # xterm.LICENSE.txt daneben liegt.
        Name      = '@xterm/addon-serialize'
        Version   = '0.14.0'
        Tarball   = 'https://registry.npmjs.org/@xterm/addon-serialize/-/addon-serialize-0.14.0.tgz'
        Integrity = 'sha512-uteyTU1EkrQa2Ux6P/uFl2fzmXI46jy5uoQMKEOM0fKTyiW7cSn0WrFenHm5vO5uEXX/GpwW/FgILvv3r0WbkA=='
        Files     = @{ 'lib/addon-serialize.js' = 'addon-serialize.js' }
    }
)

$vendor = Join-Path $PSScriptRoot '..\frontend\NexusSSH.App\Assets\terminal\vendor'
$work = Join-Path ([IO.Path]::GetTempPath()) ("nexusssh-vendor-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $vendor, $work | Out-Null

try {
    $versions = @('Eingecheckte Fremddateien, erzeugt von tools/vendor-xterm.ps1. Nicht von Hand aendern.', '')
    foreach ($package in $packages) {
        $archive = Join-Path $work 'package.tgz'
        Invoke-WebRequest -UseBasicParsing -Uri $package.Tarball -OutFile $archive

        $sha = [Security.Cryptography.SHA512]::Create()
        try { $hash = [Convert]::ToBase64String($sha.ComputeHash([IO.File]::ReadAllBytes($archive))) }
        finally { $sha.Dispose() }
        if ("sha512-$hash" -cne $package.Integrity) {
            throw "$($package.Name) $($package.Version): Pruefsumme stimmt nicht (sha512-$hash)."
        }

        $unpacked = Join-Path $work 'unpacked'
        if (Test-Path $unpacked) { Remove-Item -Recurse -Force $unpacked }
        New-Item -ItemType Directory -Force -Path $unpacked | Out-Null
        # Ausdruecklich das tar von Windows: Ein tar aus Git Bash im PATH haelt "C:" fuer einen Rechnernamen.
        & (Join-Path $env:SystemRoot 'System32\tar.exe') -xzf $archive -C $unpacked
        if ($LASTEXITCODE) { throw "tar konnte $($package.Name) nicht entpacken." }

        foreach ($file in $package.Files.GetEnumerator()) {
            $text = [IO.File]::ReadAllText((Join-Path $unpacked "package/$($file.Key)"))
            # Die Source-Maps liefern wir nicht mit; der Verweis wuerde im WebView nur einen 404 erzeugen.
            $text = [regex]::Replace($text, '(?m)^\s*(//#|/\*#) sourceMappingURL=.*$', '')
            [IO.File]::WriteAllText((Join-Path $vendor $file.Value), $text, [Text.UTF8Encoding]::new($false))
        }

        $versions += "$($package.Name) $($package.Version)  $($package.Integrity)"
        Write-Host "$($package.Name) $($package.Version) -> $vendor"
    }

    [IO.File]::WriteAllLines((Join-Path $vendor 'VERSIONS.txt'), $versions, [Text.UTF8Encoding]::new($false))
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
