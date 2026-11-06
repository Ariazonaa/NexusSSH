<#
.SYNOPSIS
    Bedient die App über UI Automation durch ihre wichtigsten Wege und meldet, ob jeder Schritt gelang.

.DESCRIPTION
    Der Selbsttest (--self-test) lädt die Ansichten, bedient sie aber nicht. Dieser Lauf tut, was sonst von Hand
    geprüft wurde: Er startet die App mit den Beispieldaten für Prüfläufe (--demo-full) und drückt Knöpfe, füllt Felder und wählt
    Einträge – allein über UI Automation, also ohne Maus und ohne Tasten. Das Fenster nimmt den Fokus nicht
    (--no-activate), der Lauf stört niemanden, der gerade am Rechner arbeitet.

    Die App bekommt einen eigenen Datenordner und ein eigenes ~/.ssh im Temp-Verzeichnis und läuft auf Deutsch,
    damit die Namen der Elemente feststehen. Exit 0 = alle Schritte gelungen, 1 = mindestens einer nicht.

        ./tools/ui-test.ps1                         # prüft den Debug-Build
        ./tools/ui-test.ps1 -Exe pfad\NexusSSH.exe  # z. B. die veröffentlichte AOT-Fassung

    Läuft unter Windows PowerShell 5.1 und unter pwsh. Braucht einen Bildschirm (keine Sitzung 0).
#>
[CmdletBinding()]
param(
    [string]$Exe = (Join-Path $env:LOCALAPPDATA 'NexusSSH.build\artifacts\bin\NexusSSH.App\debug_win-x64\NexusSSH.exe'),
    # Ein Dienst ohne Bildschirm kann kein WinUI-Fenster öffnen. Mit diesem Schalter gilt das als „nicht prüfbar“.
    [switch]$AllowNoDesktop,
    # So lange wird auf ein Element gewartet, in Sekunden.
    [int]$Patience = 15,
    # Nennt nach einem misslungenen Schritt, was gerade zu sehen ist – zum Nachziehen, wenn sich Namen geändert haben.
    [switch]$Dump,
    # Schickt auch echte Tasten (Tippen in der Dateiliste, Strg+A, Umschalt+Pfeil, Strg+L): nur im Windows-Gast, nie am
    # Rechner eines Benutzers. Die App startet dann ohne --no-activate und muss vorn sein.
    [switch]$Keys
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Exe)) { throw "$Exe nicht gefunden." }

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$Automation = [System.Windows.Automation.AutomationElement]
$Walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker

$data = Join-Path ([IO.Path]::GetTempPath()) ("nexusssh-uitest-" + [guid]::NewGuid().ToString('N'))
$ssh = Join-Path $data 'ssh'
New-Item -ItemType Directory -Force $data, $ssh | Out-Null
$log = Join-Path $data 'logs\app.log'

$script:App = $null
$script:Failed = 0

# Die Fenster der App: das Hauptfenster und Aufklapper, die Windows als eigene Fenster führt.
function Get-Roots {
    $ofApp = New-Object System.Windows.Automation.PropertyCondition($Automation::ProcessIdProperty, $script:App.Id)
    $Automation::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $ofApp)
}

# Zu Fuß durch den Baum und nicht in den WebView hinein: Eine Suche über alle Nachfahren bleibt dort stecken.
function Search-Tree($element, [scriptblock]$wanted, [System.Collections.ArrayList]$hits, [int]$depth = 0) {
    if ($depth -gt 40) { return }
    try { $child = $Walker.GetFirstChild($element) } catch { return }
    while ($child) {
        # Ein Element, das inzwischen verschwunden ist (die Ansicht hat gewechselt, eine Liste wurde ersetzt), wird
        # übergangen – seine Geschwister dahinter sollen trotzdem gefunden werden.
        try {
            $current = $child.Current
            if (& $wanted $current) { [void]$hits.Add($child) }
            if ($current.AutomationId -ne 'WebView') { Search-Tree $child $wanted $hits ($depth + 1) }
        }
        catch { }

        try { $child = $Walker.GetNextSibling($child) } catch { return }
    }
}
# Alle Elemente mit diesem Namen oder – mit # davor – dieser AutomationId; -Type schränkt auf eine Art ein
# (Button, ListItem, TabItem, Text, Edit, CheckBox).
function Find-All([string]$what, [string]$type = '') {
    $hits = New-Object System.Collections.ArrayList
    $match = {
        param($current)
        $named = if ($what.StartsWith('#')) { $current.AutomationId -eq $what.Substring(1) } elseif ($what.StartsWith('~')) { $current.Name -match $what.Substring(1) } else { $current.Name -eq $what }
        $named -and (-not $type -or $current.ControlType.ProgrammaticName -eq "ControlType.$type")
    }.GetNewClosure()
    foreach ($root in Get-Roots) { Search-Tree $root $match $hits }
    , $hits
}

function Wait-For([string]$what, [string]$type = '', [switch]$Gone) {
    $until = [DateTime]::UtcNow.AddSeconds($Patience)
    do {
        $hits = Find-All $what $type
        if ($Gone -and $hits.Count -eq 0) { return $true }
        if (-not $Gone -and $hits.Count -gt 0) { return $hits }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $until)
    if ($Gone) { return $false }
    , @()
}

function Get-Pattern($element, $pattern) {
    $found = $null
    if ($element.TryGetCurrentPattern($pattern, [ref]$found)) { $found } else { $null }
}

