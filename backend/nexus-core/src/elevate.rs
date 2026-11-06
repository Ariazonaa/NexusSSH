//! Der Dateibrowser als root: Statt des SFTP-Kanals, den der Server dem Benutzer gibt, startet der Kern den
//! SFTP-Server über `sudo` in einem Befehlskanal und spricht darüber SFTP – wie `sftp -s "sudo …/sftp-server"`.
//!
//! Das geht nur, wo der Benutzer `sudo` benutzen darf, und geschieht nur auf ausdrücklichen Wunsch je Sitzung.
//! Was dabei gilt:
//!
//! - Die Befehlszeile baut der Kern allein. Der Pfad des SFTP-Servers stammt aus einer festen Liste; der Server
//!   sagt nur, welcher davon bei ihm vorhanden ist, und seine Antwort wird mit der Liste verglichen, nie eingesetzt.
//! - Erst `sudo -n` (ohne Passwort). Nur wenn `sudo` dann ein Passwort verlangt, fragt die App den Benutzer; es
//!   wird für diesen einen Aufruf über die Eingabe des Befehls geschickt (`-S`), nie als Argument, und nirgends
//!   gespeichert.
//! - Mit Passwort immer `-k`: Dann fragt `sudo` sicher danach. Täte es das nicht (weil es sich den Benutzer von
//!   eben gemerkt hat), bekäme der SFTP-Server die Passwortzeile als erstes Paket zu lesen.
//! - Das Passwort wird vorher mit `sudo -k -S true` geprüft: Ein falsches gäbe sonst die ersten Bytes des
//!   SFTP-Gesprächs als weitere „Versuche“ an `sudo`.

use std::sync::Arc;
use std::time::Duration;

use russh::ChannelMsg;
use russh::client::{Handle, Handler};
use russh_sftp::client::SftpSession;
use tokio::time::timeout;

use crate::exec;
use crate::sftp::Failure;

/// Wo der SFTP-Server liegt – Debian/Ubuntu, Fedora/RHEL, Arch/SUSE/Alpine, die BSDs und macOS, ältere SUSE.
const SERVERS: [&str; 6] = [
    "/usr/lib/openssh/sftp-server",
    "/usr/libexec/openssh/sftp-server",
    "/usr/lib/ssh/sftp-server",
    "/usr/libexec/sftp-server",
    "/usr/lib/sftp-server",
    "/usr/lib64/ssh/sftp-server",
];

/// So lange darf `sudo` für eine Antwort brauchen (es kann einen Verzeichnisdienst fragen).
const SUDO_LIMIT: Duration = Duration::from_secs(20);

/// Der Befehl, der den vorhandenen SFTP-Server nennt.
pub(crate) fn find_command() -> String {
    format!(
        "for s in {}; do if [ -x \"$s\" ]; then echo \"$s\"; exit 0; fi; done; exit 1",
        SERVERS.join(" ")
    )
}

/// Der Eintrag der festen Liste, den der Server genannt hat – seine Ausgabe selbst wird nie verwendet.
pub(crate) fn known_server(output: &str) -> Option<&'static str> {
    let named = output.lines().next()?.trim();
    SERVERS.iter().copied().find(|server| *server == named)
}

/// Der Befehl, der den SFTP-Server als root startet: ohne Passwort (`-n`) oder mit einem von der Eingabe (`-k -S`,
/// leere Aufforderung, damit nichts davon in das SFTP-Gespräch gerät).
pub(crate) fn server_command(server: &'static str, with_password: bool) -> String {
    if with_password {
        format!("exec sudo -k -S -p '' {server}")
    } else {
        format!("exec sudo -n {server}")
    }
}

/// Was `sudo` zu einem Versuch ohne Passwort gesagt hat.
pub(crate) fn refusal(output: &exec::Output) -> Failure {
    let text = output.stderr.to_lowercase();
    if output.status == Some(127) || text.contains("not found") {
        Failure {
            code: "no_sudo",
            message: "sudo is not installed on the server".to_owned(),
        }
    } else if text.contains("password")
        || text.contains("passwor")
        || text.contains("askpass")
        || text.contains("terminal")
    {
        Failure {
            code: "sudo_password",
            message: "sudo asks for a password".to_owned(),
        }
    } else {
        Failure {
            code: "sudo_refused",
            message: if output.stderr.is_empty() {
                "sudo refused".to_owned()
            } else {
                output.stderr.clone()
            },
        }
    }
}

