//! X11-Forwarding: Grafische Programme des Servers zeichnen auf dem X-Server dieses Rechners (VcXsrv, Xming,
//! unter Linux der eigene).
//!
//! Wie Agent Forwarding gilt es nur dem Ziel und nur mit Erlaubnis im Profil – wer auf dem Server Root ist,
//! erreicht über den Kanal den lokalen X-Server und könnte dort Tasten mitlesen. Dagegen hilft, was OpenSSH auch
//! tut: Der Server bekommt ein erfundenes Geheimnis (Cookie), das nur für diese Verbindung gilt. Jede Verbindung
//! eines Programms muss es vorzeigen ([`setup`]); erst dann wird sie zum lokalen X-Server durchgereicht – ohne das
//! Cookie, denn das kennt der X-Server nicht. Er muss Verbindungen von diesem Rechner selbst zulassen, was VcXsrv
//! und Xming von Haus aus tun.

use rand::RngExt;
use tokio::io::{AsyncRead, AsyncReadExt, AsyncWrite, AsyncWriteExt};
use tokio::net::TcpStream;

/// Der Name des Verfahrens, wie X11 ihn nennt.
pub(crate) const PROTOCOL: &str = "MIT-MAGIC-COOKIE-1";

/// Wo der X-Server dieses Rechners lauscht, wenn nichts anderes eingestellt ist: Display 0.
pub(crate) const DEFAULT_SERVER: &str = "127.0.0.1:6000";

/// Höhere Displays gibt es bei den X-Servern für Windows nicht; darüber begännen fremde Ports.
pub(crate) const MAX_DISPLAY: u16 = 63;

/// Die Adresse des X-Servers dieses Rechners zu einem Display: Display `n` lauscht auf Port 6000 + `n`.
pub(crate) fn server_of(display: u16) -> String {
    format!("127.0.0.1:{}", 6000 + display)
}

const HEADER: usize = 12;

/// Länger als das ist der Anfang einer X11-Verbindung nie; mehr wird nicht abgewartet.
const MAX_SETUP: usize = 4096;

/// Das Geheimnis einer Verbindung: 16 zufällige Bytes.
pub(crate) type Cookie = [u8; 16];

pub(crate) fn new_cookie() -> Cookie {
    rand::rng().random()
}

/// Das Cookie, wie es in der Anfrage an den Server steht: als Hexadezimalzahl.
pub(crate) fn hex(cookie: &Cookie) -> String {
    cookie.iter().map(|byte| format!("{byte:02x}")).collect()
}

/// Was sich aus dem Anfang einer X11-Verbindung ergibt.
#[derive(Debug, PartialEq, Eq)]
pub(crate) enum Setup {
    /// Es fehlen noch Bytes.
    Incomplete,
    /// Kein gültiger Anfang, oder das Cookie stimmt nicht: Die Verbindung wird nicht durchgereicht.
    Refused,
    /// Das Cookie stimmt. `header` ist der Anfang, wie er an den lokalen X-Server geht (ohne Cookie), `consumed`
    /// die Zahl der Bytes, die er ersetzt.
    Accepted { header: [u8; HEADER], consumed: usize },
}

/// Prüft den Anfang einer X11-Verbindung: Reihenfolge der Bytes, Version, dann Name und Daten der Anmeldung,
/// beide auf vier Bytes aufgefüllt.
pub(crate) fn setup(bytes: &[u8], cookie: &Cookie) -> Setup {
    let Some(&order) = bytes.first() else {
        return Setup::Incomplete;
    };
    if !matches!(order, b'B' | b'l') {
        return Setup::Refused;
    }

    if bytes.len() < HEADER {
        return Setup::Incomplete;
    }

    let number = |at: usize| {
        let pair = [bytes[at], bytes[at + 1]];
        usize::from(if order == b'B' {
            u16::from_be_bytes(pair)
        } else {
            u16::from_le_bytes(pair)
        })
    };
    let padded = |length: usize| length.div_ceil(4) * 4;
    let (name, data) = (number(6), number(8));
    let consumed = HEADER + padded(name) + padded(data);
    if consumed > MAX_SETUP {
        return Setup::Refused;
    }

    if bytes.len() < consumed {
        return Setup::Incomplete;
    }

    let name_at = HEADER;
    let data_at = HEADER + padded(name);
    if &bytes[name_at..name_at + name] != PROTOCOL.as_bytes() || &bytes[data_at..data_at + data] != cookie {
        return Setup::Refused;
    }

    // Derselbe Anfang ohne Anmeldung: Längen null, nichts dahinter.
    let mut header = [0_u8; HEADER];
    header[..6].copy_from_slice(&bytes[..6]);
    Setup::Accepted { header, consumed }
}

