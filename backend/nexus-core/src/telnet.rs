//! Telnet (RFC 854) für Geräte, die kein SSH sprechen – Switches, alte Router, serielle Umsetzer.
//!
//! Unverschlüsselt: Alles, auch ein Passwort, geht im Klartext über die Leitung. Deshalb gibt es hier keine
//! Anmeldung durch den Kern, kein gespeichertes Passwort und keine Sprung-Server – der Benutzer tippt, was das
//! Gerät fragt. Dateien und Auslastung gibt es über Telnet nicht.
//!
//! [`Telnet`] ist die Aushandlung als reine Zustandsmaschine (ohne Netz, also prüfbar), [`drive`] die Sitzung.

use std::sync::Arc;

use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::sync::mpsc::UnboundedReceiver;

use crate::core::Shared;
use crate::profiles::Profile;
use crate::protocol::{CloseReason, Event, SessionState};
use crate::session::Command;

const IAC: u8 = 255;
const DONT: u8 = 254;
const DO: u8 = 253;
const WONT: u8 = 252;
const WILL: u8 = 251;
const SB: u8 = 250;
const SE: u8 = 240;

const BINARY: u8 = 0;
const ECHO: u8 = 1;
const SUPPRESS_GO_AHEAD: u8 = 3;
const TERMINAL_TYPE: u8 = 24;
const WINDOW_SIZE: u8 = 31;

/// Länger als das ist keine Unterverhandlung, die hier jemand versteht; der Rest wird verworfen.
const MAX_SUBNEGOTIATION: usize = 256;

#[derive(Clone, Copy, PartialEq, Eq)]
enum State {
    Data,
    /// Nach einem Wagenrücklauf: Ein NUL dahinter gehört zum Zeilenende und nicht zum Text.
    AfterReturn,
    Command,
    /// Nach `WILL`, `WONT`, `DO` oder `DONT`: Jetzt kommt die Option.
    Option(u8),
    Subnegotiation,
    SubnegotiationCommand,
}

/// Die Aushandlung einer Telnet-Verbindung aus Sicht des Terminals.
///
/// Angenommen wird, was ein Terminal braucht: dass der Server das Echo übernimmt, zeichenweise Übertragung
/// (kein „Go Ahead“), der Name des Terminals, seine Größe und die Übertragung von acht Bit. Alles andere wird
/// abgelehnt. Geantwortet wird nur, wenn sich etwas ändert – sonst schaukeln sich zwei Seiten endlos hoch.
pub(crate) struct Telnet {
    state: State,
    subnegotiation: Vec<u8>,
    /// Was diese Seite zugesagt hat (`WILL`), je Option.
    local: [bool; 256],
    /// Was die Gegenseite tut (`DO` von hier), je Option.
    remote: [bool; 256],
    size: (u16, u16),
    /// Als welches Terminal sich die App ausgibt (TERMINAL-TYPE).
    terminal: Vec<u8>,
}

impl Telnet {
    pub(crate) fn new(size: (u32, u32)) -> Self {
        Self {
            state: State::Data,
            subnegotiation: Vec::new(),
            local: [false; 256],
            remote: [false; 256],
            size: clamp(size),
            terminal: crate::profiles::DEFAULT_TERM.as_bytes().to_vec(),
        }
    }

    /// Gibt sich als dieses Terminal aus statt als das übliche. Der Name ist geprüft (`profiles::terminal_name`):
    /// Er steht in einer Unterverhandlung, und ein IAC darin wäre ein Befehl.
    pub(crate) fn terminal(mut self, name: &str) -> Self {
        self.terminal = name.as_bytes().to_vec();
        self
    }

