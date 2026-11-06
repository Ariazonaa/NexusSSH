//! Befehle für Server mit OpenSSH unter Windows. Deren Anmelde-Shell ist `cmd.exe` (oder PowerShell, wenn der
//! Betreiber es so eingestellt hat) – beide lesen keine POSIX-Befehle, und in `cmd.exe` wäre ein Name wie
//! `a&calc` ein Befehl. Deshalb geht jeder Befehl des Kerns dorthin als **ein PowerShell-Skript, kodiert**:
//! `powershell … -EncodedCommand <Base64 von UTF-16>`. Die Befehlszeile besteht dann nur aus festen Wörtern und
//! Base64 – keine Shell sieht je einen Namen. Im Skript stehen Namen und Pfade nur als Literale in einfachen
//! Anführungszeichen ([`quote`]), Pfade immer mit `-LiteralPath` (sonst läse PowerShell `[` und `*` als Muster).
//!
//! Die Skripte geben dieselben Formen aus wie die POSIX-Befehle (`ps`, `du`, `sha256sum`, `/proc/…`) – so lesen die
//! vorhandenen Leser auch, was ein Windows-Server antwortet.

use crate::sftp::Failure;

/// Was PowerShell auf [`probe`] antwortet.
pub(crate) const ANSWER: &str = "nexus-powershell";

/// Die Schleife der Auslastung ([`crate::monitor`]): dieselben Abschnitte wie unter Linux, die Zahlen aus den
/// Leistungsdaten von Windows (CIM). Rechenzeit in Hundertstelsekunden, Speicher in KiB, Laufwerke wie `df -Pk`
/// (mit `C:\` an der Stelle des Mountpoints), Netz wie `/proc/net/dev`, Adressen wie `ip -o addr`, Datenträger wie
/// `/proc/diskstats`. Namen von Netzwerkkarten werden zu dem, was [`crate::monitor`] als Namen gelten lässt.
///
/// **Sie endet nicht von selbst mit ihrem Kanal:** Der SSH-Server von Windows hält die Leitungen zum Prozess offen,
/// solange die Verbindung steht – ein geschlossener Kanal, `EOF` oder ein Schreiben ins Leere kommen bei PowerShell
/// nicht an (geprüft 2026-10-15 an Windows Server 2025). Deshalb nennt sie zuerst ihre Nummer und ihre Startzeit
/// (`@pid <nummer> <filetime>`), und der Kern beendet sie beim Abschalten ausdrücklich ([`end_loop`]). Endet die
/// ganze Verbindung, endet sie mit der SSH-Sitzung, unter der sie läuft (sie merkt sich deren Prozess).
pub(crate) const PID_LINE: &str = "@pid ";

/// Die Schleife, wie sie sich mit [`PID_LINE`] genannt hat.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) struct Loop {
    pid: u32,
    started: u64,
}

impl Loop {
    /// Liest den Rest einer Zeile nach [`PID_LINE`]: zwei Zahlen, sonst nichts.
    pub(crate) fn parse(rest: &str) -> Option<Self> {
        let mut words = rest.split_whitespace();
        let pid = words.next()?.parse().ok().filter(|pid| *pid > 0)?;
        let started = words.next()?.parse().ok().filter(|started| *started > 0)?;
        words.next().is_none().then_some(Self { pid, started })
    }
}

/// Beendet die Schleife der Auslastung – nur, wenn unter ihrer Nummer noch derselbe Prozess läuft. Windows vergibt
/// Nummern schnell neu (am Gast: fünf neue Prozesse, fünf Nummern aus dem frei gewordenen Bereich); ohne den
/// Vergleich der Startzeit träfe `Stop-Process -Force` einen fremden Prozess, wenn die Schleife schon geendet hat.
pub(crate) fn end_loop(running: Loop) -> String {
    script(&format!(
        "$p=Get-Process -Id {} -ErrorAction SilentlyContinue;\
         if($p-and$p.StartTime.ToFileTimeUtc()-eq{}){{Stop-Process -InputObject $p -Force}}",
        running.pid, running.started
    ))
}