/// Reicht eine X11-Verbindung des Servers an den lokalen X-Server durch – wenn sie das Cookie vorzeigt. Sonst
/// endet sie, ohne dass der X-Server von ihr erfährt.
pub(crate) async fn forward<S>(mut remote: S, server: &str, cookie: Cookie)
where
    S: AsyncRead + AsyncWrite + Unpin,
{
    let mut begin = Vec::new();
    let mut buffer = [0_u8; 1024];
    let (header, consumed) = loop {
        match setup(&begin, &cookie) {
            Setup::Accepted { header, consumed } => break (header, consumed),
            Setup::Refused => return,
            Setup::Incomplete => match remote.read(&mut buffer).await {
                Ok(count) if count > 0 && begin.len() + count <= MAX_SETUP + buffer.len() => {
                    begin.extend_from_slice(&buffer[..count]);
                }
                _ => return,
            },
        }
    };

    let Ok(mut local) = TcpStream::connect(server).await else {
        return;
    };
    let _ = local.set_nodelay(true);
    // Verlangt der X-Server dieses Rechners selbst eine Anmeldung, bekommt er sein eigenes Geheimnis vorgelegt –
    // wie von jedem Programm, das hier läuft. Das des Servers hat er nie gesehen.
    let header = with_local_cookie(header, own_cookie(server).as_deref());
    if local.write_all(&header).await.is_err() || local.write_all(&begin[consumed..]).await.is_err() {
        return;
    }

    let _ = tokio::io::copy_bidirectional(&mut remote, &mut local).await;
}

// Der Anfang einer Verbindung für den lokalen X-Server: ohne Anmeldung, oder mit dessen eigenem Geheimnis.
fn with_local_cookie(header: [u8; HEADER], cookie: Option<&[u8]>) -> Vec<u8> {
    let mut bytes = header.to_vec();
    let Some(cookie) = cookie else {
        return bytes;
    };
    let pair = |value: usize| {
        let value = u16::try_from(value).unwrap_or(0);
        if header[0] == b'B' {
            value.to_be_bytes()
        } else {
            value.to_le_bytes()
        }
    };
    bytes[6..8].copy_from_slice(&pair(PROTOCOL.len()));
    bytes[8..10].copy_from_slice(&pair(cookie.len()));
    for part in [PROTOCOL.as_bytes(), cookie] {
        bytes.extend_from_slice(part);
        bytes.resize(bytes.len().div_ceil(4) * 4, 0);
    }

    bytes
}

/// Eine Xauthority-Datei ist klein; mehr wird nicht gelesen.
const MAX_AUTHORITY: u64 = 64 * 1024;

/// Länger ist ein Geheimnis nie; was mehr nennt, ist keine Xauthority-Datei.
const MAX_COOKIE: usize = 256;

/// Das Geheimnis, mit dem sich Programme dieses Rechners am X-Server unter `server` anmelden: aus der Datei, die
/// `XAUTHORITY` nennt, sonst `.Xauthority` im Ordner des Benutzers. `None`, wenn es keine gibt, sie das Display
/// nicht kennt oder `server` kein Display dieses Rechners ist – dann geht die Verbindung ohne Anmeldung hinüber,
/// wie bisher (VcXsrv und Xming lassen das von Haus aus zu). Das Geheimnis verlässt den Rechner nie: Es geht
/// allein an 127.0.0.1, und es steht in keinem Protokoll.
fn own_cookie(server: &str) -> Option<Vec<u8>> {
    use std::io::Read;

    let port: u16 = server.strip_prefix("127.0.0.1:")?.parse().ok()?;
    let display = port.checked_sub(6000).filter(|display| *display <= MAX_DISPLAY)?;
    let path = std::env::var_os("XAUTHORITY")
        .filter(|path| !path.is_empty())
        .map(std::path::PathBuf::from)
        .or_else(|| {
            let home = std::env::var_os(if cfg!(windows) { "USERPROFILE" } else { "HOME" })?;
            Some(std::path::PathBuf::from(home).join(".Xauthority"))
        })?;
    let file = std::fs::File::open(path).ok()?;
    if !file.metadata().ok()?.is_file() {
        return None;
    }

    let mut bytes = Vec::new();
    file.take(MAX_AUTHORITY).read_to_end(&mut bytes).ok()?;
    cookie_in(&bytes, display)
}

/// Arten von Adressen in einer Xauthority-Datei, die einen Rechner im Netz nennen (IPv4, IPv6).
const FAMILY_INTERNET: u16 = 0;
const FAMILY_INTERNET6: u16 = 6;

