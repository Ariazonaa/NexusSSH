//! Ein kleiner FTP-Server über einem echten Verzeichnis – Gegenstelle für die Tests des FTP-Teils im Kern.
//!
//! Genug für das, was der Kern spricht: Anmeldung, passive Datenverbindungen, `MLSD`/`LIST`, `RETR`/`STOR` samt
//! `REST`, Anlegen, Umbenennen, Löschen – und `AUTH TLS` mit einem Zertifikat, das der Test mitbringt. Kein
//! vollständiger FTP-Server; Pfade können die Wurzel nicht verlassen.

use std::net::SocketAddr;
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::UNIX_EPOCH;

use tokio::io::{AsyncRead, AsyncReadExt, AsyncWrite, AsyncWriteExt};
use tokio::net::{TcpListener, TcpStream};
use tokio_rustls::TlsAcceptor;
use tokio_rustls::rustls;

/// So lange versucht der Server mit `blocked_active` vergeblich, den Client zu erreichen.
const BLOCKED_FOR: std::time::Duration = std::time::Duration::from_secs(20);

/// Wie der FTP-Server sich verhält.
#[derive(Clone)]
pub struct FtpOptions {
    /// Der Port auf 127.0.0.1; 0 = ein freier.
    pub port: u16,
    /// Benutzer und Passwort.
    pub login: (String, String),
    /// Verzeichnis, das als `/` erscheint.
    pub root: PathBuf,
    /// Zertifikat und Schlüssel (DER) für `AUTH TLS`; `None`: Der Server kann kein TLS.
    pub tls: Option<(Vec<u8>, Vec<u8>)>,
    /// Die Liste in der gewöhnlichen Schreibweise (`LIST`) statt `MLSD` – wie ein älterer Server.
    pub plain_list: bool,
    /// Nur der aktive Modus: `PASV` und `EPSV` werden abgelehnt – so zeigt ein Test, dass der Client wirklich
    /// den Server zu sich kommen lässt.
    pub active_only: bool,
    /// Lehnt `PORT` und `EPRT` ab, wie ein Server, dem der Client eine Adresse nennt, unter der er ihn nicht sieht
    /// (vsftpd hinter einem VPN oder NAT: „500 Illegal EPRT command.“).
    pub refuse_active: bool,
    /// Nimmt `PORT` und `EPRT` an, kommt aber nie beim Client an – wie hinter einer Firewall vor ihm, die den
    /// Aufbau still verwirft: Der Server versucht es eine Weile und meldet dann „425“, ohne vorher „150“ zu sagen.
    pub blocked_active: bool,
    /// Dateinamen gehen als ISO-8859-1 über die Leitung statt als UTF-8 – wie bei einem alten Server. Auf der
    /// Platte heißen die Dateien, wie sie heißen; nur Befehle, Antworten und Listen sind so kodiert.
    pub latin1: bool,
    /// Die Stelle von `REST` gilt nur für den Befehl gleich danach – kommt erst noch `PASV`, ist sie vergessen,
    /// und die Übertragung beginnt am Anfang der Datei. So hält es FileZilla Server (und so steht es in RFC 3659).
    pub forgets_restart: bool,
    /// Auf `::1` statt 127.0.0.1, und `PASV` wird abgelehnt – über IPv6 gilt nur `EPSV` (RFC 2428), so hält es IIS.
    pub ipv6: bool,
}

trait Stream: AsyncRead + AsyncWrite + Unpin + Send {}
impl<T: AsyncRead + AsyncWrite + Unpin + Send> Stream for T {}

/// Startet den Server auf 127.0.0.1 (mit `ipv6` auf `::1`) und nennt seine Adresse.
pub async fn start_ftp(options: FtpOptions) -> std::io::Result<SocketAddr> {
    let listener = TcpListener::bind((loopback(&options), options.port)).await?;
    let address = listener.local_addr()?;
    let acceptor = match &options.tls {
        Some((certificate, key)) => {
            let provider = Arc::new(rustls::crypto::ring::default_provider());
            let config = rustls::ServerConfig::builder_with_provider(provider)
                .with_safe_default_protocol_versions()
                .map_err(std::io::Error::other)?
                .with_no_client_auth()
                .with_single_cert(
                    vec![rustls::pki_types::CertificateDer::from(certificate.clone())],
                    rustls::pki_types::PrivateKeyDer::try_from(key.clone()).map_err(std::io::Error::other)?,
                )
                .map_err(std::io::Error::other)?;
            Some(TlsAcceptor::from(Arc::new(config)))
        }
        None => None,
    };

    tokio::spawn(async move {
        while let Ok((stream, _)) = listener.accept().await {
            tokio::spawn(serve(stream, options.clone(), acceptor.clone()));
        }
    });
    Ok(address)
}

