//! Die Eingänge von außen, einzeln aufrufbar – für Zufallstests.
//!
//! Jede Funktion nimmt beliebige Bytes und reicht sie an einen Leser des Kerns, der sonst Text eines Servers oder
//! eine Datei bekommt. Erwartet wird nur, dass er damit fertig wird: kein Panic, kein endloses Rechnen. Benutzt
//! werden sie von `tests/fuzz.rs` (läuft bei jedem `cargo test` mit) und von den Zielen unter `backend/fuzz`
//! (`cargo fuzz`, nur unter Linux). Die App ruft nichts davon auf.

use crate::{exec, ftp, keyfiles, metered, monitor, shellfs, sshconfig, telnet, tunnels, x11};

/// Die Namen aller Eingänge, wie [`run`] sie kennt.
pub const TARGETS: [&str; 11] = [
    "sshconfig",
    "telnet",
    "monitor",
    "ls",
    "ftp_list",
    "exec",
    "x11",
    "socks",
    "keyfile",
    "sftp_stream",
    "mosh_bootstrap",
];

/// Reicht `data` an den Eingang mit diesem Namen; ein unbekannter Name tut nichts.
pub fn run(target: &str, data: &[u8]) {
    match target {
        "sshconfig" => ssh_config(data),
        "telnet" => telnet_negotiation(data),
        "monitor" => monitor_output(data),
        "ls" => ls_output(data),
        "ftp_list" => ftp_listing(data),
        "exec" => command_output(data),
        "x11" => x11_setup(data),
        "socks" => socks_request(data),
        "keyfile" => key_file(data),
        "sftp_stream" => sftp_stream(data),
        "mosh_bootstrap" => mosh_bootstrap(data),
        _ => {}
    }
}

/// Der Strom unter einer SFTP-Sitzung ([`metered`]): zählt Einträge und ersetzt unbekannte Statuscodes. Das erste
/// Byte bestimmt die Größe der Stücke; was die Sitzung sieht, darf nicht davon abhängen, wo das Netz schneidet.
pub fn sftp_stream(data: &[u8]) {
    let Some((&step, stream)) = data.split_first() else {
        return;
    };
    let whole = metered::exercise(stream, stream.len().max(1));
    let pieces = metered::exercise(stream, usize::from(step).max(1));
    assert_eq!(whole, pieces, "the stream reads differently in pieces of {step}");
    assert_eq!(whole.1.len(), stream.len());
}

/// `~/.ssh/config`: Zeilen zerlegen, Blöcke bilden, jeden Server auswerten – ohne `Include`, also ohne Platte.
pub fn ssh_config(data: &[u8]) {
    sshconfig::exercise(&String::from_utf8_lossy(data));
}

/// Die Aushandlung von Telnet. Das erste Byte bestimmt, in welchen Stücken der Rest ankommt: Was der Server
/// schickt, darf nicht davon abhängen, wo das Netz es zerschneidet.
pub fn telnet_negotiation(data: &[u8]) {
    let Some((&step, input)) = data.split_first() else {
        return;
    };

    let whole = telnet_in_pieces(input, input.len().max(1));
    let pieces = telnet_in_pieces(input, usize::from(step % 7) + 1);
    assert_eq!(whole, pieces, "telnet: die Stückelung ändert das Ergebnis");

    // Text für das Terminal ist nie mehr, als ankam; die Antworten bleiben im Verhältnis zur Eingabe.
    assert!(whole.0.len() <= input.len(), "telnet: mehr Ausgabe als Eingabe");
    assert!(whole.1.len() <= input.len() * 16 + 64, "telnet: Antworten ohne Maß");
}

fn telnet_in_pieces(input: &[u8], size: usize) -> (Vec<u8>, Vec<u8>) {
    let mut telnet = telnet::Telnet::new((80, 24));
    let (mut output, mut reply) = (Vec::new(), Vec::new());
    for piece in input.chunks(size) {
        telnet.receive(piece, &mut output, &mut reply);
    }

    telnet.resize((132, 43), &mut reply);
    let _ = telnet.encode(input);
    (output, reply)
}

/// Die Ausgabe der Schleife für die Auslastung, Zeile für Zeile.
pub fn monitor_output(data: &[u8]) {
    let mut parser = monitor::Parser::new();
    for line in String::from_utf8_lossy(data).lines() {
        let _ = parser.line(line);
    }
}

/// Zeilen von `ls -ln` (Dateibrowser ohne SFTP).
pub fn ls_output(data: &[u8]) {
    for line in String::from_utf8_lossy(data).lines() {
        let _ = shellfs::parse_line(line, 1_800_000_000);
    }
}