    /// Nimmt Bytes des Servers entgegen: Text für das Terminal landet in `output`, Antworten auf seine
    /// Aushandlung in `reply`.
    pub(crate) fn receive(&mut self, input: &[u8], output: &mut Vec<u8>, reply: &mut Vec<u8>) {
        for &byte in input {
            self.state = match (self.state, byte) {
                (State::Data | State::AfterReturn, IAC) => State::Command,
                (State::AfterReturn, 0) => State::Data,
                (State::Data | State::AfterReturn, b'\r') => {
                    output.push(byte);
                    State::AfterReturn
                }
                (State::Data | State::AfterReturn, _) => {
                    output.push(byte);
                    State::Data
                }

                (State::Command, IAC) => {
                    output.push(IAC);
                    State::Data
                }
                (State::Command, WILL | WONT | DO | DONT) => State::Option(byte),
                (State::Command, SB) => {
                    self.subnegotiation.clear();
                    State::Subnegotiation
                }
                // NOP, Go Ahead und dergleichen: nichts zu tun.
                (State::Command, _) => State::Data,

                (State::Option(verb), option) => {
                    self.negotiate(verb, option, reply);
                    State::Data
                }

                (State::Subnegotiation, IAC) => State::SubnegotiationCommand,
                (State::Subnegotiation, _) => {
                    if self.subnegotiation.len() < MAX_SUBNEGOTIATION {
                        self.subnegotiation.push(byte);
                    }

                    State::Subnegotiation
                }
                (State::SubnegotiationCommand, SE) => {
                    self.subnegotiated(reply);
                    State::Data
                }
                (State::SubnegotiationCommand, IAC) => {
                    if self.subnegotiation.len() < MAX_SUBNEGOTIATION {
                        self.subnegotiation.push(IAC);
                    }

                    State::Subnegotiation
                }
                // Kein gültiges Ende: so tun, als wäre es eines, statt für immer darin zu bleiben.
                (State::SubnegotiationCommand, _) => State::Data,
            };
        }
    }

    fn negotiate(&mut self, verb: u8, option: u8, reply: &mut Vec<u8>) {
        let index = usize::from(option);
        match verb {
            DO => {
                let wanted = matches!(option, BINARY | SUPPRESS_GO_AHEAD | TERMINAL_TYPE | WINDOW_SIZE);
                if wanted != self.local[index] || !wanted {
                    reply.extend([IAC, if wanted { WILL } else { WONT }, option]);
                }

                let fresh = wanted && !self.local[index];
                self.local[index] = wanted;
                if fresh && option == WINDOW_SIZE {
                    self.window_size(reply);
                }
            }
            DONT if self.local[index] => {
                self.local[index] = false;
                reply.extend([IAC, WONT, option]);
            }
            WILL => {
                let wanted = matches!(option, BINARY | ECHO | SUPPRESS_GO_AHEAD);
                if wanted != self.remote[index] || !wanted {
                    reply.extend([IAC, if wanted { DO } else { DONT }, option]);
                }

                self.remote[index] = wanted;
            }
            WONT if self.remote[index] => {
                self.remote[index] = false;
                reply.extend([IAC, DONT, option]);
            }
            // Auch: DONT und WONT für etwas, das ohnehin nicht gilt – darauf wird nicht geantwortet.
            _ => {}
        }
    }

    // Die einzige Frage, die hier beantwortet wird: „Welches Terminal?“ (TERMINAL-TYPE SEND).
    fn subnegotiated(&mut self, reply: &mut Vec<u8>) {
        if self.subnegotiation == [TERMINAL_TYPE, 1] && self.local[usize::from(TERMINAL_TYPE)] {
            reply.extend([IAC, SB, TERMINAL_TYPE, 0]);
            reply.extend(&self.terminal);
            reply.extend([IAC, SE]);
        }
    }

    fn window_size(&self, reply: &mut Vec<u8>) {
        reply.extend([IAC, SB, WINDOW_SIZE]);
        for byte in self.size.0.to_be_bytes().into_iter().chain(self.size.1.to_be_bytes()) {
            // Ein Byte 255 in der Größe ist verdoppelt zu senden, sonst sähe es wie ein Befehl aus.
            reply.push(byte);
            if byte == IAC {
                reply.push(IAC);
            }
        }

        reply.extend([IAC, SE]);
    }

    /// Das Terminal hat eine neue Größe; gemeldet wird sie, wenn der Server danach gefragt hat.
    pub(crate) fn resize(&mut self, size: (u32, u32), reply: &mut Vec<u8>) {
        self.size = clamp(size);
        if self.local[usize::from(WINDOW_SIZE)] {
            self.window_size(reply);
        }
    }

    /// Getipptes, wie es auf die Leitung geht: Ein Byte 255 wird verdoppelt, und auf einen Wagenrücklauf folgt
    /// NUL (RFC 854: ein Wagenrücklauf steht nie allein) – außer bei ausgehandelter Übertragung von acht Bit.
    pub(crate) fn encode(&self, input: &[u8]) -> Vec<u8> {
        let binary = self.local[usize::from(BINARY)];
        let mut bytes = Vec::with_capacity(input.len() + 2);
        for &byte in input {
            bytes.push(byte);
            match byte {
                IAC => bytes.push(IAC),
                b'\r' if !binary => bytes.push(0),
                _ => {}
            }
        }

        bytes
    }
}