fn loopback(options: &FtpOptions) -> &'static str {
    if options.ipv6 { "::1" } else { "127.0.0.1" }
}

fn local(root: &Path, current: &str, path: &str) -> PathBuf {
    let joined = if path.starts_with('/') {
        path.to_owned()
    } else {
        format!("{current}/{path}")
    };
    let mut parts: Vec<&str> = Vec::new();
    for part in joined.split('/') {
        match part {
            "" | "." => {}
            ".." => {
                parts.pop();
            }
            part => parts.push(part),
        }
    }

    let mut local = root.to_owned();
    local.extend(parts);
    local
}

async fn line(stream: &mut Box<dyn Stream>, latin1: bool) -> Option<String> {
    // Byte für Byte: Nach `AUTH TLS` darf nichts vom Handschlag schon gelesen sein.
    let mut text = Vec::new();
    let mut byte = [0u8; 1];
    loop {
        match stream.read(&mut byte).await {
            Ok(1) if byte[0] == b'\n' => break,
            Ok(1) => text.push(byte[0]),
            _ => return None,
        }
    }

    let text: String = if latin1 {
        text.iter().map(|byte| char::from(*byte)).collect()
    } else {
        String::from_utf8_lossy(&text).into_owned()
    };
    Some(text.trim_end_matches('\r').to_owned())
}

/// Text, wie er über die Leitung geht: UTF-8, oder ISO-8859-1 (was es dort nicht gibt, wird ein Fragezeichen).
fn wire(text: &str, latin1: bool) -> Vec<u8> {
    if latin1 {
        text.chars()
            .map(|character| u8::try_from(u32::from(character)).unwrap_or(b'?'))
            .collect()
    } else {
        text.as_bytes().to_vec()
    }
}

fn stamp(metadata: &std::fs::Metadata) -> String {
    let seconds = metadata
        .modified()
        .ok()
        .and_then(|time| time.duration_since(UNIX_EPOCH).ok())
        .map_or(0, |since| since.as_secs());
    let (days, rest) = (seconds / 86_400, seconds % 86_400);

    // Tage seit 1970 als Datum (Algorithmus von Howard Hinnant).
    let z = days as i64 + 719_468;
    let era = z.div_euclid(146_097);
    let doe = z.rem_euclid(146_097);
    let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let (day, month) = (doy - (153 * mp + 2) / 5 + 1, if mp < 10 { mp + 3 } else { mp - 9 });
    let year = yoe + era * 400 + i64::from(month <= 2);
    format!(
        "{year:04}{month:02}{day:02}{:02}{:02}{:02}",
        rest / 3600,
        rest % 3600 / 60,
        rest % 60
    )
}

fn listing(directory: &Path, machine: bool) -> std::io::Result<String> {
    let mut text = String::new();
    for entry in std::fs::read_dir(directory)? {
        let entry = entry?;
        let metadata = entry.metadata()?;
        let name = entry.file_name().to_string_lossy().into_owned();
        if machine {
            let kind = if metadata.is_dir() { "dir" } else { "file" };
            text.push_str(&format!(
                "type={kind};size={};modify={}; {name}\r\n",
                metadata.len(),
                stamp(&metadata)
            ));
        } else if metadata.file_type().is_symlink() {
            // Wie `ls -l`: Ein Link nennt sein Ziel hinter dem Namen. (`MLSD` oben kennt das nicht.)
            let target = std::fs::read_link(entry.path())?;
            text.push_str(&format!(
                "lrwxrwxrwx 1 1000 1000 {} Jan  5  2021 {name} -> {}\r\n",
                metadata.len(),
                target.to_string_lossy()
            ));
        } else {
            let kind = if metadata.is_dir() { "drwxr-xr-x" } else { "-rw-r--r--" };
            text.push_str(&format!(
                "{kind} 1 1000 1000 {} Jan  5  2021 {name}\r\n",
                metadata.len()
            ));
        }
    }

    Ok(text)
}