/// Sucht in einer Xauthority-Datei das Geheimnis für ein Display. Je Eintrag: Art der Adresse (zwei Bytes), dann
/// vier Felder mit je zwei Bytes Länge (höchstes zuerst) – Adresse, Nummer des Displays als Text, Name des
/// Verfahrens, Daten. Ein Eintrag mit der Nummer des Displays geht einem ohne Nummer (gilt für alle) vor. Von
/// der Adresse zählt nur, ob sie ein anderer Rechner im Netz ist – der Name dieses Rechners kann sich geändert
/// haben, gefragt wird ohnehin nur für ihn. Die Datei ist fremde Eingabe.
pub(crate) fn cookie_in(bytes: &[u8], display: u16) -> Option<Vec<u8>> {
    fn field<'a>(rest: &mut &'a [u8]) -> Option<&'a [u8]> {
        let length = usize::from(u16::from_be_bytes([*rest.first()?, *rest.get(1)?]));
        let field = rest.get(2..2 + length)?;
        *rest = &rest[2 + length..];
        Some(field)
    }

    let wanted = display.to_string();
    let mut any = None;
    let mut rest = bytes;
    while rest.len() >= 2 {
        let family = u16::from_be_bytes([rest[0], rest[1]]);
        rest = &rest[2..];
        // Ein abgeschnittener Eintrag am Ende: Was davor stand, gilt.
        let (Some(address), Some(number), Some(name), Some(data)) =
            (field(&mut rest), field(&mut rest), field(&mut rest), field(&mut rest))
        else {
            break;
        };
        if name != PROTOCOL.as_bytes() || data.is_empty() || data.len() > MAX_COOKIE {
            continue;
        }

        // Ein Eintrag für eine Adresse im Netz gehört einem anderen Rechner (Hinweis von Codex).
        let elsewhere = match (family, address) {
            (FAMILY_INTERNET, [first, ..]) => address.len() != 4 || *first != 127,
            (FAMILY_INTERNET6, _) => address != std::net::Ipv6Addr::LOCALHOST.octets(),
            _ => false,
        };
        if elsewhere {
            continue;
        }

        if number == wanted.as_bytes() {
            return Some(data.to_vec());
        }

        if number.is_empty() && any.is_none() {
            any = Some(data.to_vec());
        }
    }

    any
}

