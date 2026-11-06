//! Der erste Schritt jeder Verbindung im Netz: den Namen auflösen und einen TCP-Strom zum Server öffnen.
//!
//! Hier steht, was das Profil dazu sagt (nur IPv4 oder nur IPv6, wie lange gewartet wird) – und hier entsteht die
//! Auskunft, *woran* es lag, wenn es nicht ging: Name unbekannt, niemand lauscht, keine Antwort. Die Oberfläche
//! macht daraus einen Satz mit Vorschlag ([`Trouble`] wandert als Kennung mit dem Ende der Sitzung hinüber).

use std::io;
use std::net::SocketAddr;
use std::time::Duration;

use tokio::net::{TcpStream, lookup_host};
use tokio::time::timeout;

use crate::profiles::Profile;

/// So lange darf der Aufbau dauern, wenn das Profil nichts anderes sagt.
pub(crate) const CONNECT_TIMEOUT: Duration = Duration::from_secs(15);

/// Woran der Aufbau der Verbindung gescheitert ist – soweit es sich sagen lässt.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum Trouble {
    /// Der Name ließ sich nicht auflösen.
    Name,
    /// Der Name hat keine Adresse der verlangten Art (nur IPv4 oder nur IPv6 im Profil).
    Family,
    /// Der Rechner antwortet, aber auf dem Port lauscht niemand.
    Refused,
    /// Keine Antwort in der Zeit: Rechner aus, Firewall, falsche Adresse.
    Timeout,
    /// Kein Weg dorthin (kein Netz, keine Route).
    Unreachable,
    /// Etwas anderes; die Meldung sagt mehr.
    Other,
}

impl Trouble {
    /// Die Kennung für die Oberfläche (`session.closed`, Feld `trouble`).
    pub(crate) fn name(self) -> &'static str {
        match self {
            Self::Name => "name",
            Self::Family => "family",
            Self::Refused => "refused",
            Self::Timeout => "timeout",
            Self::Unreachable => "unreachable",
            Self::Other => "other",
        }
    }

    fn of(error: &io::Error) -> Self {
        match error.kind() {
            io::ErrorKind::ConnectionRefused => Self::Refused,
            io::ErrorKind::TimedOut => Self::Timeout,
            io::ErrorKind::NetworkUnreachable | io::ErrorKind::HostUnreachable | io::ErrorKind::NetworkDown => {
                Self::Unreachable
            }
            _ => Self::Other,
        }
    }
}

/// Ein gescheiterter Aufbau: die Art und die Meldung (englisch, als Einzelheit).
#[derive(Debug)]
pub(crate) struct Failed {
    pub(crate) trouble: Trouble,
    pub(crate) message: String,
}

/// Wie lange der Aufbau zu diesem Server dauern darf.
pub(crate) fn patience(profile: &Profile) -> Duration {
    profile
        .connect_timeout
        .map_or(CONNECT_TIMEOUT, |seconds| Duration::from_secs(u64::from(seconds)))
}

/// Die Adressen, die nach dem Profil in Frage kommen, in der Reihenfolge des Systems.
pub(crate) fn wanted(profile: &Profile, addresses: impl IntoIterator<Item = SocketAddr>) -> Vec<SocketAddr> {
    addresses
        .into_iter()
        .filter(|address| match profile.address_family.as_deref() {
            Some("ipv4") => address.is_ipv4(),
            Some("ipv6") => address.is_ipv6(),
            _ => true,
        })
        .collect()
}

