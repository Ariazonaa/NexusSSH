//! Einen Befehl auf dem Server ausführen und sein Ergebnis einsammeln – für das Wenige, was SFTP nicht kann:
//! packen und entpacken, die Liste der Prozesse.
//!
//! Die Befehle baut der Kern selbst (`pack_command` …); von der App kommen nur Pfade und Namen, und die stehen
//! jeweils als ein einziges, in einfache Anführungszeichen gesetztes Argument darin ([`quote`]). Ein Name mit
//! Steuerzeichen wird gar nicht erst ausgeführt.
//!
//! Die eine Ausnahme ist [`run_command`]: Dort ist der Text der App der Befehl (`exec.run`, für das Werkzeug
//! `run_command` des MCP-Servers). Die App schickt ihn nur, nachdem der Benutzer genau diesen Text freigegeben hat.

use std::time::Duration;

use russh::ChannelMsg;
use russh::client::{Handle, Handler};
use tokio::time::timeout;

use crate::sftp::Failure;

/// So viel Ausgabe wird höchstens eingesammelt; was darüber hinausgeht, fällt weg. Bemessen am Journal: Ein Eintrag
/// kostet dort als JSON gut 500 Bytes, und [`crate::logs::MAX_LINES`] davon sollen hineinpassen.
pub(crate) const MAX_OUTPUT: usize = 2 * 1024 * 1024;

/// So oft und in diesem Abstand versucht der Kern noch einmal, den Kanal für einen Befehl zu öffnen.
const OPEN_RETRIES: u32 = 5;
const OPEN_RETRY_DELAY: Duration = Duration::from_millis(200);

/// So lange darf ein Befehl laufen, bevor der Kern aufgibt.
pub(crate) const LIMIT: Duration = Duration::from_secs(300);

/// Fehlercode, wenn der Server keinen weiteren Kanal öffnet – meist, weil die Verbindung so viele hat, wie er
/// zulässt (`MaxSessions`; OpenSSH antwortet dann „connect failed“).
pub(crate) const NO_CHANNEL: &str = "no_channel";

/// Öffnet einen Kanal für einen Befehl. Ein Server, der nur wenige Kanäle je Verbindung zulässt (`MaxSessions`),
/// lehnt einen neuen ab, solange er den eines vorigen Befehls noch nicht abgeräumt hat – das dauert dort einen
/// Augenblick länger als hier. Deshalb ein paar Versuche in kurzem Abstand; danach [`NO_CHANNEL`].
pub(crate) async fn open_channel<H: Handler>(
    handle: &Handle<H>,
) -> Result<russh::Channel<russh::client::Msg>, Failure> {
    let mut attempt = 0;
    loop {
        match handle.channel_open_session().await {
            Ok(channel) => return Ok(channel),
            Err(_) if attempt < OPEN_RETRIES => {
                attempt += 1;
                tokio::time::sleep(OPEN_RETRY_DELAY).await;
            }
            Err(error) => {
                return Err(Failure {
                    code: NO_CHANNEL,
                    message: error.to_string(),
                });
            }
        }
    }
}

/// Was ein Befehl hinterlassen hat.
#[derive(Debug, Default)]
pub(crate) struct Output {
    /// Rückgabewert; `None`, wenn der Server keinen nannte.
    pub(crate) status: Option<u32>,
    /// Standardausgabe, höchstens [`MAX_OUTPUT`].
    pub(crate) stdout: String,
    /// Fehlerausgabe, gekürzt – sie ist die Erklärung, wenn der Befehl scheitert.
    pub(crate) stderr: String,
}

/// Führt `command` in einem eigenen Kanal aus und wartet auf sein Ende.
pub(crate) async fn run<H: Handler>(handle: &Handle<H>, command: &str, limit: Duration) -> Result<Output, Failure> {
    run_with(handle, command, None, limit).await
}

/// Wie [`run`], mit einer Eingabe für den Befehl: Sie wird ihm geschickt, danach endet seine Eingabe.
pub(crate) async fn run_with<H: Handler>(
    handle: &Handle<H>,
    command: &str,
    input: Option<&[u8]>,
    limit: Duration,
) -> Result<Output, Failure> {
    let raw = run_raw(handle, command, input, MAX_OUTPUT, limit).await?;
    Ok(Output {
        status: raw.status,
        stdout: String::from_utf8_lossy(&raw.stdout).into_owned(),
        stderr: raw.stderr,
    })
}

/// Führt `work` auf einem eigenen Kanal aus, alles zusammen höchstens `limit` lang, und **schließt den Kanal danach
/// in jedem Fall** – auch nach Ablauf der Frist und nach einem Fehler, und bevor die Antwort zurückgeht. Fiele er
/// nur weg, bliebe er für den Server offen (russh schickt dann nichts), und ein Server, der nur wenige Kanäle je
/// Verbindung zulässt (`MaxSessions`), lehnte danach jeden weiteren Befehl ab – so an einer Abfrage, die ihre Frist
/// überschritt (gefunden 2026-10-24).
async fn on_channel<H: Handler, T>(
    handle: &Handle<H>,
    limit: Duration,
    work: impl AsyncFnOnce(&mut russh::Channel<russh::client::Msg>) -> Result<T, Failure>,
) -> Result<T, Failure> {
    let timed_out = || Failure {
        code: "timeout",
        message: "the command did not finish in time".to_owned(),
    };
    let started = std::time::Instant::now();
    let mut channel = timeout(limit, open_channel(handle)).await.map_err(|_| timed_out())??;
    let result = timeout(limit.saturating_sub(started.elapsed()), work(&mut channel)).await;
    let _ = channel.close().await;
    result.unwrap_or_else(|_| Err(timed_out()))
}

/// Was ein Befehl hinterlassen hat, die Ausgabe als rohe Bytes.
#[derive(Debug, Default)]
pub(crate) struct RawOutput {
    pub(crate) status: Option<u32>,
    pub(crate) stdout: Vec<u8>,
    pub(crate) stderr: String,
}

/// Wie [`run_with`], aber die Ausgabe bleibt, wie sie ist – für den Inhalt von Dateien (höchstens `limit_output`
/// Bytes). Eine Eingabe wird dem Befehl geschickt, danach endet sie.
pub(crate) async fn run_raw<H: Handler>(
    handle: &Handle<H>,
    command: &str,
    input: Option<&[u8]>,
    limit_output: usize,
    limit: Duration,
) -> Result<RawOutput, Failure> {
    let failed = |message: String| Failure {
        code: "failed",
        message,
    };
    on_channel(handle, limit, async |channel| {
        channel
            .exec(true, command)
            .await
            .map_err(|error| failed(error.to_string()))?;
        if let Some(input) = input {
            channel.data(input).await.map_err(|error| failed(error.to_string()))?;
            let _ = channel.eof().await;
        }

        let mut output = RawOutput::default();
        let mut stderr = Vec::new();
        loop {
            match channel.wait().await {
                Some(ChannelMsg::Data { data }) => append(&mut output.stdout, &data, limit_output),
                Some(ChannelMsg::ExtendedData { data, .. }) => append(&mut stderr, &data, 4096),
                Some(ChannelMsg::ExitStatus { exit_status }) => output.status = Some(exit_status),
                // Der Server lässt für dieses Konto keine Befehle zu (nur SFTP).
                Some(ChannelMsg::Failure) => {
                    return Err(Failure {
                        code: "no_exec",
                        message: "the server does not run commands for this account".to_owned(),
                    });
                }
                Some(ChannelMsg::Close) | None => break,
                Some(_) => {}
            }
        }

        output.stderr = String::from_utf8_lossy(&stderr).trim().to_owned();
        Ok(output)
    })
    .await
}

/// Nennt die Anmelde-Shell des Kontos. Fester Text, nichts vom Benutzer oder vom Server darin.
pub(crate) const LOGIN_SHELL: &str = "printf %s \"$SHELL\"";

