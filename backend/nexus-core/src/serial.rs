//! Serielle Verbindungen (COM-Anschluss) – die Konsole eines Switches, eines Routers, eines Mikrocontrollers.
//!
//! Keine Verbindung im Netz: Das Profil nennt als `host` die Schnittstelle dieses Rechners und in `baud` die
//! Geschwindigkeit. Übertragen wird mit 8 Datenbits, ohne Parität, mit einem Stoppbit und ohne Flusskontrolle –
//! was fast jede Konsole erwartet. Wie bei Telnet gibt es keine Anmeldung durch den Kern, keine Dateien und keine
//! Auslastung.
//!
//! Der Name der Schnittstelle kommt aus dem Profil und wird Teil eines Gerätepfads: Angenommen wird nur `COM` mit
//! einer Nummer ([`port_path`]) – sonst ließe sich darüber jedes Gerät des Rechners öffnen.
//!
//! Bisher nur unter Windows; anderswo scheitert das Verbinden mit einer Meldung.

use std::sync::Arc;

use tokio::sync::mpsc::UnboundedReceiver;

use crate::core::Shared;
use crate::profiles::Profile;
use crate::protocol::{CloseReason, Event, SessionState};
use crate::session::Command;

/// Die Geschwindigkeit, wenn das Profil keine nennt – die übliche einer Gerätekonsole.
pub(crate) const DEFAULT_BAUD: u32 = 9600;

/// Der Gerätepfad zu einem Namen wie `COM3`; `None` für alles, was kein solcher Name ist.
pub(crate) fn port_path(name: &str) -> Option<String> {
    let number = name
        .strip_prefix("COM")
        .or_else(|| name.strip_prefix("com"))
        .or_else(|| name.strip_prefix("Com"))?;
    (matches!(number.len(), 1..=3) && number.chars().all(|c| c.is_ascii_digit()) && number != "0")
        .then(|| format!(r"\\.\COM{number}"))
}

/// Eine serielle Sitzung: öffnen, dann Bytes hin und her, bis der Benutzer sie schließt oder das Gerät
/// verschwindet (ein abgezogener USB-Adapter). Von selbst endet sie nie – `Ok` gibt es deshalb nicht.
pub(crate) async fn drive(
    shared: &Arc<Shared>,
    session: u64,
    profile: &Profile,
    mut commands: UnboundedReceiver<Command>,
) -> Result<(), (CloseReason, String)> {
    shared.state(session, SessionState::Connecting, None);
    let Some(path) = port_path(&profile.host) else {
        return Err((
            CloseReason::ConnectFailed,
            format!("{:?} is not a serial port (expected a name like COM3)", profile.host),
        ));
    };
    let baud = profile.baud.unwrap_or(DEFAULT_BAUD);
    let port = tokio::task::spawn_blocking(move || port::Port::open(&path, baud))
        .await
        .map_err(|error| (CloseReason::ConnectFailed, error.to_string()))?
        .map_err(|error| (CloseReason::ConnectFailed, error.to_string()))?;
    shared.connected_without_link(session, &profile.id);

    let lost = |error: std::io::Error| (CloseReason::ConnectionLost, error.to_string());
    // Der Leser lebt, solange diese Sitzung läuft; endet sie (auch durch Abbruch), fällt er und der Thread hört auf.
    let (mut incoming, _reader) = port.read_in_background().map_err(lost)?;
    let port = Arc::new(port);
    let mut paused = false;
    loop {
        tokio::select! {
            // Angehalten bleibt, was das Gerät schickt, in der Warteschlange – bei seriellen Geschwindigkeiten
            // ist das nie viel.
            bytes = incoming.recv(), if !paused => match bytes {
                Some(Ok(bytes)) => shared.sink.output(session, &bytes),
                Some(Err(error)) => return Err(lost(error)),
                None => return Err((CloseReason::ConnectionLost, "the serial port was closed".to_owned())),
            },
            command = commands.recv() => match command {
                Some(Command::Input(bytes)) => {
                    let port = Arc::clone(&port);
                    tokio::task::spawn_blocking(move || port.write(&bytes))
                        .await
                        .map_err(|error| (CloseReason::ConnectionLost, error.to_string()))?
                        .map_err(lost)?;
                }
                Some(Command::Flow(pause)) => paused = pause,
                Some(Command::Monitor(true)) => shared.emit(Event::MonitorUnavailable {
                    session,
                    message: "a serial port has no way to measure the device".to_owned(),
                    retrying: false,
                }),
                // Eine serielle Konsole erfährt nicht, wie groß das Terminal ist.
                Some(Command::Resize(..) | Command::Monitor(false) | Command::Relink) => {}
                // Der Sender lebt, solange die Sitzung eingetragen ist; hierher kommt man praktisch nicht.
                None => std::future::pending().await,
            },
        }
    }
}

#[cfg(windows)]
mod port {
    use std::fs::File;
    use std::io::{Read, Write};
    use std::os::windows::io::AsRawHandle;
    use std::sync::Arc;
    use std::sync::atomic::{AtomicBool, Ordering};

    use tokio::sync::mpsc::{UnboundedReceiver, unbounded_channel};
    use windows_sys::Win32::Devices::Communication::{COMMTIMEOUTS, DCB, GetCommState, SetCommState, SetCommTimeouts};

