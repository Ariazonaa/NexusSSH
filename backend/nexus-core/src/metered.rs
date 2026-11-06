//! Zählt mit, wie viele Einträge ein SFTP-Server in seinen Verzeichnislisten nennt.
//!
//! `russh-sftp` sammelt eine Liste, bis der Server „Ende“ sagt – wie viel das wird, bestimmt der Server, und an die
//! einzelnen Antworten kommt man von außen nicht heran. Deshalb liest hier der Strom unter der SFTP-Sitzung mit:
//! Er erkennt die Antworten mit Namen (`SSH_FXP_NAME`) an ihrem Kopf und zählt deren Einträge. Wer eine Liste
//! abwartet ([`crate::sftp::read_dir`]), sieht daran, wenn sie über jedes Maß wächst, und gibt sie auf.
//!
//! Zweitens hält der Strom die Sitzung am Leben, wenn ein Server einen Statuscode nennt, den das Protokoll nicht
//! kennt: `russh-sftp` liest nur die Codes 0 bis 8 und beendet bei jedem anderen die ganze Sitzung – danach endete
//! jede Anfrage mit „session closed“. Das NAS von UGREEN (UGOS, OpenSSH 9.2) antwortet so (4294967294) schon auf das
//! Lesen von `/etc/passwd` in seinem eingesperrten Baum (gefunden 2026-10-14; `sftp` von OpenSSH nimmt den Code als
//! Fehler hin). Ein solcher Code wird deshalb hier zu `SSH_FX_FAILURE` (4); der Text der Meldung bleibt.

use std::collections::HashMap;
use std::pin::Pin;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex, OnceLock, Weak};
use std::task::{Context, Poll};

use russh_sftp::client::SftpSession;
use tokio::io::{AsyncRead, AsyncWrite, ReadBuf};
use tokio::sync::Notify;

/// Die Art einer Antwort mit Namen im SFTP-Protokoll.
const FXP_NAME: u8 = 104;

/// Die Art einer Antwort mit Status, und der höchste Code, den `russh-sftp` kennt (`SSH_FX_OP_UNSUPPORTED`).
const FXP_STATUS: u8 = 101;
const KNOWN_STATUS: u8 = 8;

/// Was an die Stelle eines unbekannten Codes tritt: `SSH_FX_FAILURE`.
const FX_FAILURE: u8 = 4;

/// Was der Strom einer Sitzung bisher an Einträgen gesehen hat.
#[derive(Default)]
pub(crate) struct Meter {
    entries: AtomicU64,
    grew: Notify,
    few: std::sync::atomic::AtomicBool,
    by_commands: std::sync::atomic::AtomicBool,
    /// Wie viele gerade Inhalte über die Sitzung bewegen ([`patient`]).
    moving: Mutex<usize>,
}

/// So lange wartet eine Sitzung gewöhnlich auf die Antwort zu einer Anfrage (die Vorgabe von `russh-sftp`).
const REQUEST_SECONDS: u64 = 10;

/// Und so lange, während Inhalte unterwegs sind. Die Bibliothek schickt beim Lesen bis zu 16 Anfragen voraus (je
/// gut 250 KiB), beim Schreiben 16 Pakete von 32 KiB, und die Frist jeder Anfrage läuft ab dem Absenden – nicht
/// ab dem Moment, in dem sie an der Reihe ist. Mit zehn Sekunden muss die Leitung deshalb knapp 4 MB in zehn
/// Sekunden schaffen, sonst scheitert der Download mitten in der Datei (gemessen 2026-10-12 an einer auf
/// 256 kbit/s gedrosselten Leitung: nach zwei Blöcken „timeout“, während `scp` dieselbe Datei holte). Mit einer
/// Viertelstunde reichen gut 4 KB/s. Dass der Server gar nicht mehr antwortet, sieht der Benutzer in dieser Zeit
/// am stehenden Fortschritt; eine abgerissene Verbindung beendet die Anfragen sofort.
const PATIENT_SECONDS: u64 = 15 * 60;

/// Hält die Sitzung geduldig, solange es lebt ([`patient`]).
pub(crate) struct Patience<'a> {
    sftp: &'a SftpSession,
    meter: Option<Arc<Meter>>,
}