/// So viel vom Anfang eines Stroms bleibt bei einem Befehl der App ([`run_command`]).
const RUN_HEAD: usize = 16 * 1024;

/// So viel vom Ende bleibt. Zusammen passt das noch in die Antwort an ein MCP-Programm.
const RUN_TAIL: usize = 32 * 1024;

/// Anfang und Ende eines Stroms; die Mitte fällt weg. Beim Suchen eines Fehlers steht das Wichtige am Schluss.
#[derive(Default)]
struct Kept {
    head: Vec<u8>,
    tail: std::collections::VecDeque<u8>,
    left_out: u64,
}

impl Kept {
    fn add(&mut self, data: &[u8]) {
        let into_head = data.len().min(RUN_HEAD - self.head.len());
        self.head.extend_from_slice(&data[..into_head]);
        self.tail.extend(&data[into_head..]);
        let excess = self.tail.len().saturating_sub(RUN_TAIL);
        self.tail.drain(..excess);
        self.left_out += excess as u64;
    }

    fn text(self) -> String {
        let head = String::from_utf8_lossy(&self.head);
        let tail = self.tail.into_iter().collect::<Vec<u8>>();
        let tail = String::from_utf8_lossy(&tail);
        if self.left_out > 0 {
            format!("{head}\n[... {} bytes left out ...]\n{tail}", self.left_out)
        } else {
            format!("{head}{tail}")
        }
    }
}

/// So lang darf ein Befehl der App sein: genug für ein kleines Skript, und `sh -c` samt Maskierung passt noch in ein
/// SSH-Paket.
const RUN_COMMAND: usize = 16 * 1024;

/// Zusätzliche Zeit für den Kanal: Die Frist des Befehls läuft innerhalb von [`on_channel`], damit bei ihrem Ablauf
/// die Ausgabe bis dahin noch da ist.
const RUN_GRACE: Duration = Duration::from_secs(5);

/// Was ein Befehl der App hinterlassen hat.
#[derive(Clone, Debug, Default, PartialEq, Eq, serde::Serialize)]
pub struct RunOutput {
    /// Rückgabewert; `None`, wenn der Server keinen nannte oder der Befehl noch lief.
    pub(crate) status: Option<u32>,
    pub(crate) stdout: String,
    pub(crate) stderr: String,
    /// In einem der Ströme fehlt die Mitte ([`Kept`]).
    pub(crate) cut: bool,
    /// Die Frist lief ab, bevor der Befehl endete; er kann am Server weiterlaufen.
    pub(crate) timed_out: bool,
}

/// Lehnt ab, was kein Befehl sein kann. Sonst wird am Text nichts geprüft: Was er tut, hat der Benutzer gesehen.
pub(crate) fn check_command(command: &str) -> Result<(), Failure> {
    if command.trim().is_empty() || command.contains('\0') || command.len() > RUN_COMMAND {
        return Err(Failure {
            code: "invalid_command",
            message: "the command is empty, too long or contains a NUL character".to_owned(),
        });
    }

    Ok(())
}

/// Führt einen fertigen Befehl der App aus. Anders als [`run_raw`] gibt der Befehl bei Ablauf von `limit` nicht auf,
/// sondern liefert, was bis dahin kam – gerade beim Suchen eines Fehlers ist das die Antwort.
pub(crate) async fn run_command<H: Handler>(
    handle: &Handle<H>,
    command: &str,
    limit: Duration,
) -> Result<RunOutput, Failure> {
    let failed = |message: String| Failure {
        code: "failed",
        message,
    };
    on_channel(handle, limit + RUN_GRACE, async |channel| {
        channel
            .exec(true, command)
            .await
            .map_err(|error| failed(error.to_string()))?;

        // Es kommt keine Eingabe: Ein `cat` soll enden, statt bis zur Frist zu warten.
        let _ = channel.eof().await;

        let deadline = tokio::time::Instant::now() + limit;
        let mut output = RunOutput::default();
        let (mut stdout, mut stderr) = (Kept::default(), Kept::default());
        loop {
            match tokio::time::timeout_at(deadline, channel.wait()).await {
                Err(_) => {
                    output.timed_out = true;
                    break;
                }
                Ok(Some(ChannelMsg::Data { data })) => stdout.add(&data),
                Ok(Some(ChannelMsg::ExtendedData { data, .. })) => stderr.add(&data),
                Ok(Some(ChannelMsg::ExitStatus { exit_status })) => output.status = Some(exit_status),
                Ok(Some(ChannelMsg::Failure)) => {
                    return Err(Failure {
                        code: "no_exec",
                        message: "the server does not run commands for this account".to_owned(),
                    });
                }
                Ok(Some(ChannelMsg::Close) | None) => break,
                Ok(Some(_)) => {}
            }
        }

        output.cut = stdout.left_out > 0 || stderr.left_out > 0;
        output.stdout = stdout.text();
        output.stderr = stderr.text();
        Ok(output)
    })
    .await
}

fn append(target: &mut Vec<u8>, data: &[u8], limit: usize) {
    let room = limit.saturating_sub(target.len());
    target.extend_from_slice(&data[..data.len().min(room)]);
}

/// Macht aus dem Ergebnis eines Befehls, der nur gelingen oder scheitern kann, eine Antwort: Bei einem
/// Rückgabewert ungleich 0 wird die Fehlerausgabe zur Meldung.
pub(crate) fn succeeded(output: Output) -> Result<Output, Failure> {
    match output.status {
        Some(0) | None => Ok(output),
        Some(status) => Err(Failure {
            code: "command_failed",
            message: if output.stderr.is_empty() {
                format!("exit status {status}")
            } else {
                output.stderr
            },
        }),
    }
}

/// Was eine Anmelde-Shell antwortet, die [`posix`] und [`quote`] so liest wie gedacht.
pub(crate) const POSIX_ANSWER: &str = "nexus-posix";

/// Ein Befehl ohne Wirkung, an dem sich zeigt, ob die Anmelde-Shell des Kontos einfache Anführungszeichen so liest
/// wie eine POSIX-Shell (sh, bash, fish, csh tun es). Alle Befehle des Kerns verlassen sich darauf: Ein Name vom
/// Server steht in `'…'`, ein `'` darin als `'\''`. `cmd.exe` (OpenSSH unter Windows) kennt das nicht und trennte
/// einen Namen wie `a&calc&b` an `&` – dort liefe dann, was im Namen steht. Die Probe benutzt genau diese
/// Schreibweise und hängt ein Argument an, das nur eine solche Shell für einen Befehl hält; eine POSIX-Shell
/// antwortet mit [`POSIX_ANSWER`] und sonst nichts.
pub(crate) fn posix_probe() -> String {
    format!("{} '&echo nexus-other'", posix("echo nexus-'posix'"))
}

/// Ein Text als ein einziges Argument für eine POSIX-Shell: in einfachen Anführungszeichen, ein enthaltenes `'`
/// als `'\''`. `None` für einen leeren Text oder einen mit Steuerzeichen – der ließe sich nicht sicher übergeben.
pub(crate) fn quote(text: &str) -> Option<String> {
    if text.is_empty() || text.chars().any(char::is_control) {
        return None;
    }

    Some(format!("'{}'", text.replace('\'', r"'\''")))
}