/// Ein gewöhnliches Konto darf über SSH **kein WMI/CIM** – jede Abfrage endet mit „Access denied“, auch
/// `Win32_OperatingSystem` (eine Anmeldung über SSH gilt WMI als eine aus dem Netz, und die erlaubt es nur
/// Administratoren; am Gast geprüft 2026-10-19). Leistungsindikatoren sind ebenso gesperrt. Was dann bleibt, sind
/// Aufrufe ohne Rechte: `NtQuerySystemInformation` nennt die Zeiten der Kerne und alle Prozesse samt Eltern,
/// Rechenzeit, Speicher, Startzeit und Namen – ohne einen Prozess zu öffnen (so liest es auch der Task-Manager).
/// Diese Klasse übersetzt PowerShell mit `Add-Type`; nur im Zweig ohne CIM, damit ein Server, der kein `Add-Type`
/// erlaubt (eingeschränkte Sprache), für Administratoren weiter misst. Die Stellen der Felder gelten für 64 Bit.
/// Steht in einfachen Anführungszeichen in PowerShell – also nie ein `'` darin.
macro_rules! nxw {
    () => {
        "using System;using System.Collections.Generic;using System.Runtime.InteropServices;\
         public static class NxW{\
         [DllImport(\"ntdll.dll\")]static extern int NtQuerySystemInformation(int c,IntPtr b,int l,out int r);\
         static IntPtr Q(int c,int l){for(;l<=1<<26;l*=2){IntPtr b=Marshal.AllocHGlobal(l);int r;\
         if(NtQuerySystemInformation(c,b,l,out r)==0)return b;Marshal.FreeHGlobal(b);if(c==8)break;}return IntPtr.Zero;}\
         public static List<long[]> Cores(){var o=new List<long[]>();int n=Environment.ProcessorCount;\
         IntPtr b=Q(8,n*48);if(b==IntPtr.Zero)return o;for(int i=0;i<n;i++){o.Add(new long[]{\
         Marshal.ReadInt64(b,i*48),Marshal.ReadInt64(b,i*48+8),Marshal.ReadInt64(b,i*48+16)});}\
         Marshal.FreeHGlobal(b);return o;}\
         public static Dictionary<long,object[]> Procs(){var o=new Dictionary<long,object[]>();\
         if(IntPtr.Size!=8)return o;IntPtr b=Q(5,1<<18);if(b==IntPtr.Zero)return o;long i=0;\
         while(true){IntPtr e=new IntPtr(b.ToInt64()+i);IntPtr n=Marshal.ReadIntPtr(e,0x40);long p=Marshal.ReadInt64(e,0x50);\
         o[p]=new object[]{p,Marshal.ReadInt64(e,0x58),Marshal.ReadInt64(e,0x28)+Marshal.ReadInt64(e,0x30),\
         Marshal.ReadInt64(e,0x90),Marshal.ReadInt64(e,0x20),\
         n==IntPtr.Zero?\"\":Marshal.PtrToStringUni(n,(ushort)Marshal.ReadInt16(e,0x38)/2)};\
         int x=Marshal.ReadInt32(e);if(x==0)break;i+=x;}Marshal.FreeHGlobal(b);return o;}}"
    };
}

/// Das Skript der Schleife: aus CIM (`monitor.ps1`) oder, für ein Konto ohne CIM, aus [`nxw`] und dem, was .NET
/// ohne Rechte nennt (`monitor-plain.ps1`: Laufwerke, Netz, Speicher, System aus der Registry; ohne Auslagerung und
/// Durchsatz der Platten). Beides zusammen wäre auch komprimiert länger als eine Zeile von `cmd.exe`. Die Fassung
/// mit CIM meldet [`NO_CIM`] und endet, wenn CIM verweigert wird – dann startet der Kern die andere.
pub(crate) fn monitor(plain: bool) -> String {
    if plain {
        include_str!("winserver/monitor-plain.ps1").replace("__NXW__", nxw!())
    } else {
        include_str!("winserver/monitor.ps1").to_owned()
    }
}

/// Was die Schleife mit CIM sagt, bevor sie endet, weil das Konto kein CIM darf.
pub(crate) const NO_CIM: &str = "@nocim";