/// Ob auf diesem Rechner ein X-Server lauscht – sonst wäre das Forwarding eine Zusage ohne Gegenstück.
pub(crate) async fn available(server: &str) -> bool {
    matches!(
        tokio::time::timeout(std::time::Duration::from_secs(2), TcpStream::connect(server)).await,
        Ok(Ok(_))
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    const COOKIE: Cookie = [0xA5; 16];

    fn begin(order: u8, name: &[u8], data: &[u8]) -> Vec<u8> {
        let pair = |value: usize| {
            let value = u16::try_from(value).unwrap();
            if order == b'B' {
                value.to_be_bytes()
            } else {
                value.to_le_bytes()
            }
        };
        let mut bytes = vec![order, 0];
        bytes.extend(pair(11));
        bytes.extend(pair(0));
        bytes.extend(pair(name.len()));
        bytes.extend(pair(data.len()));
        bytes.extend([0, 0]);
        for part in [name, data] {
            bytes.extend(part);
            bytes.resize(bytes.len().div_ceil(4) * 4, 0);
        }

        bytes
    }

    // Ein Eintrag einer Xauthority-Datei, wie `xauth` ihn schreibt.
    fn entry(address: &[u8], number: &str, name: &str, data: &[u8]) -> Vec<u8> {
        let mut bytes = vec![0x01, 0x00];
        for part in [address, number.as_bytes(), name.as_bytes(), data] {
            bytes.extend(u16::try_from(part.len()).unwrap().to_be_bytes());
            bytes.extend(part);
        }

        bytes
    }

    #[test]
    fn the_local_servers_own_cookie_comes_from_its_entry_in_the_authority_file() {
        let mut file = entry(b"pc", "1", PROTOCOL, &[1; 16]);
        file.extend(entry(b"pc", "0", "XDM-AUTHORIZATION-1", &[9; 16]));
        file.extend(entry(b"pc", "", PROTOCOL, &[7; 16]));
        file.extend(entry(b"pc", "0", PROTOCOL, &[2; 16]));

        assert_eq!(cookie_in(&file, 0), Some(vec![2; 16]));
        assert_eq!(cookie_in(&file, 1), Some(vec![1; 16]));
        // Kein eigener Eintrag: der ohne Nummer gilt für jedes Display.
        assert_eq!(cookie_in(&file, 5), Some(vec![7; 16]));
        assert_eq!(cookie_in(&file[..file.len() - 40], 5), Some(vec![7; 16]));

        // Ein Eintrag für einen anderen Rechner im Netz zählt nicht, einer für 127.0.0.1 schon.
        let mut net = vec![0, 0];
        net.extend(&entry(&[192, 0, 2, 1], "0", PROTOCOL, &[4; 16])[2..]);
        assert_eq!(cookie_in(&net, 0), None);
        let mut own = vec![0, 0];
        own.extend(&entry(&[127, 0, 0, 1], "0", PROTOCOL, &[5; 16])[2..]);
        assert_eq!(cookie_in(&own, 0), Some(vec![5; 16]));

        // Was keine solche Datei ist, nennt kein Geheimnis – und hält niemanden auf.
        assert_eq!(cookie_in(b"", 0), None);
        assert_eq!(cookie_in(&[0xFF; 300], 0), None);
        assert_eq!(cookie_in(&entry(b"pc", "0", PROTOCOL, &[]), 0), None);
        assert_eq!(cookie_in(&entry(b"pc", "0", PROTOCOL, &[3; MAX_COOKIE + 1]), 0), None);

        // Nur für ein Display dieses Rechners wird überhaupt nachgesehen.
        assert_eq!(own_cookie("192.0.2.1:6000"), None);
        assert_eq!(own_cookie("127.0.0.1:5999"), None);
        assert_eq!(own_cookie("127.0.0.1:7000"), None);
    }

    #[test]
    fn the_local_server_gets_its_own_cookie_in_the_byte_order_of_the_program() {
        for order in *b"Bl" {
            let Setup::Accepted { header, .. } = setup(&begin(order, PROTOCOL.as_bytes(), &COOKIE), &COOKIE) else {
                panic!("not accepted");
            };

            // Ohne eigenes Geheimnis bleibt der Anfang, wie er war: ohne Anmeldung.
            assert_eq!(with_local_cookie(header, None), header.to_vec());

            // Mit ihm ist er, was ein Programm dieses Rechners schickte – das Geheimnis des Servers steht nicht darin.
            let own = [0x3C; 16];
            let sent = with_local_cookie(header, Some(&own));
            assert_eq!(sent, begin(order, PROTOCOL.as_bytes(), &own));
            assert!(!sent.windows(COOKIE.len()).any(|window| window == COOKIE));
        }
    }

    #[test]
    fn a_connection_with_the_cookie_is_passed_on_without_it() {
        for order in *b"Bl" {
            let mut bytes = begin(order, PROTOCOL.as_bytes(), &COOKIE);
            let consumed = bytes.len();
            bytes.extend(b"first request");

            let Setup::Accepted {
                header,
                consumed: taken,
            } = setup(&bytes, &COOKIE)
            else {
                panic!("not accepted");
            };

            // Reihenfolge und Version bleiben, die Längen der Anmeldung sind null.
            assert_eq!(taken, consumed);
            assert_eq!(header[..6], bytes[..6]);
            assert_eq!(header[6..], [0; 6]);
            assert_eq!(&bytes[taken..], b"first request");
        }
    }

    #[test]
    fn anything_else_is_refused_and_a_partial_beginning_waits() {
        let good = begin(b'l', PROTOCOL.as_bytes(), &COOKIE);
        for cut in 0..good.len() {
            assert_eq!(setup(&good[..cut], &COOKIE), Setup::Incomplete, "{cut}");
        }

        assert_eq!(
            setup(&begin(b'l', PROTOCOL.as_bytes(), &[0x5A; 16]), &COOKIE),
            Setup::Refused
        );
        assert_eq!(
            setup(&begin(b'l', PROTOCOL.as_bytes(), &COOKIE[..15]), &COOKIE),
            Setup::Refused
        );
        assert_eq!(setup(&begin(b'l', b"", b""), &COOKIE), Setup::Refused);
        assert_eq!(
            setup(&begin(b'B', b"XDM-AUTHORIZATION-1", &COOKIE), &COOKIE),
            Setup::Refused
        );
        assert_eq!(setup(b"GET / HTTP/1.1\r\n", &COOKIE), Setup::Refused);

        // Eine Länge, die niemand braucht, wird nicht erst abgewartet.
        let mut huge = begin(b'l', PROTOCOL.as_bytes(), &COOKIE);
        huge[8..10].copy_from_slice(&u16::MAX.to_le_bytes());
        assert_eq!(setup(&huge, &COOKIE), Setup::Refused);
    }

    #[test]
    fn every_connection_gets_its_own_cookie() {
        let (first, second) = (new_cookie(), new_cookie());

        assert_ne!(first, second);
        assert_eq!(hex(&[0x0f; 16]), "0f".repeat(16));
        assert_eq!(hex(&first).len(), 32);
    }
}