/// Ein Befehl für `sh`, gleich welche Anmelde-Shell das Konto hat. Der Server führt, was er per `exec` bekommt,
/// mit der Shell des Benutzers aus – und die kann fish sein, das weder `{ …; }` noch `if …; then …; fi` versteht
/// (oder csh). Die Abfrage der Auslastung macht es genauso ([`crate::monitor`]).
///
/// Der Befehl steht in einfachen Anführungszeichen; ein `'` darin wird zu `'\''` und ein `\` zu `'\\'` – beide
/// also außerhalb der Anführungszeichen. Das zweite ist für fish: Dort ist `\'` auch *in* einfachen
/// Anführungszeichen ein maskiertes Zeichen, in `sh` nicht – ein Rückstrich direkt vor einem `'` (wie ihn [`quote`]
/// für einen Namen mit Apostroph schreibt) ließe die beiden Shells Verschiedenes lesen. Ebenso ein `!` als `'\!'`:
/// csh (unter FreeBSD die Shell von root, unter OPNsense hinter dem Menü) liest `!` auch in einfachen
/// Anführungszeichen als Griff in den Verlauf und bricht mit „Event not found“ ab (gefunden 2026-10-15 an
/// OPNsense 26.7); `\!` ist dort wie in sh, bash und fish ein schlichtes `!`.
pub(crate) fn posix(command: &str) -> String {
    let mut wrapped = String::with_capacity(command.len() + 16);
    wrapped.push_str("sh -c '");
    for character in command.chars() {
        match character {
            '\'' => wrapped.push_str(r"'\''"),
            '\\' => wrapped.push_str(r"'\\'"),
            '!' => wrapped.push_str(r"'\!'"),
            other => wrapped.push(other),
        }
    }

    wrapped.push('\'');
    wrapped
}

fn unsafe_argument(text: &str) -> Failure {
    Failure {
        code: "unsafe_name",
        message: format!("{text:?} cannot be passed to a command"),
    }
}

fn argument(text: &str) -> Result<String, Failure> {
    quote(text).ok_or_else(|| unsafe_argument(text))
}

/// Ein Name im Verzeichnis: ohne Schrägstrich und nicht `.`/`..` – er soll nicht aus dem Verzeichnis herausführen.
/// Mit `./` davor wird er auch dann nicht für eine Option gehalten, wenn er mit `-` beginnt.
fn member(name: &str) -> Result<String, Failure> {
    if name.contains('/') || name == "." || name == ".." {
        return Err(unsafe_argument(name));
    }

    argument(&format!("./{name}"))
}

/// `tar`, das die genannten Einträge von `directory` in `archive` (im selben Verzeichnis) packt, gzip-komprimiert.
/// Eine vorhandene Datei dieses Namens überschreibt es nicht.
pub(crate) fn pack_command(directory: &str, names: &[String], archive: &str) -> Result<String, Failure> {
    if names.is_empty() {
        return Err(Failure {
            code: "failed",
            message: "nothing to pack".to_owned(),
        });
    }

    let (directory, archive) = (argument(directory)?, member(archive)?);
    let names = names
        .iter()
        .map(|name| member(name))
        .collect::<Result<Vec<_>, _>>()?
        .join(" ");

    // `test -e` vorab: tar überschriebe ein vorhandenes Archiv ohne Rückfrage.
    Ok(format!(
        "cd {directory} && if test -e {archive}; then echo 'the archive already exists' >&2; exit 17; fi && tar -czf {archive} -- {names}"
    ))
}

/// Entpackt `archive` in sein eigenes Verzeichnis: `.zip` mit `unzip` (ohne Vorhandenes zu überschreiben), alles
/// andere mit `tar`, das die Kompression selbst erkennt (ebenfalls ohne zu überschreiben).
///
/// Ohne `unzip` (DSM von Synology hat keines, aber `7z`) entpackt `7z x -aos` – vorher sieht der Befehl die Liste
/// durch und lehnt ein Archiv ab, dessen Einträge mit `/` beginnen, `..` enthalten oder Verweise sind: Anders als
/// `unzip` legt `7z` sonst an, was außerhalb des Ordners liegt, oder schreibt durch einen Verweis dorthin.
pub(crate) fn unpack_command(directory: &str, archive: &str) -> Result<String, Failure> {
    let (directory, file) = (argument(directory)?, member(archive)?);
    Ok(if archive.to_lowercase().ends_with(".zip") {
        format!(
            "cd {directory} && if command -v unzip >/dev/null 2>&1; then unzip -n -q {file}; \
             elif command -v 7z >/dev/null 2>&1; then \
             if 7z l -slt -- {file} | sed -n '/^----------/,$p' | grep -qE '^(Path = ([/\\]|(.*[/\\])?[.][.]([/\\]|$))|Attributes = .*[ ]l[rwx-]{{9}})'; then \
             echo 'the archive holds paths that would leave the folder' >&2; exit 18; fi; \
             7z x -aos -bd -- {file} >/dev/null; \
             else echo 'unzip: command not found' >&2; exit 127; fi"
        )
    } else {
        // Das `tar` von BusyBox unter OpenWrt erkennt gzip nicht von selbst („short read“): Liest `tar` das Archiv
        // nicht, geht es durch `gzip` – vorher geprüft, denn hinter der Pipe ginge ein Fehler von `gzip` verloren.
        // Das `tar` von OpenBSD kennt kein `-k` und überschriebe; dort entpackt `pax -k` (gzip reicht mit `-f` auch
        // Unkomprimiertes unverändert durch). Anders als jedes `tar` legt `pax` aber auch an, was im Archiv mit `/`
        // beginnt oder `..` enthält – außerhalb des Ordners (geprüft an OpenBSD 7.9). Deshalb sieht es das Archiv
        // vorher durch und lehnt ein solches ab.
        format!(
            "cd {directory} && if test \"$(uname -s)\" = OpenBSD; then \
             if gzip -dcf < {file} | pax | grep -qE '(^/|(^|/)[.][.](/|$))'; then \
             echo 'the archive holds paths that would leave the folder' >&2; exit 18; fi; \
             gzip -dcf < {file} | pax -rk; elif tar -tf {file} >/dev/null 2>&1; then tar -xkf {file}; \
             else gzip -t < {file} && gzip -dc < {file} | tar -xkf -; fi"
        )
    })
}

/// Was ein Befehl dem Aufrufer außer „gelungen“ zu sagen hat.
#[derive(Debug, Default, PartialEq, Eq)]
pub(crate) enum Answer {
    /// Nichts weiter.
    #[default]
    Nothing,
    /// Belegter Platz in Bytes; `None`, wenn der Server keine Zahl nannte.
    Size(Option<u64>),
    /// Belegter Platz je Eintrag eines Verzeichnisses.
    Usage(Vec<Usage>),
    /// SHA-256 je gefragter Datei, als Hex; `None`, wo es keine gab.
    Sums(Vec<Option<String>>),
    /// Ein Stück einer Datei als Base64 und die Größe der ganzen Datei.
    Data(String, u64),
    /// Wohin ein Link zeigt.
    Target(String),
    /// Laufende Sitzungen von tmux und screen.
    Mux(Vec<MuxSession>),
    /// Zeilen eines Protokolls, und ob das Konto nur einen Teil des Journals sieht.
    Logs(Vec<crate::logs::LogLine>, bool),
    /// Die Namen der Programme im Suchpfad des Kontos.
    Commands(Vec<String>),
    /// Eine Zeile Text – die Art der Shell (`commands.probe`).
    Line(String),
    /// Was ein Befehl der App hinterlassen hat (`exec.run`).
    Run(RunOutput),
}

/// Fragt nach den laufenden Sitzungen von tmux und screen. Fester Text, nichts vom Benutzer oder vom Server darin.
/// Bei tmux steht der Name am Ende der Zeile – er darf Leerzeichen enthalten. Fehlt eines der Programme oder
/// läuft nichts, kommt dafür einfach keine Zeile; das `true` am Ende hält den Befehl erfolgreich.
pub(crate) const MUX_SESSIONS: &str = concat!(
    "tmux list-sessions -F \"T #{session_windows} #{session_attached} #{session_name}\" 2>/dev/null; ",
    "screen -ls 2>/dev/null | sed \"s/^/S /\"; true"
);