/// Die Befehlszeile für ein Skript: PowerShell ohne Profil und ohne Rückfragen, das Skript als Base64 von UTF-16LE.
/// Läuft gleich, ob die Anmelde-Shell `cmd.exe` oder PowerShell ist.
///
/// `cmd.exe` nimmt höchstens 8191 Zeichen je Zeile, und Base64 von UTF-16 macht ein Skript fast dreimal so lang.
/// Ein längeres geht deshalb komprimiert hinüber (Deflate, als Base64 in einem kurzen Skript, das es auspackt
/// und mit `Invoke-Expression` im selben Prozess ausführt – Nummer und Eltern bleiben dieselben). Auch dann
/// steht in der Befehlszeile nur Base64.
pub(crate) fn command(script: &str) -> String {
    let plain = encoded(script);
    if plain.len() <= LINE_LIMIT {
        return plain;
    }

    let mut packer = flate2::write::DeflateEncoder::new(Vec::new(), flate2::Compression::best());
    std::io::Write::write_all(&mut packer, script.as_bytes()).expect("writing to memory");
    let packed = crate::sftp::to_base64(&packer.finish().expect("writing to memory"));
    encoded(&format!(
        "$ProgressPreference='SilentlyContinue';\
         $z=New-Object IO.Compression.DeflateStream((New-Object IO.MemoryStream(,[Convert]::FromBase64String('{packed}'))),\
         ([IO.Compression.CompressionMode]::Decompress));\
         Invoke-Expression ((New-Object IO.StreamReader($z,[Text.Encoding]::UTF8)).ReadToEnd())"
    ))
}

/// So lang darf eine Befehlszeile ohne Komprimieren werden – unter den 8191 Zeichen von `cmd.exe`, mit Abstand.
const LINE_LIMIT: usize = 7600;

fn encoded(script: &str) -> String {
    let utf16: Vec<u8> = script.encode_utf16().flat_map(u16::to_le_bytes).collect();
    format!(
        "powershell -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {}",
        crate::sftp::to_base64(&utf16)
    )
}

/// Ein Befehl ohne Wirkung, an dem sich zeigt, ob der Server PowerShell so ausführt wie gedacht.
pub(crate) fn probe() -> String {
    command("'nexus-'+'powershell'")
}

/// Ein Skript, das bei einem Fehler mit dessen Text auf der Fehlerausgabe und Rückgabewert 1 endet; die Ausgabe
/// als UTF-8 (Namen kommen darin zurück).
pub(crate) fn script(body: &str) -> String {
    command(&format!(
        "$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue';\
         [Console]::OutputEncoding=[Text.Encoding]::UTF8;\
         try{{{body}}}catch{{[Console]::Error.WriteLine($_.Exception.Message);exit 1}}"
    ))
}

/// Text der App als Skript (`exec.run`). Anders als bei [`script`] bleibt ein Fehler, was er in PowerShell ist –
/// nicht jeder endet das Skript –, und der Rückgabewert sagt, wie die letzte Anweisung ausging (`$?` gleich danach
/// merken: Jede weitere Abfrage überschreibt es). Die Zeilenumbrüche halten einen Kommentar am Ende des Textes vom
/// Schluss des Skripts fern.
pub(crate) fn user_command(text: &str) -> String {
    command(&format!(
        "$ProgressPreference='SilentlyContinue';[Console]::OutputEncoding=[Text.Encoding]::UTF8;\n\
         {text}\n\
         $nxOk=$?;if($nxOk){{exit 0}};if($LASTEXITCODE){{exit $LASTEXITCODE}};exit 1"
    ))
}

/// Ein Text als Literal für PowerShell: in einfachen Anführungszeichen. PowerShell nimmt dafür auch die
/// typografischen (`‘ ’ ‚ ‛`) – jedes davon wird wie `'` verdoppelt, sonst endete das Literal dort. `None` für
/// einen leeren Text oder einen mit Steuerzeichen.
pub(crate) fn quote(text: &str) -> Option<String> {
    if text.is_empty() || text.chars().any(char::is_control) {
        return None;
    }

    let mut quoted = String::with_capacity(text.len() + 2);
    quoted.push('\'');
    for character in text.chars() {
        if matches!(character, '\'' | '\u{2018}' | '\u{2019}' | '\u{201A}' | '\u{201B}') {
            quoted.push(character);
        }
        quoted.push(character);
    }
    quoted.push('\'');
    Some(quoted)
}

fn unsafe_path(path: &str) -> Failure {
    Failure {
        code: "unsafe_name",
        message: format!("not a path this command can use on Windows: {path}"),
    }
}