/// Für die Dauer einer Übertragung über diese Sitzung: Anfragen bekommen die lange Frist. Mehrere zugleich zählen
/// mit; die kurze gilt wieder, wenn die letzte endet. Wer Inhalte von Dateien über SFTP liest oder schreibt, ruft
/// das vorher – sonst scheitert er an jeder langsamen Leitung.
pub(crate) fn patient(sftp: &SftpSession) -> Patience<'_> {
    let meter = of(sftp);
    match &meter {
        Some(meter) => {
            let mut moving = meter.moving.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
            *moving += 1;
            sftp.set_timeout(PATIENT_SECONDS);
        }
        // Ohne Zähler ließe sich nicht sagen, wann die letzte Übertragung endet – dann bleibt es bei der langen.
        None => sftp.set_timeout(PATIENT_SECONDS),
    }

    Patience { sftp, meter }
}

impl Drop for Patience<'_> {
    fn drop(&mut self) {
        if let Some(meter) = &self.meter {
            let mut moving = meter.moving.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
            *moving = moving.saturating_sub(1);
            if *moving == 0 {
                self.sftp.set_timeout(REQUEST_SECONDS);
            }
        }
    }
}

impl Meter {
    /// So viele Einträge haben die Antworten des Servers bisher genannt, über alle Listen der Sitzung.
    pub(crate) fn entries(&self) -> u64 {
        self.entries.load(Ordering::Relaxed)
    }

    /// Kehrt zurück, sobald der Server weitere Einträge genannt hat.
    pub(crate) async fn grew(&self) {
        self.grew.notified().await;
    }

    /// Hinter dieser Sitzung braucht jede Datei, die gerade übertragen wird, eine eigene Verbindung zum Server
    /// (FTP) – und Server begrenzen, wie viele einer haben darf.
    pub(crate) fn mark_few(&self) {
        self.few.store(true, Ordering::Relaxed);
    }

    /// Ob [`Meter::mark_few`] gilt.
    pub(crate) fn few(&self) -> bool {
        self.few.load(Ordering::Relaxed)
    }

    /// Hinter dieser Sitzung steht kein SFTP-Server, sondern der Kern selbst ([`crate::shellfs`]): Jede Anfrage
    /// kostet einen Befehl auf dem Server, und die Befehle laufen einer nach dem anderen.
    pub(crate) fn mark_by_commands(&self) {
        self.by_commands.store(true, Ordering::Relaxed);
    }

    /// Ob [`Meter::mark_by_commands`] gilt.
    pub(crate) fn by_commands(&self) -> bool {
        self.by_commands.load(Ordering::Relaxed)
    }

    fn add(&self, count: u32) {
        self.entries.fetch_add(u64::from(count), Ordering::Relaxed);
        self.grew.notify_waiters();
    }
}

/// Wo der Leser im Strom der Pakete steht. Ein Paket: vier Bytes Länge, ein Byte Art, dann der Rest; bei einer
/// Antwort mit Namen beginnt der Rest mit der Nummer der Anfrage und der Zahl der Einträge (je vier Bytes), bei
/// einer mit Status mit der Nummer der Anfrage und dem Code (je vier Bytes).
enum Scan {
    Length {
        got: [u8; 4],
        have: usize,
    },
    Kind {
        left: u32,
    },
    NameHead {
        left: u32,
        got: [u8; 8],
        have: usize,
    },
    /// `have`: wie viele Bytes von Nummer und Code schon durch sind; `unknown`: Ein höheres Byte des Codes war
    /// nicht 0 – der Code ist also unbekannt, gleich was danach kommt.
    StatusHead {
        left: u32,
        have: usize,
        unknown: bool,
    },
    Skip {
        left: u32,
    },
}