/// Eine laufende Sitzung von tmux oder screen.
#[derive(Clone, Debug, PartialEq, Eq, serde::Serialize)]
pub struct MuxSession {
    /// `tmux` oder `screen`.
    pub kind: &'static str,
    /// Name der Sitzung (bei screen samt Prozessnummer, `1234.pts-0.host`).
    pub name: String,
    /// Zahl der Fenster; nur tmux nennt sie.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub windows: Option<u32>,
    /// Ob gerade jemand daran hängt.
    pub attached: bool,
    /// Die Zeile, die in der Shell an diese Sitzung anhängt. Der Kern baut sie – die App tippt sie nur.
    pub command: String,
}

/// Ein Name, der sich ohne Anführungszeichen in eine Befehlszeile setzen lässt, gleich welche Shell sie liest:
/// Buchstaben, Ziffern, Punkt, Unterstrich, Bindestrich. Der Name kommt vom Server und wird später in dessen
/// Shell getippt – alles andere bleibt draußen, statt es zu maskieren.
fn plain_name(name: &str) -> bool {
    (1..=64).contains(&name.len())
        && !name.starts_with('-')
        && name
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'.' | b'_' | b'-'))
}

/// Liest die Zeilen von [`MUX_SESSIONS`]. Sitzungen, deren Name sich nicht gefahrlos tippen lässt, fehlen.
pub(crate) fn parse_mux(output: &str) -> Vec<MuxSession> {
    const MOST: usize = 50;
    let mut sessions = Vec::new();
    for line in output.lines() {
        if sessions.len() >= MOST {
            break;
        }

        if let Some(rest) = line.strip_prefix("T ") {
            // „T 3 1 name“: Fenster, wie oft angehängt, Name bis zum Ende der Zeile.
            let mut parts = rest.splitn(3, ' ');
            let (Some(windows), Some(attached), Some(name)) = (parts.next(), parts.next(), parts.next()) else {
                continue;
            };
            let (Ok(windows), Ok(attached)) = (windows.parse::<u32>(), attached.parse::<u32>()) else {
                continue;
            };
            if plain_name(name) {
                sessions.push(MuxSession {
                    kind: "tmux",
                    name: name.to_owned(),
                    windows: Some(windows),
                    attached: attached > 0,
                    // „=“ davor: genau diese Sitzung, nicht eine, deren Name so anfängt.
                    command: format!("tmux attach-session -t ={name}"),
                });
            }
        } else if let Some(rest) = line.strip_prefix("S ") {
            // „<Tab>1234.pts-0.host<Tab>(Detached)“ – alles andere von `screen -ls` sind Sätze drumherum.
            let Some(name) = rest.split_whitespace().next() else {
                continue;
            };
            let numbered = name
                .split_once('.')
                .is_some_and(|(pid, _)| !pid.is_empty() && pid.bytes().all(|byte| byte.is_ascii_digit()));
            if numbered && plain_name(name) {
                sessions.push(MuxSession {
                    kind: "screen",
                    name: name.to_owned(),
                    windows: None,
                    attached: rest.contains("(Attached)"),
                    // -x hängt an, ob schon jemand daran sitzt oder nicht, und wirft niemanden hinaus.
                    command: format!("screen -x {name}"),
                });
            }
        }
    }

    sessions
}

/// `du` für ein Verzeichnis: wie viel Platz es samt allem darin belegt, in KiB. `-s` und `-k` kennt jedes `du`
/// (POSIX). Was sich nicht lesen lässt, zählt nicht mit – `du` meldet das als Fehler und nennt trotzdem die Summe,
/// deshalb gilt die Zahl und nicht der Rückgabewert.
pub(crate) fn size_command(path: &str) -> Result<String, Failure> {
    if !path.starts_with('/') {
        return Err(unsafe_argument(path));
    }

    Ok(format!("du -sk {} 2>/dev/null || true", argument(path)?))
}

/// Liest die Summe von [`size_command`]: die Zahl am Anfang der letzten Zeile, in Bytes.
pub(crate) fn parse_size(output: &str) -> Option<u64> {
    let kib: u64 = output.lines().last()?.split_whitespace().next()?.parse().ok()?;
    kib.checked_mul(1024)
}

/// Was unter einem Verzeichnis wie viel Platz belegt.
#[derive(Debug, Clone, PartialEq, Eq, serde::Serialize)]
pub struct Usage {
    /// Absoluter Pfad des Eintrags.
    pub path: String,
    /// Belegter Platz in Bytes, samt allem darin.
    pub bytes: u64,
}

/// `du` für alles direkt unter einem Verzeichnis: je Eintrag eine Zeile (`./name`), zuletzt das Verzeichnis selbst
/// (`.`). Bewusst mit `-s` über die Einträge statt `-a -d 1`: Das schließt sich bei den BSDs gegenseitig aus und
/// liefert dort nichts (gemessen an FreeBSD 14.5). Die drei Muster treffen auch versteckte Einträge; eines, das
/// auf nichts passt, bleibt wörtlich stehen und ergibt nur eine Fehlermeldung. `-x` bleibt auf dem Dateisystem –
/// sonst zählte unter `/` alles Eingehängte mit. Wie bei [`size_command`] gilt die Ausgabe, nicht der Rückgabewert.
pub(crate) fn usage_command(path: &str) -> Result<String, Failure> {
    if !path.starts_with('/') {
        return Err(unsafe_argument(path));
    }

    Ok(format!(
        "cd {} && {{ du -x -k -s ./* ./.[!.]* ./..?* 2>/dev/null; du -x -k -s . 2>/dev/null; }} || true",
        argument(path)?
    ))
}

/// Liest die Zeilen von [`usage_command`]: Zahl in KiB, Leerraum, `./name` – oder `.` für das Verzeichnis selbst.
/// Daraus wird wieder der volle Pfad unter `directory`.
pub(crate) fn parse_usage(output: &str, directory: &str) -> Vec<Usage> {
    let base = directory.trim_end_matches('/');
    output
        .lines()
        .filter_map(|line| {
            let (kib, name) = line.split_once(char::is_whitespace)?;
            let path = match name.trim_start() {
                "." => directory.to_owned(),
                name => format!("{base}/{}", name.strip_prefix("./").filter(|name| !name.is_empty())?),
            };
            Some(Usage {
                path,
                bytes: kib.parse::<u64>().ok()?.checked_mul(1024)?,
            })
        })
        .collect()
}
/// So viele Dateien nimmt eine Anfrage nach Prüfsummen höchstens – die Befehlszeile soll nicht zu lang werden.
pub(crate) const MAX_CHECKSUMS: usize = 200;

/// SHA-256 je Datei, eine Zeile je Datei in der Reihenfolge der Anfrage. Welches Programm es gibt, unterscheidet
/// sich: `sha256sum` (GNU, BusyBox), `shasum` (macOS), `sha256 -q` (die BSDs), sonst `openssl`. Gelingt keines
/// (oder lässt sich die Datei nicht lesen), steht für sie ein `-` – so bleibt die Zuordnung der Zeilen erhalten.
pub(crate) fn checksum_command(paths: &[String]) -> Result<String, Failure> {
    if paths.is_empty() || paths.len() > MAX_CHECKSUMS {
        return Err(Failure {
            code: "bad_request",
            message: format!("between 1 and {MAX_CHECKSUMS} files at once"),
        });
    }

    let mut command = String::new();
    for path in paths {
        if !path.starts_with('/') {
            return Err(unsafe_argument(path));
        }

        let file = argument(path)?;
        command.push_str(&format!(
            "{{ sha256sum {file} 2>/dev/null || shasum -a 256 {file} 2>/dev/null || sha256 -q {file} 2>/dev/null || openssl dgst -sha256 -r {file} 2>/dev/null || echo -; }} | head -n 1; "
        ));
    }

    Ok(command)
}