/// Ein Name, der unter Windows genau diese Datei meint. Nicht: `.` und `..`, `\` und `:` (Trenner, Laufwerk,
/// Alternate Data Stream), ein Punkt oder Leerzeichen am Ende (Windows kürzt sie – `a.` ist `a`) und die Namen der
/// Geräte (`CON`, `NUL.txt`, `COM1` …): `tar` schrieb ein Archiv `CON` auf die Konsole statt in eine Datei, und
/// PowerShell lehnt sie ohnehin ab (am Gast geprüft 2026-10-19, Hinweis von Codex).
fn plain_name(name: &str) -> bool {
    if matches!(name, "." | "..") || name.contains(['\\', ':']) || name.ends_with(['.', ' ']) {
        return false;
    }

    // Der Teil vor dem ersten Punkt, ohne Leerzeichen dahinter – `nul .txt` ist dasselbe Gerät.
    let stem = name
        .split('.')
        .next()
        .unwrap_or_default()
        .trim_end()
        .to_ascii_uppercase();
    let numbered = |prefix: &str| {
        stem.strip_prefix(prefix).is_some_and(|rest| {
            matches!(
                rest,
                "0" | "1" | "2" | "3" | "4" | "5" | "6" | "7" | "8" | "9" | "\u{B9}" | "\u{B2}" | "\u{B3}"
            )
        })
    };
    !(matches!(stem.as_str(), "CON" | "PRN" | "AUX" | "NUL" | "CONIN$" | "CONOUT$")
        || numbered("COM")
        || numbered("LPT"))
}

/// Ein Pfad, wie ihn der SFTP-Server von OpenSSH unter Windows nennt (`/C:/Users/x`), als Windows-Pfad
/// (`C:\Users\x`) – als Literal für PowerShell. Nur ein Laufwerksbuchstabe und darunter Namen; kein `..`, kein
/// Netzwerkpfad, kein `/` allein (das ist dort die Liste der Laufwerke).
pub(crate) fn path(sftp: &str) -> Result<String, Failure> {
    let rest = sftp.strip_prefix('/').ok_or_else(|| unsafe_path(sftp))?;
    let mut characters = rest.chars();
    let (Some(drive), Some(':')) = (characters.next(), characters.next()) else {
        return Err(unsafe_path(sftp));
    };
    let below = characters.as_str();
    if !drive.is_ascii_alphabetic() || !(below.is_empty() || below.starts_with('/')) {
        return Err(unsafe_path(sftp));
    }

    let names: Vec<&str> = below.split('/').filter(|name| !name.is_empty()).collect();
    if names.iter().any(|name| !plain_name(name)) {
        return Err(unsafe_path(sftp));
    }

    quote(&format!("{}:\\{}", drive.to_ascii_uppercase(), names.join("\\"))).ok_or_else(|| unsafe_path(sftp))
}

/// Ein Name direkt in einem Verzeichnis (beim Packen): kein Pfad, nicht `.` oder `..`; als `.\name`, damit ein
/// Name, der mit `-` beginnt, für `tar` kein Schalter ist (Windows PowerShell reicht `--` an Programme nicht
/// verlässlich weiter).
fn member(name: &str) -> Result<String, Failure> {
    if name.contains('/') || !plain_name(name) {
        return Err(unsafe_path(name));
    }

    quote(&format!(".\\{name}")).ok_or_else(|| unsafe_path(name))
}

/// Belegter Platz eines Verzeichnisses wie `du -sk`: KiB, dann ein Name. Fehlt es, ist das ein Fehler wie bei `du`
/// und nicht „0 KiB“ (`Get-ChildItem` schweigt dazu – Hinweis von Codex); was darin unlesbar ist, fehlt still.
pub(crate) fn size(sftp: &str) -> Result<String, Failure> {
    let path = path(sftp)?;
    Ok(script(&format!(
        "if(-not(Test-Path -LiteralPath {path})){{throw 'no such file or directory'}};$s=(Get-ChildItem -LiteralPath {path} -Recurse -Force -File -ErrorAction SilentlyContinue|Measure-Object Length -Sum).Sum;\
         '{{0}} .'-f{KIB}"
    )))
}