/// Eine Liste eines FTP-Servers, als `MLSD` und als `LIST` gelesen.
pub fn ftp_listing(data: &[u8]) {
    let lines: Vec<String> = String::from_utf8_lossy(data).lines().map(str::to_owned).collect();
    let _ = ftp::parse_listing(&lines, true);
    let _ = ftp::parse_listing(&lines, false);
}

/// Was der Start von `mosh-server` ausgibt (`mosh::connect_record`): Gelingt das Lesen, geht Mosh zu der Adresse,
/// die der Kern nannte – nie zu einer aus der Ausgabe –, und keine Beschreibung des Ergebnisses nennt den Schlüssel.
pub fn mosh_bootstrap(data: &[u8]) {
    let server = std::net::IpAddr::from([192, 0, 2, 7]);
    match crate::mosh::connect_record(server, data) {
        Ok(bootstrap) => {
            assert_eq!(bootstrap.server_addr().ip(), server);
            let described = format!("{bootstrap:?}");
            for word in String::from_utf8_lossy(data)
                .split_whitespace()
                .filter(|word| word.len() == 22)
            {
                assert!(!described.contains(word), "the key is in {described}");
            }
        }
        Err(start) => {
            let _ = start.to_string();
        }
    }
}

/// Was Befehle auf dem Server antworten: Größen, Belegung, Prüfsummen, Protokolle.
pub fn command_output(data: &[u8]) {
    let text = String::from_utf8_lossy(data);
    let _ = exec::parse_size(&text);
    let _ = exec::parse_usage(&text, "/srv/data/");
    let _ = exec::parse_checksums(&text, 16);
    let _ = crate::logs::parse_journal(&text);
    let _ = crate::logs::parse_file(&text);

    // Was hier durchkommt, wird später in eine Shell getippt: nie etwas außer dem schlichten Namen.
    for session in exec::parse_mux(&text) {
        assert!(
            session
                .command
                .bytes()
                .all(|byte| byte.is_ascii_alphanumeric() || b" .=_-".contains(&byte)),
            "mux: {}",
            session.command
        );
    }
}

/// Der Anfang einer X11-Verbindung, den der Server weiterreicht.
pub fn x11_setup(data: &[u8]) {
    // Ein festes Geheimnis: So kann eine Eingabe es auch treffen, und derselbe Lauf tut zweimal dasselbe.
    let _ = x11::setup(data, b"0123456789abcdef");
    // Und die Xauthority-Datei des Benutzers, aus der das Geheimnis des lokalen X-Servers kommt.
    let _ = x11::cookie_in(data, 0);
}

/// Was ein Programm dem SOCKS-Proxy eines dynamischen Tunnels schickt: beide Nachrichten, ganz und an jeder
/// Stelle abgeschnitten. Ein Ziel, das dabei herauskommt, ist ein Name ohne Leerraum mit einem Port – und wer
/// schon „mehr“ gesagt hat, darf sich mit weniger Bytes nicht anders entschieden haben.
pub fn socks_request(data: &[u8]) {
    use tunnels::socks::{Step, hello, request};
    for read in [hello as fn(&[u8]) -> Step, request] {
        let whole = read(data);
        if let Step::Connect(wanted) = &whole {
            assert!(
                !wanted.host.is_empty() && wanted.host.len() <= 255 && wanted.port != 0,
                "socks: unbrauchbares Ziel {wanted:?}"
            );
            assert!(
                wanted.host.bytes().all(|byte| byte.is_ascii_graphic()),
                "socks: Leerraum oder Steuerzeichen im Namen {wanted:?}"
            );
        }

        for cut in 0..data.len().min(64) {
            let part = read(&data[..cut]);
            assert!(
                part == Step::More || part == whole,
                "socks: mit {cut} Bytes schon {part:?}, mit allen {whole:?}"
            );
        }
    }
}

/// Eine Schlüsseldatei, wie die Übersicht der Schlüssel sie ansieht: Format erkennen, Kopf lesen, ohne
/// Passphrase entsperren – auch mit dem eigenen Leser für PuTTYs Format.
pub fn key_file(data: &[u8]) {
    keyfiles::exercise(&String::from_utf8_lossy(data));
}

/// Echte Eingaben für [`key_file`]: ein frisch erzeugter Schlüssel in beiden Formaten. Erzeugt statt eingecheckt –
/// ein privater Schlüssel hat im Repository nichts verloren.
pub fn key_file_samples() -> Vec<Vec<u8>> {
    keyfiles::samples()
}