/// Liest die Zeilen von [`checksum_command`]: je Datei die 64 Hex-Zeichen am Anfang, sonst `None`. Es kommen immer
/// so viele Antworten zurück, wie Dateien gefragt waren – fehlende Zeilen zählen als „keine Summe“.
pub(crate) fn parse_checksums(output: &str, count: usize) -> Vec<Option<String>> {
    let mut sums: Vec<Option<String>> = output
        .lines()
        .take(count)
        .map(|line| {
            let sum = line.split_whitespace().next()?;
            (sum.len() == 64 && sum.bytes().all(|byte| byte.is_ascii_hexdigit())).then(|| sum.to_ascii_lowercase())
        })
        .collect();
    sums.resize(count, None);
    sums
}

/// Fragt, ob der Server ein NAS von Synology (DSM) ist, und nennt dann den eigenen Benutzerordner und die
/// freigegebenen Ordner auf den Volumes. Fester Text; auf jedem anderen Server gibt er nichts aus.
pub(crate) const SHARES: &str = "[ -r /etc.defaults/synoinfo.conf ] || exit 0; echo \"@home $HOME\"; for d in /volume[0-9]*/*/; do echo \"@share $d\"; done";

/// Wie Pfade des SFTP-Servers in der Shell heißen, wo das nicht dasselbe ist: Der SFTP-Server von DSM zeigt eine
/// eigene Wurzel mit den freigegebenen Ordnern (`/homes`, `/video`) und dem eigenen Benutzerordner (`/home`) –
/// in der Shell liegen sie unter `/volume1/…` und `$HOME`. Ohne Umrechnung ging jeder Befehl mit einem Pfad ins
/// Leere (`cp: cannot stat '/homes/…'`, gefunden 2026-10-15 an DSM 7.2).
#[derive(Debug, Default, PartialEq, Eq)]
pub(crate) struct SharePaths {
    home: Option<String>,
    /// Freigabe → Ordner in der Shell (`homes` → `/volume1/homes`). Ein Name gilt auf dem ersten Volume.
    shares: Vec<(String, String)>,
}

impl SharePaths {
    /// Liest, was [`SHARES`] ausgibt; `None`, wenn der Server kein DSM ist. Was nicht wie ein Volume und ein
    /// schlichter Name aussieht, bleibt draußen (Ordner wie `@appstore` sind keine Freigaben).
    pub(crate) fn parse(output: &str) -> Option<Self> {
        let mut paths = Self::default();
        let mut seen = false;
        for line in output.lines().take(1000) {
            if let Some(home) = line.strip_prefix("@home ") {
                seen = true;
                let home = home.trim_end_matches('/');
                if home.starts_with('/') && !home.contains("/../") && !home.chars().any(char::is_control) {
                    paths.home = Some(home.to_owned());
                }
            } else if let Some(folder) = line.strip_prefix("@share ") {
                let Some((volume, name)) = folder.trim_end_matches('/').rsplit_once('/') else {
                    continue;
                };
                let numbered = volume
                    .strip_prefix("/volume")
                    .is_some_and(|number| !number.is_empty() && number.bytes().all(|byte| byte.is_ascii_digit()));
                let plain = !name.is_empty()
                    && !name.starts_with(['@', '#', '.'])
                    && !name.chars().any(|character| character.is_control() || character == '/');
                if numbered && plain && !paths.shares.iter().any(|(known, _)| known == name) {
                    paths.shares.push((name.to_owned(), format!("{volume}/{name}")));
                }
            }
        }

        seen.then_some(paths)
    }

    /// Der Pfad in der Shell; bleibt, wie er ist, wo keine Freigabe passt.
    pub(crate) fn shell_path(&self, sftp: &str) -> String {
        let rest = sftp.strip_prefix('/').unwrap_or(sftp);
        let (first, below) = rest.split_once('/').map_or((rest, ""), |(first, below)| (first, below));
        let base = if first == "home" {
            self.home.as_deref()
        } else {
            self.shares
                .iter()
                .find(|(name, _)| name == first)
                .map(|(_, path)| path.as_str())
        };
        match base {
            Some(base) if below.is_empty() => base.to_owned(),
            Some(base) => format!("{base}/{below}"),
            None => sftp.to_owned(),
        }
    }
}

/// So viele Einträge kopiert oder verschiebt eine Anfrage höchstens.
pub(crate) const MAX_COPIES: usize = 200;

/// Kopieren (`cp`) oder Verschieben (`mv`) auf dem Server selbst, je Paar von Quelle und Ziel – beides volle
/// Pfade. `cp -RPp` ist POSIX: samt Unterordnern, Verweise bleiben Verweise, Rechte und Zeiten bleiben. Beim ersten
/// Fehler hört die Reihe auf. Ob am Ziel schon etwas liegt, fragt die Oberfläche vorher – wie bei Transfers.
pub(crate) fn copy_command(pairs: &[(String, String)], moving: bool) -> Result<String, Failure> {
    if pairs.is_empty() || pairs.len() > MAX_COPIES {
        return Err(Failure {
            code: "bad_request",
            message: format!("between 1 and {MAX_COPIES} entries at once"),
        });
    }

    let tool = if moving { "mv" } else { "cp -RPp" };
    let mut steps = Vec::with_capacity(pairs.len());
    for (from, to) in pairs {
        // In sich selbst hinein hörte ein Kopieren nie auf, ein Verschieben scheiterte unverständlich.
        let inside = to
            .strip_prefix(from.as_str())
            .is_some_and(|rest| rest.is_empty() || rest.starts_with('/'));
        if !from.starts_with('/') || !to.starts_with('/') || from == "/" || inside {
            return Err(unsafe_argument(to));
        }

        steps.push(format!("{tool} {} {}", argument(from)?, argument(to)?));
    }

    Ok(steps.join(" && "))
}

/// So viele Zeilen vom Ende der Datei zeigt das Mitlesen zu Beginn.
pub(crate) const FOLLOW_LINES: u32 = 200;

/// So viel Text geht höchstens in ein Ereignis; eine Datei, die schneller wächst, kommt in mehreren.
const FOLLOW_CHUNK: usize = 64 * 1024;

/// `tail` für eine Datei, das ihr folgt – so, dass es mit dem Kanal endet. Ohne Terminal bekäme `tail` vom Ende des
/// Kanals nichts mit und bliebe liegen, bis die Datei das nächste Mal wächst (bei einem stillen Protokoll: nie).
/// Deshalb liest daneben ein `cat` die Eingabe: Endet sie, weil der Kern den Kanal schließt, beendet es `tail`. Die
/// Eingabe bekommt der Hintergrundprozess über eine eigene Nummer (`exec 3<&0`, dann `<&3`): Seine gewöhnliche
/// Eingabe ist `/dev/null`, und ein `<&0` ändert daran nicht überall etwas – die Shell von FreeBSD setzt
/// `/dev/null` zuerst und verdoppelt es dann nur (gemessen an 14.5; `tail` wurde so sofort wieder beendet).
///
/// Gefolgt wird dem **Namen**, nicht der einmal geöffneten Datei: Nach einem `logrotate` (`app.log` wird
/// `app.log.1`, eine neue `app.log` entsteht) käme sonst nichts mehr, ohne dass es auffiele. Dafür steht `-F`,
/// wo `tail` es kennt (GNU, BusyBox, FreeBSD, macOS). Das `tail` von OpenBSD kennt `-F` nicht und folgt mit `-f`
/// schon dem Namen (gemessen an 7.9); ob `-F` geht, sagt deshalb ein Versuch an `/dev/null` – läuft er nach
/// einer Sekunde noch, kennt `tail` den Schalter. Mit `-F` wartet GNU auf eine Datei, die es nicht gibt, statt zu
/// scheitern: Ob sie sich lesen lässt, wird deshalb vorher gefragt.
pub(crate) fn follow_command(path: &str) -> Result<String, Failure> {
    if !path.starts_with('/') {
        return Err(unsafe_argument(path));
    }

    let path = argument(path)?;
    Ok(format!(
        "exec 3<&0; [ -r {path} ] || {{ echo cannot read the file >&2; exit 1; }}; \
         tail -F -n 0 /dev/null >/dev/null 2>&1 & p=$!; sleep 1; \
         if kill $p 2>/dev/null; then f=-F; else f=-f; fi; wait $p 2>/dev/null; \
         tail -n {FOLLOW_LINES} $f {path} & t=$!; ( cat <&3; kill $t ) >/dev/null 2>&1 & wait $t"
    ))
}