/// Belegter Platz je Eintrag eines Verzeichnisses wie [`crate::exec::usage_command`]: `KiB ./name`, zuletzt
/// `KiB .` für das Verzeichnis selbst. Fehlt es, wie bei [`size`] ein Fehler.
pub(crate) fn usage(sftp: &str) -> Result<String, Failure> {
    let path = path(sftp)?;
    Ok(script(&format!(
        "if(-not(Test-Path -LiteralPath {path})){{throw 'no such file or directory'}};$a=0;foreach($e in Get-ChildItem -LiteralPath {path} -Force -ErrorAction SilentlyContinue){{\
         $s=if($e.PSIsContainer){{(Get-ChildItem -LiteralPath $e.FullName -Recurse -Force -File -ErrorAction SilentlyContinue|Measure-Object Length -Sum).Sum}}else{{$e.Length}};\
         $a+=$s;'{{0}} ./{{1}}'-f{KIB},$e.Name}};$s=$a;'{{0}} .'-f{KIB}"
    )))
}

/// `$s` Bytes als angefangene KiB wie bei `du -k`. Aufrunden mit `Ceiling`: Ein Wandeln nach `[uint64]` rundet
/// kaufmännisch, und `(s+1023)/1024` ergab so für genau 1024 Bytes 2 KiB (gesehen 2026-10-19 am Windows-Gast).
const KIB: &str = "[uint64][math]::Ceiling([double]$s/1024)";

/// SHA-256 je Datei wie `sha256sum`: eine Zeile je Datei in der Reihenfolge der Anfrage, `-` wo es keine gab.
pub(crate) fn checksums(paths: &[String]) -> Result<String, Failure> {
    let files = paths.iter().map(|sftp| path(sftp)).collect::<Result<Vec<_>, _>>()?;
    Ok(script(&format!(
        "foreach($f in @({})){{try{{(Get-FileHash -LiteralPath $f -Algorithm SHA256).Hash.ToLower()+' x'}}catch{{'-'}}}}",
        files.join(",")
    )))
}

/// Kopieren oder Verschieben auf dem Server, je Paar von Quelle und Ziel; beim ersten Fehler hört die Reihe auf.
/// Die Prüfungen davor (Anzahl, nicht in sich selbst hinein) macht [`crate::exec::copy_command`] für beide Arten.
pub(crate) fn copy(pairs: &[(String, String)], moving: bool) -> Result<String, Failure> {
    let mut steps = Vec::with_capacity(pairs.len());
    for (from, to) in pairs {
        let (from, to) = (path(from)?, path(to)?);
        steps.push(if moving {
            format!("Move-Item -LiteralPath {from} -Destination {to}")
        } else {
            format!("Copy-Item -LiteralPath {from} -Destination {to} -Recurse")
        });
    }

    Ok(script(&steps.join(";")))
}

/// Packen mit dem `tar` von Windows (bsdtar, seit Windows 10 1803 dabei), gzip-komprimiert, wie unter Linux. Ein
/// vorhandenes Archiv wird nicht überschrieben. Die Argumente gehen als Liste an das Programm, nicht durch eine Shell.
pub(crate) fn pack(directory: &str, names: &[String], archive: &str) -> Result<String, Failure> {
    if names.is_empty() {
        return Err(Failure {
            code: "failed",
            message: "nothing to pack".to_owned(),
        });
    }

    let (directory, archive) = (path(directory)?, member(archive)?);
    let names = names.iter().map(|name| member(name)).collect::<Result<Vec<_>, _>>()?;
    Ok(script(&format!(
        "Set-Location -LiteralPath {directory};\
         if(Test-Path -LiteralPath {archive}){{throw 'the archive already exists'}};\
         & tar.exe -czf {archive} {};if($LASTEXITCODE){{throw 'tar failed'}}",
        names.join(" ")
    )))
}