# Drückt, was sich drücken lässt; ein Listeneintrag ohne „Aufrufen“ wird gewählt.
function Invoke-Element([string]$what, [string]$type = '', [switch]$Last) {
    # Manches Element steht zweimal im Baum (ein Vorschlag der Palette: einmal unter dem Feld, einmal im Fenster
    # des Aufklappers), und nur eines davon nimmt die Bedienung an – also der Reihe nach versuchen. Und ein
    # Element, das gerade erst erscheint oder neu aufgebaut wird, lehnt den ersten Versuch mitunter ab: bis zum
    # Ende der Geduld wiederholen.
    $until = [DateTime]::UtcNow.AddSeconds($Patience)
    $why = "nicht gefunden: $what"
    do {
        $hits = Find-All $what $type
        $ordered = if ($Last -and $hits.Count -gt 1) { $hits[($hits.Count - 1)..0] } else { $hits }
        foreach ($hit in $ordered) {
            try {
                $invoke = Get-Pattern $hit ([System.Windows.Automation.InvokePattern]::Pattern)
                if ($invoke) { $invoke.Invoke(); return }
                $select = Get-Pattern $hit ([System.Windows.Automation.SelectionItemPattern]::Pattern)
                if ($select) { $select.Select(); return }
                $toggle = Get-Pattern $hit ([System.Windows.Automation.TogglePattern]::Pattern)
                if ($toggle) { $toggle.Toggle(); return }
                $why = "lässt sich nicht bedienen: $what"
            }
            catch {
                $why = "lässt sich nicht bedienen: $what ($($_.Exception.GetBaseException().Message.Split("`n")[0].Trim()))"
            }
        }

        Start-Sleep -Milliseconds 300
    } while ([DateTime]::UtcNow -lt $until)
    throw $why
}
# Öffnet ein Menü und wählt einen Eintrag – oder sieht mit -Look nur nach, dass die genannten Einträge da sind,
# und lässt es offen. Ein Menü ist ein eigenes kleines Fenster, das sich beim Öffnen den Vordergrund holt; unter
# --no-activate gibt die App ihn sofort zurück (ForegroundGuard), und davon schließt das Menü wieder – je nach
# Reihenfolge der Ereignisse sofort oder gar nicht, und ebenso, sobald am Rechner jemand tippt oder klickt.
# Deshalb: öffnen, kurz nachsehen, und wenn es schon wieder zu ist, noch einmal.
function Invoke-Menu([string]$opener, [string]$item, [string]$openerType = '', [string[]]$Look = @()) {
    $until = [DateTime]::UtcNow.AddSeconds($Patience * 2)
    $why = "Menü öffnet nicht: $opener"
    do {
        Invoke-Element $opener $openerType
        $soon = [DateTime]::UtcNow.AddMilliseconds(1500)
        do {
            Start-Sleep -Milliseconds 100
            $missing = @($Look | Where-Object { (Find-All $_ 'MenuItem').Count -eq 0 })
            $hits = if ($item) { Find-All $item 'MenuItem' } else { @() }
            if ($missing.Count -eq 0 -and (-not $item -or $hits.Count -gt 0)) {
                if (-not $item) { return }
                foreach ($hit in $hits) {
                    try {
                        $invoke = Get-Pattern $hit ([System.Windows.Automation.InvokePattern]::Pattern)
                        if ($invoke) { $invoke.Invoke(); return }
                        $toggle = Get-Pattern $hit ([System.Windows.Automation.TogglePattern]::Pattern)
                        if ($toggle) { $toggle.Toggle(); return }
                    }
                    catch { $why = "Eintrag lässt sich nicht wählen: $item" }
                }
            }
        } while ([DateTime]::UtcNow -lt $soon)
        if ($missing.Count -gt 0) { $why = "im Menü von $opener fehlt: $($missing -join ', ')" } elseif ($item) { $why = "im Menü von $opener nicht gefunden: $item" }
    } while ([DateTime]::UtcNow -lt $until)
    throw $why
}

function Set-Text([string]$what, [string]$text) {
    foreach ($hit in (Wait-For $what 'Edit')) {
        $value = Get-Pattern $hit ([System.Windows.Automation.ValuePattern]::Pattern)
        if ($value) { $value.SetValue($text); return }
    }
    throw "kein Feld: $what"
}

function Assert-Shown([string]$what, [string]$type = '') {
    if ((Wait-For $what $type).Count -eq 0) { throw "fehlt: $what" }
}

function Assert-Gone([string]$what, [string]$type = '') {
    if (-not (Wait-For $what $type -Gone)) { throw "noch da: $what" }
}

function Step([string]$name, [scriptblock]$body) {
    try {
        & $body
        Write-Output "  ok    $name"
    }
    catch {
        $script:Failed++
        Write-Output "  FEHLT $name – $($_.Exception.Message)"
        if ($Dump) {
            foreach ($element in (Find-All '~.')) {
                $kind = $element.Current.ControlType.ProgrammaticName -replace 'ControlType\.', ''
                if ($kind -in 'Button', 'Edit', 'ListItem', 'CheckBox', 'TabItem', 'ComboBox') { Write-Output "        $kind | $($element.Current.Name) | $($element.Current.AutomationId)" }
            }
        }
    }
}

# Tippt in die Befehlspalette und wählt den Vorschlag mit diesem Namen (oder, mit ~ davor, diesem Muster).
function Invoke-Palette([string]$text, [string]$suggestion) {
    Set-Text '#TextBox' $text
    Invoke-Element $suggestion 'ListItem'
}

# --- Echte Tasten (nur mit -Keys) -------------------------------------------------------------------------------
# Wie von Hand getippt, mit Scancode (sonst fehlt der Seite im WebView event.code). Die Tasten gehen an das Fenster,
# das gerade vorn ist: Deshalb nur im Windows-Gast, nie auf dem Desktop eines Benutzers, und nur, wenn die App vorn ist.
if ($Keys) {
    Add-Type -Namespace Nx -Name Keys -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, System.UIntPtr extra);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint type);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern short VkKeyScan(char ch);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern System.IntPtr GetForegroundWindow();
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr window);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(System.IntPtr window, out uint process);
'@
}

function Assert-AppInFront {
    $script:App.Refresh()
    [void][Nx.Keys]::SetForegroundWindow($script:App.MainWindowHandle)
    Start-Sleep -Milliseconds 300
    $owner = [uint32]0
    [void][Nx.Keys]::GetWindowThreadProcessId([Nx.Keys]::GetForegroundWindow(), [ref]$owner)
    if ($owner -ne $script:App.Id) { throw 'die App ist nicht das vordere Fenster – Tasten würden woanders ankommen' }
}

function Press([byte]$vk, [bool]$extended) {
    $scan = [byte][Nx.Keys]::MapVirtualKey($vk, 0)
    $flags = if ($extended) { 8 -bor 1 } else { 8 }
    [Nx.Keys]::keybd_event($vk, $scan, $flags, [UIntPtr]::Zero)
    [Nx.Keys]::keybd_event($vk, $scan, ($flags -bor 2), [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 120
}

# Eine Taste (A–Z, DOWN, ESCAPE, ENTER), mit Strg, Umschalt und/oder Alt.
function Send-Key([string]$name, [switch]$Ctrl, [switch]$Shift, [switch]$Alt) {
    Assert-AppInFront
    $vk = switch ($name) { 'DOWN' { 0x28 } 'ESCAPE' { 0x1B } 'ENTER' { 0x0D } default { [byte][char]$name.ToUpper() } }
    $mods = @()
    if ($Ctrl) { $mods += 0x11 }
    if ($Shift) { $mods += 0x10 }
    if ($Alt) { $mods += 0x12 }
    try {
        foreach ($mod in $mods) { [Nx.Keys]::keybd_event([byte]$mod, [byte][Nx.Keys]::MapVirtualKey($mod, 0), 8, [UIntPtr]::Zero) }
        Press $vk ($name -eq 'DOWN')
    }
    finally {
        foreach ($mod in $mods) { [Nx.Keys]::keybd_event([byte]$mod, [byte][Nx.Keys]::MapVirtualKey($mod, 0), (8 -bor 2), [UIntPtr]::Zero) }
    }
}

# Text Taste für Taste, wie getippt (kleine Buchstaben und Ziffern).
function Send-Text([string]$text) {
    Assert-AppInFront
    foreach ($ch in $text.ToCharArray()) { Press ([byte]([Nx.Keys]::VkKeyScan($ch) -band 0xFF)) $false }
}

function Focus-Element([string]$what, [string]$type = '') {
    foreach ($hit in (Wait-For $what $type)) { $hit.SetFocus(); return }
    throw "nicht gefunden: $what"
}

function Test-Selected($element) {
    $select = Get-Pattern $element ([System.Windows.Automation.SelectionItemPattern]::Pattern)
    [bool]($select -and $select.Current.IsSelected)
}

function Assert-Selected([string]$what, [bool]$expected) {
    $until = [DateTime]::UtcNow.AddSeconds(5)
    do {
        $hit = Find-All $what 'ListItem'
        if ($hit.Count -gt 0 -and (Test-Selected $hit[0]) -eq $expected) { return }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $until)
    throw "$what ist $(if ($expected) { 'nicht' } else { 'noch' }) gewählt"
}

# Wie viele der fünf Beispiel-Einträge in /var/www gerade gewählt sind.
function Count-Selected {
    $count = 0
    foreach ($hit in (Find-All '~^(assets|html|app\.log|docker-compose\.yml|nginx\.conf)$' 'ListItem')) { if (Test-Selected $hit) { $count++ } }
    $count
}

# Startet die App mit Beispieldaten in der genannten Sprache und wartet auf ihr Fenster.
function Start-App([string]$language) {
    [IO.File]::WriteAllText((Join-Path $data 'settings.json'), "{ `"Language`": `"$language`" }")
    $script:App = Start-Process $Exe -ArgumentList $(if ($Keys) { '--demo-full' } else { '--demo-full', '--no-activate' }) -PassThru
    $until = [DateTime]::UtcNow.AddSeconds(60)
    while ([DateTime]::UtcNow -lt $until -and -not $script:App.HasExited) {
        $script:App.Refresh()
        if ($script:App.MainWindowHandle -ne [IntPtr]::Zero) { return $true }
        Start-Sleep -Milliseconds 300
    }

    $false
}

function Stop-App {
    if ($script:App -and -not $script:App.HasExited) {
        Stop-Process -Id $script:App.Id -Force -ErrorAction SilentlyContinue
        $script:App.WaitForExit(5000) | Out-Null
    }
}

$previousData = $env:NEXUSSSH_DATA_DIR
$previousSsh = $env:NEXUSSSH_SSH_DIR
$env:NEXUSSSH_DATA_DIR = $data
$env:NEXUSSSH_SSH_DIR = $ssh
try {
    # Deutsch, damit die Namen der Elemente nicht von der Sprache des Rechners abhängen; Englisch folgt am Ende.
    if (-not (Start-App 'German')) {
        if ($AllowNoDesktop) {
            Write-Output '::warning::Oberflächentest: kein Fenster (kein Bildschirm) – nicht geprüft.'
            exit 0
        }

        Write-Output '::error::Oberflächentest: Die App hat kein Fenster geöffnet.'
        exit 1
    }

    Write-Output 'Oberflächentest (UI Automation, Beispieldaten):'

    Step 'Die Beispielsitzung steht: Reiter, Zustand, Dateiliste' {
        Assert-Shown 'web01' 'TabItem'
        Assert-Shown 'Überwachung' 'CheckBox'
        Assert-Shown 'nginx.conf' 'ListItem'
        Assert-Shown '5 Einträge' 'Text'
    }

    Step 'Der Filter der Dateiliste schränkt ein und gibt wieder frei' {
        Set-Text 'Liste filtern' 'nginx'
        Assert-Gone 'app.log' 'ListItem'
        Assert-Shown 'nginx.conf' 'ListItem'
        Set-Text 'Liste filtern' ''
        Assert-Shown 'app.log' 'ListItem'
    }

    Step 'Symbolzeile und Menü „⋯“: eine Ebene höher, „Zurück“ führt zurück, der Pfad steht im Feld' {
        Assert-Shown '#PathBox' 'Edit'
        Invoke-Element 'Eine Ebene höher' 'Button'
        Assert-Gone 'nginx.conf' 'ListItem'
        Invoke-Menu 'Ansicht und mehr' 'Zurück' 'Button'
        Assert-Shown 'nginx.conf' 'ListItem'
        # Die Symbole der Zeile, jedes mit seinem Namen; „Terminal-Ordner folgen“ steht darunter (die Beispielshell
        # meldet ihren Ordner nicht – ausgegraut).
        foreach ($button in 'Aktualisieren', 'Herunterladen', 'Hochladen', 'Neuer Ordner', 'Löschen') { Assert-Shown $button 'Button' }
        Assert-Shown 'Terminal-Ordner folgen' 'CheckBox'
    }

    Step 'Der Verzeichnisbaum lässt sich über „⋯“ ein- und ausblenden' {
        Invoke-Menu 'Ansicht und mehr' 'Verzeichnisbaum anzeigen' 'Button' -Look 'Lesezeichen', 'Ordner hochladen …', 'Sortieren'
        Assert-Shown 'Verzeichnisse' 'Tree'
        Invoke-Menu 'Ansicht und mehr' 'Verzeichnisbaum anzeigen' 'Button'
        Assert-Gone 'Verzeichnisse' 'Tree'
    }

    Step 'Die Seite „Server-Details“ zeigt Werte, liegt über den Bereichen und schließt wieder' {
        Invoke-Element '#DetailsButton'
        Assert-Shown 'Server-Details: web01' 'Text'
        Assert-Shown 'Dateisysteme' 'Text'
        Assert-Shown '~^Ubuntu' 'Text'
        Assert-Gone '#SnippetSearchBox'
        Invoke-Element '#DetailsCloseButton'
        Assert-Gone 'Server-Details: web01' 'Text'
        Assert-Shown '#SnippetSearchBox'
    }

    Step 'Die Snippets rechts lassen sich aus- und einblenden, „+“ führt zum Editor' {
        Assert-Shown 'Noch keine Befehle. „+“ legt einen an.' 'Text'
        Invoke-Element 'Snippets ein-/ausblenden' 'Button'
        Assert-Gone '#SnippetSearchBox'
        Invoke-Element 'Snippets ein-/ausblenden' 'Button'
        Assert-Shown '#SnippetSearchBox'
        Invoke-Element '#AddSnippetButton'
        Assert-Shown 'Gespeicherte Befehle' 'Text'
        # „+“ führt zur Seite „Snippets“ der Startseite, und „Start“ zeigt später die zuletzt gewählte Seite –
        # zurück zu den Servern, damit die Schritte zur Startseite sie dort finden.
        Invoke-Element 'Server' 'ListItem'
        Invoke-Element 'web01' 'TabItem'
        Assert-Shown '#SnippetSearchBox'
    }

    Step 'Der Verlauf der ganzen Sitzung öffnet sich mit seinen Kurven' {
        Invoke-Element '#DetailsButton'
        Assert-Shown '~^Zwischenspeicher ' 'Text'
        Assert-Shown '~^Auslagerung ' 'Text'
        Invoke-Element '#LoadHistoryButton'
        Assert-Shown 'Auslastung von web01' 'Text'
        Assert-Shown 'Als CSV speichern …' 'Button'
        Invoke-Element 'Schließen' 'Button'
        Assert-Gone 'Auslastung von web01' 'Text'
        Invoke-Element '#DetailsCloseButton'
    }

    Step 'Der Dateibrowser lässt sich aus- und einblenden' {
        Invoke-Element 'SFTP-Browser ein-/ausblenden' 'Button'
        Assert-Gone 'nginx.conf' 'ListItem'
        Invoke-Element 'SFTP-Browser ein-/ausblenden' 'Button'
        Assert-Shown 'nginx.conf' 'ListItem'
    }

    Step 'Der untere Bereich wechselt durch seine Reiter' {
        if ((Find-All '#TransfersTab').Count -eq 0) { Invoke-Element '#TransfersPill' }
        Invoke-Element '#TransfersTab'
        Assert-Shown '~^nginx\.conf, web01' 'ListItem'
        foreach ($tab in '#OutputTab', '#ProblemsTab', '#SearchTab', '#HistoryTab', '#FollowTab', '#TransfersTab') { Invoke-Element $tab }
        Assert-Shown '~^nginx\.conf, web01' 'ListItem'
    }

    Step 'Die Zeile unten: „Transfers n“ blendet die Transfers aus und wieder ein, „Details“ steht da' {
        Assert-Shown '#DetailsButton'
        Assert-Shown '~^Transfers [0-9]+$' 'Button'
        Invoke-Element '#TransfersPill'
        Assert-Gone '#TransfersTab'
        Invoke-Element '#TransfersPill'
        Assert-Shown '~^nginx\.conf, web01' 'ListItem'
    }

    Step 'Ein einzelner Transfer lässt sich anhalten und fortsetzen' {
        Invoke-Menu 'Weitere Aktionen: database.sql' 'Anhalten' 'Button'
        Assert-Shown '~^database\.sql, database01, Angehalten' 'ListItem'
        Invoke-Element '~: database\.sql$' 'Button'
        Assert-Gone '~^database\.sql, database01, Angehalten' 'ListItem'
    }

    Step 'Zwei Spalten: links dieser Rechner, rechts der Server – und zurück' {
        Invoke-Menu '#SplitButton' '#view.dualFiles'
        Assert-Shown 'nginx.conf' 'ListItem'
        Invoke-Menu '#SplitButton' '#view.dualFiles'
        Assert-Shown 'Überwachung' 'CheckBox'
    }

    Step 'Die Startseite zeigt die Server, die Sitzung läuft weiter' {
        Invoke-Element '#HomeButton'
        Assert-Shown 'database01, admin@10.0.1.20' 'ListItem'
        Assert-Shown 'web01' 'TabItem'
        Assert-Gone '#SnippetSearchBox'
    }

    Step 'Der Filter der Startseite findet einen Server' {
        Set-Text 'Server suchen …' 'stag'
        Assert-Gone 'database01, admin@10.0.1.20' 'ListItem'
        Assert-Shown 'staging, deploy@10.0.2.10:2222' 'ListItem'
        Set-Text 'Server suchen …' ''
        Assert-Shown 'database01, admin@10.0.1.20' 'ListItem'
    }

    Step 'Tabelle statt Kacheln – und zurück' {
        Invoke-Menu '#HomeMoreButton' '#TableToggle'
        Assert-Shown '~^database01' 'ListItem'
        Invoke-Menu '#HomeMoreButton' '#TableToggle'
        Assert-Shown 'database01, admin@10.0.1.20' 'ListItem'
    }

    Step 'Die Seiten der Startseite öffnen sich: fünf Einträge, „Mehr“ klappt drei auf' {
        Invoke-Element 'Snippets' 'ListItem'
        Invoke-Element 'Schlüssel & Identitäten' 'ListItem'
        Invoke-Element 'Identitäten' 'Button'
        Invoke-Element 'Schlüssel' 'Button'
        Invoke-Element 'Tunnel' 'ListItem'
        if ((Find-All 'Bekannte Server' 'ListItem').Count -eq 0) { Invoke-Element 'Mehr' 'ListItem' }
        foreach ($page in 'Abgleiche', 'Bekannte Server', 'Übersicht', 'Server') { Invoke-Element $page 'ListItem' }
        Assert-Shown 'database01, admin@10.0.1.20' 'ListItem'
    }

    Step 'Links: Ziel in der Zeile, ins Leere und ohne Ziel gesagt; die Eigenschaften mit 4000 Zeichen Ziel lassen sich schließen' {
        # database01 beginnt in seinem Heimatordner, in dem die Beispieldaten ihre Links haben (Mock/MockBackend.cs).
        Invoke-Element 'database01, admin@10.0.1.20' 'ListItem'
        Assert-Shown 'database01' 'TabItem'
        Assert-Shown '~^skripte, → scripts' 'ListItem'
        Assert-Shown '~^notizen, → /home/admin/notes\.txt' 'ListItem'
        Assert-Shown '~^verwaist, → gibt-es-nicht, ' 'ListItem'
        Assert-Shown 'unbekannt, Symbolischer Link' 'ListItem'
        Assert-Shown '~^langes-ziel, → ' 'ListItem'

        # Die Eigenschaften stehen im Kontextmenü (über UI Automation nicht zu öffnen) und auf Alt+Eingabe – also nur
        # mit echten Tasten.
        if ($Keys) {
            Invoke-Element '~^langes-ziel, → ' 'ListItem'
            Focus-Element '~^langes-ziel, → ' 'ListItem'
            Send-Key 'ENTER' -Alt
            Assert-Shown '~sehr-langer-ordner' 'Text'
            # Der Dialog rollt sein Ziel selbst: Der Knopf zum Schließen muss zu sehen und zu bedienen sein.
            $close = @(Wait-For 'Schließen' 'Button' | Where-Object { -not $_.Current.IsOffscreen -and $_.Current.BoundingRectangle.Width -gt 0 })
            if ($close.Count -eq 0) { throw 'der Knopf „Schließen“ ist nicht erreichbar' }
            Invoke-Element 'Schließen' 'Button' -Last
            Assert-Gone '~sehr-langer-ordner' 'Text'
        }
        else { Write-Output '        (Eigenschaften übersprungen: nur mit -Keys, das schickt echte Tasten)' }

        Invoke-Element '#CloseButton' 'Button' -Last
        Assert-Gone 'database01' 'TabItem'
        Invoke-Element '#HomeButton'
        Invoke-Element 'Server' 'ListItem'
    }

    Step 'Tastatur in der Dateiliste: Tippen springt, Strg+A und Umschalt+Pfeil wählen, Strg+L öffnet die Pfadzeile' {
        if (-not $Keys) { Write-Output '        (übersprungen: nur mit -Keys, das schickt echte Tasten)'; return }
        Invoke-Element 'web01' 'TabItem'
        Assert-Shown 'nginx.conf' 'ListItem'
        # Fokus in die Liste: eine Zeile wählen und ihr den Fokus geben.
        Invoke-Element 'nginx.conf' 'ListItem'
        Focus-Element 'nginx.conf' 'ListItem'
        Send-Text 'app'
        Assert-Selected 'app.log' $true
        Assert-Selected 'nginx.conf' $false
        Send-Key 'DOWN' -Shift
        if ((Count-Selected) -ne 2) { throw "Umschalt+↓ sollte zwei Zeilen wählen, gewählt: $(Count-Selected) (der Fokus blieb nach dem Tippen wohl auf der alten Zeile)" }
        Send-Key 'A' -Ctrl
        if ((Count-Selected) -ne 5) { throw "Strg+A sollte alle fünf wählen, gewählt: $(Count-Selected)" }
        Send-Key 'L' -Ctrl
        Start-Sleep -Milliseconds 500
        $focused = $Automation::FocusedElement
        if ($focused.Current.ControlType.ProgrammaticName -ne 'ControlType.Edit') { throw 'Strg+L hat die Pfadzeile nicht fokussiert' }
        Send-Key 'ESCAPE'
    }

    Step 'Tunnel: die Seite listet sie, der Dialog öffnet sich; mit der Verbindung steht, was soll, und der Schalter hält an' {
        Invoke-Element 'Tunnel' 'ListItem'
        Assert-Shown '~^PostgreSQL, database01, Keine Verbindung' 'ListItem'
        Invoke-Element '#AddTunnelButton'
        Assert-Shown 'Mit der Verbindung starten' 'CheckBox'
        Invoke-Element 'Abbrechen' 'Button'
        Assert-Gone 'Mit der Verbindung starten' 'CheckBox'
        Invoke-Element 'Server' 'ListItem'
        Invoke-Element 'database01, admin@10.0.1.20' 'ListItem'
        Assert-Shown 'database01' 'TabItem'
        Invoke-Palette '>Tunnel' '~^Tunnel \(Portweiterleitung\)'
        Assert-Shown '~^PostgreSQL, database01, Steht' 'ListItem'
        # Der zweite soll nicht von selbst starten – auch nicht, weil die Liste ihre Zeilen neu belegt.
        Assert-Shown '~^SOCKS-Proxy localhost:1080, database01, Aus' 'ListItem'
        Invoke-Element 'Tunnel an/aus: PostgreSQL'
        Assert-Shown '~^PostgreSQL, database01, Aus' 'ListItem'
        # Die Seite der Tunnel gehört zur Startseite, also ist gerade kein Tab aktiv – und das × steht nur am aktiven.
        Invoke-Element 'database01' 'TabItem'
        Invoke-Element '#CloseButton' 'Button' -Last
        Assert-Gone 'database01' 'TabItem'
        # Mit dem Schließen wird der Nachbar-Tab aktiv – zurück zur Startseite, dort steht die Seite der Tunnel noch.
        Invoke-Element '#HomeButton'
        Assert-Shown '~^PostgreSQL, database01, Keine Verbindung' 'ListItem'
        Invoke-Element 'Server' 'ListItem'
    }

    Step 'Schlüssel: die Liste nennt Art und Format, die falsche Passphrase bleibt als Satz im Dialog, umwandeln legt eine Datei dazu' {
        Invoke-Element 'Schlüssel & Identitäten' 'ListItem'
        if ((Find-All 'id_rsa_work, RSA 4096' 'ListItem').Count -eq 0) { Invoke-Element 'Schlüssel' 'Button' }
        Assert-Shown 'id_rsa_work, RSA 4096' 'ListItem'
        Assert-Shown '~^RSA 4096 .* OpenSSH .* mit Passphrase' 'Text'
        Invoke-Element 'Passphrase ändern …: id_rsa_work'
        Set-Text 'Bisherige Passphrase' 'falsch'
        Start-Sleep -Milliseconds 1000
        Set-Text 'Neue Passphrase (leer: ohne)' 'neu'
        Start-Sleep -Milliseconds 1000
        Set-Text 'Passphrase wiederholen' 'neu'
        Start-Sleep -Milliseconds 1000
        Invoke-Element '#PrimaryButton' 'Button'
        Assert-Shown 'Die Passphrase stimmt nicht.' 'Text'
        Invoke-Element 'Abbrechen' 'Button'
        Assert-Gone 'Bisherige Passphrase' 'Edit'
        Invoke-Element 'Für PuTTY umwandeln …: id_ed25519'
        Assert-Shown 'Name der neuen Datei' 'Edit'
        Invoke-Element '#PrimaryButton' 'Button'
        Assert-Shown 'id_ed25519.ppk, Ed25519' 'ListItem'
        Invoke-Element 'Server' 'ListItem'
    }

    Step 'Bekannte Server: eine Zertifizierungsstelle lässt sich eintragen und wieder entfernen' {
        if ((Find-All 'Bekannte Server' 'ListItem').Count -eq 0) { Invoke-Element 'Mehr' 'ListItem' }
        Invoke-Element 'Bekannte Server' 'ListItem'
        Invoke-Element '#AddAuthorityButton'
        Set-Text 'Gilt für diese Server' '*.example.org'
        Set-Text 'Öffentlicher Schlüssel der Stelle' 'ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIGtleWtleWtleWtleWtleWtleWtl ca@test'
        Start-Sleep -Milliseconds 500
        Invoke-Element '#PrimaryButton' 'Button'
        Assert-Shown '~^\*\.example\.org, ssh-ed25519' 'ListItem'
        Invoke-Element 'Vergessen: *.example.org'
        Assert-Gone '~^\*\.example\.org, ssh-ed25519' 'ListItem'
        Invoke-Element 'Server' 'ListItem'
    }

    Step 'Die Einstellungen öffnen sich, ein Schalter lässt sich umlegen, sie schließen wieder' {
        Invoke-Palette '>Einstellungen' 'Einstellungen'
        Assert-Shown 'Beim Start nach Updates suchen' 'CheckBox'
        # Die Bereiche stehen links; zu sehen ist immer nur der gewählte.
        Invoke-Element '#NavAbout' 'ListItem'
        Assert-Shown '#DiagnosticsButton' 'Button'
        Assert-Gone 'Beim Start nach Updates suchen' 'CheckBox'
        Invoke-Element '#NavLook' 'ListItem'
        $box = (Wait-For 'Bereiche und Startseite weich einblenden' 'CheckBox')[0]
        $toggle = Get-Pattern $box ([System.Windows.Automation.TogglePattern]::Pattern)
        $before = $toggle.Current.ToggleState
        $toggle.Toggle()
        if ($toggle.Current.ToggleState -eq $before) { throw 'der Schalter blieb, wie er war' }
        $toggle.Toggle()
        Invoke-Palette '>Einstellungen' 'Einstellungen'
        Assert-Gone '#NavAbout' 'ListItem'
    }

    Step 'Ein zweiter Server öffnet eine zweite Sitzung' {
        Invoke-Element 'database01, admin@10.0.1.20' 'ListItem'
        Assert-Shown 'database01' 'TabItem'
        Assert-Shown 'Überwachung' 'CheckBox'
    }

    Step 'Der Reiter der zweiten Sitzung schließt sich, die erste bleibt' {
        Invoke-Element '#CloseButton' 'Button' -Last
        Assert-Gone 'database01' 'TabItem'
        Assert-Shown 'web01' 'TabItem'
    }

    Step 'Die Befehlspalette findet einen Befehl und führt ihn aus' {
        # Nach dem Schließen eines Reiters steht nicht fest, welche Ansicht vorn ist – also die Sitzung wählen.
        Invoke-Element 'web01' 'TabItem'
        Assert-Shown '#SnippetSearchBox'
        Invoke-Palette '>Snippets' 'Snippets ein-/ausblenden'
        Assert-Gone '#SnippetSearchBox'
        Invoke-Palette '>Snippets' 'Snippets ein-/ausblenden'
        Assert-Shown '#SnippetSearchBox'
    }

    Step 'Die Befehlspalette findet einen Server und öffnet ihn' {
        Invoke-Palette 'stag' '~^staging'
        Assert-Shown 'staging' 'TabItem'
        Invoke-Element '#CloseButton' 'Button' -Last
        Assert-Gone 'staging' 'TabItem'
    }

    Step 'Schnellverbinden: ein Ziel in der Palette öffnet eine Sitzung ohne Server; behalten macht einen daraus' {
        Invoke-Palette 'deploy@build.example:2222' '~^Verbinden mit deploy@build\.example:2222, Schnellverbinden'
        Assert-Shown 'deploy@build.example:2222' 'TabItem'
        Invoke-Element '#HomeButton'
        Assert-Gone '~^deploy@build\.example:2222, ' 'ListItem'
        Invoke-Element 'deploy@build.example:2222' 'TabItem'
        Invoke-Palette '>als Server' 'Diese Verbindung als Server speichern'
        Invoke-Element '#HomeButton'
        Assert-Shown '~^deploy@build\.example:2222, ' 'ListItem'
        Invoke-Element 'deploy@build.example:2222' 'TabItem'
        Invoke-Element '#CloseButton' 'Button' -Last
        Assert-Gone 'deploy@build.example:2222' 'TabItem'
        Invoke-Element 'web01' 'TabItem'
    }

    Step 'Dialog „Server hinzufügen“: ausfüllen, speichern, der Server steht auf der Startseite' {
        Invoke-Palette '>Server hinzu' '~^Server hinzuf'
        Set-Text 'Name' 'uitest'
        Set-Text 'Host' '10.9.9.9'
        Set-Text 'Benutzer' 'tester'
        Start-Sleep -Milliseconds 500
        Invoke-Element '#PrimaryButton' 'Button'
        Invoke-Element '#HomeButton'
        Assert-Shown 'uitest, tester@10.9.9.9' 'ListItem'
    }

    Step 'Dialog „Aus SSH-Konfiguration importieren“ öffnet sich, nennt, was nicht übernommen wird, und lässt sich abbrechen' {
        Invoke-Palette '>SSH-Konf' '~^Aus SSH-Konfiguration'
        Assert-Shown '#PrimaryButton' 'Button'
        Assert-Shown '~^Nicht übernommen: ProxyCommand, Ciphers'
        Assert-Shown '#UncheckedText'
        Invoke-Element 'Abbrechen' 'Button'
        Assert-Gone '#PrimaryButton' 'Button'
    }

    Step 'Laufende tmux- und screen-Sitzungen werden angeboten' {
        Invoke-Element 'web01' 'TabItem'
        Invoke-Palette '>tmux' '~^An laufende tmux'
        Assert-Shown 'Laufende Sitzungen auf web01' 'Text'
        Assert-Shown '~^deploy, tmux' 'ListItem'
        Assert-Shown '~^4711\.backup, screen' 'ListItem'
        Invoke-Element 'Abbrechen' 'Button'
        Assert-Gone 'Laufende Sitzungen auf web01' 'Text'
    }

    Step 'Die Hilfe öffnet sich über die Palette mit Neuem, Handgriffen und Kürzeln' {
        Invoke-Palette '>Hilfe' '~^Hilfe und was neu'
        Assert-Shown 'Die wichtigsten Handgriffe' 'Text'
        Assert-Shown '~^Neu in Version ' 'Text'
        Assert-Shown 'Aus dem Terminal heraus' 'Text'
        Invoke-Element '#HelpCloseButton'
        Assert-Gone 'Die wichtigsten Handgriffe' 'Text'
    }

    Step 'Das Schließen des Fensters fragt nach, solange eine Sitzung verbunden ist' {
        Invoke-Element '#Close' 'Button'
        Assert-Shown 'NexusSSH beenden?' 'Text'
        Invoke-Element 'Abbrechen' 'Button'
        Assert-Gone 'NexusSSH beenden?' 'Text'
        Assert-Shown 'web01' 'TabItem'
    }

    Step 'Die App läuft noch und hat keine Ausnahme notiert' {
        $script:App.Refresh()
        if ($script:App.HasExited) { throw "die App hat sich beendet (Exit $($script:App.ExitCode))" }
        if ((Test-Path $log) -and (Get-Item $log).Length -gt 0) { throw 'app.log ist nicht leer' }
    }

    # Zweiter Start auf Englisch: Die Oberfläche ist zweisprachig, und ein Text, der nur in einer Sprache stimmt,
    # fiele sonst erst einem Benutzer auf.
    Stop-App
    if (Start-App 'English') {
        Write-Output 'Englisch:'
        Step 'Sitzung, Dateiliste und Snippets tragen englische Namen' {
            Assert-Shown 'Monitoring' 'CheckBox'
            Assert-Shown '5 entries' 'Text'
            Assert-Shown 'Search snippets' 'Edit'
            Assert-Gone 'Überwachung' 'CheckBox'
        }

        Step 'Startseite und Einstellungen tragen englische Namen' {
            Invoke-Element '#HomeButton'
            Assert-Shown 'Snippets' 'ListItem'
            Assert-Shown 'Keys & identities' 'ListItem'
            if ((Find-All 'Known servers' 'ListItem').Count -eq 0) { Invoke-Element 'More' 'ListItem' }
            Assert-Shown 'Known servers' 'ListItem'
            Invoke-Palette '>Settings' 'Settings'
            Assert-Shown 'Check for updates at startup' 'CheckBox'
            Invoke-Palette '>Settings' 'Settings'
        }

        Step 'Die Befehlspalette versteht englische Befehle' {
            Invoke-Element 'web01' 'TabItem'
            Assert-Shown 'Search snippets' 'Edit'
            Invoke-Palette '>snippets' 'Toggle snippets'
            Assert-Gone 'Search snippets' 'Edit'
        }

        Step 'Auch auf Englisch keine Ausnahme' {
            $script:App.Refresh()
            if ($script:App.HasExited) { throw "die App hat sich beendet (Exit $($script:App.ExitCode))" }
            if ((Test-Path $log) -and (Get-Item $log).Length -gt 0) { throw 'app.log ist nicht leer' }
        }
    }
    else {
        $script:Failed++
        Write-Output '  FEHLT Englisch – die App hat kein Fenster geöffnet'
    }

    # Die übrigen Sprachen kurz: Die App startet darin, nennt Zustand, Zahl der Einträge und Einstellungen in der
    # Sprache, öffnet Einstellungen und Startseite und notiert keine Ausnahme.
    foreach ($other in @(
            @{ Language = 'French'; Name = 'Französisch'; Monitoring = 'Surveillance'; Entries = '5 entrées'; Settings = 'Paramètres'; Updates = 'Rechercher les mises à jour au démarrage' },
            @{ Language = 'Spanish'; Name = 'Spanisch'; Monitoring = 'Supervisión'; Entries = '5 entradas'; Settings = 'Configuración'; Updates = 'Buscar actualizaciones al iniciar' })) {
        Stop-App
        if (-not (Start-App $other.Language)) {
            $script:Failed++
            Write-Output "  FEHLT $($other.Name) – die App hat kein Fenster geöffnet"
            continue
        }

        Write-Output "$($other.Name):"
        Step 'Sitzung, Dateiliste und Einstellungen sprechen die Sprache' {
            Assert-Shown $other.Monitoring 'CheckBox'
            Assert-Shown $other.Entries 'Text'
            Invoke-Palette ">$($other.Settings)" $other.Settings
            Assert-Shown $other.Updates 'CheckBox'
            Invoke-Palette ">$($other.Settings)" $other.Settings
            Invoke-Element '#HomeButton'
            Assert-Shown 'database01, admin@10.0.1.20' 'ListItem'
        }

        Step 'Keine Ausnahme' {
            $script:App.Refresh()
            if ($script:App.HasExited) { throw "die App hat sich beendet (Exit $($script:App.ExitCode))" }
            if ((Test-Path $log) -and (Get-Item $log).Length -gt 0) { throw 'app.log ist nicht leer' }
        }
    }
    if ($script:Failed -gt 0) {
        if (Test-Path $log) { Write-Output '--- app.log'; Get-Content $log -Tail 40 }
        Write-Output "::error::Oberflächentest: $script:Failed Schritt(e) nicht gelungen."
        exit 1
    }

    Write-Output 'Oberflächentest bestanden.'
    exit 0
}
finally {
    Stop-App
    $env:NEXUSSSH_DATA_DIR = $previousData
    $env:NEXUSSSH_SSH_DIR = $previousSsh
    Start-Sleep -Milliseconds 500
    Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
}