/// Führt `command` aus und reicht seine Ausgabe als Text weiter, bis er endet oder `stop` kommt. Ein
/// UTF-8-Zeichen, das über zwei Blöcke verteilt ankommt, wird zusammengehalten; was kein UTF-8 ist, erscheint als
/// Ersatzzeichen. Die Fehlerausgabe ist die Erklärung, wenn der Befehl scheitert.
pub(crate) async fn follow<H: Handler>(
    handle: &Handle<H>,
    command: &str,
    mut stop: tokio::sync::oneshot::Receiver<()>,
    mut emit: impl FnMut(String),
) -> Result<(), Failure> {
    let failed = |message: String| Failure {
        code: "failed",
        message,
    };
    let mut channel = handle
        .channel_open_session()
        .await
        .map_err(|error| failed(error.to_string()))?;
    channel
        .exec(true, command)
        .await
        .map_err(|error| failed(error.to_string()))?;

    let (mut pending, mut stderr, mut status) = (Vec::new(), Vec::new(), None);
    loop {
        tokio::select! {
            _ = &mut stop => break,
            message = channel.wait() => match message {
                Some(ChannelMsg::Data { data }) => {
                    pending.extend_from_slice(&data);
                    while let Some(text) = take_text(&mut pending, FOLLOW_CHUNK) {
                        emit(text);
                    }
                }
                Some(ChannelMsg::ExtendedData { data, .. }) => append(&mut stderr, &data, 4096),
                Some(ChannelMsg::ExitStatus { exit_status }) => {
                    status = Some(exit_status);
                    break;
                }
                Some(ChannelMsg::Failure) => {
                    return Err(Failure {
                        code: "no_exec",
                        message: "the server does not run commands for this account".to_owned(),
                    });
                }
                Some(ChannelMsg::Close) | None => break,
                Some(_) => {}
            },
        }
    }

    // Erst das Ende der Eingabe (daran hängt das `cat` neben `tail`), dann der Kanal selbst.
    let _ = channel.eof().await;
    let _ = channel.close().await;
    match status {
        Some(status) if status != 0 => Err(Failure {
            code: "command_failed",
            message: match String::from_utf8_lossy(&stderr).trim() {
                "" => format!("exit status {status}"),
                text => text.to_owned(),
            },
        }),
        _ => Ok(()),
    }
}

/// Nimmt aus `pending` den Text heraus, der schon vollständig ist: alles bis auf ein am Ende angeschnittenes
/// UTF-8-Zeichen, höchstens `limit` Bytes. `None`, wenn noch nichts Ganzes da ist.
fn take_text(pending: &mut Vec<u8>, limit: usize) -> Option<String> {
    let mut end = pending.len().min(limit);

    // Höchstens drei Bytes zurück: Dort beginnt ein Zeichen, das noch nicht ganz da ist – oder keines.
    for back in 1..=3.min(end) {
        let byte = pending[end - back];
        if byte & 0b1100_0000 == 0b1000_0000 {
            continue;
        }

        let needed = match byte {
            byte if byte >= 0b1111_0000 => 4,
            byte if byte >= 0b1110_0000 => 3,
            byte if byte >= 0b1100_0000 => 2,
            _ => 1,
        };
        if needed > back {
            end -= back;
        }

        break;
    }

    if end == 0 {
        return None;
    }

    let text = String::from_utf8_lossy(&pending[..end]).into_owned();
    pending.drain(..end);
    Some(text)
}

/// Der Anfang einer Mosh-Sitzung; [`crate::mosh::start_line`] hängt den Aufruf an. `mosh-server` kommt aus dem
/// Suchpfad, sonst aus einer festen Liste von Ordnern – nie ein Pfad aus dem Profil oder vom Server (ein leerer
/// `PATH` bekommt kein `:` vorn, das stünde für das Verzeichnis, in dem man gerade ist). Fehlt er, sagt das die
/// eine Zeile `@nexus-no-mosh`; sonst steht `@nexus-mosh` vor seiner Ausgabe – was davor kommt (eine `~/.bashrc`,
/// die etwas ausgibt), liest der Kern gar nicht erst.
pub(crate) const MOSH_START: &str = concat!(
    "PATH=\"${PATH:+$PATH:}/usr/local/bin:/opt/homebrew/bin:/usr/bin\"; ",
    "command -v mosh-server >/dev/null 2>&1 || { echo @nexus-no-mosh; exit 127; }; ",
    "echo @nexus-mosh; exec "
);

/// Fragt nach den Namen der Programme im Suchpfad des Kontos – für das Ergänzen beim Tippen im Terminal. Fester
/// Text, nichts vom Benutzer oder vom Server darin: jedes Verzeichnis aus `PATH` auflisten (ohne Muster zu
/// erweitern, `set -f`), sortiert, jedes einmal, mit Obergrenze. Läuft unter jeder POSIX-Shell (GNU, BusyBox, BSD).
/// Nur Einträge, die mit `/` beginnen: Ein leerer oder relativer meint das Verzeichnis, in dem man gerade steht –
/// dessen Dateien sind keine Programme –, und einer mit `-` vorn wäre für `ls` ein Schalter (dazu das `--`).
pub(crate) const COMMAND_NAMES: &str = concat!(
    "set -f; IFS=:; for d in $PATH; do case $d in /*) [ -d \"$d\" ] && ls -1 -- \"$d\" 2>/dev/null;; esac; done ",
    "| LC_ALL=C sort -u | head -n 6000; true"
);

/// So viele Namen von Programmen gehen höchstens an die App.
pub(crate) const MAX_COMMAND_NAMES: usize = 4000;