/// Entpacken mit bsdtar – auch ZIP. `-k` lässt Vorhandenes stehen; Pfade, die aus dem Ordner führen (absolut oder
/// mit `..`), legt bsdtar ohne `-P` gar nicht erst an.
pub(crate) fn unpack(directory: &str, archive: &str) -> Result<String, Failure> {
    let (directory, archive) = (path(directory)?, member(archive)?);
    Ok(script(&format!(
        "Set-Location -LiteralPath {directory};& tar.exe -xkf {archive};if($LASTEXITCODE){{throw 'tar failed'}}"
    )))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_command_of_the_app_is_only_ever_base64_on_the_line() {
        let line = user_command("Get-Date & calc # 'x'");

        assert!(line.starts_with("powershell -NoLogo"), "{line}");
        let encoded = line.rsplit(' ').next().unwrap();
        assert!(
            encoded.chars().all(|c| c.is_ascii_alphanumeric() || "+/=".contains(c)),
            "{encoded}"
        );
    }

    /// Führt das echte PowerShell dieses Rechners aus, über `cmd` wie bei OpenSSH unter Windows.
    #[cfg(windows)]
    #[test]
    fn a_command_of_the_app_ends_with_the_status_powershell_gives() {
        for (text, status) in [
            ("Write-Output 'a'", 0),
            ("cmd /c exit 3", 3),
            ("Get-Item 'C:\\nexus-gibt-es-nicht'", 1),
            ("Write-Output 'a' # ein Kommentar am Ende", 0),
            ("cmd /c exit 3; Write-Output 'a'", 0),
        ] {
            let ran = std::process::Command::new("cmd")
                .args(["/c", &user_command(text)])
                .output()
                .unwrap();
            assert_eq!(ran.status.code(), Some(status), "{text}");
        }
    }

    #[test]
    fn a_literal_cannot_be_left_with_any_kind_of_single_quote() {
        assert_eq!(quote("plain").unwrap(), "'plain'");
        assert_eq!(quote("it's").unwrap(), "'it''s'");
        assert_eq!(quote("a\u{2019}b").unwrap(), "'a\u{2019}\u{2019}b'");
        assert_eq!(quote("$(calc)&x;\"y\"").unwrap(), "'$(calc)&x;\"y\"'");
        assert_eq!(quote(""), None);
        assert_eq!(quote("a\nb"), None);
    }

    #[test]
    fn sftp_paths_become_windows_paths_and_nothing_else() {
        assert_eq!(path("/C:/Users/Administrator").unwrap(), r"'C:\Users\Administrator'");
        assert_eq!(path("/d:").unwrap(), r"'D:\'");
        assert_eq!(path("/C:/").unwrap(), r"'C:\'");
        assert_eq!(path("/C:/it's here").unwrap(), r"'C:\it''s here'");
        for bad in [
            "/",
            "C:/x",
            "/C:/a/../b",
            "/C:/a/./b",
            "//server/share",
            "/CD:/x",
            "/1:/x",
            r"/C:/a\b",
            "/C:x",
            "/C:/a:b",
            "/C:/a\u{0}b",
            "/C:/a./b",
            "/C:/.. /b",
            "/C:/a ",
            "/C:/.../b",
            "/C:/CON",
            "/C:/x/nul.txt",
            "/C:/x/Com1",
            "/C:/x/lpt\u{B9}.log",
            "/C:/x/aux .txt",
            "/C:/x/CONIN$",
        ] {
            assert!(path(bad).is_err(), "{bad}");
        }
    }

    #[test]
    fn a_script_travels_as_base64_and_carries_no_name_in_the_clear() {
        let line = copy(&[("/C:/a&calc&b".to_owned(), "/C:/x".to_owned())], false).unwrap();
        let (fixed, encoded) = line.rsplit_once(' ').unwrap();
        assert_eq!(
            fixed,
            "powershell -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand"
        );
        assert!(
            encoded
                .bytes()
                .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'+' | b'/' | b'='))
        );
        let bytes = crate::sftp::from_base64(encoded).unwrap();
        let utf16: Vec<u16> = bytes
            .chunks(2)
            .map(|pair| u16::from_le_bytes([pair[0], pair[1]]))
            .collect();
        let script = String::from_utf16(&utf16).unwrap();
        assert!(
            script.contains(r"Copy-Item -LiteralPath 'C:\a&calc&b' -Destination 'C:\x' -Recurse"),
            "{script}"
        );
    }

    /// Das Skript hinter der Befehlszeile, zum Nachsehen in Tests – ein komprimiertes ausgepackt.
    fn decoded(line: &str) -> String {
        let bytes = crate::sftp::from_base64(line.rsplit_once(' ').unwrap().1).unwrap();
        let utf16: Vec<u16> = bytes
            .chunks(2)
            .map(|pair| u16::from_le_bytes([pair[0], pair[1]]))
            .collect();
        let script = String::from_utf16(&utf16).unwrap();
        let Some(packed) = script
            .strip_prefix(
                "$ProgressPreference='SilentlyContinue';\
                 $z=New-Object IO.Compression.DeflateStream((New-Object IO.MemoryStream(,[Convert]::FromBase64String('",
            )
            .and_then(|rest| rest.split_once('\''))
        else {
            return script;
        };
        let mut unpacked = String::new();
        std::io::Read::read_to_string(
            &mut flate2::read::DeflateDecoder::new(&crate::sftp::from_base64(packed.0).unwrap()[..]),
            &mut unpacked,
        )
        .unwrap();
        unpacked
    }

    #[test]
    fn every_script_fits_on_a_line_of_cmd_and_a_long_one_travels_packed() {
        // `cmd.exe` nimmt höchstens 8191 Zeichen je Zeile; die Schleife wäre ungepackt länger.
        for (name, line, script) in [
            ("monitor", command(&monitor(false)), monitor(false)),
            ("monitor without CIM", command(&monitor(true)), monitor(true)),
        ] {
            assert!(line.len() <= 8191, "{name}: {}", line.len());
            assert!(decoded(&line).contains(&script), "{name}");
        }
        assert!(encoded(&monitor(true)).len() > 8191);
        // Kurze bleiben, wie sie sind – lesbar beim Nachsehen.
        assert_eq!(command("'x'"), encoded("'x'"));
        assert!(!monitor(true).contains("__NXW__") && monitor(true).contains("public static class NxW"));
        assert!(monitor(false).contains("catch{E @('@nocim');exit}"));
        // Beide nennen zuerst ihre Nummer.
        for plain in [false, true] {
            assert!(monitor(plain).contains("E @(\"@pid $PID $((Get-Process -Id $PID).StartTime.ToFileTimeUtc())\")"));
        }
        // Die Klasse steht in einfachen Anführungszeichen von PowerShell.
        assert!(!nxw!().contains('\''));
    }

    #[test]
    fn the_loop_is_ended_only_while_its_number_still_names_it() {
        let running = Loop::parse("3740 134052012345678901").unwrap();
        assert_eq!(
            running,
            Loop {
                pid: 3740,
                started: 134_052_012_345_678_901
            }
        );
        for bad in ["", "3740", "0 5", "3740 0", "-1 5", "3740 5 6", "x 5", "3740 1e5"] {
            assert_eq!(Loop::parse(bad), None, "{bad}");
        }

        let script = decoded(&end_loop(running));
        assert!(
            script.contains(
                "$p=Get-Process -Id 3740 -ErrorAction SilentlyContinue;\
                 if($p-and$p.StartTime.ToFileTimeUtc()-eq134052012345678901){Stop-Process -InputObject $p -Force}"
            ),
            "{script}"
        );
    }

    #[test]
    fn sizes_are_started_kib_like_du() {
        // Aufgerundet, nicht kaufmännisch gerundet: 1024 Bytes sind 1 KiB.
        assert!(decoded(&size("/C:/data").unwrap()).contains("[uint64][math]::Ceiling([double]$s/1024)"));
        assert!(!decoded(&usage("/C:/data").unwrap()).contains("+1023"));
    }

    #[test]
    fn packing_names_only_what_lies_in_the_folder() {
        assert!(pack("/C:/data", &["a.txt".to_owned()], "a.tar.gz").is_ok());
        assert!(pack("/C:/data", &[r"..\x".to_owned()], "a.tar.gz").is_err());
        assert!(pack("/C:/data", &["a.txt".to_owned()], "../a.tar.gz").is_err());
        assert!(pack("/C:/data", &[], "a.tar.gz").is_err());
        assert!(unpack("/C:/data", "C:a.zip").is_err());
        // Was Windows anders läse, als es dasteht: ein Gerät, ein gekürzter Name.
        for bad in ["CON", "nul.tar.gz", "COM9", "a.", "a "] {
            assert!(pack("/C:/data", &["a.txt".to_owned()], bad).is_err(), "{bad}");
            assert!(pack("/C:/data", &[bad.to_owned()], "a.tar.gz").is_err(), "{bad}");
        }
        // Ähnliche Namen bleiben erlaubt.
        for fine in ["CONSOLE.txt", "COM10", "nullable", "lpt", "a.b"] {
            assert!(pack("/C:/data", &[fine.to_owned()], "a.tar.gz").is_ok(), "{fine}");
            assert!(path(&format!("/C:/x/{fine}")).is_ok(), "{fine}");
        }
    }
}