fn clamp(size: (u32, u32)) -> (u16, u16) {
    let side = |value: u32| u16::try_from(value.max(1)).unwrap_or(u16::MAX);
    (side(size.0), side(size.1))
}

/// Eine Telnet-Sitzung: verbinden, dann Bytes hin und her, bis eine Seite aufhört. `Ok` heißt, der Server hat
/// die Verbindung beendet.
pub(crate) async fn drive(
    shared: &Arc<Shared>,
    session: u64,
    profile: &Profile,
    size: (u32, u32),
    mut commands: UnboundedReceiver<Command>,
) -> Result<(), (CloseReason, String)> {
    shared.state(session, SessionState::Connecting, None);
    let stream = match crate::dial::tcp(profile).await {
        Ok(stream) => stream,
        Err(failed) => return Err((CloseReason::ConnectFailed, failed.message)),
    };
    // Tastendrücke sofort senden, nicht auf weitere Daten warten.
    let _ = stream.set_nodelay(true);
    shared.connected_without_link(session, &profile.id);

    let lost = |error: std::io::Error| (CloseReason::ConnectionLost, error.to_string());
    let (mut reader, mut writer) = stream.into_split();
    let mut telnet = Telnet::new(size).terminal(profile.term.as_deref().unwrap_or(crate::profiles::DEFAULT_TERM));
    let mut buffer = vec![0_u8; 16 * 1024];
    let (mut output, mut reply) = (Vec::new(), Vec::new());
    let mut paused = false;
    loop {
        tokio::select! {
            // Angehalten wird nichts mehr abgeholt; der Rückstau bremst den Server.
            read = reader.read(&mut buffer), if !paused => {
                let count = read.map_err(lost)?;
                if count == 0 {
                    return Ok(());
                }

                telnet.receive(&buffer[..count], &mut output, &mut reply);
                if !output.is_empty() {
                    shared.sink.output(session, &output);
                    output.clear();
                }
            }
            command = commands.recv() => match command {
                Some(Command::Input(bytes)) => writer.write_all(&telnet.encode(&bytes)).await.map_err(lost)?,
                Some(Command::Resize(columns, rows)) => telnet.resize((columns, rows), &mut reply),
                Some(Command::Flow(pause)) => paused = pause,
                // Über Telnet lässt sich nichts messen: Das sagt die Sitzung einmal, statt zu schweigen.
                Some(Command::Monitor(true)) => shared.emit(Event::MonitorUnavailable {
                    session,
                    message: "telnet has no way to measure the server".to_owned(),
                    retrying: false,
                }),
                Some(Command::Monitor(false) | Command::Relink) => {}
                // Der Sender lebt, solange die Sitzung eingetragen ist; hierher kommt man praktisch nicht.
                None => std::future::pending().await,
            },
        }

        if !reply.is_empty() {
            writer.write_all(&reply).await.map_err(lost)?;
            reply.clear();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn exchange(telnet: &mut Telnet, input: &[u8]) -> (Vec<u8>, Vec<u8>) {
        let (mut output, mut reply) = (Vec::new(), Vec::new());
        telnet.receive(input, &mut output, &mut reply);
        (output, reply)
    }

    #[test]
    fn what_a_terminal_needs_is_accepted_and_everything_else_refused() {
        let mut telnet = Telnet::new((80, 24));

        // Der Server übernimmt das Echo, will Terminal und Größe wissen – und Zeilenmodus (34), den es nicht gibt.
        let (output, reply) = exchange(
            &mut telnet,
            &[
                IAC,
                WILL,
                ECHO,
                IAC,
                WILL,
                SUPPRESS_GO_AHEAD,
                IAC,
                DO,
                TERMINAL_TYPE,
                IAC,
                DO,
                WINDOW_SIZE,
                IAC,
                DO,
                34,
                IAC,
                WILL,
                5,
            ],
        );

        assert!(output.is_empty());
        assert_eq!(
            reply,
            [
                &[IAC, DO, ECHO][..],
                &[IAC, DO, SUPPRESS_GO_AHEAD],
                &[IAC, WILL, TERMINAL_TYPE],
                &[IAC, WILL, WINDOW_SIZE],
                &[IAC, SB, WINDOW_SIZE, 0, 80, 0, 24, IAC, SE],
                &[IAC, WONT, 34],
                &[IAC, DONT, 5],
            ]
            .concat()
        );

        // Dasselbe noch einmal ändert nichts mehr: keine Antwort auf das, was schon gilt.
        let (_, again) = exchange(&mut telnet, &[IAC, WILL, ECHO, IAC, DO, WINDOW_SIZE]);
        assert!(again.is_empty());

        // Nimmt der Server etwas zurück, wird das bestätigt – einmal.
        let (_, taken_back) = exchange(&mut telnet, &[IAC, WONT, ECHO, IAC, WONT, ECHO, IAC, DONT, WINDOW_SIZE]);
        assert_eq!(taken_back, [IAC, DONT, ECHO, IAC, WONT, WINDOW_SIZE]);
    }

    #[test]
    fn the_terminal_names_itself_when_asked() {
        let mut telnet = Telnet::new((80, 24));

        // Ohne Zusage keine Auskunft.
        let (_, unasked) = exchange(&mut telnet, &[IAC, SB, TERMINAL_TYPE, 1, IAC, SE]);
        assert!(unasked.is_empty());

        exchange(&mut telnet, &[IAC, DO, TERMINAL_TYPE]);
        let (_, reply) = exchange(&mut telnet, &[IAC, SB, TERMINAL_TYPE, 1, IAC, SE]);

        assert_eq!(
            reply,
            [&[IAC, SB, TERMINAL_TYPE, 0][..], b"xterm-256color", &[IAC, SE]].concat()
        );
    }

    #[test]
    fn text_passes_through_whatever_the_blocks_are_cut_like() {
        let mut telnet = Telnet::new((80, 24));
        let stream = [
            b"login: ".as_slice(),
            &[IAC, WILL, ECHO],
            b"a\r\0b\r\nc",
            &[IAC, IAC],
            &[IAC, 241],
            b"d",
            &[IAC, SB, 99, 1, 2, IAC, IAC, 3, IAC, SE],
            b"e",
        ]
        .concat();

        // Byte für Byte: Kein Befehl darf an einer Blockgrenze zerfallen.
        let (mut output, mut reply) = (Vec::new(), Vec::new());
        for byte in &stream {
            telnet.receive(std::slice::from_ref(byte), &mut output, &mut reply);
        }

        assert_eq!(output, [b"login: a\rb\r\nc".as_slice(), &[IAC], b"de"].concat());
        assert_eq!(reply, [IAC, DO, ECHO]);

        let (whole, _) = exchange(&mut Telnet::new((80, 24)), &stream);
        assert_eq!(whole, output);
    }

    #[test]
    fn a_new_size_is_reported_once_the_server_asked_for_it() {
        let mut telnet = Telnet::new((80, 24));
        let mut reply = Vec::new();

        telnet.resize((100, 30), &mut reply);
        assert!(reply.is_empty());

        // Die Zusage nennt gleich die Größe von jetzt, nicht die vom Anfang.
        let (_, agreed) = exchange(&mut telnet, &[IAC, DO, WINDOW_SIZE]);
        assert_eq!(
            agreed,
            [IAC, WILL, WINDOW_SIZE, IAC, SB, WINDOW_SIZE, 0, 100, 0, 30, IAC, SE]
        );

        // 255 Spalten: Das Byte ist verdoppelt, sonst begänne dort ein Befehl. Zu große Werte werden gekappt.
        telnet.resize((255, 70_000), &mut reply);
        assert_eq!(reply, [IAC, SB, WINDOW_SIZE, 0, 255, 255, 255, 255, 255, 255, IAC, SE]);
    }

    #[test]
    fn typed_bytes_are_escaped_for_the_wire() {
        let mut telnet = Telnet::new((80, 24));

        assert_eq!(telnet.encode(b"ls\r"), b"ls\r\0");
        assert_eq!(telnet.encode(&[b'a', IAC, b'b']), [b'a', IAC, IAC, b'b']);

        // Mit acht Bit ausgehandelt geht der Wagenrücklauf allein.
        exchange(&mut telnet, &[IAC, DO, BINARY]);
        assert_eq!(telnet.encode(b"ls\r"), b"ls\r");
        assert_eq!(telnet.encode(&[IAC]), [IAC, IAC]);
    }

    #[test]
    fn the_terminal_of_the_profile_is_named_instead_of_the_usual_one() {
        let mut telnet = Telnet::new((80, 24)).terminal("vt100");
        exchange(&mut telnet, &[IAC, DO, TERMINAL_TYPE]);
        let (_, reply) = exchange(&mut telnet, &[IAC, SB, TERMINAL_TYPE, 1, IAC, SE]);

        assert_eq!(reply, [&[IAC, SB, TERMINAL_TYPE, 0][..], b"vt100", &[IAC, SE]].concat());
    }
}