impl Scan {
    /// Liest mit und ersetzt dabei einen unbekannten Statuscode – Byte für Byte, denn der Code kann über zwei
    /// Lesevorgänge verteilt ankommen, und was schon weitergereicht ist, lässt sich nicht mehr ändern. Das geht,
    /// weil der Ersatz (`0 0 0 4`) in den ersten drei Bytes 0 ist: Bis dahin wird jedes Byte zu 0, und erst das
    /// letzte entscheidet.
    fn feed(&mut self, bytes: &mut [u8], meter: &Meter) {
        let mut at = 0;
        while at < bytes.len() {
            match self {
                Self::Length { got, have } => {
                    got[*have] = bytes[at];
                    *have += 1;
                    at += 1;
                    if *have == 4 {
                        *self = match u32::from_be_bytes(*got) {
                            0 => Self::Length { got: [0; 4], have: 0 },
                            left => Self::Kind { left },
                        };
                    }
                }
                Self::Kind { left } => {
                    let kind = bytes[at];
                    at += 1;
                    let left = *left - 1;
                    *self = if kind == FXP_NAME && left >= 8 {
                        Self::NameHead {
                            left,
                            got: [0; 8],
                            have: 0,
                        }
                    } else if kind == FXP_STATUS && left >= 8 {
                        Self::StatusHead {
                            left,
                            have: 0,
                            unknown: false,
                        }
                    } else {
                        Self::skip(left)
                    };
                }
                Self::NameHead { left, got, have } => {
                    got[*have] = bytes[at];
                    *have += 1;
                    *left -= 1;
                    at += 1;
                    if *have == 8 {
                        meter.add(u32::from_be_bytes([got[4], got[5], got[6], got[7]]));
                        *self = Self::skip(*left);
                    }
                }
                Self::StatusHead { left, have, unknown } => {
                    // Erst die Nummer der Anfrage (bleibt), dann die vier Bytes des Codes.
                    let byte = &mut bytes[at];
                    if *have >= 4 {
                        if *have < 7 {
                            *unknown |= *byte != 0;
                            *byte = 0;
                        } else if *unknown || *byte > KNOWN_STATUS {
                            *byte = FX_FAILURE;
                        }
                    }
                    *have += 1;
                    *left -= 1;
                    at += 1;
                    if *have == 8 {
                        *self = Self::skip(*left);
                    }
                }
                Self::Skip { left } => {
                    // Am Stück: Der Inhalt einer Datei läuft hier auch durch.
                    let taken = (bytes.len() - at).min(*left as usize);
                    at += taken;
                    *self = Self::skip(*left - taken as u32);
                }
            }
        }
    }
    fn skip(left: u32) -> Self {
        if left == 0 {
            Self::Length { got: [0; 4], have: 0 }
        } else {
            Self::Skip { left }
        }
    }
}

/// Ein Strom, der beim Lesen die Einträge der Verzeichnislisten mitzählt und sonst nichts ändert.
pub(crate) struct Counted<S> {
    inner: S,
    scan: Scan,
    meter: Arc<Meter>,
}

/// Hängt den Zähler unter einen Strom, auf dem gleich eine SFTP-Sitzung entsteht. Den Zähler bekommt die Sitzung
/// danach mit [`attach`].
pub(crate) fn counted<S>(inner: S) -> (Counted<S>, Arc<Meter>) {
    let meter = Arc::new(Meter::default());
    let stream = Counted {
        inner,
        scan: Scan::Length { got: [0; 4], have: 0 },
        meter: Arc::clone(&meter),
    };
    (stream, meter)
}

impl<S: AsyncRead + Unpin> AsyncRead for Counted<S> {
    fn poll_read(
        mut self: Pin<&mut Self>,
        context: &mut Context<'_>,
        buffer: &mut ReadBuf<'_>,
    ) -> Poll<std::io::Result<()>> {
        let before = buffer.filled().len();
        let this = &mut *self;
        let outcome = Pin::new(&mut this.inner).poll_read(context, buffer);
        if let Poll::Ready(Ok(())) = &outcome {
            this.scan.feed(&mut buffer.filled_mut()[before..], &this.meter);
        }

        outcome
    }
}

impl<S: AsyncWrite + Unpin> AsyncWrite for Counted<S> {
    fn poll_write(mut self: Pin<&mut Self>, context: &mut Context<'_>, data: &[u8]) -> Poll<std::io::Result<usize>> {
        Pin::new(&mut self.inner).poll_write(context, data)
    }

    fn poll_flush(mut self: Pin<&mut Self>, context: &mut Context<'_>) -> Poll<std::io::Result<()>> {
        Pin::new(&mut self.inner).poll_flush(context)
    }

    fn poll_shutdown(mut self: Pin<&mut Self>, context: &mut Context<'_>) -> Poll<std::io::Result<()>> {
        Pin::new(&mut self.inner).poll_shutdown(context)
    }
}

/// Für die Zufallstests ([`crate::fuzzing`]): liest `stream` in Stücken dieser Größe und liefert die gezählten
/// Einträge und den Strom, wie die Sitzung ihn sieht.
pub(crate) fn exercise(stream: &[u8], piece: usize) -> (u64, Vec<u8>) {
    let meter = Meter::default();
    let mut scan = Scan::Length { got: [0; 4], have: 0 };
    let mut seen = stream.to_vec();
    for part in seen.chunks_mut(piece.max(1)) {
        scan.feed(part, &meter);
    }

    (meter.entries(), seen)
}

// Welche Sitzung welchen Zähler hat. Der Zähler lebt, solange der Strom der Sitzung lebt; danach ist der Eintrag
// leer und fällt beim nächsten Eintragen weg. Der Schlüssel ist die Adresse der Sitzung – wird sie neu vergeben,
// trägt die neue Sitzung ihren eigenen Zähler ein.
fn meters() -> &'static Mutex<HashMap<usize, Weak<Meter>>> {
    static METERS: OnceLock<Mutex<HashMap<usize, Weak<Meter>>>> = OnceLock::new();
    METERS.get_or_init(Mutex::default)
}