/// Öffnet einen SFTP-Kanal als root. Ohne Passwort scheitert das mit `sudo_password`, wenn `sudo` eines verlangt.
pub(crate) async fn open<H: Handler>(handle: &Handle<H>, password: Option<&str>) -> Result<Arc<SftpSession>, Failure> {
    // Ein Passwort mit Zeilenumbruch wäre zwei Zeilen Eingabe – die zweite läse der SFTP-Server.
    if password.is_some_and(|password| password.is_empty() || password.contains(['\n', '\r'])) {
        return Err(Failure {
            code: "sudo_refused",
            message: "the password cannot be passed to sudo".to_owned(),
        });
    }

    let found = exec::run(handle, &exec::posix(&find_command()), SUDO_LIMIT).await?;
    let Some(server) = known_server(&found.stdout) else {
        return Err(Failure {
            code: "no_sftp_server",
            message: "no sftp-server program was found on the server".to_owned(),
        });
    };

    // Vorab prüfen, was `sudo` sagt – der Kanal für SFTP selbst könnte es nicht mehr erklären.
    let line = password.map(|password| format!("{password}\n"));
    let check = match &line {
        Some(line) => {
            exec::run_with(
                handle,
                &exec::posix("sudo -k -S -p '' true"),
                Some(line.as_bytes()),
                SUDO_LIMIT,
            )
            .await?
        }
        None => exec::run(handle, &exec::posix("sudo -n true"), SUDO_LIMIT).await?,
    };
    if check.status != Some(0) {
        return Err(match password {
            // Mit Passwort heißt eine Absage: falsches Passwort oder kein Recht – beides sagt `sudo` selbst.
            Some(_) => Failure {
                code: "sudo_refused",
                message: if check.stderr.is_empty() {
                    "sudo refused".to_owned()
                } else {
                    check.stderr
                },
            },
            None => refusal(&check),
        });
    }

    let failed = |message: String| Failure {
        code: "failed",
        message,
    };
    let attempt = async {
        let mut channel = handle
            .channel_open_session()
            .await
            .map_err(|error| failed(error.to_string()))?;
        channel
            .exec(true, exec::posix(&server_command(server, password.is_some())))
            .await
            .map_err(|error| failed(error.to_string()))?;
        loop {
            match channel.wait().await {
                Some(ChannelMsg::Success) => break,
                Some(ChannelMsg::Failure | ChannelMsg::Close | ChannelMsg::Eof) | None => {
                    return Err(Failure {
                        code: "no_exec",
                        message: "the server does not run commands for this account".to_owned(),
                    });
                }
                Some(_) => {}
            }
        }

        if let Some(line) = &line {
            channel
                .data(line.as_bytes())
                .await
                .map_err(|error| failed(error.to_string()))?;
        }

        let (stream, meter) = crate::metered::counted(channel.into_stream());
        let sftp = SftpSession::new(stream).await.map(Arc::new).map_err(|error| Failure {
            code: "sudo_refused",
            message: format!("the SFTP server did not start under sudo: {error}"),
        })?;
        crate::metered::attach(&sftp, &meter);
        Ok(sftp)
    };

    timeout(SUDO_LIMIT, attempt).await.unwrap_or_else(|_| {
        Err(Failure {
            code: "timeout",
            message: "sudo did not answer in time".to_owned(),
        })
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_server_only_picks_from_a_fixed_list() {
        assert!(find_command().starts_with("for s in /usr/lib/openssh/sftp-server /usr/libexec/openssh/sftp-server "));
        assert_eq!(
            known_server("/usr/libexec/sftp-server\n"),
            Some("/usr/libexec/sftp-server")
        );

        // Was nicht in der Liste steht, wird nicht zur Befehlszeile – was immer der Server antwortet.
        assert_eq!(known_server("/tmp/evil; rm -rf /\n"), None);
        assert_eq!(known_server("/usr/lib/openssh/sftp-server; id\n"), None);
        assert_eq!(known_server(""), None);
    }

    #[test]
    fn the_password_never_appears_in_the_command_line() {
        let server = SERVERS[0];
        assert_eq!(
            server_command(server, false),
            "exec sudo -n /usr/lib/openssh/sftp-server"
        );
        assert_eq!(
            server_command(server, true),
            "exec sudo -k -S -p '' /usr/lib/openssh/sftp-server"
        );
    }

    #[test]
    fn what_sudo_says_without_a_password_becomes_a_reason() {
        let said = |status: u32, stderr: &str| {
            refusal(&exec::Output {
                status: Some(status),
                stdout: String::new(),
                stderr: stderr.to_owned(),
            })
            .code
        };
        assert_eq!(said(1, "sudo: a password is required"), "sudo_password");
        assert_eq!(
            said(1, "sudo: a terminal is required to read the password"),
            "sudo_password"
        );
        assert_eq!(said(127, "sh: sudo: not found"), "no_sudo");
        assert_eq!(said(1, "demo is not in the sudoers file."), "sudo_refused");
    }
}