    /// So lange wartet ein Lesen höchstens auf das erste Byte. Schreiben auf derselben Schnittstelle kommt erst
    /// dazwischen, wenn das Lesen zurückkehrt – das hier ist also auch die Verzögerung beim Tippen.
    const READ_WAIT_MS: u32 = 20;

    /// Eine geöffnete, eingestellte Schnittstelle.
    pub(super) struct Port(File);

    /// Hält das Lesen im Hintergrund am Leben; fällt er, endet es beim nächsten Nachsehen.
    pub(super) struct Reader(Arc<AtomicBool>);

    impl Drop for Reader {
        fn drop(&mut self) {
            self.0.store(true, Ordering::Relaxed);
        }
    }

    impl Port {
        pub(super) fn open(path: &str, baud: u32) -> std::io::Result<Self> {
            let file = File::options().read(true).write(true).open(path)?;
            let handle = file.as_raw_handle();

            // 8 Datenbits, keine Parität, ein Stoppbit, keine Flusskontrolle; DTR und RTS gesetzt, weil manche
            // Geräte ohne sie schweigen. Im Bitfeld: binär (Bit 0), DTR an (Bit 4), RTS an (Bit 12).
            // SAFETY: `handle` gehört der offenen Datei, die Strukturen sind vollständig initialisiert und leben
            // über die Aufrufe hinaus.
            unsafe {
                let mut state: DCB = std::mem::zeroed();
                state.DCBlength = size_of::<DCB>() as u32;
                if GetCommState(handle, &mut state) == 0 {
                    return Err(std::io::Error::last_os_error());
                }

                state.BaudRate = baud;
                state.ByteSize = 8;
                state.Parity = 0;
                state.StopBits = 0;
                state._bitfield = 0x1011;
                if SetCommState(handle, &state) == 0 {
                    return Err(std::io::Error::last_os_error());
                }

                // Lesen kehrt zurück, sobald etwas da ist – und sonst nach kurzer Zeit mit nichts.
                let timeouts = COMMTIMEOUTS {
                    ReadIntervalTimeout: u32::MAX,
                    ReadTotalTimeoutMultiplier: u32::MAX,
                    ReadTotalTimeoutConstant: READ_WAIT_MS,
                    WriteTotalTimeoutMultiplier: 0,
                    WriteTotalTimeoutConstant: 2000,
                };
                if SetCommTimeouts(handle, &timeouts) == 0 {
                    return Err(std::io::Error::last_os_error());
                }
            }

            Ok(Self(file))
        }

        pub(super) fn write(&self, bytes: &[u8]) -> std::io::Result<()> {
            (&self.0).write_all(bytes)
        }

        /// Liest in einem eigenen Thread, bis der [`Reader`] fällt oder die Schnittstelle einen Fehler meldet.
        pub(super) fn read_in_background(
            &self,
        ) -> std::io::Result<(UnboundedReceiver<std::io::Result<Vec<u8>>>, Reader)> {
            let mut file = self.0.try_clone()?;
            let stop = Arc::new(AtomicBool::new(false));
            let stopped = Arc::clone(&stop);
            let (sender, receiver) = unbounded_channel();
            std::thread::spawn(move || {
                let mut buffer = [0_u8; 4096];
                while !stopped.load(Ordering::Relaxed) {
                    let message = match file.read(&mut buffer) {
                        Ok(0) => continue,
                        Ok(count) => Ok(buffer[..count].to_vec()),
                        Err(error) => Err(error),
                    };
                    let failed = message.is_err();
                    if sender.send(message).is_err() || failed {
                        break;
                    }
                }
            });
            Ok((receiver, Reader(stop)))
        }
    }
}

#[cfg(not(windows))]
mod port {
    use tokio::sync::mpsc::UnboundedReceiver;

    pub(super) struct Port;

    pub(super) struct Reader;

    impl Port {
        pub(super) fn open(_path: &str, _baud: u32) -> std::io::Result<Self> {
            Err(std::io::Error::new(
                std::io::ErrorKind::Unsupported,
                "serial ports are not supported on this system yet",
            ))
        }

        pub(super) fn write(&self, _bytes: &[u8]) -> std::io::Result<()> {
            Ok(())
        }

        pub(super) fn read_in_background(
            &self,
        ) -> std::io::Result<(UnboundedReceiver<std::io::Result<Vec<u8>>>, Reader)> {
            Err(std::io::Error::new(std::io::ErrorKind::Unsupported, "unsupported"))
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn only_a_com_port_with_a_number_becomes_a_device_path() {
        assert_eq!(port_path("COM3").as_deref(), Some(r"\\.\COM3"));
        assert_eq!(port_path("com12").as_deref(), Some(r"\\.\COM12"));
        assert_eq!(port_path("COM256").as_deref(), Some(r"\\.\COM256"));

        for name in [
            "",
            "COM",
            "COM0",
            "COM1234",
            "COM1 ",
            "COM-1",
            "COM1\\..\\PhysicalDrive0",
            r"\\.\PhysicalDrive0",
            "PhysicalDrive0",
            "LPT1",
            "C:\\file.txt",
            "/dev/ttyUSB0",
            "COM１",
        ] {
            assert_eq!(port_path(name), None, "{name:?}");
        }
    }
}