/// Verbindet eine Sitzung mit dem Zähler ihres Stroms. Die Sitzung muss dafür schon an ihrem festen Platz stehen
/// (im `Arc`).
pub(crate) fn attach(sftp: &Arc<SftpSession>, meter: &Arc<Meter>) {
    let mut meters = meters().lock().unwrap_or_else(|poisoned| poisoned.into_inner());
    meters.retain(|_, meter| meter.strong_count() > 0);
    meters.insert(Arc::as_ptr(sftp) as usize, Arc::downgrade(meter));
}

/// Der Zähler einer Sitzung, wenn sie einen hat.
pub(crate) fn of(sftp: &SftpSession) -> Option<Arc<Meter>> {
    let meters = meters().lock().unwrap_or_else(|poisoned| poisoned.into_inner());
    meters.get(&(std::ptr::from_ref(sftp) as usize)).and_then(Weak::upgrade)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn packet(kind: u8, body: &[u8]) -> Vec<u8> {
        let mut bytes = (body.len() as u32 + 1).to_be_bytes().to_vec();
        bytes.push(kind);
        bytes.extend_from_slice(body);
        bytes
    }

    fn names(id: u32, count: u32, rest: &[u8]) -> Vec<u8> {
        let mut body = id.to_be_bytes().to_vec();
        body.extend_from_slice(&count.to_be_bytes());
        body.extend_from_slice(rest);
        packet(FXP_NAME, &body)
    }

    fn counted_in_pieces(stream: &[u8], size: usize) -> u64 {
        read_in_pieces(stream, size).0
    }

    /// Schickt den Strom in Stücken durch und liefert die gezählten Einträge und den Strom, wie ihn die Sitzung sieht.
    fn read_in_pieces(stream: &[u8], size: usize) -> (u64, Vec<u8>) {
        exercise(stream, size)
    }

    fn status(id: u32, code: u32, message: &str) -> Vec<u8> {
        let mut body = id.to_be_bytes().to_vec();
        body.extend_from_slice(&code.to_be_bytes());
        body.extend_from_slice(&(message.len() as u32).to_be_bytes());
        body.extend_from_slice(message.as_bytes());
        body.extend_from_slice(&0u32.to_be_bytes());
        packet(FXP_STATUS, &body)
    }

    #[test]
    fn a_status_code_the_protocol_does_not_know_becomes_failure_and_nothing_else_changes() {
        // Wie das NAS von UGREEN: 4294967294 auf das Lesen von /etc/passwd.
        let mut stream = status(3, 0xFFFF_FFFE, "denied");
        stream.extend(status(4, 2, "no such file"));
        stream.extend(status(5, 9, "v6 code"));
        stream.extend(status(6, 0x0100, "high byte"));
        // Der Inhalt einer Datei, der wie ein Paket mit Status aussieht, bleibt, wie er ist.
        stream.extend(packet(103, &status(7, 0xFFFF_FFFE, "data")));
        stream.extend(names(8, 2, b"rest"));

        let mut expected = status(3, 4, "denied");
        expected.extend(status(4, 2, "no such file"));
        expected.extend(status(5, 4, "v6 code"));
        expected.extend(status(6, 4, "high byte"));
        expected.extend(packet(103, &status(7, 0xFFFF_FFFE, "data")));
        expected.extend(names(8, 2, b"rest"));

        for size in [1, 2, 3, 5, 7, 13, stream.len()] {
            let (entries, seen) = read_in_pieces(&stream, size);
            assert_eq!(seen, expected, "Stücke zu {size}");
            assert_eq!(entries, 2, "Stücke zu {size}");
        }
    }

    #[test]
    fn entries_of_name_packets_are_counted_wherever_the_stream_is_cut() {
        let mut stream = packet(2, &[3, 0, 0, 0]);
        stream.extend(names(7, 200, &[b'x'; 500]));
        // Daten einer Datei, die zufällig wie ein Kopf aussehen, zählen nicht.
        stream.extend(packet(103, &names(1, 9999, b"")));
        stream.extend(packet(101, b""));
        stream.extend(names(8, 34, b""));
        stream.extend(packet(104, &[1, 2, 3]));
        stream.extend([0, 0, 0, 0]);
        stream.extend(names(9, 1, b"tail"));

        for size in [1, 2, 3, 5, 7, 64, stream.len()] {
            assert_eq!(counted_in_pieces(&stream, size), 235, "Stücke zu {size}");
        }
    }
}