/// Liest die Zeilen von [`COMMAND_NAMES`]. Die Namen kommen vom Server und werden später im Terminal vorgeschlagen
/// und getippt: Es gilt nur, was ein schlichter Programmname ist – Buchstaben, Ziffern, `.`, `_`, `+`, `-`, vorn
/// ein Buchstabe oder eine Ziffer, höchstens 64 Zeichen. Alles andere fällt weg.
pub(crate) fn parse_command_names(output: &str) -> Vec<String> {
    let mut names: Vec<String> = Vec::new();
    for line in output.lines() {
        let plain = (1..=64).contains(&line.len())
            && line.starts_with(|c: char| c.is_ascii_alphanumeric())
            && line
                .chars()
                .all(|c| c.is_ascii_alphanumeric() || matches!(c, '.' | '_' | '+' | '-'));
        if plain && names.last().is_none_or(|last| last != line) {
            names.push(line.to_owned());
            if names.len() == MAX_COMMAND_NAMES {
                break;
            }
        }
    }

    names
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn paths_of_the_sftp_server_of_dsm_become_paths_of_its_shell() {
        let output = "@home /volume1/homes/nxadmin\n@share /volume1/homes/\n@share /volume1/@appstore/\n\
                      @share /volume2/video/\n@share /volume1/#recycle/\n@share /volume2/homes/\n@share /mnt/x/\n";
        let paths = SharePaths::parse(output).unwrap();
        assert_eq!(paths.shell_path("/homes/nxadmin/a b"), "/volume1/homes/nxadmin/a b");
        assert_eq!(paths.shell_path("/home"), "/volume1/homes/nxadmin");
        assert_eq!(paths.shell_path("/home/it's"), "/volume1/homes/nxadmin/it's");
        assert_eq!(paths.shell_path("/video"), "/volume2/video");
        // Was keine Freigabe ist, bleibt, wie es ist.
        assert_eq!(paths.shell_path("/@appstore/x"), "/@appstore/x");
        assert_eq!(paths.shell_path("/x/y"), "/x/y");
        assert_eq!(paths.shell_path("/"), "/");

        // Kein DSM: keine Umrechnung.
        assert_eq!(SharePaths::parse(""), None);
    }

    #[test]
    fn running_tmux_and_screen_sessions_are_listed_with_the_line_that_attaches_to_them() {
        let output = "T 3 1 work\nT 1 0 logs-2\nS There are screens on:\nS \t1234.pts-0.web01\t(Detached)\n\
                      S \t777.build\t(01/02/2026 10:00:00 AM)\t(Attached)\nS 2 Sockets in /run/screen/S-me.\n";

        let sessions = parse_mux(output);

        let rows: Vec<_> = sessions
            .iter()
            .map(|session| {
                (
                    session.kind,
                    session.name.as_str(),
                    session.windows,
                    session.attached,
                    session.command.as_str(),
                )
            })
            .collect();
        assert_eq!(
            rows,
            [
                ("tmux", "work", Some(3), true, "tmux attach-session -t =work"),
                ("tmux", "logs-2", Some(1), false, "tmux attach-session -t =logs-2"),
                ("screen", "1234.pts-0.web01", None, false, "screen -x 1234.pts-0.web01"),
                ("screen", "777.build", None, true, "screen -x 777.build"),
            ]
        );
    }

    #[test]
    fn a_session_whose_name_could_be_read_as_a_command_is_left_out() {
        // Der Name wird in die Shell des Servers getippt – ein Server, der Sitzungen so nennt, bekommt keine Zeile.
        let output = "T 1 0 a; rm -rf ~\nT 1 0 $(id)\nT 1 0 two words\nT 1 0 -t\nT 1 0 `x`\nT x 0 broken\nT 1 0 \n\
                      S \t12.a|b\t(Detached)\nS \tnot-a-number.x\t(Detached)\nS \t99.ok\t(Detached)\nrubbish\n";

        let sessions = parse_mux(output);

        assert_eq!(sessions.len(), 1);
        assert_eq!(sessions[0].command, "screen -x 99.ok");
    }

    #[test]
    fn the_list_of_sessions_is_bounded() {
        let output: String = (0..500).map(|number| format!("T 1 0 s{number}\n")).collect();
        assert_eq!(parse_mux(&output).len(), 50);
    }

    #[test]
    fn the_probe_uses_the_very_quoting_the_commands_rely_on() {
        // Genau so geprüft an bash, dash, BusyBox und fish (antworten „nexus-posix“) und an cmd.exe (führt das
        // angehängte Stück als zweiten Befehl aus) – die Zeile darf sich nicht unbemerkt ändern.
        assert_eq!(posix_probe(), r"sh -c 'echo nexus-'\''posix'\''' '&echo nexus-other'");
    }

    #[test]
    fn arguments_are_quoted_as_one_word_and_control_characters_are_refused() {
        assert_eq!(quote("/var/www").as_deref(), Some("'/var/www'"));
        assert_eq!(quote("it's; rm -rf $HOME").as_deref(), Some(r"'it'\''s; rm -rf $HOME'"));
        assert_eq!(quote("a\nb"), None);
        assert_eq!(quote(""), None);
    }

    #[test]
    fn packing_stays_inside_the_folder_and_never_overwrites_an_archive() {
        let command = pack_command("/var/my site", &["html".to_owned(), "-rf".to_owned()], "site.tar.gz").unwrap();

        assert_eq!(
            command,
            "cd '/var/my site' && if test -e './site.tar.gz'; then echo 'the archive already exists' >&2; exit 17; fi \
             && tar -czf './site.tar.gz' -- './html' './-rf'"
        );

        // Namen, die aus dem Verzeichnis führen oder Steuerzeichen tragen, werden nicht ausgeführt.
        for bad in ["../etc", "a/b", ".", "..", "a\nb"] {
            assert_eq!(
                pack_command("/srv", &[bad.to_owned()], "x.tar.gz").unwrap_err().code,
                "unsafe_name",
                "{bad:?}"
            );
        }
        assert_eq!(
            pack_command("/srv", &["a".to_owned()], "../x.tar.gz").unwrap_err().code,
            "unsafe_name"
        );
        assert_eq!(pack_command("/srv", &[], "x.tar.gz").unwrap_err().code, "failed");
    }

    #[test]
    fn unpacking_picks_the_tool_by_the_name_and_keeps_existing_files() {
        assert_eq!(
            unpack_command("/srv", "site.tar.gz").unwrap(),
            "cd '/srv' && if test \"$(uname -s)\" = OpenBSD; then if gzip -dcf < './site.tar.gz' | pax | grep -qE '(^/|(^|/)[.][.](/|$))'; then echo 'the archive holds paths that would leave the folder' >&2; exit 18; fi; gzip -dcf < './site.tar.gz' | pax -rk; elif tar -tf './site.tar.gz' >/dev/null 2>&1; then tar -xkf './site.tar.gz'; else gzip -t < './site.tar.gz' && gzip -dc < './site.tar.gz' | tar -xkf -; fi"
        );
        // ZIP mit `unzip`, sonst (DSM) mit `7z` – das nur nach einem Blick auf die Einträge.
        assert_eq!(
            unpack_command("/srv", "Site.ZIP").unwrap(),
            "cd '/srv' && if command -v unzip >/dev/null 2>&1; then unzip -n -q './Site.ZIP'; \
             elif command -v 7z >/dev/null 2>&1; then \
             if 7z l -slt -- './Site.ZIP' | sed -n '/^----------/,$p' | grep -qE '^(Path = ([/\\]|(.*[/\\])?[.][.]([/\\]|$))|Attributes = .*[ ]l[rwx-]{9})'; then \
             echo 'the archive holds paths that would leave the folder' >&2; exit 18; fi; \
             7z x -aos -bd -- './Site.ZIP' >/dev/null; \
             else echo 'unzip: command not found' >&2; exit 127; fi"
        );
        assert_eq!(unpack_command("/srv", "a/b.tar").unwrap_err().code, "unsafe_name");
    }

    #[test]
    fn a_failed_command_explains_itself_with_its_error_output() {
        let failed = succeeded(Output {
            status: Some(2),
            stderr: "tar: site: Cannot open".to_owned(),
            ..Output::default()
        })
        .unwrap_err();
        assert_eq!(
            (failed.code, failed.message.as_str()),
            ("command_failed", "tar: site: Cannot open")
        );

        let silent = succeeded(Output {
            status: Some(1),
            ..Output::default()
        })
        .unwrap_err();
        assert_eq!(silent.message, "exit status 1");
        assert!(succeeded(Output::default()).is_ok());
    }

    #[test]
    fn a_command_is_handed_to_sh_whatever_the_login_shell() {
        assert_eq!(posix("echo hi"), "sh -c 'echo hi'");
        // Jedes Anführungszeichen des Befehls wird zu '\'' – auch die, mit denen er seine eigenen Namen schützt.
        assert_eq!(posix("cd '/srv' && { true; }"), r"sh -c 'cd '\''/srv'\'' && { true; }'");

        // Ein Rückstrich steht nie innerhalb der Anführungszeichen: fish läse `\'` dort als maskiertes Zeichen.
        assert_eq!(posix(r"printf '%s\n'"), r"sh -c 'printf '\''%s'\\'n'\'''");
        let name = quote("it's").unwrap();
        assert_eq!(name, r"'it'\''s'");
        assert_eq!(posix(&name), r"sh -c ''\''it'\'''\\''\'''\''s'\'''");

        // Ein `!` ebenso außerhalb: csh griffe sonst in seinen Verlauf.
        assert_eq!(posix("[ ! -e x ]"), r"sh -c '[ '\!' -e x ]'");
        // Die Schleife der Auslastung steht schon fertig in `sh -c '…'` und geht nicht durch `posix`.
        assert!(!crate::monitor::SCRIPT.contains('!'));
    }

    #[test]
    fn the_size_of_a_folder_is_asked_with_du_and_read_from_its_last_line() {
        assert_eq!(
            size_command("/srv/my data").unwrap(),
            "du -sk '/srv/my data' 2>/dev/null || true"
        );
        assert_eq!(size_command("relative").unwrap_err().code, "unsafe_name");
        assert_eq!(
            size_command(
                "/a
b"
            )
            .unwrap_err()
            .code,
            "unsafe_name"
        );

        assert_eq!(
            parse_size(
                "2048	/srv/my data
"
            ),
            Some(2048 * 1024)
        );
        assert_eq!(
            parse_size(
                "12 /a
  7340032   /srv
"
            ),
            Some(7_340_032 * 1024)
        );
        assert_eq!(parse_size(""), None);
        assert_eq!(
            parse_size(
                "du: cannot access
"
            ),
            None
        );
        assert_eq!(
            parse_size(
                "99999999999999999999 /x
"
            ),
            None
        );
    }

    #[test]
    fn the_usage_of_a_folder_lists_what_lies_directly_in_it() {
        assert_eq!(
            usage_command("/var/my site").unwrap(),
            "cd '/var/my site' && { du -x -k -s ./* ./.[!.]* ./..?* 2>/dev/null; du -x -k -s . 2>/dev/null; } || true"
        );
        assert!(usage_command("relative").is_err());

        // Ein Muster, das auf nichts passte („./..?*“), und Fehlermeldungen ergeben keine Zeile mit Zahl.
        let usage = parse_usage(
            "4\t./a.txt\n2048\t./big dir\ndu: ./..?*: No such file or directory\n2052\t.\n",
            "/srv",
        );
        assert_eq!(
            usage,
            [
                Usage {
                    path: "/srv/a.txt".into(),
                    bytes: 4096
                },
                Usage {
                    path: "/srv/big dir".into(),
                    bytes: 2048 * 1024
                },
                Usage {
                    path: "/srv".into(),
                    bytes: 2052 * 1024
                },
            ]
        );
    }

    #[test]
    fn checksums_come_back_one_per_file_whatever_tool_the_server_has() {
        let command = checksum_command(&["/srv/a b".to_owned(), "/srv/it's".to_owned()]).unwrap();
        assert!(command.contains("sha256sum '/srv/a b' 2>/dev/null || shasum -a 256 '/srv/a b'"));
        assert!(command.contains(r"sha256 -q '/srv/it'\''s'"));
        assert_eq!(command.matches("| head -n 1; ").count(), 2);
        assert!(checksum_command(&[]).is_err());
        assert!(checksum_command(&["relative".to_owned()]).is_err());
        assert!(checksum_command(&vec!["/x".to_owned(); MAX_CHECKSUMS + 1]).is_err());

        let sum = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
        // GNU nennt den Namen dahinter, BSD nur die Summe, openssl einen Stern vor dem Namen; `-` heißt „keine“.
        let output = format!("{sum}  /srv/a\n{}\n-\n{sum} */srv/d\nnot a sum\n", sum.to_uppercase());
        assert_eq!(
            parse_checksums(&output, 6),
            [
                Some(sum.to_owned()),
                Some(sum.to_owned()),
                None,
                Some(sum.to_owned()),
                None,
                None
            ]
        );
    }

    #[test]
    fn following_a_file_ends_with_its_channel_and_text_is_never_cut_inside_a_character() {
        assert_eq!(
            follow_command("/var/log/my app.log").unwrap(),
            "exec 3<&0; [ -r '/var/log/my app.log' ] || { echo cannot read the file >&2; exit 1; }; \
             tail -F -n 0 /dev/null >/dev/null 2>&1 & p=$!; sleep 1; \
             if kill $p 2>/dev/null; then f=-F; else f=-f; fi; wait $p 2>/dev/null; \
             tail -n 200 $f '/var/log/my app.log' & t=$!; ( cat <&3; kill $t ) >/dev/null 2>&1 & wait $t"
        );
        assert!(follow_command("app.log").is_err());

        // „ü“ sind zwei Bytes, „€“ drei: Was am Ende angeschnitten ist, wartet auf den Rest.
        let mut pending = "grün".as_bytes()[..3].to_vec();
        assert_eq!(take_text(&mut pending, 1024).as_deref(), Some("gr"));
        assert_eq!(take_text(&mut pending, 1024), None);
        pending.extend_from_slice(&"grün €".as_bytes()[3..7]);
        assert_eq!(take_text(&mut pending, 1024).as_deref(), Some("ün "));
        pending.extend_from_slice(&"€".as_bytes()[1..]);
        assert_eq!(take_text(&mut pending, 1024).as_deref(), Some("€"));
        assert!(pending.is_empty());

        // Mehr als in ein Ereignis passt, kommt in Stücken – auch dann nicht mitten im Zeichen.
        let mut long = "aaü".as_bytes().to_vec();
        assert_eq!(take_text(&mut long, 3).as_deref(), Some("aa"));
        assert_eq!(take_text(&mut long, 3).as_deref(), Some("ü"));
    }

    #[test]
    fn copying_and_moving_take_full_paths_and_refuse_to_go_into_themselves() {
        let pairs = [
            ("/srv/a b".to_owned(), "/tmp/a b".to_owned()),
            ("/srv/-x".to_owned(), "/tmp/y".to_owned()),
        ];
        assert_eq!(
            copy_command(&pairs, false).unwrap(),
            "cp -RPp '/srv/a b' '/tmp/a b' && cp -RPp '/srv/-x' '/tmp/y'"
        );
        assert_eq!(copy_command(&pairs[..1], true).unwrap(), "mv '/srv/a b' '/tmp/a b'");

        let refused = |from: &str, to: &str| copy_command(&[(from.to_owned(), to.to_owned())], false).is_err();
        assert!(refused("/srv/a", "/srv/a"));
        assert!(refused("/srv/a", "/srv/a/b"));
        assert!(refused("/", "/tmp/x"));
        assert!(refused("srv/a", "/tmp/x"));
        assert!(refused("/srv/a", "tmp/x"));
        assert!(refused("/srv/a\nb", "/tmp/x"));
        // Ein Name, der nur so anfängt wie die Quelle, liegt nicht in ihr.
        assert!(!refused("/srv/a", "/srv/ab"));
        assert!(copy_command(&[], false).is_err());
    }

    #[test]
    fn only_plain_program_names_are_read_from_the_search_path() {
        let output = "[\na2enmod\nawk\nawk\nbash\nc++\ngit-upload-pack\npython3.12\nx86_64-linux-gnu-gcc\n\
                      -rf\n.hidden\nmit leer\nb\u{f6}se\nrm;reboot\n$(id)\nname\twith-tab\n\n";
        assert_eq!(
            parse_command_names(output),
            [
                "a2enmod",
                "awk",
                "bash",
                "c++",
                "git-upload-pack",
                "python3.12",
                "x86_64-linux-gnu-gcc"
            ]
        );
        assert!(parse_command_names(&"x".repeat(65)).is_empty());

        // Die Liste hat ein Ende.
        let many: String = (0..MAX_COMMAND_NAMES + 50)
            .map(|index| format!("p{index:05}\n"))
            .collect();
        assert_eq!(parse_command_names(&many).len(), MAX_COMMAND_NAMES);

        // Der Befehl ist ein fester Text und übersteht die Hülle für `sh -c`.
        assert!(COMMAND_NAMES.starts_with("set -f; IFS=:;"));
        assert!(posix(COMMAND_NAMES).starts_with("sh -c '"));
    }
}
