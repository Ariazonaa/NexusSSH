//! Startet den Testserver zum Ausprobieren der App: `nexus-testserver [port] [benutzer] [passwort] [sftp-ordner]`.
//!
//! Er taugt auch als Sprung-Server (für ein zweites Profil auf denselben Server) und spielt im Server-Inspektor
//! einen Linux-Server mit schwankender Auslastung vor.
//!
//! Dazu zwei kleine FTP-Server über demselben Ordner und mit derselben Anmeldung: auf dem Port danach ohne
//! Verschlüsselung, auf dem übernächsten mit TLS und einem selbst signierten Zertifikat (bei jedem Start ein
//! neues – so lässt sich auch die Rückfrage zu einem geänderten Zertifikat ansehen).
//!
//! Lauscht nur auf 127.0.0.1. Standard: Port 2222, Benutzer `demo`, Passwort `demo`. Der SFTP-Ordner erscheint
//! dem Client als `/`; ohne Angabe legt der Server `nexus-testserver-files` im Temp-Verzeichnis an und füllt
//! ihn mit ein paar Beispieldateien.

use std::path::PathBuf;

use nexus_testserver::{FtpOptions, Monitor, Options, Shell, Sudo, host_key, start, start_ftp};
use russh::keys::HashAlg;

fn sample_root() -> std::io::Result<PathBuf> {
    let root = std::env::temp_dir().join("nexus-testserver-files");
    if !root.exists() {
        std::fs::create_dir_all(root.join("www").join("html"))?;
        std::fs::create_dir_all(root.join("logs"))?;
        std::fs::write(root.join("readme.txt"), "Dateien des nexus-testserver.\n")?;
        std::fs::write(root.join("www").join("nginx.conf"), "server { listen 80; }\n")?;
        std::fs::write(root.join("www").join("html").join("index.html"), "<h1>It works</h1>\n")?;
        std::fs::write(root.join("logs").join("app.log"), "started\n".repeat(2000))?;
    }

    Ok(root)
}

#[tokio::main]
async fn main() -> std::io::Result<()> {
    let mut arguments = std::env::args().skip(1);
    let port: u16 = arguments.next().and_then(|value| value.parse().ok()).unwrap_or(2222);
    let user = arguments.next().unwrap_or_else(|| "demo".to_owned());
    let password = arguments.next().unwrap_or_else(|| "demo".to_owned());
    let root = match arguments.next() {
        Some(path) => PathBuf::from(path),
        None => sample_root()?,
    };

    // „Als root“ zeigt einen eigenen Ordner neben dem des Benutzers; `sudo` verlangt das Passwort der Anmeldung.
    let mut as_root = root.clone().into_os_string();
    as_root.push("-root");
    let as_root = PathBuf::from(as_root);
    if !as_root.exists() {
        std::fs::create_dir_all(as_root.join("etc"))?;
        std::fs::write(as_root.join("etc").join("shadow"), "root:*:19000:0:99999:7:::\n")?;
    }

    // Ein anderer Host-Schlüssel auf Wunsch (`NEXUS_TESTSERVER_KEY_SEED`, 1–255): So sieht ein Neustart aus wie ein
    // Server, dessen Schlüssel sich geändert hat – und die App fragt wieder.
    let seed = std::env::var("NEXUS_TESTSERVER_KEY_SEED")
        .ok()
        .and_then(|seed| seed.parse::<u8>().ok())
        .filter(|seed| *seed > 0)
        .unwrap_or(1);
    let key = host_key(seed);
    let fingerprint = key.public_key().fingerprint(HashAlg::Sha256);
    let listener = nexus_testserver::listen(port)?;
    let server = start(
        listener,
        key,
        Options {
            password: Some((user.clone(), password.clone())),
            authorized_keys: Vec::new(),
            sftp_root: Some(root.clone()),
            forwarding: true,
            monitor: Monitor::Linux,
            agent_forwarding: true,
            banner: Some("nexus-testserver – nur zum Ausprobieren, kein echter Server.\n".to_owned()),
            max_sessions: None,
            // Der Benutzer `sftponly` ist ein Konto, das nur Dateien darf: keine Shell, keine Befehle.
            shell: if user == "sftponly" {
                Shell::SftpOnly
            } else {
                Shell::Interactive
            },
            old_device: false,
            host_certificates: Vec::new(),
            sudo: Some(Sudo {
                password: Some(password.clone()),
                root: as_root.clone(),
            }),
            real_shell: None,
            command_log: None,
            foreign_shell: false,
            // Kein mosh-server: Ein Profil mit Mosh fällt auf das Terminal über SSH zurück.
            mosh: Default::default(),
        },
    )?;

    println!(
        "nexus-testserver auf {} (Benutzer {user}, Passwort {password})",
        server.address
    );
    println!("Host-Schlüssel: {fingerprint}");
    println!("SFTP-Ordner:    {}", root.display());
    println!(
        "Als root:       {} (sudo mit dem Passwort der Anmeldung)",
        as_root.display()
    );

    let ftp = FtpOptions {
        port: port.saturating_add(1),
        login: (user.clone(), password.clone()),
        root: root.clone(),
        tls: None,
        plain_list: false,
        active_only: false,
        refuse_active: false,
        blocked_active: false,
        latin1: false,
        forgets_restart: false,
        ipv6: false,
    };
    let made = rcgen::generate_simple_self_signed(vec!["127.0.0.1".to_owned(), "localhost".to_owned()])
        .map_err(std::io::Error::other)?;
    let secure = FtpOptions {
        port: port.saturating_add(2),
        tls: Some((made.cert.der().to_vec(), made.signing_key.serialize_der())),
        ..ftp.clone()
    };
    println!("FTP (unverschlüsselt): {}", start_ftp(ftp).await?);
    println!("FTPS (TLS, selbst signiert): {}", start_ftp(secure).await?);
    println!("Strg+C beendet.");
    std::future::pending::<()>().await;
    Ok(())
}