async fn serve(stream: TcpStream, options: FtpOptions, acceptor: Option<TlsAcceptor>) {
    let mut control: Box<dyn Stream> = Box::new(stream);
    let mut current = String::from("/");
    let (mut user, mut signed_in, mut protected) = (String::new(), false, false);
    let (mut passive, mut restart, mut rename_from): (Option<TcpListener>, u64, Option<PathBuf>) = (None, 0, None);
    // Aktiver Modus: Dorthin baut der Server die nächste Datenverbindung auf.
    let mut active: Option<std::net::SocketAddr> = None;
    macro_rules! reply {
        ($($text:tt)*) => {
            if control.write_all(&wire(&format!("{}\r\n", format!($($text)*)), options.latin1)).await.is_err() {
                return;
            }
        };
    }

    reply!("220 nexus-testserver FTP");
    while let Some(command) = line(&mut control, options.latin1).await {
        let (verb, argument) = command.split_once(' ').unwrap_or((command.as_str(), ""));
        let verb = verb.to_ascii_uppercase();
        if options.forgets_restart && !matches!(verb.as_str(), "REST" | "RETR" | "STOR" | "APPE") {
            restart = 0;
        }

        // Vor der Anmeldung geht nur, was zu ihr führt.
        if !signed_in
            && !matches!(
                verb.as_str(),
                "USER" | "PASS" | "AUTH" | "PBSZ" | "PROT" | "FEAT" | "SYST" | "QUIT"
            )
        {
            reply!("530 Please login with USER and PASS.");
            continue;
        }

        match verb.as_str() {
            "USER" => {
                user = argument.to_owned();
                reply!("331 Password required");
            }
            "PASS" => {
                signed_in = (user.as_str(), argument) == (options.login.0.as_str(), options.login.1.as_str());
                if signed_in {
                    reply!("230 Logged in");
                } else {
                    reply!("530 Login incorrect.");
                }
            }
            "AUTH" => match &acceptor {
                Some(acceptor) => {
                    reply!("234 AUTH TLS successful");
                    match acceptor.accept(control).await {
                        Ok(secured) => control = Box::new(secured),
                        Err(_) => return,
                    }
                }
                None => reply!("502 TLS is not available"),
            },
            "PBSZ" => reply!("200 PBSZ=0"),
            "PROT" => {
                protected = argument.eq_ignore_ascii_case("P");
                reply!("200 Protection level set");
            }
            "SYST" => reply!("215 UNIX Type: L8"),
            "FEAT" => {
                reply!("211-Features\r\n MLSD\r\n REST STREAM\r\n SIZE\r\n MDTM\r\n AUTH TLS\r\n UTF8\r\n211 End")
            }
            "OPTS" | "TYPE" | "NOOP" | "MODE" | "STRU" => reply!("200 OK"),
            "SITE" => reply!("200 SITE command ok"),
            "PWD" => reply!("257 \"{current}\" is the current directory"),
            "CWD" => {
                if local(&options.root, &current, argument).is_dir() {
                    current = if argument.starts_with('/') {
                        argument.to_owned()
                    } else {
                        format!("{}/{argument}", current.trim_end_matches('/'))
                    };
                    reply!("250 Directory changed");
                } else {
                    reply!("550 No such directory");
                }
            }
            "PASV" | "EPSV" if options.active_only => reply!("502 Passive mode is switched off"),
            "PASV" if options.ipv6 => reply!("501 Server cannot accept argument"),
            "PASV" | "EPSV" => match TcpListener::bind((loopback(&options), 0)).await {
                Ok(listener) => {
                    let port = listener.local_addr().map_or(0, |address| address.port());
                    passive = Some(listener);
                    if verb == "EPSV" {
                        reply!("229 Entering Extended Passive Mode (|||{port}|)");
                    } else {
                        // Mit Absicht eine fremde Adresse: Ein Client soll trotzdem beim Server bleiben.
                        reply!("227 Entering Passive Mode (10,255,255,1,{},{})", port >> 8, port & 0xff);
                    }
                }
                Err(_) => reply!("425 Cannot open a data connection"),
            },
            // `PORT h1,h2,h3,h4,p1,p2` und `EPRT |1|adresse|port|` (bzw. `|2|` für IPv6).
            "PORT" | "EPRT" if options.refuse_active => reply!("500 Illegal {verb} command."),
            "PORT" | "EPRT" => {
                let named = if verb == "PORT" {
                    let parts: Vec<u16> = argument
                        .split(',')
                        .filter_map(|part| part.trim().parse().ok())
                        .collect();
                    match parts.as_slice() {
                        [a, b, c, d, high, low] if parts.iter().all(|part| *part <= 255) => Some(
                            std::net::SocketAddr::from(([*a as u8, *b as u8, *c as u8, *d as u8], high << 8 | low)),
                        ),
                        _ => None,
                    }
                } else {
                    let parts: Vec<&str> = argument.split('|').collect();
                    match parts.as_slice() {
                        [_, _, address, port, _] => address
                            .parse::<std::net::IpAddr>()
                            .ok()
                            .zip(port.parse::<u16>().ok())
                            .map(std::net::SocketAddr::from),
                        _ => None,
                    }
                };
                match named {
                    Some(address) => {
                        (active, passive) = (Some(address), None);
                        reply!("200 {verb} command successful");
                    }
                    None => reply!("501 Cannot read the address"),
                }
            }
            "REST" => {
                restart = argument.parse().unwrap_or(0);
                reply!("350 Restarting at {restart}");
            }
            "SIZE" => match std::fs::metadata(local(&options.root, &current, argument)) {
                Ok(metadata) if metadata.is_file() => reply!("213 {}", metadata.len()),
                _ => reply!("550 No such file"),
            },
            "MDTM" => match std::fs::metadata(local(&options.root, &current, argument)) {
                Ok(metadata) => reply!("213 {}", stamp(&metadata)),
                Err(_) => reply!("550 No such file"),
            },
            "MKD" => match std::fs::create_dir(local(&options.root, &current, argument)) {
                Ok(()) => reply!("257 \"{argument}\" created"),
                Err(_) => reply!("550 Cannot create the directory"),
            },
            "RMD" => match std::fs::remove_dir(local(&options.root, &current, argument)) {
                Ok(()) => reply!("250 Removed"),
                Err(_) => reply!("550 Cannot remove the directory"),
            },
            "DELE" => match std::fs::remove_file(local(&options.root, &current, argument)) {
                Ok(()) => reply!("250 Deleted"),
                Err(_) => reply!("550 No such file"),
            },
            "RNFR" => {
                let from = local(&options.root, &current, argument);
                if from.exists() {
                    rename_from = Some(from);
                    reply!("350 Ready for RNTO");
                } else {
                    reply!("550 No such file");
                }
            }
            "RNTO" => match rename_from.take() {
                Some(from) if std::fs::rename(&from, local(&options.root, &current, argument)).is_ok() => {
                    reply!("250 Renamed");
                }
                _ => reply!("550 Rename failed"),
            },
            "MLSD" | "LIST" | "NLST" | "RETR" | "STOR" | "APPE" => {
                if verb == "MLSD" && options.plain_list {
                    reply!("500 MLSD not understood");
                    continue;
                }

                let (listener, dial) = (passive.take(), active.take());
                if listener.is_none() && dial.is_none() {
                    reply!("425 Use PASV or PORT first");
                    continue;
                }
                let target = local(&options.root, &current, argument);
                let offset = std::mem::take(&mut restart);

                // Erst prüfen, dann die Datenverbindung: Was es nicht gibt, wird gar nicht erst angekündigt.
                let prepared: Result<Vec<u8>, ()> = match verb.as_str() {
                    "MLSD" => listing(&target, true)
                        .map(|text| wire(&text, options.latin1))
                        .map_err(|_| ()),
                    "LIST" | "NLST" => listing(&target, false)
                        .map(|text| wire(&text, options.latin1))
                        .map_err(|_| ()),
                    "RETR" => std::fs::read(&target)
                        .map(|content| content[content.len().min(offset as usize)..].to_vec())
                        .map_err(|_| ()),
                    _ => Ok(Vec::new()),
                };
                let Ok(outgoing) = prepared else {
                    reply!("550 No such file or directory");
                    continue;
                };

                // Wie vsftpd und ProFTPD: Im aktiven Modus kommt „150“ erst, wenn die Datenverbindung steht.
                if options.blocked_active && dial.is_some() {
                    tokio::time::sleep(BLOCKED_FOR).await;
                    reply!("425 Cannot open a data connection");
                    continue;
                }

                reply!("150 Opening data connection");
                let opened = match (listener, dial) {
                    (Some(listener), _) => listener.accept().await.map(|(data, _)| data),
                    (None, Some(address)) => TcpStream::connect(address).await,
                    (None, None) => unreachable!("geprüft, bevor die Verbindung angekündigt wurde"),
                };
                let Ok(data) = opened else {
                    reply!("425 Cannot open a data connection");
                    continue;
                };
                let mut data: Box<dyn Stream> = match (&acceptor, protected) {
                    (Some(acceptor), true) => match acceptor.accept(data).await {
                        Ok(secured) => Box::new(secured),
                        Err(_) => {
                            reply!("425 TLS on the data connection failed");
                            continue;
                        }
                    },
                    _ => Box::new(data),
                };

                let done = if matches!(verb.as_str(), "STOR" | "APPE") {
                    let mut incoming = Vec::new();
                    let received = data.read_to_end(&mut incoming).await.is_ok();
                    let mut content = if verb == "APPE" {
                        std::fs::read(&target).unwrap_or_default()
                    } else if offset > 0 {
                        // `REST` vor `STOR`: Was davor steht, bleibt.
                        let mut kept = std::fs::read(&target).unwrap_or_default();
                        kept.truncate(offset as usize);
                        kept
                    } else {
                        Vec::new()
                    };
                    content.extend_from_slice(&incoming);
                    received && std::fs::write(&target, content).is_ok()
                } else {
                    let sent = data.write_all(&outgoing).await.is_ok();
                    let _ = data.shutdown().await;
                    sent
                };
                drop(data);
                if done {
                    reply!("226 Transfer complete");
                } else {
                    reply!("426 Transfer aborted");
                }
            }
            "QUIT" => {
                reply!("221 Bye");
                return;
            }
            _ => reply!("502 Command not implemented"),
        }
    }
}