/// Löst den Namen auf und lässt nur die Adressen übrig, die das Profil will. Ohne Frist – die setzt der Aufrufer.
pub(crate) async fn resolve(profile: &Profile) -> Result<Vec<SocketAddr>, Failed> {
    let resolved = lookup_host((profile.host.as_str(), profile.port))
        .await
        .map_err(|error| Failed {
            trouble: Trouble::Name,
            message: format!("could not resolve {}: {error}", profile.host),
        })?;
    let all: Vec<SocketAddr> = resolved.collect();
    let addresses = wanted(profile, all.iter().copied());
    if !addresses.is_empty() {
        return Ok(addresses);
    }

    Err(if all.is_empty() {
        Failed {
            trouble: Trouble::Name,
            message: format!("{} has no address", profile.host),
        }
    } else {
        Failed {
            trouble: Trouble::Family,
            message: format!(
                "{} has no {} address",
                profile.host,
                if profile.address_family.as_deref() == Some("ipv6") {
                    "IPv6"
                } else {
                    "IPv4"
                }
            ),
        }
    })
}

/// Verbindet der Reihe nach mit jeder Adresse, bis eine antwortet, und sagt, welche es war. Gemeldet wird der
/// Fehler der letzten. Ohne Frist – die setzt der Aufrufer.
pub(crate) async fn connect_any(addresses: &[SocketAddr]) -> Result<(TcpStream, SocketAddr), Failed> {
    let mut last = None;
    for address in addresses {
        match TcpStream::connect(address).await {
            Ok(stream) => return Ok((stream, *address)),
            Err(error) => last = Some(error),
        }
    }

    let error = last.unwrap_or_else(|| io::Error::other("no address"));
    Err(Failed {
        trouble: Trouble::of(&error),
        message: error.to_string(),
    })
}

/// Das Scheitern an der Frist.
pub(crate) fn timed_out() -> Failed {
    Failed {
        trouble: Trouble::Timeout,
        message: "connection attempt timed out".to_owned(),
    }
}

/// Löst den Namen auf und verbindet: der Reihe nach mit jeder passenden Adresse, bis eine antwortet oder die Zeit
/// des Profils um ist. Gemeldet wird der Fehler der letzten Adresse.
pub(crate) async fn tcp(profile: &Profile) -> Result<TcpStream, Failed> {
    let attempt = async {
        let addresses = resolve(profile).await?;
        connect_any(&addresses).await.map(|(stream, _)| stream)
    };

    timeout(patience(profile), attempt)
        .await
        .unwrap_or_else(|_| Err(timed_out()))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn addresses() -> Vec<SocketAddr> {
        vec!["[2001:db8::1]:22".parse().unwrap(), "192.0.2.7:22".parse().unwrap()]
    }

    #[test]
    fn the_profile_picks_the_address_family() {
        let mut profile = Profile::default();
        assert_eq!(wanted(&profile, addresses()).len(), 2);

        profile.address_family = Some("ipv4".to_owned());
        assert_eq!(
            wanted(&profile, addresses()),
            ["192.0.2.7:22".parse::<SocketAddr>().unwrap()]
        );

        profile.address_family = Some("ipv6".to_owned());
        assert_eq!(
            wanted(&profile, addresses()),
            ["[2001:db8::1]:22".parse::<SocketAddr>().unwrap()]
        );
    }

    #[test]
    fn the_profile_sets_how_long_to_wait() {
        let mut profile = Profile::default();
        assert_eq!(patience(&profile), CONNECT_TIMEOUT);
        profile.connect_timeout = Some(5);
        assert_eq!(patience(&profile), Duration::from_secs(5));
    }

    #[tokio::test]
    async fn a_port_nobody_listens_on_is_refused_and_a_missing_family_says_so() {
        // Ein Port, der gerade frei wurde: Dort lauscht niemand.
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let port = listener.local_addr().unwrap().port();
        drop(listener);

        let mut profile = Profile {
            host: "127.0.0.1".to_owned(),
            port,
            ..Profile::default()
        };
        let failed = tcp(&profile).await.unwrap_err();
        assert_eq!(failed.trouble, Trouble::Refused, "{}", failed.message);

        profile.address_family = Some("ipv6".to_owned());
        let failed = tcp(&profile).await.unwrap_err();
        assert_eq!(failed.trouble, Trouble::Family);
        assert_eq!(failed.message, "127.0.0.1 has no IPv6 address");
    }
}
