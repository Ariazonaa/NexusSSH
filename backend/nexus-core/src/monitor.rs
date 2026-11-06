//! Auslastung des Servers für den Server-Inspektor (Spec §6): CPU, Arbeitsspeicher, Datenträger, System.
//!
//! Auf der bestehenden Verbindung läuft dafür ein zweiter Kanal mit einer kleinen Shell-Schleife, die alle paar
//! Sekunden `/proc` ausliest – oder, wo es das nicht gibt (FreeBSD, OpenBSD), die Zähler von `sysctl`, `vmstat`
//! und `netstat`. Alles andere meldet sich als „nicht verfügbar“. Die Schleife läuft nur, solange die App die Werte
//! zeigt (`monitor.set`), und endet mit ihrem Kanal.

use std::convert::Infallible;
use std::time::Duration;

use russh::ChannelMsg;
use russh::client::{Handle, Handler};
use tokio::sync::watch;
use tokio::time::timeout;

use crate::core::Shared;
use crate::protocol::Event;

const OPEN_TIMEOUT: Duration = Duration::from_secs(10);

/// Was die App als Grund zeigt, wenn der Server keinen Kanal mehr hergibt. Sie erkennt es am Wort `MaxSessions`.
const NO_CHANNEL_TEXT: &str =
    "the server allows no further channel on this connection (MaxSessions in its sshd_config)";

/// Ohne eine Antwort in dieser Zeit gilt die Überwachung als gescheitert (die Schleife meldet sich alle 3 s).
const SILENCE_TIMEOUT: Duration = Duration::from_secs(20);

/// Bis zur ersten Messung darf es länger dauern: Unter Windows startet PowerShell und fragt CIM, bevor die erste
/// Messung kommt – am Gast 13 bis 32 Sekunden (2026-10-19); mit der gewöhnlichen Frist galt der Server als stumm.
const FIRST_ANSWER_TIMEOUT: Duration = Duration::from_secs(60);

/// So lange wartet die Messung, bevor sie wieder fragt, wie der Server Befehle liest – wenn die Probe dafür stumm
/// blieb ([`crate::session::Shell::Unknown`]).
const SHELL_RETRY: Duration = Duration::from_secs(10);

/// Mehr als das in einer Zeile kommt nicht von unserer Schleife.
const MAX_LINE: usize = 32 * 1024;

/// Texte des Servers werden für die Anzeige gekürzt.
const MAX_TEXT: usize = 80;

/// Die Schleife auf dem Server. Über `sh -c`, weil die Anmelde-Shell des Benutzers eine andere sein kann (fish,
/// csh – unter FreeBSD ist csh die Voreinstellung); innen stehen deshalb keine einfachen Anführungszeichen. Der erste
/// Abstand ist kurz, damit die CPU-Auslastung – der Unterschied zweier Messungen – schnell erscheint.
///
/// Ohne `/proc/stat` nimmt sie die Zähler der BSDs und gibt sie roh aus; der [`Parser`] kennt alle Formen:
/// `kern.cp_time` (FreeBSD user nice sys intr idle, OpenBSD mit Kommas und zusätzlich spin), die Speicherseiten
/// aus `vm.stats.vm` (FreeBSD) oder `vmstat -s` (OpenBSD, dort gibt es `vm.stats` nicht – und `sysctl` endet bei
/// einem unbekannten Namen trotzdem mit 0, deshalb der Blick auf die Ausgabe), die Laufzeit aus
/// `kern.boottime` (auf die Sekunde), `vm.loadavg` und `netstat -ibn`.
///
/// Einmal am Anfang (`@info`) fragt sie, was sich nicht ändert: den Prozessor und die Zahl der Kerne
/// (`/proc/cpuinfo`, sonst `hw.model` und `hw.ncpu`), Hersteller und Modell des
/// Rechners (DMI unter `/sys`, auf ARM `/proc/device-tree/model`; FreeBSD `kenv`, OpenBSD `hw.vendor`/`hw.product`)
/// und ob er virtuell ist (`systemd-detect-virt`, sonst das Merkmal `hypervisor` der CPU; FreeBSD
/// `kern.vm_guest`). Am Ende jeder Messung (`@time`) steht die Uhr des Servers – zuletzt, damit zwischen dem
/// Ablesen und dem Ankommen möglichst wenig liegt.
///
/// Die einzelnen Kerne stehen unter Linux in den Zeilen `cpu0`, `cpu1`, … von `/proc/stat`, unter FreeBSD in
/// `kern.cp_times` (dieselben fünf Zähler je Kern hintereinander). Die Liste aller Dateisysteme nennt `df` – mit
/// `-l` nur die dieses Rechners, wo `df` das kennt: Ein Netzlaufwerk, dessen Server nicht antwortet, hielte sonst
/// die ganze Schleife an. BusyBox kennt `-l` nicht und nennt alle. Die Inodes dazu (`df -Pi`) liest der
/// [`Parser`] in der Schreibweise von GNU und BusyBox und in der von FreeBSD, erkannt am Kopf; OpenBSD kennt
/// `-P` und `-i` nicht zusammen und nennt keine. Den Durchsatz der Datenträger (`@io`) nennt jedes System
/// anders – `/proc/diskstats`, `iostat -Ix` (FreeBSD), `iostat -DI` (OpenBSD, nur Lesen und Schreiben zusammen);
/// die Formen stehen bei [`Parser::io_line`]. Alle zählen seit dem Start, keine wartet.
pub(crate) const SCRIPT: &str = concat!(
    "sh -c 'export LC_ALL=C; echo @os; cat /etc/os-release 2>/dev/null; ",
    "[ -r /etc.defaults/VERSION ] && sed -n \"s/^productversion=.\\(.*\\).$/PRETTY_NAME=Synology DSM \\1/p\" /etc.defaults/VERSION; ",
    "echo @kernel; uname -sr; echo @info; if [ -r /proc/cpuinfo ]; then ",
    "echo cpu=$(grep -m 1 -E \"^(model name|Model|Hardware)\" /proc/cpuinfo | cut -d: -f2); ",
    "echo cores=$(grep -c \"^processor\" /proc/cpuinfo); ",
    "echo vendor=$(cat /sys/class/dmi/id/sys_vendor 2>/dev/null); ",
    "echo product=$(cat /sys/class/dmi/id/product_name 2>/dev/null || ",
    "tr -d \"\\000\" 2>/dev/null < /proc/device-tree/model); ",
    "echo virt=$(systemd-detect-virt 2>/dev/null || { grep -q \"^flags.* hypervisor\" /proc/cpuinfo && echo vm; }); ",
    "else echo cpu=$(sysctl -n hw.model 2>/dev/null); ",
    "echo cores=$(sysctl -n hw.ncpu 2>/dev/null); ",
    "echo vendor=$(kenv -q smbios.system.maker 2>/dev/null || sysctl -n hw.vendor 2>/dev/null); ",
    "echo product=$(kenv -q smbios.system.product 2>/dev/null || sysctl -n hw.product 2>/dev/null); ",
    "echo virt=$(sysctl -n kern.vm_guest 2>/dev/null); fi; ",
    "n=0; ",
    "while :; do echo @sample; ",
    "if [ -r /proc/stat ]; then grep \"^cpu\" /proc/stat; ",
    "grep -E \"^(MemTotal|MemAvailable|MemFree|Buffers|Cached|SReclaimable|SwapTotal|SwapFree):\" /proc/meminfo 2>/dev/null; ",
    "echo @uptime; cat /proc/uptime 2>/dev/null; ",
    "else echo cp_time $(sysctl -n kern.cp_time 2>/dev/null); ",
    "echo cp_times $(sysctl -n kern.cp_times 2>/dev/null); ",
    "sysctl hw.physmem hw.pagesize vm.stats.vm.v_free_count vm.stats.vm.v_inactive_count ",
    "vm.stats.vm.v_cache_count 2>/dev/null; ",
    "[ -n \"$(sysctl -n vm.stats.vm.v_free_count 2>/dev/null)\" ] || ",
    "vmstat -s 2>/dev/null | grep -E \"pages (free|inactive)$\"; ",
    "echo @uptime; b=$(sysctl -n kern.boottime 2>/dev/null | tr -dc 0-9, | cut -d, -f1); ",
    "[ -n \"$b\" ] && echo $(( $(date +%s) - b )); fi; ",
    "echo @disk; df -Pk / 2>/dev/null | tail -n 1; ",
    "echo @disks; df -Pkl 2>/dev/null || df -Pk 2>/dev/null; ",
    "echo @inodes; df -Pil 2>/dev/null || df -Pi 2>/dev/null; echo @load; ",
    "if [ -r /proc/loadavg ]; then cat /proc/loadavg; else sysctl -n vm.loadavg 2>/dev/null | tr -d {}; fi; ",
    "echo @net; if [ -r /proc/net/dev ]; then cat /proc/net/dev; else netstat -ibn 2>/dev/null; fi; echo @addr; ip -o addr show 2>/dev/null || ifconfig 2>/dev/null; echo @io; ",
    "if [ -r /proc/diskstats ]; then cat /proc/diskstats; ",
    "else iostat -Ix 2>/dev/null || iostat -DI 2>/dev/null; fi; echo @time; date \"+%s %z %Z\"; echo @end; ",
    "if [ $n -eq 0 ]; then n=1; sleep 1; else sleep 3; fi; done'"
);

/// Eine Messung.
#[derive(Clone, Debug, PartialEq, Eq)]
pub(crate) struct Stats {
    pub(crate) cpu: u8,
    pub(crate) memory_used: u64,
    pub(crate) memory_total: u64,
    /// Was der Speicher sonst noch hält: Zwischenspeicher, den das System bei Bedarf hergibt, und ganz Freies.
    /// `None`, wenn der Server es nicht aufteilt.
    pub(crate) memory_cache: Option<u64>,
    pub(crate) memory_free: Option<u64>,
    /// Auslagerung in Bytes; `swap_total` 0 heißt: keine, oder der Server nennt sie nicht.
    pub(crate) swap_used: u64,
    pub(crate) swap_total: u64,
    pub(crate) disk_used: u64,
    pub(crate) disk_total: u64,
    pub(crate) os: String,
    pub(crate) kernel: String,
    pub(crate) uptime: u64,
    /// Lastdurchschnitt über 1, 5 und 15 Minuten; leer, wenn der Server ihn nicht nennt.
    pub(crate) load: String,
    /// Empfangene und gesendete Bytes je Sekunde seit der vorigen Messung, ohne `lo`.
    pub(crate) net_rx: u64,
    pub(crate) net_tx: u64,
    /// Dasselbe je Netzwerkkarte, mit ihren Adressen; ohne `lo`.
    pub(crate) interfaces: Vec<Interface>,
    /// Durchsatz der Datenträger seit der vorigen Messung; `None`, wenn der Server ihn nicht nennt (nur Linux).
    pub(crate) io: Option<Io>,
    /// Auslastung jedes Kerns, 0–100; leer, wenn der Server sie nicht einzeln nennt.
    pub(crate) cores: Vec<u8>,
    /// Woraus die Auslastung besteht; `None`, wenn der Server es nicht aufteilt.
    pub(crate) split: Option<Split>,
    /// Die Dateisysteme des Servers.
    pub(crate) disks: Vec<Disk>,
    /// Was sich nicht ändert: Prozessor, Kerne, Modell, Virtualisierung.
    pub(crate) info: Info,
    /// Die Uhr des Servers bei dieser Messung; `None`, wenn er sie nicht nennt.
    pub(crate) clock: Option<Clock>,
}

/// Angaben zum Rechner, einmal am Anfang gelesen. Leer bzw. `None` heißt: Der Server nennt es nicht.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub(crate) struct Info {
    pub(crate) cpu: String,
    pub(crate) cores: Option<u32>,
    /// Hersteller und Modell in einem.
    pub(crate) machine: String,
    /// `none` für eigene Hardware, sonst die Art (`kvm`, `vmware`, `docker` …; `vm`, wenn nur bekannt ist, dass).
    pub(crate) virtualization: String,
}

/// Die Uhr des Servers: Sekunden seit 1970, Abstand zu UTC in Sekunden, Kürzel der Zeitzone.
#[derive(Clone, Debug, PartialEq, Eq)]
pub(crate) struct Clock {
    pub(crate) time: u64,
    pub(crate) utc_offset: i32,
    pub(crate) zone: String,
}

/// Was Hersteller in die Felder für Hersteller und Modell schreiben, wenn sie nichts hineinschreiben.
const NO_NAME: [&str; 9] = [
    "to be filled by o.e.m.",
    "default string",
    "system product name",
    "system manufacturer",
    "not specified",
    "not applicable",
    "unknown",
    "none",
    "o.e.m.",
];

/// Eine Angabe zum Rechner: als Text für die Anzeige, mit einfachen Leerzeichen, und leer, wenn sie nichts sagt.
fn info_text(value: &str) -> String {
    let text = display_text(&value.split_whitespace().collect::<Vec<_>>().join(" "));
    if NO_NAME.contains(&text.to_lowercase().as_str()) {
        String::new()
    } else {
        text
    }
}

/// „+0200“ oder „-0930“ als Sekunden; nur, was eine Zeitzone sein kann.
fn utc_offset(text: &str) -> Option<i32> {
    let (sign, digits) = match text.split_at_checked(1)? {
        ("+", digits) => (1, digits),
        ("-", digits) => (-1, digits),
        _ => return None,
    };
    if digits.len() != 4 || !digits.bytes().all(|byte| byte.is_ascii_digit()) {
        return None;
    }

    let (hours, minutes) = (digits[..2].parse::<i32>().ok()?, digits[2..].parse::<i32>().ok()?);
    (hours <= 14 && minutes < 60).then_some(sign * (hours * 3600 + minutes * 60))
}

/// Anteile an der ganzen CPU-Zeit seit der vorigen Messung, je 0–100. Warten auf E/A zählt bei `cpu` als untätig
/// und steht hier für sich; „gestohlen“ ist Zeit, die der Wirt einer virtuellen Maschine anderen gab.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub(crate) struct Split {
    pub(crate) user: u8,
    pub(crate) system: u8,
    pub(crate) iowait: u8,
    pub(crate) steal: u8,
}

/// Ein Dateisystem: wo es eingehängt ist, woher es kommt und wie voll es ist.
#[derive(Clone, Debug, PartialEq, Eq)]
pub(crate) struct Disk {
    pub(crate) mount: String,
    pub(crate) device: String,
    pub(crate) used: u64,
    pub(crate) total: u64,
    /// Belegte und alle Inodes; `None`, wenn der Server sie nicht nennt oder das Dateisystem keine feste Zahl hat.
    pub(crate) inodes: Option<(u64, u64)>,
}

/// Was die Datenträger zusammen leisten: gelesene und geschriebene Bytes je Sekunde über alle ganzen Platten, und
/// wie beschäftigt die vollste war (Anteil der Zeit mit E/A, 0–100).
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub(crate) struct Io {
    pub(crate) read: u64,
    pub(crate) write: u64,
    pub(crate) busy: u8,
    /// Der Server nennt Lesen und Schreiben nur zusammen (OpenBSD): Die Summe steht in `read`, `write` ist 0.
    pub(crate) mixed: bool,
    /// Dasselbe je Platte.
    pub(crate) disks: Vec<IoDisk>,
}

/// Eine Platte: gelesene und geschriebene Bytes je Sekunde und der Anteil der Zeit mit E/A, 0–100.
#[derive(Clone, Debug, PartialEq, Eq)]
pub(crate) struct IoDisk {
    pub(crate) name: String,
    pub(crate) read: u64,
    pub(crate) write: u64,
    pub(crate) busy: u8,
}

/// Eine Netzwerkkarte: empfangene und gesendete Bytes je Sekunde seit der vorigen Messung und ihre Adressen
/// (ohne die nur auf der Leitung gültigen, `fe80::`).
#[derive(Clone, Debug, PartialEq, Eq)]
pub(crate) struct Interface {
    pub(crate) name: String,
    pub(crate) rx: u64,
    pub(crate) tx: u64,
    pub(crate) addresses: Vec<String>,
}

/// So viele Netzwerkkarten und Adressen je Karte nimmt der Kern einem Server ab.
const MAX_INTERFACES: usize = 64;
const MAX_ADDRESSES: usize = 8;

/// Die Zähler einer Netzwerkkarte: Name, empfangene und gesendete Bytes.
type InterfaceCounters = (String, u64, u64);

/// Der Name einer Netzwerkkarte, wie die App ihn zeigt: kurz und aus Zeichen, die ein Name dort haben kann.
fn interface_name(name: &str) -> Option<String> {
    // „eth0@if5“ (in einem Container) heißt „eth0“.
    let name = name.trim_end_matches(':').split('@').next().unwrap_or_default();
    (!name.is_empty()
        && name.len() <= 32
        && name
            .chars()
            .all(|character| character.is_ascii_alphanumeric() || "._-".contains(character)))
    .then(|| name.to_owned())
}

/// Eine Adresse aus der Ausgabe des Servers – nur, was wirklich eine ist, und in der üblichen Schreibweise.
/// `None` auch für Adressen, die nur auf der Leitung gelten.
fn address(text: &str) -> Option<String> {
    // „10.0.0.5/24“ (ip), „addr:10.0.0.5“ (altes ifconfig), „fe80::1%em0“ (BSD).
    let text = text.strip_prefix("addr:").unwrap_or(text);
    let text = text.split(['/', '%']).next().unwrap_or_default();
    match text.parse::<std::net::IpAddr>().ok()? {
        std::net::IpAddr::V6(v6) if (v6.segments()[0] & 0xffc0) == 0xfe80 => None,
        address if address.is_loopback() || address.is_unspecified() => None,
        address => Some(address.to_string()),
    }
}

/// Mehr Kerne und Dateisysteme zeigt die App nicht – und mehr nimmt der Kern einem Server auch nicht ab.
const MAX_CORES: usize = 256;
const MAX_DISKS: usize = 64;
/// So viele Zeilen von `/proc/diskstats` liest der Kern je Messung (jede Partition hat eine).
const MAX_IO: usize = 512;
/// `/proc/diskstats` zählt in Sektoren zu 512 Bytes, gleich wie die Platte sie einteilt.
const SECTOR: u64 = 512;
/// So viele Platten nennt der Kern der App einzeln.
const MAX_IO_DISKS: usize = 32;

/// Ein Datenträger: Name, gelesene und geschriebene Bytes, Millisekunden mit E/A – alles seit dem Start.
type DiskCounters = (String, [u64; 3]);

/// Wie der Server die Zähler seiner Datenträger nennt ([`Parser::io_line`]).
#[derive(Clone, Debug, Default, PartialEq, Eq)]
enum IoForm {
    #[default]
    Linux,
    FreeBsd,
    /// Mit den Namen der Geräte aus der Zeile über dem Kopf.
    OpenBsd(Vec<String>),
}

/// Aus den Zeilen von `/proc/diskstats` die ganzen Platten: ohne Partitionen (sonst zählte jedes Byte doppelt),
/// ohne Abbilder und Speicherplatten, und ohne die Schichten darüber (`dm-…`, `md…` – LVM, Verschlüsselung und
/// RAID reichen jede Anfrage an eine Platte weiter, die selbst in der Liste steht). Unter den BSDs fallen so auch
/// die Laufwerke für CDs und Disketten und die Durchreichen von FreeBSD (`pass…`) heraus.
fn whole_disks(lines: &[DiskCounters]) -> Vec<DiskCounters> {
    const LAYERS: [&str; 9] = ["loop", "ram", "zram", "sr", "fd", "dm-", "md", "cd", "pass"];
    let is_part = |name: &str| {
        lines.iter().any(|(parent, _)| {
            name.strip_prefix(parent.as_str()).is_some_and(|rest| {
                // „sda1“ zu „sda“; endet die Platte auf eine Ziffer, steht ein `p` dazwischen („nvme0n1p1“).
                let rest = if parent.ends_with(|last: char| last.is_ascii_digit()) {
                    // Fest verlöteter Speicher (eMMC, etwa im Compute Module eines Raspberry Pi) nennt dazu
                    // seine Bereiche für den Start und den Schlüsselspeicher: „mmcblk0boot0“, „mmcblk0rpmb“.
                    if rest == "rpmb" {
                        return true;
                    }

                    rest.strip_prefix("boot")
                        .or_else(|| rest.strip_prefix('p'))
                        .unwrap_or("")
                } else {
                    rest
                };
                !rest.is_empty() && rest.bytes().all(|byte| byte.is_ascii_digit())
            })
        })
    };
    lines
        .iter()
        .filter(|(name, _)| !LAYERS.iter().any(|layer| name.starts_with(layer)) && !is_part(name))
        .cloned()
        .collect()
}

/// Die Zähler einer Zeile `cpu…` aus `/proc/stat`: user nice system idle iowait irq softirq steal.
#[derive(Clone, Copy, Default)]
struct Times {
    user: u64,
    system: u64,
    idle: u64,
    iowait: u64,
    steal: u64,
}

/// Zählt zusammen, ohne überzulaufen: Die Zahlen nennt der Server, und der kann nennen, was er will.
fn sum(values: impl IntoIterator<Item = u64>) -> u64 {
    values.into_iter().fold(0, u64::saturating_add)
}

impl Times {
    fn total(self) -> u64 {
        sum([self.user, self.system, self.idle, self.iowait, self.steal])
    }

    /// Beschäftigt in Prozent zwischen zwei Ständen; steht der Zähler, gilt der Kern als untätig.
    fn busy_since(self, before: Self) -> u8 {
        let total = self.total().saturating_sub(before.total());
        let idle = self
            .idle
            .saturating_add(self.iowait)
            .saturating_sub(before.idle.saturating_add(before.iowait));
        share(total.saturating_sub(idle), total)
    }
}

fn share(part: u64, total: u64) -> u8 {
    (part.saturating_mul(100)).checked_div(total).unwrap_or(0).min(100) as u8
}

/// Dateisysteme, die keinen Platz auf einer Platte belegen oder nur ein Abbild zeigen (`/dev/loop…`: die
/// Pakete von snap sind immer zu 100 % voll und würden nur falschen Alarm geben).
fn is_pseudo(device: &str) -> bool {
    const NAMES: [&str; 14] = [
        "tmpfs", "devtmpfs", "udev", "devfs", "none", "shm", "run", "efivarfs", "proc", "sysfs", "fdescfs", "procfs",
        "map", "cgroup",
    ];
    NAMES.contains(&device)
        || device.starts_with("/dev/loop")
        || device.starts_with("cgroup")
        || device.starts_with("map ")
}

#[derive(Debug, PartialEq, Eq)]
pub(crate) enum Outcome {
    /// Die Zeile hat keine Messung abgeschlossen.
    Nothing,
    /// Eine Messung ist vollständig.
    Stats(Box<Stats>),
    /// Der Server liefert die Werte nicht (kein `/proc`).
    Unavailable,
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum Section {
    None,
    Os,
    Kernel,
    Sample,
    Uptime,
    Disk,
    Disks,
    Inodes,
    Load,
    Net,
    Addr,
    Io,
    Info,
    Time,
}

/// Liest die Ausgabe der Schleife zeilenweise.
pub(crate) struct Parser {
    section: Section,
    os: String,
    os_name: String,
    kernel: String,
    /// Zähler der vorigen Messung: (gesamt, untätig).
    previous: Option<(u64, u64)>,
    current: Option<(u64, u64)>,
    /// Die Zähler aufgeteilt, und die jedes Kerns – jeweils vorige und laufende Messung.
    previous_times: Option<Times>,
    times: Option<Times>,
    previous_cores: Vec<Times>,
    cores: Vec<Times>,
    disks: Vec<Disk>,
    /// Die Liste der Inodes hat die Schreibweise, die der Parser kennt (Kopf gesehen).
    inodes_known: bool,
    /// Die Liste der Inodes hat die Schreibweise von FreeBSD: an die gewöhnliche Liste angehängt.
    inodes_bsd: bool,
    memory: [Option<u64>; 5],
    /// Linux, daneben: SReclaimable, SwapTotal, SwapFree.
    memory_more: [Option<u64>; 3],
    /// BSD: physischer Speicher und Seitengröße in Bytes, freie, inaktive und Cache-Seiten (`vm.stats.vm`, unter
    /// OpenBSD `vmstat -s` ohne Cache-Seiten).
    bsd_memory: [Option<u64>; 5],
    /// Laufzeit in Hundertstelsekunden – genau genug, um aus zwei Messungen eine Rate zu bilden.
    uptime: Option<u64>,
    disk: Option<(u64, u64)>,
    load: String,
    /// Zähler aller Netzwerkkarten außer `lo`: (empfangen, gesendet).
    net: Option<(u64, u64)>,
    /// Netzzähler und Laufzeit der vorigen Messung.
    previous_net: Option<(u64, u64, u64)>,
    /// `netstat -ibn` (BSD): Spalte der empfangenen und der gesendeten Bytes und wie viele Spalten der Kopf hat.
    netstat: Option<(usize, usize, usize)>,
    /// Die Zähler je Netzwerkkarte.
    net_each: Vec<InterfaceCounters>,
    /// Die der vorigen Messung und deren Laufzeit.
    previous_each: Option<(Vec<InterfaceCounters>, u64)>,
    /// Adressen je Karte, und die Karte, deren Zeilen gerade gelesen werden (`ifconfig`).
    addresses: Vec<(String, Vec<String>)>,
    address_of: Option<String>,
    /// `/proc/diskstats`: je Gerät gelesene und geschriebene Sektoren und die Millisekunden mit E/A.
    io: Vec<DiskCounters>,
    /// In welcher Form der Abschnitt `@io` gerade gelesen wird, und die Zeile davor (OpenBSD nennt die Namen dort).
    io_form: IoForm,
    io_last: String,
    /// Der Server nennt Lesen und Schreiben nur zusammen (OpenBSD).
    io_mixed: bool,
    /// Die ganzen Platten der vorigen Messung und deren Laufzeit.
    previous_io: Option<(Vec<DiskCounters>, u64)>,
    info: Info,
    /// Hersteller und Modell, wie der Server sie einzeln nennt.
    maker: (String, String),
    clock: Option<Clock>,
}

/// Was `sysctl` unter den BSDs für den Speicher nennt, in der Reihenfolge von [`Parser::bsd_memory`].
const BSD_MEMORY: [&str; 5] = [
    "hw.physmem",
    "hw.pagesize",
    "vm.stats.vm.v_free_count",
    "vm.stats.vm.v_inactive_count",
    "vm.stats.vm.v_cache_count",
];

/// Eine Netzwerkkarte, die nur den Rechner selbst verbindet: `lo` unter Linux, `lo0` unter BSD.
fn is_loopback(name: &str) -> bool {
    name.strip_prefix("lo")
        .is_some_and(|rest| rest.chars().all(|character| character.is_ascii_digit()))
}

impl Parser {
    pub(crate) fn new() -> Self {
        Self {
            section: Section::None,
            os: String::new(),
            os_name: String::new(),
            kernel: String::new(),
            previous: None,
            current: None,
            previous_times: None,
            times: None,
            previous_cores: Vec::new(),
            cores: Vec::new(),
            disks: Vec::new(),
            inodes_known: false,
            inodes_bsd: false,
            memory: [None; 5],
            memory_more: [None; 3],
            bsd_memory: [None; 5],
            uptime: None,
            disk: None,
            load: String::new(),
            net: None,
            previous_net: None,
            netstat: None,
            net_each: Vec::new(),
            previous_each: None,
            addresses: Vec::new(),
            address_of: None,
            io: Vec::new(),
            io_form: IoForm::Linux,
            io_last: String::new(),
            io_mixed: false,
            previous_io: None,
            info: Info::default(),
            maker: (String::new(), String::new()),
            clock: None,
        }
    }

    pub(crate) fn line(&mut self, line: &str) -> Outcome {
        let line = line.trim();
        match line {
            "@os" => self.section = Section::Os,
            "@kernel" => self.section = Section::Kernel,
            "@sample" => {
                self.section = Section::Sample;
                self.current = None;
                self.times = None;
                self.cores.clear();
                self.disks.clear();
                self.inodes_known = false;
                self.inodes_bsd = false;
                self.memory = [None; 5];
                self.memory_more = [None; 3];
                self.bsd_memory = [None; 5];
                self.uptime = None;
                self.disk = None;
                self.load.clear();
                self.net = None;
                self.netstat = None;
                self.net_each.clear();
                self.addresses.clear();
                self.address_of = None;
                self.io.clear();
                self.io_form = IoForm::Linux;
                self.io_last.clear();
                self.io_mixed = false;
                self.clock = None;
            }
            "@uptime" => self.section = Section::Uptime,
            "@disk" => self.section = Section::Disk,
            "@disks" => self.section = Section::Disks,
            "@inodes" => self.section = Section::Inodes,
            "@load" => self.section = Section::Load,
            "@net" => self.section = Section::Net,
            "@io" => self.section = Section::Io,
            "@addr" => self.section = Section::Addr,
            "@info" => self.section = Section::Info,
            "@time" => self.section = Section::Time,
            "@end" => {
                self.section = Section::None;
                return self.finish();
            }
            _ => self.value(line),
        }

        Outcome::Nothing
    }

    fn value(&mut self, line: &str) {
        match self.section {
            Section::Os => {
                if let Some(value) = line.strip_prefix("PRETTY_NAME=") {
                    self.os = display_text(value);
                } else if let Some(value) = line.strip_prefix("NAME=") {
                    self.os_name = display_text(value);
                }
            }
            Section::Kernel if self.kernel.is_empty() => self.kernel = display_text(line),
            Section::Info => {
                if let Some((key, value)) = line.split_once('=') {
                    match key {
                        "cpu" => self.info.cpu = info_text(value),
                        "cores" => {
                            self.info.cores = value.trim().parse().ok().filter(|count| (1..=4096).contains(count));
                        }
                        "vendor" => self.maker.0 = info_text(value),
                        "product" => self.maker.1 = info_text(value),
                        // Ein Wort aus Buchstaben, Ziffern und Bindestrichen (`kvm`, `vmware`, `systemd-nspawn`).
                        // `systemd-detect-virt` sagt „none“ – das ist eine Auskunft, kein leerer Wert.
                        "virt" => {
                            self.info.virtualization = value
                                .split_whitespace()
                                .next()
                                .filter(|word| {
                                    word.len() <= 24
                                        && word.bytes().all(|byte| byte.is_ascii_alphanumeric() || byte == b'-')
                                })
                                .unwrap_or_default()
                                .to_lowercase();
                        }
                        _ => {}
                    }

                    // Viele nennen den Hersteller im Modell noch einmal („Dell Inc.“ / „Dell PowerEdge R640“ nicht,
                    // aber „QEMU“ / „QEMU Standard PC“ schon).
                    let (vendor, product) = (&self.maker.0, &self.maker.1);
                    self.info.machine = if product.to_lowercase().starts_with(&vendor.to_lowercase()) {
                        product.clone()
                    } else {
                        format!("{vendor} {product}").trim().to_owned()
                    };
                }
            }
            Section::Time => {
                // „1791590000 +0200 CEST“; das Kürzel darf fehlen oder selbst eine Zahl sein (BusyBox ohne Zonendaten).
                let mut words = line.split_whitespace();
                if let (Some(time), Some(offset)) = (
                    words.next().and_then(|seconds| seconds.parse::<u64>().ok()),
                    words.next().and_then(utc_offset),
                ) {
                    let zone = words
                        .next()
                        .filter(|zone| zone.len() <= 8 && zone.bytes().all(|byte| byte.is_ascii_alphabetic()))
                        .unwrap_or_default()
                        .to_owned();
                    self.clock = Some(Clock {
                        time,
                        utc_offset: offset,
                        zone,
                    });
                }
            }
            Section::Sample => {
                let mut words = line.split_whitespace();
                match words.next() {
                    Some("cpu") => {
                        let counters: Vec<u64> = words.map_while(|word| word.parse().ok()).collect();
                        // user nice system idle iowait irq softirq steal – „untätig“ sind idle und iowait.
                        if counters.len() >= 4 {
                            let total = sum(counters.iter().take(8).copied());
                            let idle = counters[3].saturating_add(counters.get(4).copied().unwrap_or(0));
                            self.current = Some((total, idle));
                            self.times = Some(proc_times(&counters));
                        }
                    }
                    // „cpu0 …“, „cpu1 …“: dieselben Zähler je Kern.
                    Some(name)
                        if name.len() > 3
                            && name.starts_with("cpu")
                            && name[3..].bytes().all(|byte| byte.is_ascii_digit()) =>
                    {
                        let counters: Vec<u64> = words.map_while(|word| word.parse().ok()).collect();
                        if counters.len() >= 4 && self.cores.len() < MAX_CORES {
                            self.cores.push(proc_times(&counters));
                        }
                    }
                    // FreeBSD: die fünf Zähler von `kern.cp_time` für jeden Kern hintereinander.
                    Some("cp_times") => {
                        let counters: Vec<u64> = words.map_while(|word| word.parse().ok()).collect();
                        if counters.len().is_multiple_of(5) {
                            self.cores = counters.chunks(5).take(MAX_CORES).map(bsd_times).collect();
                        }
                    }
                    // FreeBSD: user nice sys intr idle (OpenBSD trennt mit Kommas und hat sechs) – untätig ist der
                    // letzte.
                    Some("cp_time") => {
                        let counters: Vec<u64> = words
                            .flat_map(|word| word.split(','))
                            .map_while(|word| word.parse().ok())
                            .collect();
                        if let [.., idle] = counters.as_slice()
                            && counters.len() >= 4
                        {
                            self.current = Some((sum(counters.iter().copied()), *idle));
                            self.times = Some(bsd_times(&counters));
                        }
                    }
                    // „hw.physmem: 1037541376“ unter FreeBSD, „hw.physmem=1056821248“ unter OpenBSD.
                    Some(name) if name.starts_with("hw.") || name.starts_with("vm.") => {
                        if let Some((name, value)) = line.split_once([':', '='])
                            && let Ok(value) = value.trim().parse::<u64>()
                            && let Some(slot) = BSD_MEMORY.iter().position(|known| *known == name.trim())
                        {
                            self.bsd_memory[slot] = Some(value);
                        }
                    }
                    // OpenBSD, `vmstat -s`: „10843 pages free“, „25207 pages inactive“.
                    Some(count) if count.bytes().all(|byte| byte.is_ascii_digit()) => {
                        let slot = match (words.next(), words.next(), words.next()) {
                            (Some("pages"), Some("free"), None) => Some(2),
                            (Some("pages"), Some("inactive"), None) => Some(3),
                            _ => None,
                        };
                        if let (Some(slot), Ok(pages)) = (slot, count.parse::<u64>()) {
                            self.bsd_memory[slot] = Some(pages);
                        }
                    }
                    Some(name) => {
                        let slot = ["MemTotal:", "MemAvailable:", "MemFree:", "Buffers:", "Cached:"]
                            .iter()
                            .position(|known| *known == name);
                        let more = ["SReclaimable:", "SwapTotal:", "SwapFree:"]
                            .iter()
                            .position(|known| *known == name);
                        if let Some(kilobytes) = words.next().and_then(|word| word.parse::<u64>().ok()) {
                            if let Some(slot) = slot {
                                self.memory[slot] = Some(kilobytes.saturating_mul(1024));
                            } else if let Some(slot) = more {
                                self.memory_more[slot] = Some(kilobytes.saturating_mul(1024));
                            }
                        }
                    }
                    None => {}
                }
            }
            Section::Uptime => {
                // „2764800.55 1000.00“: Sekunden mit zwei Nachkommastellen.
                let mut parts = line.split(' ').next().unwrap_or_default().split('.');
                let seconds = parts.next().and_then(|seconds| seconds.parse::<u64>().ok());
                let hundredths = parts
                    .next()
                    .and_then(|fraction| fraction.get(..2))
                    .and_then(|fraction| fraction.parse::<u64>().ok())
                    .unwrap_or(0);
                self.uptime = seconds.map(|seconds| seconds.saturating_mul(100).saturating_add(hundredths));
            }
            Section::Load if self.load.is_empty() => {
                // „0.06 0.02 0.00 1/171 2874409“ – nur die drei Durchschnitte, und nur, wenn es Zahlen sind.
                let values: Vec<&str> = line.split_whitespace().take(3).collect();
                if values.len() == 3 && values.iter().all(|value| value.parse::<f32>().is_ok()) {
                    self.load = values.join(" ");
                }
            }
            Section::Net if self.netstat.is_some() || line.split_whitespace().any(|word| word == "Ibytes") => {
                self.netstat_line(line);
            }
            Section::Net => {
                // „  eth0: 12345 100 0 0 0 0 0 0 67890 …“: Feld 1 sind empfangene, Feld 9 gesendete Bytes.
                if let Some((name, counters)) = line.split_once(':')
                    && !is_loopback(name.trim())
                {
                    let counters: Vec<u64> = counters
                        .split_whitespace()
                        .map_while(|word| word.parse().ok())
                        .collect();
                    if counters.len() >= 9 {
                        let (received, sent) = self.net.unwrap_or_default();
                        self.net = Some((received.saturating_add(counters[0]), sent.saturating_add(counters[8])));
                        self.count_interface(name, counters[0], counters[8]);
                    }
                }
            }
            Section::Addr => self.address_line(line),
            Section::Io => self.io_line(line),
            Section::Disks => {
                if let Some((device, [total, used, available], mount)) = df_line(line)
                    && total > 0
                    && !is_pseudo(&device)
                    && self.disks.len() < MAX_DISKS
                {
                    // Dasselbe Dateisystem mehrfach eingehängt (Teilbäume von btrfs, Bind-Mounts): einmal zeigen,
                    // am kürzesten Pfad. Dasselbe gilt für eine Schicht darüber (`overlay` von Docker – auf einem
                    // Wirt stehen davon viele, alle mit den Zahlen der Platte darunter): Was kein Gerät nennt und
                    // dieselben Zahlen hat wie ein anderer Eintrag, ist kein eigener Platz.
                    let (used, total) = (
                        used.saturating_mul(1024),
                        used.saturating_add(available).saturating_mul(1024),
                    );
                    let real = |device: &str| device.starts_with('/');
                    match self.disks.iter_mut().find(|disk| {
                        disk.total == total
                            && disk.used == used
                            && (disk.device == device || !real(&disk.device) || !real(&device))
                    }) {
                        Some(same) => {
                            if mount.len() < same.mount.len() {
                                same.mount = mount;
                            }

                            if real(&device) && !real(&same.device) {
                                same.device = device;
                            }
                        }
                        None => self.disks.push(Disk {
                            mount,
                            device,
                            used,
                            total,
                            inodes: None,
                        }),
                    }
                }
            }
            Section::Inodes => {
                // Zwei Schreibweisen, erkannt am Kopf: GNU und BusyBox („Filesystem Inodes IUsed IFree IUse% Mounted
                // on“) und FreeBSD, das belegte und freie Inodes an die gewöhnliche Liste anhängt („… Capacity iused
                // ifree %iused Mounted on“). Alles andere bleibt aus.
                if self.inodes_bsd {
                    if let Some((used, total, mount)) = bsd_inode_line(line)
                        && let Some(disk) = self.disks.iter_mut().find(|disk| disk.mount == mount)
                    {
                        disk.inodes = Some((used, total));
                    }
                } else if !self.inodes_known {
                    let mut words = line.split_whitespace();
                    self.inodes_known = words.nth(1) == Some("Inodes");
                    self.inodes_bsd = !self.inodes_known && words.any(|word| word == "iused");
                } else if let Some((_, [total, used, _], mount)) = df_line(line)
                    && total > 0
                    && let Some(disk) = self.disks.iter_mut().find(|disk| disk.mount == mount)
                {
                    disk.inodes = Some((used.min(total), total));
                }
            }
            Section::Disk => {
                // Dateisystem  1024-Blöcke  Benutzt  Verfügbar  Kapazität  Eingehängt
                let fields: Vec<&str> = line.split_whitespace().collect();
                if fields.len() >= 6
                    && let (Ok(used), Ok(available)) = (fields[2].parse::<u64>(), fields[3].parse::<u64>())
                {
                    // Wie df: Der Anteil bezieht sich auf das, was Benutzern zur Verfügung steht.
                    self.disk = Some((
                        used.saturating_mul(1024),
                        used.saturating_add(available).saturating_mul(1024),
                    ));
                }
            }
            _ => {}
        }
    }

    /// Eine Zeile von `netstat -ibn` (BSD): erst der Kopf, der sagt, in welcher Spalte die Bytes stehen, dann je
    /// Karte und Adresse eine Zeile. Gezählt wird nur die mit `<Link#…>` – die anderen nennen dieselben Bytes noch
    /// einmal. Fehlt einer Zeile eine Spalte (eine Karte ohne Hardware-Adresse), fehlt sie vor den Bytes.
    fn netstat_line(&mut self, line: &str) {
        let fields: Vec<&str> = line.split_whitespace().collect();
        let Some((received, sent, columns)) = self.netstat else {
            let column = |name| fields.iter().position(|field| *field == name);
            if let (Some(received), Some(sent)) = (column("Ibytes"), column("Obytes")) {
                self.netstat = Some((received, sent, fields.len()));
            }
            return;
        };

        let name = fields
            .first()
            .map(|name| name.trim_end_matches('*'))
            .unwrap_or_default();
        if fields.len() < 3 || fields.len() > columns || !fields[2].starts_with("<Link") || is_loopback(name) {
            return;
        }

        let missing = columns - fields.len();
        let count = |column: usize| {
            column
                .checked_sub(missing)
                .and_then(|column| fields.get(column))
                .and_then(|value| value.parse::<u64>().ok())
        };
        if let (Some(in_bytes), Some(out_bytes)) = (count(received), count(sent)) {
            let (old_in, old_out) = self.net.unwrap_or_default();
            self.net = Some((old_in.saturating_add(in_bytes), old_out.saturating_add(out_bytes)));
            self.count_interface(name, in_bytes, out_bytes);
        }
    }

    fn count_interface(&mut self, name: &str, received: u64, sent: u64) {
        if let Some(name) = interface_name(name.trim())
            && self.net_each.len() < MAX_INTERFACES
            && !self.net_each.iter().any(|(known, ..)| *known == name)
        {
            self.net_each.push((name, received, sent));
        }
    }

    /// Eine Zeile mit Adressen. `ip -o addr` nennt Karte und Adresse in einer Zeile („2: eth0 inet 10.0.0.5/24 …“);
    /// `ifconfig` nennt erst die Karte („em0: flags=…“, älter „eth0 Link encap:…“) und darunter ihre Adressen.
    /// Eine Zeile des Abschnitts `@io`. Jedes System nennt die Zähler seiner Datenträger anders; alle drei Formen
    /// enden als Bytes und Millisekunden seit dem Start:
    ///
    /// - Linux, `/proc/diskstats`: „   8  0 sda 1200 30 96000 400 800 20 64000 900 0 700 1300“ – nach dem Namen an
    ///   dritter Stelle gelesene, an siebter geschriebene Sektoren, an zehnter die Millisekunden mit E/A.
    /// - FreeBSD, `iostat -Ix`: nach dem Kopf „device r/i w/i kr/i kw/i qlen tsvc_t/i sb/i“ je Gerät gelesene und
    ///   geschriebene KiB und die Sekunden mit E/A.
    /// - OpenBSD, `iostat -DI`: eine Zeile mit den Namen, eine mit „KB xfr time“ je Gerät, eine mit den Werten –
    ///   KiB nur als Summe aus Lesen und Schreiben ([`Io::mixed`]).
    fn io_line(&mut self, line: &str) {
        let words: Vec<&str> = line.split_whitespace().collect();
        let previous = std::mem::replace(&mut self.io_last, line.chars().take(1024).collect());
        let number = |word: &str| {
            word.parse::<f64>()
                .ok()
                .filter(|value| value.is_finite() && *value >= 0.0)
        };
        // Gleitkommazahlen des Servers als ganze Zahl, gesättigt (`as` läuft nicht über).
        let scaled = |value: f64, factor: f64| (value * factor) as u64;

        if words.first() == Some(&"device") && words.contains(&"kr/i") {
            self.io_form = IoForm::FreeBsd;
        } else if !words.is_empty() && words.chunks(3).all(|group| group == ["KB", "xfr", "time"]) {
            self.io_form = IoForm::OpenBsd(previous.split_whitespace().map(str::to_owned).collect());
        } else if self.io_form == IoForm::FreeBsd {
            if let [name, _, _, read, written, _, _, busy] = words.as_slice()
                && let (Some(read), Some(written), Some(busy)) = (number(read), number(written), number(busy))
            {
                self.count_disk(
                    name,
                    [scaled(read, 1024.0), scaled(written, 1024.0), scaled(busy, 1000.0)],
                );
            }
        } else if let IoForm::OpenBsd(names) = std::mem::take(&mut self.io_form) {
            if words.len() == names.len() * 3 {
                self.io_mixed = true;
                for (name, group) in names.iter().zip(words.chunks(3)) {
                    if let (Some(both), Some(busy)) = (number(group[0]), number(group[2])) {
                        self.count_disk(name, [scaled(both, 1024.0), 0, scaled(busy, 1000.0)]);
                    }
                }
            }
        } else if let [_, _, name, counters @ ..] = words.as_slice() {
            let counters: Vec<u64> = counters.iter().map_while(|word| word.parse().ok()).collect();
            if counters.len() >= 10 {
                self.count_disk(
                    name,
                    [
                        counters[2].saturating_mul(SECTOR),
                        counters[6].saturating_mul(SECTOR),
                        counters[9],
                    ],
                );
            }
        }
    }

    fn count_disk(&mut self, name: &str, counters: [u64; 3]) {
        if self.io.len() < MAX_IO && name.len() <= 32 {
            self.io.push((name.to_owned(), counters));
        }
    }

    fn address_line(&mut self, line: &str) {
        let words: Vec<&str> = line.split_whitespace().collect();
        let (name, found) = match words.as_slice() {
            [number, name, "inet" | "inet6", found, ..] if number.ends_with(':') => (interface_name(name), *found),
            ["inet" | "inet6", "addr:", found, ..] | ["inet" | "inet6", found, ..] => (self.address_of.clone(), *found),
            [name, rest, ..] if rest.starts_with("flags=") || (*rest == "Link" && line.contains("encap:")) => {
                self.address_of = interface_name(name);
                return;
            }
            _ => return,
        };
        let (Some(name), Some(found)) = (name, address(found)) else {
            return;
        };
        if is_loopback(&name) {
            return;
        }

        match self.addresses.iter().position(|(known, _)| *known == name) {
            Some(index) => {
                let list = &mut self.addresses[index].1;
                if list.len() < MAX_ADDRESSES && !list.contains(&found) {
                    list.push(found);
                }
            }
            None if self.addresses.len() < MAX_INTERFACES => self.addresses.push((name, vec![found])),
            None => {}
        }
    }

    /// Die Karten dieser Messung mit ihrer Rate seit der vorigen und ihren Adressen. Eine Karte, die es davor nicht
    /// gab, hat noch keine Rate; eine, die nur Adressen hat (kein Zähler gelesen), steht mit null da.
    fn interfaces(&mut self) -> Vec<Interface> {
        let before = self.previous_each.take();
        let mut list: Vec<Interface> = self
            .net_each
            .iter()
            .map(|(name, received, sent)| {
                let (rx, tx) = match (&before, self.uptime) {
                    (Some((old, then)), Some(now)) if now > *then => old
                        .iter()
                        .find(|(known, ..)| known == name)
                        .map(|(_, old_received, old_sent)| {
                            let rate = |new: u64, old: u64| new.saturating_sub(old).saturating_mul(100) / (now - then);
                            (rate(*received, *old_received), rate(*sent, *old_sent))
                        })
                        .unwrap_or_default(),
                    _ => (0, 0),
                };
                Interface {
                    name: name.clone(),
                    rx,
                    tx,
                    addresses: Vec::new(),
                }
            })
            .collect();
        for (name, addresses) in &self.addresses {
            match list.iter().position(|interface| interface.name == *name) {
                Some(index) => list[index].addresses = addresses.clone(),
                None if list.len() < MAX_INTERFACES => list.push(Interface {
                    name: name.clone(),
                    rx: 0,
                    tx: 0,
                    addresses: addresses.clone(),
                }),
                None => {}
            }
        }

        self.previous_each = self.uptime.map(|now| (self.net_each.clone(), now));

        // Kennt der Server Adressen, zählen nur Karten, die eine haben: Auf einem Wirt von Containern stehen sonst
        // Dutzende Enden virtueller Leitungen (`veth…`) in der Liste, die niemand ansehen will.
        if list.iter().any(|interface| !interface.addresses.is_empty()) {
            list.retain(|interface| !interface.addresses.is_empty());
        }

        list
    }

    /// Gesamter und verfügbarer Speicher in Bytes – aus `/proc/meminfo` oder aus den Seiten der BSDs.
    fn memory(&self) -> Option<(u64, u64)> {
        if let [Some(total), available, free, buffers, cached] = self.memory {
            // Kernel vor 3.14 kennen MemAvailable nicht.
            let available =
                available.unwrap_or_else(|| sum([free.unwrap_or(0), buffers.unwrap_or(0), cached.unwrap_or(0)]));
            return Some((total, available.min(total)));
        }

        // Verfügbar ist, was frei ist oder nur noch Zwischenspeicher – wie es auch top unter FreeBSD rechnet. Unter
        // OpenBSD zählt der Puffer-Cache dagegen als belegt (`vmstat -s` nennt ihn nicht).
        let [Some(physical), Some(page), Some(free), Some(inactive), cache] = self.bsd_memory else {
            return None;
        };
        let pages = free.saturating_add(inactive).saturating_add(cache.unwrap_or(0));
        Some((physical, pages.saturating_mul(page).min(physical)))
    }

    /// Zwischenspeicher und Freies in Bytes, wenn der Server es aufteilt – nie mehr als der ganze Speicher.
    fn memory_parts(&self, total: u64) -> (Option<u64>, Option<u64>) {
        if let [Some(_), _, Some(free), buffers, cached] = self.memory {
            // Wie `free`: Puffer, Seiten-Cache und der Teil des Kern-Speichers, der sich zurückholen lässt.
            let cache = sum([
                buffers.unwrap_or(0),
                cached.unwrap_or(0),
                self.memory_more[0].unwrap_or(0),
            ]);
            return (Some(cache.min(total)), Some(free.min(total)));
        }

        // Die BSDs nennen Seiten: frei ist frei, „inaktiv“ (und Cache) gibt das System her.
        if let [_, Some(page), Some(free), Some(inactive), cache] = self.bsd_memory {
            let held = inactive.saturating_add(cache.unwrap_or(0)).saturating_mul(page);
            return (Some(held.min(total)), Some(free.saturating_mul(page).min(total)));
        }

        (None, None)
    }

    /// Belegte und gesamte Auslagerung in Bytes; (0, 0), wenn der Server keine nennt.
    fn swap(&self) -> (u64, u64) {
        match (self.memory_more[1], self.memory_more[2]) {
            (Some(total), Some(free)) => (total.saturating_sub(free), total),
            _ => (0, 0),
        }
    }

    fn finish(&mut self) -> Outcome {
        let Some((memory_total, memory_available)) = self.memory() else {
            return Outcome::Unavailable;
        };
        let (memory_cache, memory_free) = self.memory_parts(memory_total);
        let (swap_used, swap_total) = self.swap();
        let Some(current) = self.current else {
            return Outcome::Unavailable;
        };

        // Die Netzrate ist wie die CPU-Auslastung ein Unterschied zweier Messungen, geteilt durch die Zeit dazwischen.
        let (net_rx, net_tx) = match (self.net, self.uptime, self.previous_net) {
            (Some((received, sent)), Some(now), Some((old_received, old_sent, then))) if now > then => {
                let rate = |new: u64, old: u64| new.saturating_sub(old).saturating_mul(100) / (now - then);
                (rate(received, old_received), rate(sent, old_sent))
            }
            _ => (0, 0),
        };
        self.previous_net = match (self.net, self.uptime) {
            (Some((received, sent)), Some(now)) => Some((received, sent, now)),
            _ => None,
        };

        let interfaces = self.interfaces();

        // Ebenso der Durchsatz der Datenträger: nur ganze Platten, die es in beiden Messungen gab.
        let whole = whole_disks(&self.io);
        let io = match (self.uptime, &self.previous_io) {
            (Some(now), Some((before, then))) if now > *then && !whole.is_empty() => {
                let elapsed = now - then;
                let mut io = Io {
                    mixed: self.io_mixed,
                    ..Io::default()
                };
                for (name, [read, written, busy]) in &whole {
                    if let Some((_, [old_read, old_written, old_busy])) = before.iter().find(|(old, _)| old == name) {
                        let rate = |new: u64, old: u64| new.saturating_sub(old).saturating_mul(100) / elapsed;
                        let (read, written) = (rate(*read, *old_read), rate(*written, *old_written));
                        io.read = io.read.saturating_add(read);
                        io.write = io.write.saturating_add(written);
                        // Millisekunden mit E/A je vergangene Zeit; die vollste Platte zählt.
                        let busy = (busy.saturating_sub(*old_busy).saturating_mul(10) / elapsed).min(100) as u8;
                        io.busy = io.busy.max(busy);
                        // Der Name ist Text des Servers: nur, was der Name eines Geräts sein kann.
                        if let Some(name) = interface_name(name)
                            && io.disks.len() < MAX_IO_DISKS
                        {
                            io.disks.push(IoDisk {
                                name,
                                read,
                                write: written,
                                busy,
                            });
                        }
                    }
                }
                Some(io)
            }
            _ => None,
        };
        self.previous_io = self.uptime.filter(|_| !whole.is_empty()).map(|now| (whole, now));

        let previous = self.previous.replace(current);
        let Some((previous_total, previous_idle)) = previous else {
            // Die erste Messung hat noch keinen Vergleichswert – auch nicht für die Kerne und die Aufteilung.
            self.previous_cores = self.cores.clone();
            self.previous_times = self.times;
            return Outcome::Nothing;
        };

        let total = current.0.saturating_sub(previous_total);
        let idle = current.1.saturating_sub(previous_idle);
        // Steht der Zähler (zwei Messungen im selben Augenblick), gilt die CPU als untätig.
        let cpu = (total.saturating_sub(idle).saturating_mul(100))
            .checked_div(total)
            .unwrap_or(0)
            .min(100) as u8;

        // Kerne und Aufteilung sind wie die Auslastung Unterschiede zweier Messungen. Ändert sich die Zahl der
        // Kerne (einer wurde abgeschaltet), gibt es für diese Messung keine.
        let cores = if self.cores.len() == self.previous_cores.len() {
            self.cores
                .iter()
                .zip(&self.previous_cores)
                .map(|(now, before)| now.busy_since(*before))
                .collect()
        } else {
            Vec::new()
        };
        self.previous_cores = self.cores.clone();
        let split = match (self.times, self.previous_times.take()) {
            (Some(now), Some(before)) => {
                let total = now.total().saturating_sub(before.total());
                let part = |now: u64, before: u64| share(now.saturating_sub(before), total);
                Some(Split {
                    user: part(now.user, before.user),
                    system: part(now.system, before.system),
                    iowait: part(now.iowait, before.iowait),
                    steal: part(now.steal, before.steal),
                })
            }
            _ => None,
        };
        self.previous_times = self.times;

        let (disk_used, disk_total) = self.disk.unwrap_or_default();

        Outcome::Stats(Box::new(Stats {
            cpu,
            memory_used: memory_total.saturating_sub(memory_available),
            memory_total,
            memory_cache,
            memory_free,
            swap_used,
            swap_total,
            disk_used,
            disk_total,
            os: if !self.os.is_empty() {
                self.os.clone()
            } else if !self.os_name.is_empty() {
                self.os_name.clone()
            } else {
                self.kernel.split(' ').next().unwrap_or_default().to_owned()
            },
            kernel: self.kernel.clone(),
            uptime: self.uptime.unwrap_or(0) / 100,
            load: self.load.clone(),
            net_rx,
            net_tx,
            interfaces,
            io,
            cores,
            split,
            disks: self.disks.clone(),
            info: self.info.clone(),
            clock: self.clock.clone(),
        }))
    }
}

/// user nice system idle iowait irq softirq steal – „nice“ ist Zeit des Benutzers, Unterbrechungen sind die des
/// Systems.
fn proc_times(counters: &[u64]) -> Times {
    let at = |index: usize| counters.get(index).copied().unwrap_or(0);
    Times {
        user: at(0).saturating_add(at(1)),
        system: sum([at(2), at(5), at(6)]),
        idle: at(3),
        iowait: at(4),
        steal: at(7),
    }
}

/// FreeBSD: user nice sys intr idle; OpenBSD: user nice sys spin intr idle – untätig ist der letzte, die ersten
/// beiden gehören dem Benutzer, alles dazwischen dem System.
fn bsd_times(counters: &[u64]) -> Times {
    let Some((idle, busy)) = counters.split_last() else {
        return Times::default();
    };
    Times {
        user: sum(busy.iter().take(2).copied()),
        system: sum(busy.iter().skip(2).copied()),
        idle: *idle,
        iowait: 0,
        steal: 0,
    }
}

/// Eine Zeile von `df -i` unter FreeBSD: wie [`df_line`], aber zwischen Anteil und Einhängepunkt stehen belegte
/// und freie Inodes und deren Anteil (`-`, wenn das Dateisystem keine zählt). Liefert belegte und alle Inodes.
fn bsd_inode_line(line: &str) -> Option<(u64, u64, String)> {
    let fields: Vec<&str> = line.split_whitespace().collect();
    let at = (4..fields.len().saturating_sub(4)).find(|index| {
        fields[*index].ends_with('%')
            && fields[index - 3..*index]
                .iter()
                .all(|field| field.parse::<u64>().is_ok())
    })?;
    let (used, free) = (fields[at + 1].parse::<u64>().ok()?, fields[at + 2].parse::<u64>().ok()?);
    let total = used.checked_add(free).filter(|total| *total > 0)?;
    Some((used, total, display_text(&fields[at + 4..].join(" "))))
}

/// Eine Zeile von `df -P`: Gerät, drei Zahlen, ein Anteil mit `%`, dann der Einhängepunkt. Gelesen wird vom
/// Anteil aus – Gerät und Einhängepunkt können Leerzeichen enthalten. Der Kopf hat keine Zahlen und fällt durch.
fn df_line(line: &str) -> Option<(String, [u64; 3], String)> {
    let fields: Vec<&str> = line.split_whitespace().collect();
    let at = (4..fields.len().saturating_sub(1)).find(|index| {
        (fields[*index].ends_with('%') || fields[*index] == "-")
            && fields[index - 3..*index]
                .iter()
                .all(|field| field.parse::<u64>().is_ok())
    })?;
    let number = |index: usize| fields[index].parse::<u64>().ok();
    let device = display_text(&fields[..at - 3].join(" "));
    let mount = display_text(&fields[at + 1..].join(" "));
    // Ein Pfad – oder unter Windows ein Laufwerk (`C:\`, [`crate::winserver::monitor`]).
    let drive = mount.len() == 3 && mount.as_bytes()[0].is_ascii_alphabetic() && mount.ends_with(":\\");
    (!device.is_empty() && (mount.starts_with('/') || drive)).then_some(())?;
    Some((device, [number(at - 3)?, number(at - 2)?, number(at - 1)?], mount))
}

/// Text des Servers für die Anzeige: ohne Anführungs- und Steuerzeichen, gekürzt.
fn display_text(value: &str) -> String {
    value
        .trim()
        .trim_matches(['"', '\''])
        .chars()
        .filter(|character| !character.is_control())
        .take(MAX_TEXT)
        .collect()
}

enum End {
    /// Die App hat die Überwachung abgeschaltet.
    Stopped,
    /// Der Server liefert keine Werte. `silent`: Er blieb stumm oder die Schleife endete (ein hängendes
    /// Dateisystem, ein beendeter Befehl) – anders als ein Server, der die Werte gar nicht kennt oder keinen
    /// Befehl zulässt.
    Unavailable { message: String, silent: bool },
    /// Windows: Das Konto darf kein CIM ([`crate::winserver::NO_CIM`]) – gleich weiter mit der Fassung ohne.
    NoCim,
    /// Ein Befehl brauchte den Kanal ([`crate::session::Link::making_room`]) – weiter, sobald er fertig ist.
    MadeRoom,
}

/// Pausen vor einem neuen Versuch, wenn ein Server, der in dieser Sitzung schon Werte geliefert hat, verstummt.
/// Jeder Versuch startet eine weitere Schleife auf einem Server, dem es gerade schlecht geht (die alte kann an
/// einem hängenden `df` stehen bleiben) – deshalb wachsende Pausen und nach dem letzten Schluss, bis die App aus-
/// und wieder einschaltet.
const RETRY_PAUSES: [Duration; 6] = [
    Duration::from_secs(30),
    Duration::from_secs(60),
    Duration::from_secs(120),
    Duration::from_secs(240),
    Duration::from_secs(480),
    Duration::from_secs(600),
];

/// Wie lange bis zum nächsten Versuch – `None`: keiner von selbst. Nur ein Server, der schon Werte geliefert hat
/// und dann verstummte, bekommt einen; einer, der nie welche hatte, liefert sie auch beim nächsten Mal nicht.
fn retry_pause(delivered: bool, silent: bool, failures: usize) -> Option<Duration> {
    (delivered && silent)
        .then(|| RETRY_PAUSES.get(failures).copied())
        .flatten()
}

/// Überwacht den Server, solange `enabled` es verlangt. Kehrt nie zurück; endet mit der Sitzung.
pub(crate) async fn run<H: Handler>(
    shared: &Shared,
    session: u64,
    handle: &Handle<H>,
    link: &crate::session::Link,
    mut enabled: watch::Receiver<bool>,
) -> Infallible {
    // Ob der Server in dieser Sitzung schon Werte geliefert hat, und wie viele Versuche seitdem stumm blieben.
    let mut delivered = false;
    let mut failures = 0;
    // Windows: ob das Konto ohne CIM gemessen werden muss (einmal festgestellt, gilt es für die Sitzung).
    let mut plain = false;
    let mut held = link.held();
    loop {
        while !*enabled.borrow_and_update() {
            if enabled.changed().await.is_err() {
                std::future::pending::<()>().await;
            }
        }
        // Wartet ein Befehl auf einen Kanal, bekommt er den Vortritt.
        if held.wait_for(|waiting| *waiting == 0).await.is_err() {
            std::future::pending::<()>().await;
        }

        let mut answered = false;
        // Welche Schleife, entscheidet die Shell des Servers. Blieb ihre Probe stumm (ein Windows, dessen erstes
        // PowerShell noch startet), wird gewartet und neu gefragt – mit der Schleife für Linux gälte ein
        // Windows-Server sonst als einer ohne Auslastung. Die Probe gibt nach einigen stummen Versuchen selbst auf.
        let shell = loop {
            match link.shell().await {
                crate::session::Shell::Unknown => {
                    let mut waiting = enabled.clone();
                    tokio::select! {
                        () = tokio::time::sleep(SHELL_RETRY) => {}
                        () = async {
                            while *waiting.borrow_and_update() {
                                if waiting.changed().await.is_err() {
                                    std::future::pending::<()>().await;
                                }
                            }
                        } => {}
                    }
                    if !*enabled.borrow() {
                        break None;
                    }
                }
                shell => break Some(shell),
            }
        };
        let Some(shell) = shell else {
            continue;
        };
        // Unter Windows dieselben Abschnitte aus einem PowerShell-Skript ([`crate::winserver::monitor`]). Das endet
        // nicht mit seinem Kanal – der Kern beendet es danach selbst, an der Nummer, die es genannt hat.
        let windows = shell == crate::session::Shell::Windows;
        let script = if windows {
            crate::winserver::command(&crate::winserver::monitor(plain))
        } else {
            SCRIPT.to_owned()
        };
        let mut remote = None;
        let end = watch(
            shared,
            session,
            (handle, link),
            &script,
            (&mut enabled, &mut held),
            &mut answered,
            &mut remote,
        )
        .await;
        if let (true, Some(running)) = (windows, remote) {
            let _ = crate::exec::run(handle, &crate::winserver::end_loop(running), OPEN_TIMEOUT).await;
        }
        if answered {
            delivered = true;
            failures = 0;
        }

        let end = match end {
            End::NoCim if !plain => {
                plain = true;
                continue;
            }
            End::NoCim => End::Unavailable {
                message: "the account may not read the counters of Windows".to_owned(),
                silent: false,
            },
            other => other,
        };
        let End::Unavailable { message, silent } = end else {
            continue;
        };
        let pause = retry_pause(delivered, silent, failures);
        shared.emit(Event::MonitorUnavailable {
            session,
            message,
            retrying: pause.is_some(),
        });

        match pause {
            // Nach der Pause von vorn; schaltet die App vorher aus, beginnt es beim Einschalten sofort.
            Some(pause) => {
                failures += 1;
                let mut waiting = enabled.clone();
                tokio::select! {
                    () = tokio::time::sleep(pause) => {}
                    () = async {
                        while *waiting.borrow_and_update() {
                            if waiting.changed().await.is_err() {
                                std::future::pending::<()>().await;
                            }
                        }
                    } => {}
                }
            }
            // Nicht von selbst wiederholen: erst, wenn die App aus- und wieder einschaltet.
            None => {
                while *enabled.borrow_and_update() {
                    if enabled.changed().await.is_err() {
                        std::future::pending::<()>().await;
                    }
                }
            }
        }
    }
}

async fn watch<H: Handler>(
    shared: &Shared,
    session: u64,
    (handle, link): (&Handle<H>, &crate::session::Link),
    script: &str,
    (enabled, held): (&mut watch::Receiver<bool>, &mut watch::Receiver<usize>),
    answered: &mut bool,
    remote: &mut Option<crate::winserver::Loop>,
) -> End {
    let silent = |message: &str| End::Unavailable {
        message: message.to_owned(),
        silent: true,
    };
    // Mit denselben kurzen Versuchen wie ein Befehl: Den Kanal der Probe, die eben lief, räumt ein Server mit wenigen
    // Kanälen je Verbindung (`MaxSessions`) erst einen Augenblick später ab.
    let mut channel = match timeout(OPEN_TIMEOUT, crate::exec::open_channel(handle)).await {
        Ok(Ok(channel)) => channel,
        Ok(Err(failure)) if failure.code == crate::exec::NO_CHANNEL => return silent(NO_CHANNEL_TEXT),
        Ok(Err(failure)) => return silent(&failure.message),
        Err(_) => return silent("the server did not open a channel"),
    };
    let _measuring = link.measuring();
    if let Err(error) = channel.exec(true, script).await {
        return silent(&error.to_string());
    }

    let mut parser = Parser::new();
    let mut pending = Vec::new();
    loop {
        let message = tokio::select! {
            message = timeout(if *answered { SILENCE_TIMEOUT } else { FIRST_ANSWER_TIMEOUT }, channel.wait()) => message,
            // Ein Befehl bekommt keinen Kanal, weil der Server keinen weiteren zulässt: Platz machen. Unter Windows
            // endet die Schleife auf dem Server damit nicht – die beendet der Aufrufer wie beim Abschalten.
            _ = async { held.wait_for(|waiting| *waiting > 0).await.is_ok() } => {
                let _ = channel.close().await;
                return End::MadeRoom;
            }
            changed = enabled.changed() => {
                if changed.is_err() || !*enabled.borrow() {
                    // Der Kanal schließt, die Schleife auf dem Server endet beim nächsten Schreiben (unter Windows
                    // nicht – die beendet der Aufrufer, [`crate::winserver::monitor`]).
                    let _ = channel.close().await;
                    return End::Stopped;
                }

                continue;
            }
        };

        match message {
            Ok(Some(ChannelMsg::Data { data })) => {
                pending.extend_from_slice(&data);
                while let Some(end) = pending.iter().position(|byte| *byte == b'\n') {
                    let line: Vec<u8> = pending.drain(..=end).collect();
                    let line = String::from_utf8_lossy(&line);
                    // Nur die erste Nennung gilt: Sie ist die erste Zeile der Schleife, alles danach stammt aus
                    // Messungen.
                    if let (None, Some(rest)) = (*remote, line.trim().strip_prefix(crate::winserver::PID_LINE)) {
                        *remote = crate::winserver::Loop::parse(rest);
                    }
                    if line.trim() == crate::winserver::NO_CIM {
                        let _ = channel.close().await;
                        return End::NoCim;
                    }
                    match parser.line(&line) {
                        Outcome::Nothing => {}
                        Outcome::Stats(stats) => {
                            *answered = true;
                            let stats = *stats;
                            shared.emit(Event::MonitorStats {
                                session,
                                cpu: stats.cpu,
                                memory_used: stats.memory_used,
                                memory_total: stats.memory_total,
                                memory_cache: stats.memory_cache,
                                memory_free: stats.memory_free,
                                swap_used: stats.swap_used,
                                swap_total: stats.swap_total,
                                disk_used: stats.disk_used,
                                disk_total: stats.disk_total,
                                os: stats.os,
                                kernel: stats.kernel,
                                uptime: stats.uptime,
                                load: stats.load,
                                net_rx: stats.net_rx,
                                net_tx: stats.net_tx,
                                interfaces: stats
                                    .interfaces
                                    .into_iter()
                                    .map(|interface| crate::protocol::MonitorInterface {
                                        name: interface.name,
                                        rx: interface.rx,
                                        tx: interface.tx,
                                        addresses: interface.addresses,
                                    })
                                    .collect(),
                                io_read: stats.io.as_ref().map(|io| io.read),
                                io_write: stats.io.as_ref().map(|io| io.write),
                                io_busy: stats.io.as_ref().map(|io| io.busy),
                                io_mixed: stats.io.as_ref().is_some_and(|io| io.mixed),
                                io_disks: stats
                                    .io
                                    .map(|io| io.disks)
                                    .unwrap_or_default()
                                    .into_iter()
                                    .map(|disk| crate::protocol::MonitorIoDisk {
                                        name: disk.name,
                                        read: disk.read,
                                        write: disk.write,
                                        busy: disk.busy,
                                    })
                                    .collect(),
                                cores: stats.cores,
                                cpu_user: stats.split.map(|split| split.user),
                                cpu_system: stats.split.map(|split| split.system),
                                cpu_iowait: stats.split.map(|split| split.iowait),
                                cpu_steal: stats.split.map(|split| split.steal),
                                disks: stats
                                    .disks
                                    .into_iter()
                                    .map(|disk| crate::protocol::MonitorDisk {
                                        mount: disk.mount,
                                        device: disk.device,
                                        used: disk.used,
                                        total: disk.total,
                                        inodes_used: disk.inodes.map(|(used, _)| used),
                                        inodes_total: disk.inodes.map(|(_, total)| total),
                                    })
                                    .collect(),
                                cpu_model: Some(stats.info.cpu).filter(|text| !text.is_empty()),
                                core_count: stats.info.cores,
                                machine: Some(stats.info.machine).filter(|text| !text.is_empty()),
                                virtualization: Some(stats.info.virtualization).filter(|text| !text.is_empty()),
                                time: stats.clock.as_ref().map(|clock| clock.time),
                                utc_offset: stats.clock.as_ref().map(|clock| clock.utc_offset),
                                zone: stats.clock.map(|clock| clock.zone).filter(|zone| !zone.is_empty()),
                            });
                        }
                        Outcome::Unavailable => {
                            let _ = channel.close().await;
                            return End::Unavailable {
                                message: "the server provides neither /proc nor the counters of FreeBSD or OpenBSD"
                                    .to_owned(),
                                silent: false,
                            };
                        }
                    }
                }

                if pending.len() > MAX_LINE {
                    let _ = channel.close().await;
                    return End::Unavailable {
                        message: "unexpected output".to_owned(),
                        silent: false,
                    };
                }
            }
            Ok(Some(ChannelMsg::Failure)) => {
                return End::Unavailable {
                    message: "the server refused to run a command".to_owned(),
                    silent: false,
                };
            }
            Ok(Some(ChannelMsg::ExitStatus { .. } | ChannelMsg::Close)) | Ok(None) => {
                return silent("the command ended");
            }
            Ok(Some(_)) => {}
            Err(_) => {
                let _ = channel.close().await;
                return silent("the server stopped answering");
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn only_a_server_that_delivered_and_fell_silent_is_tried_again_after_growing_pauses() {
        // Nie Werte, oder kennt sie nicht: kein neuer Versuch von selbst.
        assert_eq!(retry_pause(false, true, 0), None);
        assert_eq!(retry_pause(true, false, 0), None);

        let pauses: Vec<Duration> = (0..).map_while(|failures| retry_pause(true, true, failures)).collect();
        assert_eq!(pauses.len(), RETRY_PAUSES.len());
        assert!(pauses.windows(2).all(|pair| pair[0] < pair[1]), "{pauses:?}");
        assert_eq!(pauses[0], Duration::from_secs(30));
        assert_eq!(pauses.last(), Some(&Duration::from_secs(600)));
    }

    const HEADER: &str =
        "@os\nNAME=\"Ubuntu\"\nPRETTY_NAME=\"Ubuntu 24.04.1 LTS\"\nID=ubuntu\n@kernel\nLinux 6.8.0-45-generic\n";

    fn sample_at(cpu: &str, uptime: &str, received: u64, sent: u64) -> String {
        format!(
            "@sample\n{cpu}\nMemTotal:        8000000 kB\nMemFree:          500000 kB\nMemAvailable:    6000000 kB\n\
             Buffers:          100000 kB\nCached:          2000000 kB\n@uptime\n{uptime} 1000.00\n@disk\n\
             /dev/sda1       100000000 28000000  72000000      28% /\n@load\n0.06 0.02 0.00 1/171 2874409\n@net\n\
             Inter-|   Receive  |  Transmit\n face |bytes packets|bytes packets\n\
                 lo: 5000 1 0 0 0 0 0 0 5000 1 0 0 0 0 0 0\n\
               eth0: {received} 9 0 0 0 0 0 0 {sent} 9 0 0 0 0 0 0\n\
             docker0: 100 1 0 0 0 0 0 0 200 1 0 0 0 0 0 0\n@end\n"
        )
    }

    fn sample(cpu: &str) -> String {
        sample_at(cpu, "2764800.55", 0, 0)
    }

    fn feed(parser: &mut Parser, text: &str) -> Vec<Outcome> {
        text.lines()
            .map(|line| parser.line(line))
            .filter(|outcome| *outcome != Outcome::Nothing)
            .collect()
    }

    #[test]
    fn memory_is_split_into_cache_and_free_and_swap_is_read() {
        let block = |cpu: &str| {
            format!(
                "@sample\n{cpu}\nMemTotal: 8000000 kB\nMemFree: 600000 kB\nMemAvailable: 5000000 kB\nBuffers: 200000 kB\n\
                 Cached: 1800000 kB\nSReclaimable: 150000 kB\nSwapTotal: 2000000 kB\nSwapFree: 1750000 kB\n\
                 @uptime\n5.0 1.0\n@disk\n@load\n@net\n@end\n"
            )
        };
        let mut parser = Parser::new();
        feed(&mut parser, HEADER);
        feed(&mut parser, &block("cpu  1000 0 500 8000 500 0 0 0 0 0"));
        let Some(Outcome::Stats(stats)) = feed(&mut parser, &block("cpu  1100 0 550 8800 550 0 0 0 0 0")).pop() else {
            panic!("keine Messung");
        };

        assert_eq!(stats.memory_cache, Some((200_000 + 1_800_000 + 150_000) * 1024));
        assert_eq!(stats.memory_free, Some(600_000 * 1024));
        assert_eq!((stats.swap_used, stats.swap_total), (250_000 * 1024, 2_000_000 * 1024));

        // Ohne die Zeilen: keine Aufteilung, keine Auslagerung – und Unsinn vom Server läuft nicht über.
        let bare = "@sample\ncpu  1200 0 600 9600 600 0 0 0 0 0\nMemTotal: 1000 kB\nMemAvailable: 500 kB\n\
                    SwapTotal: 5 kB\nSwapFree: 18446744073709551615 kB\n@uptime\n9.0 1.0\n@disk\n@load\n@net\n@end\n";
        let Some(Outcome::Stats(stats)) = feed(&mut parser, bare).pop() else {
            panic!("keine Messung");
        };
        assert_eq!((stats.memory_cache, stats.memory_free), (None, None));
        assert_eq!((stats.swap_used, stats.swap_total), (0, 5 * 1024));
    }

    /// Was die Schleife unter Windows ausgibt ([`crate::winserver::monitor`], abgeschrieben von Windows Server 2025
    /// am 2026-10-15): dieselben Abschnitte, gelesen vom selben Parser – mit `C:\` an der Stelle eines Mountpoints.
    #[test]
    fn a_windows_server_is_read_like_any_other() {
        let header = "@pid 3740 134052012345678901\n@os\nPRETTY_NAME=Microsoft Windows Server 2025 Standard Evaluation\n@kernel\nWindows 10.0.26100\n\
                      @info\ncpu=AMD Ryzen 7 5700G with Radeon Graphics         \ncores=2\nvendor=QEMU\n\
                      product=Standard PC (Q35 + ICH9, 2009)\nvirt=vm\n";
        let block = |cpu: &str, cpu1: &str, uptime: &str, received: u64| {
            format!(
                "@sample\ncpu0 {cpu}\ncpu1 {cpu1}\ncpu {cpu}\nMemTotal: 3139304 kB\nMemAvailable: 2054928 kB\n\
                 SwapTotal: 1310720 kB\nSwapFree: 1310720 kB\n@uptime\n{uptime}\n@disk\nC: 41028604 10865420 30163184 0% C:\\\n\
                 @disks\nC: 41028604 10865420 30163184 0% C:\\\n@net\nEthernet: {received} 0 0 0 0 0 0 0 290353 0\n\
                 @addr\n1: Ethernet inet 10.0.2.15/24\n1: Ethernet_2 inet6 2001:db8:0:10:ffff::2/128\n\
                 @io\n0 0 disk0 0 0 3800 0 0 0 10070 0 0 314\n@time\n1792024947 -0700\n@end\n"
            )
        };
        let mut parser = Parser::new();
        assert!(feed(&mut parser, header).is_empty());
        assert!(
            feed(
                &mut parser,
                &block(
                    "4106 0 136741 505736 0 0 0 0",
                    "3325 0 144844 512551 0 0 0 0",
                    "5315.00",
                    214_306
                )
            )
            .is_empty()
        );
        let Some(Outcome::Stats(stats)) = feed(
            &mut parser,
            &block(
                "4206 0 136841 505936 0 0 0 0",
                "3325 0 144844 512751 0 0 0 0",
                "5320.00",
                224_306,
            ),
        )
        .pop() else {
            panic!("keine Messung");
        };

        assert_eq!(
            (stats.os.as_str(), stats.kernel.as_str()),
            (
                "Microsoft Windows Server 2025 Standard Evaluation",
                "Windows 10.0.26100"
            )
        );
        assert_eq!((stats.info.cores, stats.info.virtualization.as_str()), (Some(2), "vm"));
        assert_eq!(stats.cpu, 50);
        assert_eq!(stats.memory_total, 3_139_304 * 1024);
        assert_eq!(stats.disks.len(), 1);
        assert_eq!(
            (stats.disks[0].mount.as_str(), stats.disks[0].device.as_str()),
            ("C:\\", "C:")
        );
        assert_eq!(stats.disk_total, (10_865_420 + 30_163_184) * 1024);
        assert_eq!(stats.net_rx, 2000, "10000 Bytes in 5 Sekunden");
        let names: Vec<&str> = stats
            .interfaces
            .iter()
            .map(|interface| interface.name.as_str())
            .collect();
        assert_eq!(names, ["Ethernet", "Ethernet_2"]);
        assert_eq!(stats.clock.map(|clock| clock.utc_offset), Some(-7 * 3600));
    }

    #[test]
    fn the_second_sample_yields_the_cpu_share_between_both() {
        let mut parser = Parser::new();
        assert!(feed(&mut parser, HEADER).is_empty());
        assert!(
            feed(&mut parser, &sample("cpu  1000 0 500 8000 500 0 0 0 0 0")).is_empty(),
            "die erste Messung hat keinen Vergleichswert"
        );

        // Seitdem: 300 beschäftigt, 600 untätig, 100 wartend auf E/A → 30 %.
        let outcomes = feed(&mut parser, &sample("cpu  1200 0 600 8600 600 0 0 0 0 0"));
        let [Outcome::Stats(stats)] = outcomes.as_slice() else {
            panic!("{outcomes:?}");
        };
        assert_eq!(stats.cpu, 30);
        assert_eq!(stats.memory_total, 8_000_000 * 1024);
        assert_eq!(stats.memory_used, 2_000_000 * 1024, "gesamt minus verfügbar");
        assert_eq!(stats.disk_used, 28_000_000 * 1024);
        assert_eq!(stats.disk_total, 100_000_000 * 1024);
        assert_eq!(stats.os, "Ubuntu 24.04.1 LTS");
        assert_eq!(stats.kernel, "Linux 6.8.0-45-generic");
        assert_eq!(stats.uptime, 2_764_800);
    }

    #[test]
    fn load_and_network_rates_come_from_the_time_between_two_samples() {
        let mut parser = Parser::new();
        feed(&mut parser, HEADER);
        feed(&mut parser, &sample_at("cpu 1 0 1 8", "1000.00", 1_000_000, 500_000));

        // 2,5 Sekunden später: 250 000 Bytes empfangen, 25 000 gesendet (die zweite Karte kommt jeweils dazu).
        let outcomes = feed(&mut parser, &sample_at("cpu 2 0 2 16", "1002.50", 1_250_000, 525_000));
        let [Outcome::Stats(stats)] = outcomes.as_slice() else {
            panic!("{outcomes:?}");
        };
        assert_eq!(stats.load, "0.06 0.02 0.00");
        assert_eq!(stats.net_rx, 100_000, "Bytes je Sekunde; lo zählt nicht");
        assert_eq!(stats.net_tx, 10_000);
        assert_eq!(stats.uptime, 1002);

        // Ein Zähler, der zurückspringt (Karte neu gestartet), ergibt keine riesige Rate.
        let outcomes = feed(&mut parser, &sample_at("cpu 3 0 3 24", "1005.50", 10, 10));
        let [Outcome::Stats(stats)] = outcomes.as_slice() else {
            panic!("{outcomes:?}");
        };
        assert_eq!((stats.net_rx, stats.net_tx), (0, 0));
    }

    #[test]
    fn every_interface_has_its_own_rate_and_its_addresses() {
        let with_addresses = |uptime: &str, received: u64, sent: u64, addresses: &str| {
            sample_at("cpu 1 0 1 8", uptime, received, sent).replace("@end", &format!("@addr\n{addresses}\n@end"))
        };
        let ip = "1: lo    inet 127.0.0.1/8 scope host lo\\       valid_lft forever\n\
                  2: eth0    inet 10.0.0.5/24 brd 10.0.0.255 scope global dynamic eth0\\  valid_lft 80000sec\n\
                  2: eth0    inet6 2001:db8::5/64 scope global \\  valid_lft forever\n\
                  2: eth0    inet6 fe80::5054:ff:fe12:3456/64 scope link \\  valid_lft forever\n\
                  5: wg0@if3    inet 10.66.0.2/32 scope global wg0\\  valid_lft forever\n\
                  6: evil\u{1b}[31m    inet 10.9.9.9/8 scope global\n\
                  7: odd0    inet not-an-address/8 scope global\n\
                  7: odd0    inet 0.0.0.0/8 scope global";
        let mut parser = Parser::new();
        feed(&mut parser, HEADER);
        feed(&mut parser, &with_addresses("1000.00", 1_000_000, 500_000, ip));

        // Zwei Sekunden später; `docker0` aus der Vorlage hat keine Adresse und bleibt deshalb aus.
        let outcomes = feed(&mut parser, &with_addresses("1002.00", 1_200_000, 520_000, ip));
        let [Outcome::Stats(stats)] = outcomes.as_slice() else {
            panic!("{outcomes:?}");
        };
        let interface = |name: &str, rx: u64, tx: u64, addresses: &[&str]| Interface {
            name: name.to_owned(),
            rx,
            tx,
            addresses: addresses.iter().map(|address| (*address).to_owned()).collect(),
        };
        assert_eq!(
            stats.interfaces,
            [
                interface("eth0", 100_000, 10_000, &["10.0.0.5", "2001:db8::5"]),
                interface("wg0", 0, 0, &["10.66.0.2"]),
            ]
        );
        assert_eq!(stats.net_rx, 100_000, "die Summe bleibt, was sie war");

        // `ifconfig` der BSDs und das alte von Linux nennen erst die Karte, dann ihre Adressen.
        let ifconfig = "em0: flags=8843<UP,BROADCAST,RUNNING,SIMPLEX,MULTICAST> metric 0 mtu 1500\n\
                        \tether 52:54:00:12:34:56\n\tinet 10.0.2.15 netmask 0xffffff00 broadcast 10.0.2.255\n\
                        \tinet6 fe80::5054:ff:fe12:3456%em0 prefixlen 64 scopeid 0x1\n\
                        \tinet6 2001:db8::15 prefixlen 64\n\
                        lo0: flags=8049<UP,LOOPBACK,RUNNING,MULTICAST> metric 0 mtu 16384\n\tinet 127.0.0.1 netmask 0xff000000\n\
                        eth1      Link encap:Ethernet  HWaddr 02:42:AC:11:00:02\n\
                        \tinet addr:172.17.0.2  Bcast:172.17.255.255  Mask:255.255.0.0\n\
                        \tinet6 addr: 2001:db8::2/64 Scope:Global";
        let outcomes = feed(&mut parser, &with_addresses("1004.00", 1_200_000, 520_000, ifconfig));
        let [Outcome::Stats(stats)] = outcomes.as_slice() else {
            panic!("{outcomes:?}");
        };
        assert_eq!(
            stats.interfaces,
            [
                interface("em0", 0, 0, &["10.0.2.15", "2001:db8::15"]),
                interface("eth1", 0, 0, &["172.17.0.2", "2001:db8::2"]),
            ]
        );

        // Nennt der Server gar keine Adressen (kein `ip`, kein `ifconfig`), bleiben die Karten mit ihren Raten.
        let outcomes = feed(&mut parser, &with_addresses("1006.00", 1_400_000, 520_000, ""));
        let [Outcome::Stats(stats)] = outcomes.as_slice() else {
            panic!("{outcomes:?}");
        };
        assert_eq!(
            stats.interfaces,
            [interface("eth0", 100_000, 0, &[]), interface("docker0", 0, 0, &[])]
        );
    }

    #[test]
    fn sd_cards_soldered_storage_and_nvme_are_told_from_their_partitions() {
        let names = [
            "mmcblk0",
            "mmcblk0p1",
            "mmcblk0p2",
            "mmcblk1",
            "mmcblk1p1",
            "mmcblk1boot0",
            "mmcblk1boot1",
            "mmcblk1rpmb",
            "nvme0n1",
            "nvme0n1p1",
            "nvme0n1p15",
            "nvme1n1",
            "sda",
            "sda1",
            "sdaa",
            "vda",
            "vda2",
            "zram0",
            "loop3",
        ];
        let lines: Vec<DiskCounters> = names.iter().map(|name| ((*name).to_owned(), [0; 3])).collect();
        let whole: Vec<String> = whole_disks(&lines).into_iter().map(|(name, _)| name).collect();

        assert_eq!(
            whole,
            ["mmcblk0", "mmcblk1", "nvme0n1", "nvme1n1", "sda", "sdaa", "vda"]
        );
    }

    #[test]
    fn the_throughput_of_the_disks_counts_whole_disks_once() {
        let with_io = |cpu: &str, uptime: &str, io: &str| {
            sample_at(cpu, uptime, 0, 0).replace("@end", &format!("@io\n{io}\n@end"))
        };
        let lines = |read: u64, written: u64, busy: u64| {
            format!(
                "   8       0 sda 10 0 {read} 5 10 0 {written} 5 0 {busy} 10\n\
                    8       1 sda1 10 0 {read} 5 10 0 {written} 5 0 {busy} 10\n\
                  259       0 nvme0n1 10 0 1000 5 10 0 1000 5 0 100 10\n\
                  259       1 nvme0n1p1 10 0 1000 5 10 0 1000 5 0 100 10\n\
                  253       0 dm-0 10 0 {read} 5 10 0 {written} 5 0 {busy} 10\n\
                    7       0 loop0 10 0 {read} 5 10 0 {written} 5 0 {busy} 10\n\
                  garbage \u{1b}[31m line\n   8 16 sdb 1 2 3"
            )
        };
        let mut parser = Parser::new();
        feed(&mut parser, HEADER);
        feed(
            &mut parser,
            &with_io("cpu 1 0 1 8", "1000.00", &lines(1_000, 2_000, 500)),
        );

        // Zwei Sekunden später: 4096 Sektoren gelesen, 1024 geschrieben, 500 ms beschäftigt – nur auf sda; die
        // Partition, die Schicht darüber und das Abbild zählen dasselbe noch einmal und bleiben außen vor.
        let outcomes = feed(
            &mut parser,
            &with_io("cpu 2 0 2 16", "1002.00", &lines(5_096, 3_024, 1_000)),
        );
        let [Outcome::Stats(stats)] = outcomes.as_slice() else {
            panic!("{outcomes:?}");
        };
        let disk = |name: &str, read, write, busy| IoDisk {
            name: name.to_owned(),
            read,
            write,
            busy,
        };
        assert_eq!(
            stats.io,
            Some(Io {
                read: 1024 * 1024,
                write: 256 * 1024,
                busy: 25,
                mixed: false,
                disks: vec![disk("sda", 1024 * 1024, 256 * 1024, 25), disk("nvme0n1", 0, 0, 0)],
            })
        );

        // Ein Zähler, der zurückspringt, ergibt keine riesige Rate; mehr als die ganze Zeit ist niemand beschäftigt.
        let outcomes = feed(
            &mut parser,
            &with_io("cpu 3 0 3 24", "1003.00", &lines(10, 10, 999_999)),
        );
        let [Outcome::Stats(stats)] = outcomes.as_slice() else {
            panic!("{outcomes:?}");
        };
        assert_eq!(
            stats.io,
            Some(Io {
                read: 0,
                write: 0,
                busy: 100,
                mixed: false,
                disks: vec![disk("sda", 0, 0, 100), disk("nvme0n1", 0, 0, 0)],
            })
        );

        // Ohne die Zeilen gibt es keinen Durchsatz – auch keinen von null.
        let outcomes = feed(&mut parser, &sample_at("cpu 4 0 4 32", "1006.00", 0, 0));
        let [Outcome::Stats(stats)] = outcomes.as_slice() else {
            panic!("{outcomes:?}");
        };
        assert_eq!(stats.io, None);
    }

    /// Zwei Messungen im Abstand von zwei Sekunden, jede mit dem genannten Abschnitt `@io`; der Durchsatz der zweiten.
    fn io_between(first: &str, second: &str) -> Option<Io> {
        let with_io = |cpu: &str, uptime: &str, io: &str| {
            sample_at(cpu, uptime, 0, 0).replace("@end", &format!("@io\n{io}\n@end"))
        };
        let mut parser = Parser::new();
        feed(&mut parser, HEADER);
        feed(&mut parser, &with_io("cpu 1 0 1 8", "1000.00", first));
        let outcomes = feed(&mut parser, &with_io("cpu 2 0 2 16", "1002.00", second));
        let [Outcome::Stats(stats)] = outcomes.as_slice() else {
            panic!("{outcomes:?}");
        };
        stats.io.clone()
    }

    #[test]
    fn freebsd_names_kilobytes_and_busy_seconds_per_device() {
        // So antwortet `iostat -Ix` unter FreeBSD 14 (abgelesen 2026-10-11).
        let lines = |read: &str, written: &str, busy: &str| {
            format!(
                "                        extended device statistics  \n\
                 device           r/i         w/i         kr/i         kw/i qlen   tsvc_t/i      sb/i  \n\
                 vtbd0         4998.0        78.0      {read}       {written}    0        1.5       {busy} \n\
                 cd0             34.0         0.0        128.0          0.0    0        0.0       0.0 \n\
                 pass0            0.0         0.0          0.0          0.0    0        0.0       0.0 \n\
                 ada1             1.0         1.0          nan          inf    0        0.0       0.0 "
            )
        };
        let io = io_between(&lines("69360.0", "1669.0", "1.3"), &lines("71408.0", "2181.0", "2.3")).unwrap();

        assert_eq!(
            (io.read, io.write, io.busy, io.mixed),
            (1024 * 1024, 256 * 1024, 50, false)
        );
        assert_eq!(io.disks.len(), 1);
        assert_eq!(io.disks[0].name, "vtbd0");
    }

    /// Ein Server mit zwei Platten: jede steht für sich da, die Laufwerke für CDs und die Durchreichen nicht. Die
    /// erste Antwort ist die eines echten FreeBSD 14.5 mit zwei Platten (abgelesen 2026-10-13, `iostat -Ix`); in
    /// der zweiten ist nur auf der zweiten Platte etwas geschehen.
    #[test]
    fn a_server_with_two_disks_names_each_of_them() {
        let lines = |read: &str, written: &str, busy: &str| {
            format!(
                "                        extended device statistics  \n\
                 device           r/i         w/i         kr/i         kw/i qlen   tsvc_t/i      sb/i  \n\
                 vtbd0         5005.0        30.0      69616.0        536.5    0        1.0       1.0 \n\
                 vtbd1           25.0         0.0      {read}       {written}    0        0.0       {busy} \n\
                 cd0             34.0         0.0        128.0          0.0    0        0.0       0.0 \n\
                 cd1              0.0         0.0          0.0          0.0    0        0.0       0.0 \n\
                 pass0            0.0         0.0          0.0          0.0    0        0.0       0.0 \n\
                 pass1            0.0         0.0          0.0          0.0    0        0.0       0.0 "
            )
        };
        let io = io_between(&lines("74.0", "0.0", "0.0"), &lines("4170.0", "2048.0", "1.0")).unwrap();

        assert_eq!(
            io.disks
                .iter()
                .map(|disk| (disk.name.as_str(), disk.read, disk.write, disk.busy))
                .collect::<Vec<_>>(),
            [("vtbd0", 0, 0, 0), ("vtbd1", 2 * 1024 * 1024, 1024 * 1024, 50)]
        );
        // Zusammen: was beide Platten taten.
        assert_eq!((io.read, io.write), (2 * 1024 * 1024, 1024 * 1024));
    }

    #[test]
    fn openbsd_names_reading_and_writing_only_together() {
        // So antwortet `iostat -DI` unter OpenBSD 7.9 (abgelesen 2026-10-11).
        let lines = |both: u64, busy: &str| {
            format!(
                "               cd0                cd1                sd0                fd0 \n\
                 \x20     KB  xfr time       KB  xfr time       KB  xfr time       KB  xfr time \n\
                 \x20     22   11 0.01        0    0 0.00   {both} 35190 {busy}        0    0 0.00 "
            )
        };
        let io = io_between(&lines(576_434, "12.01"), &lines(578_482, "12.51")).unwrap();

        assert_eq!((io.read, io.write, io.busy, io.mixed), (1024 * 1024, 0, 25, true));
        assert_eq!(
            io.disks.iter().map(|disk| disk.name.as_str()).collect::<Vec<_>>(),
            ["sd0"]
        );

        // Passen Namen und Werte nicht zusammen, gilt keiner.
        assert_eq!(
            io_between(
                "sd0 wd0\nKB xfr time KB xfr time\n1 2 3",
                "sd0 wd0\nKB xfr time KB xfr time\n9 9 9"
            ),
            None
        );
    }

    #[test]
    fn a_load_line_that_is_not_numbers_is_ignored() {
        let mut parser = Parser::new();
        let block = |cpu: &str| {
            format!(
                "@sample\n{cpu}\nMemTotal: 1000 kB\nMemAvailable: 500 kB\n@uptime\n5.0 1.0\n@disk\n@load\n\u{1b}[31mrot 1 2\n@net\n@end\n"
            )
        };
        feed(&mut parser, &block("cpu 10 0 10 80"));
        let outcomes = feed(&mut parser, &block("cpu 20 0 20 160"));
        let [Outcome::Stats(stats)] = outcomes.as_slice() else {
            panic!("{outcomes:?}");
        };
        assert_eq!(stats.load, "");
        assert_eq!((stats.net_rx, stats.net_tx), (0, 0));
    }

    #[test]
    fn old_kernels_without_memavailable_fall_back_to_free_plus_caches() {
        let mut parser = Parser::new();
        let block = |cpu: &str| {
            format!(
                "@sample\n{cpu}\nMemTotal: 1000 kB\nMemFree: 100 kB\nBuffers: 50 kB\nCached: 250 kB\n@uptime\n5.0 1.0\n@disk\n@end\n"
            )
        };
        feed(&mut parser, &block("cpu 10 0 10 80"));
        let outcomes = feed(&mut parser, &block("cpu 20 0 20 160"));
        let [Outcome::Stats(stats)] = outcomes.as_slice() else {
            panic!("{outcomes:?}");
        };
        assert_eq!(stats.memory_used, 600 * 1024);
        assert_eq!(stats.cpu, 20);
        assert_eq!(
            (stats.disk_used, stats.disk_total),
            (0, 0),
            "ohne df-Zeile bleibt der Datenträger leer"
        );
        assert_eq!(stats.os, "", "ohne os-release und uname bleibt der Name leer");
    }

    /// Eine Messung, wie sie die Schleife auf FreeBSD 14.5 ausgibt (aufgenommen in QEMU).
    fn freebsd_sample(cp_time: &str, free: u64, uptime: u64, received: u64, sent: u64) -> String {
        format!(
            "@sample\ncp_time {cp_time}\nhw.physmem: 1037541376\nhw.pagesize: 4096\n\
             vm.stats.vm.v_free_count: {free}\nvm.stats.vm.v_inactive_count: 464\nvm.stats.vm.v_cache_count: 0\n\
             @uptime\n{uptime}\n@disk\n/dev/gpt/rootfs     5061648 2856356 1800364    61%    /\n\
             @load\n 0.22 0.07 0.02 \n@net\n\
             Name     Mtu Network           Address                           Ipkts Ierrs Idrop     Ibytes    Opkts Oerrs     Obytes  Coll\n\
             vtnet0  1500 <Link#1>          52:54:00:12:34:56                   180     0     0 {received:>10}      150     0 {sent:>10}     0\n\
             vtnet0     - fe80::%vtnet0/64  fe80::5054:ff:fe12:3456%vtnet0        0     -     -          0        2     -        128     -\n\
             vtnet0     - 10.0.2.0/24       10.0.2.15                           175     -     -      34062      143     -      35998     -\n\
             lo0    16384 <Link#2>          lo0                                  99     0     0      99999       99     0      99999     0\n\
             lo0        - 127.0.0.0/8       127.0.0.1                             0     -     -          0        0     -          0     -\n\
             tun0*  1500 <Link#3>                                                 1     0     0       5000        1     0       7000     0\n\
             @end\n"
        )
    }

    #[test]
    fn freebsd_counters_give_the_same_values_as_proc() {
        let mut parser = Parser::new();
        feed(
            &mut parser,
            "@os\nNAME=FreeBSD\nPRETTY_NAME=\"FreeBSD 14.5-RELEASE-p1\"\n@kernel\nFreeBSD 14.5-RELEASE-p1\n",
        );
        assert!(
            feed(
                &mut parser,
                &freebsd_sample("62 0 260 18 21416", 227_194, 86, 37_848, 38_766)
            )
            .is_empty()
        );

        let outcomes = feed(
            &mut parser,
            &freebsd_sample("62 0 263 18 21689", 227_083, 87, 38_508, 41_278),
        );
        let [Outcome::Stats(stats)] = outcomes.as_slice() else {
            panic!("{outcomes:?}");
        };
        // 276 Ticks seitdem, davon 273 untätig.
        assert_eq!(stats.cpu, 1);
        assert_eq!(stats.memory_total, 1_037_541_376);
        assert_eq!(
            stats.memory_used,
            1_037_541_376 - (227_083 + 464) * 4096,
            "frei und inaktiv gelten als verfügbar"
        );
        assert_eq!(
            (stats.disk_used, stats.disk_total),
            (2_856_356 * 1024, (2_856_356 + 1_800_364) * 1024)
        );
        assert_eq!(stats.uptime, 87);
        assert_eq!(stats.load, "0.22 0.07 0.02");
        // Eine Sekunde später; lo0 zählt nicht, die Adresszeilen nicht doppelt, eine Karte ohne Hardware-Adresse
        // (tun0) schon – ihre Zähler stehen jeweils gleich und tragen nichts bei.
        assert_eq!((stats.net_rx, stats.net_tx), (660, 2512));
        assert_eq!(stats.os, "FreeBSD 14.5-RELEASE-p1");
    }

    #[test]
    fn an_interface_without_hardware_address_is_counted_from_the_header() {
        let mut parser = Parser::new();
        feed(&mut parser, &freebsd_sample("1 0 1 0 8", 1000, 10, 0, 0));
        let outcomes = feed(&mut parser, &freebsd_sample("2 0 2 0 16", 1000, 11, 0, 0));
        let [Outcome::Stats(_)] = outcomes.as_slice() else {
            panic!("{outcomes:?}");
        };
        // Beide Male nur tun0 (5000/7000 Bytes) und vtnet0 (0) – gleiche Summe, also keine Rate; aber gezählt wurde.
        assert_eq!(
            parser.previous_net.map(|(received, sent, _)| (received, sent)),
            Some((5000, 7000))
        );
    }

    /// Eine Messung, wie sie die Schleife auf OpenBSD 7.9 ausgibt (aufgenommen in QEMU): `cp_time` mit Kommas,
    /// `sysctl` mit `=`, die Seiten aus `vmstat -s`, `netstat -ibn` ohne Paketspalten.
    fn openbsd_sample(cp_time: &str, free: u64, uptime: u64, received: u64, sent: u64) -> String {
        format!(
            "@sample\ncp_time {cp_time}\nhw.physmem=1056821248\nhw.pagesize=4096\n\
             \x20    {free:>6} pages free\n      25207 pages inactive\n\
             @uptime\n{uptime}\n@disk\n/dev/sd0a        148590     100654       40508    72%   /\n\
             @load\n0.94 0.25 0.09\n@net\n\
             Name    Mtu   Network     Address               Ibytes     Obytes\n\
             lo0     32768 <Link>                               993        993\n\
             lo0     32768 127/8       127.0.0.1                993        993\n\
             vio0    1500  <Link>      52:54:00:12:34:56 {received:>10} {sent:>10}\n\
             vio0    1500  10.0.2/24   10.0.2.15              17977      19282\n\
             enc0*   0     <Link>                                 0          0\n\
             @end\n"
        )
    }

    #[test]
    fn openbsd_counters_give_the_same_values_as_proc() {
        let mut parser = Parser::new();
        // OpenBSD hat kein /etc/os-release: Der Name kommt aus uname.
        feed(&mut parser, "@os\n@kernel\nOpenBSD 7.9\n");
        assert!(
            feed(
                &mut parser,
                &openbsd_sample("679,0,2093,202,135,2666", 10_843, 58, 17_977, 19_282)
            )
            .is_empty()
        );

        let outcomes = feed(
            &mut parser,
            &openbsd_sample("689,0,2113,212,135,2726", 10_700, 59, 18_977, 21_282),
        );
        let [Outcome::Stats(stats)] = outcomes.as_slice() else {
            panic!("{outcomes:?}");
        };
        // 100 Ticks seitdem (spin zählt mit), davon 60 untätig.
        assert_eq!(stats.cpu, 40);
        assert_eq!(stats.memory_total, 1_056_821_248);
        assert_eq!(
            stats.memory_used,
            1_056_821_248 - (10_700 + 25_207) * 4096,
            "frei und inaktiv gelten als verfügbar"
        );
        assert_eq!(
            (stats.disk_used, stats.disk_total),
            (100_654 * 1024, (100_654 + 40_508) * 1024)
        );
        assert_eq!(stats.uptime, 59);
        assert_eq!(stats.load, "0.94 0.25 0.09");
        assert_eq!(
            (stats.net_rx, stats.net_tx),
            (1000, 2000),
            "nur vio0, ohne lo0 und Adresszeilen"
        );
        assert_eq!(stats.os, "OpenBSD");
        assert_eq!(stats.kernel, "OpenBSD 7.9");
    }

    #[test]
    fn other_lines_that_start_with_a_number_are_not_memory() {
        let mut parser = Parser::new();
        feed(&mut parser, "@sample\n4096 bytes per page\n12 pages free now\n");
        assert_eq!(parser.bsd_memory, [None; 5]);
    }

    #[test]
    fn a_server_without_proc_is_reported_as_unavailable() {
        let mut parser = Parser::new();
        let outcomes = feed(
            &mut parser,
            "@os\n@kernel\nDarwin 23.5.0\n@sample\n@uptime\n@disk\n/dev/disk1s1 1000 500 500 50% /\n@end\n",
        );
        assert_eq!(outcomes, [Outcome::Unavailable]);
    }

    #[test]
    fn server_text_is_cleaned_for_display() {
        let mut parser = Parser::new();
        feed(
            &mut parser,
            &format!(
                "@os\nPRETTY_NAME=\"Evil\x1b[31m {}\"\n@kernel\nLinux 6\n",
                "x".repeat(500)
            ),
        );
        feed(&mut parser, &sample("cpu 1 0 1 8"));
        let outcomes = feed(&mut parser, &sample("cpu 2 0 2 16"));
        let [Outcome::Stats(stats)] = outcomes.as_slice() else {
            panic!("{outcomes:?}");
        };
        assert!(!stats.os.contains('\x1b'));
        assert_eq!(stats.os.chars().count(), MAX_TEXT);
    }

    #[test]
    fn every_core_and_the_parts_of_the_load_come_from_the_same_two_samples() {
        let mut parser = Parser::new();
        feed(&mut parser, HEADER);
        let first = "cpu  1000 100 400 8000 400 50 50 0 0 0\ncpu0 600 0 200 4000 200 0 0 0 0 0\n\
                     cpu1 500 0 300 4000 200 0 0 0 0 0";
        assert!(feed(&mut parser, &sample(first)).is_empty());

        // Seitdem 1000 Zähler: 250 Benutzer (200 + 50 nice), 150 System (100 + 25 irq + 25 softirq), 100 warten,
        // 50 gestohlen, 450 untätig. Kern 0: 400 von 500 beschäftigt, Kern 1: 50 von 500.
        let second = "cpu  1200 150 500 8450 500 75 75 50 0 0\ncpu0 900 0 300 4100 200 0 0 0 0 0\n\
                      cpu1 525 0 325 4450 200 0 0 0 0 0";
        let outcomes = feed(&mut parser, &sample(second));
        let [Outcome::Stats(stats)] = outcomes.as_slice() else {
            panic!("{outcomes:?}");
        };
        assert_eq!(stats.cpu, 45, "Warten auf E/A zählt als untätig");
        assert_eq!(stats.cores, [80, 10]);
        assert_eq!(
            stats.split,
            Some(Split {
                user: 25,
                system: 15,
                iowait: 10,
                steal: 5
            })
        );

        // Fällt ein Kern weg, gibt es für diese Messung keine einzelnen Werte – die Zähler passten nicht zueinander.
        let outcomes = feed(
            &mut parser,
            &sample("cpu  1300 150 500 8900 500 75 75 50 0 0\ncpu0 1000 0 300 4500 200 0 0 0 0 0"),
        );
        let [Outcome::Stats(stats)] = outcomes.as_slice() else {
            panic!("{outcomes:?}");
        };
        assert!(stats.cores.is_empty());
    }

    #[test]
    fn freebsd_names_its_cores_in_one_line() {
        let mut parser = Parser::new();
        feed(&mut parser, "@os\n@kernel\nFreeBSD 14.5-RELEASE\n");
        let with_cores = |cp_time: &str, cp_times: &str| {
            freebsd_sample(cp_time, 200_000, 600, 0, 0).replace(
                &format!("cp_time {cp_time}\n"),
                &format!("cp_time {cp_time}\ncp_times {cp_times}\n"),
            )
        };
        assert!(
            feed(
                &mut parser,
                &with_cores("100 0 50 10 840", "60 0 30 10 400 40 0 20 0 440")
            )
            .is_empty()
        );

        // Seitdem 1000: 200 Benutzer, 100 System (80 sys + 20 intr), 700 untätig; Kern 0 300 von 500, Kern 1 keine.
        let outcomes = feed(
            &mut parser,
            &with_cores("300 0 130 30 1540", "260 0 110 30 600 40 0 20 0 940"),
        );
        let [Outcome::Stats(stats)] = outcomes.as_slice() else {
            panic!("{outcomes:?}");
        };
        assert_eq!((stats.cpu, stats.cores.as_slice()), (30, [60, 0].as_slice()));
        assert_eq!(
            stats.split,
            Some(Split {
                user: 20,
                system: 10,
                iowait: 0,
                steal: 0
            })
        );
    }

    #[test]
    fn all_file_systems_are_listed_without_those_that_hold_no_data() {
        let mut parser = Parser::new();
        feed(&mut parser, HEADER);
        let disks = "@disks\nFilesystem     1024-blocks      Used Available Capacity Mounted on\n\
                     /dev/sda1        100000000  28000000  72000000      28% /\n\
                     udev                400000         0    400000       0% /dev\n\
                     tmpfs               800000      1000    799000       1% /run\n\
                     /dev/sdb1        500000000 470000000  30000000      94% /srv/my data\n\
                     /dev/loop3           65536     65536         0     100% /snap/core/1\n\
                     /dev/sdc1        200000000  50000000 150000000      25% /mnt/pool/home\n\
                     /dev/sdc1        200000000  50000000 150000000      25% /home\n\
                     overlay          100000000  28000000  72000000      28% /var/lib/docker/overlay2/x/merged\n\
                     overlay           40000000  10000000  30000000      25% /\n\
                     /dev/vda1         40000000  10000000  30000000      25% /etc/hosts\n\
                     //nas/share 1  900000000 100000000 800000000      12% /mnt/nas\n\
                     \x1b[31mevil       1000 500 500 50% /evil\n\
                     /dev/sdd1                0         0         0       - /empty\n\
                     @inodes\nFilesystem      Inodes  IUsed   IFree IUse% Mounted on\n\
                     /dev/sda1      6400000 250000 6150000    4% /\n\
                     /dev/sdb1            0      0       0     - /srv/my data\n\
                     /dev/sdc1      1000000 990000   10000   99% /home\n@load";
        let text = |cpu: &str| sample(cpu).replace("@load", disks);
        feed(&mut parser, &text("cpu  1000 0 500 8000 500 0 0 0 0 0"));
        let outcomes = feed(&mut parser, &text("cpu  1200 0 600 8600 600 0 0 0 0 0"));
        let [Outcome::Stats(stats)] = outcomes.as_slice() else {
            panic!("{outcomes:?}");
        };

        // Einhängepunkt, Gerät, Größe in KiB, Inodes.
        type Shown<'a> = (&'a str, &'a str, u64, Option<(u64, u64)>);
        let shown: Vec<Shown> = stats
            .disks
            .iter()
            .map(|disk| {
                (
                    disk.mount.as_str(),
                    disk.device.as_str(),
                    disk.total / 1024,
                    disk.inodes,
                )
            })
            .collect();
        assert_eq!(
            shown,
            [
                ("/", "/dev/sda1", 100_000_000, Some((250_000, 6_400_000))),
                // Ein Einhängepunkt mit Leerzeichen; ein Dateisystem ohne feste Zahl von Inodes nennt keine.
                ("/srv/my data", "/dev/sdb1", 500_000_000, None),
                // Zweimal eingehängt: einmal gezeigt, am kürzeren Pfad.
                ("/home", "/dev/sdc1", 200_000_000, Some((990_000, 1_000_000))),
                // Die Schicht von Docker über `/` ist kein eigener Platz und fehlt. In einem Container ist es
                // umgekehrt: Dort steht die Schicht auf `/` – gezeigt wird sie dort, mit dem Gerät darunter.
                ("/", "/dev/vda1", 40_000_000, None),
                // Ein Gerät mit Leerzeichen.
                ("/mnt/nas", "//nas/share 1", 900_000_000, None),
                // Steuerzeichen des Servers erreichen die Anzeige nicht.
                ("/evil", "[31mevil", 1000, None),
            ]
        );
        assert_eq!(stats.disks[1].used, 470_000_000 * 1024);
    }

    #[test]
    fn freebsd_appends_its_inodes_to_the_usual_list() {
        // Wie FreeBSD 14.5 es ausgibt (`df -Pil`): belegte und freie Inodes hinter dem Anteil des Platzes.
        let mut parser = Parser::new();
        feed(&mut parser, HEADER);
        let disks = "@disks\nFilesystem      1024-blocks    Used   Avail Capacity  Mounted on\n\
                     /dev/gpt/rootfs     5061648 2856460 1800260    61%    /\n\
                     devfs                     1       0       1     0%    /dev\n\
                     /dev/gpt/efiesp       32764     647   32117     2%    /boot/efi\n\
                     @inodes\nFilesystem      512-blocks    Used   Avail Capacity iused  ifree %iused  Mounted on\n\
                     /dev/gpt/rootfs   10123296 5712920 3600520    61%   31309 690993    4%   /\n\
                     devfs                    2       0       2     0%       0      0     -   /dev\n\
                     /dev/gpt/efiesp      65528    1294   64234     2%       0      0     -   /boot/efi\n@load";
        let text = |cpu: &str| sample(cpu).replace("@load", disks);
        feed(&mut parser, &text("cpu  1000 0 500 8000 500 0 0 0 0 0"));
        let outcomes = feed(&mut parser, &text("cpu  1200 0 600 8600 600 0 0 0 0 0"));
        let [Outcome::Stats(stats)] = outcomes.as_slice() else {
            panic!("{outcomes:?}");
        };
        let shown: Vec<(&str, Option<(u64, u64)>)> = stats
            .disks
            .iter()
            .map(|disk| (disk.mount.as_str(), disk.inodes))
            .collect();
        assert_eq!(shown, [("/", Some((31_309, 722_302))), ("/boot/efi", None)]);
    }

    #[test]
    fn inodes_in_an_unknown_notation_are_left_out() {
        let mut parser = Parser::new();
        feed(&mut parser, HEADER);
        let disks = "@disks\nFilesystem 1024-blocks Used Avail Capacity Mounted on\n\
                     /dev/sda1 5061648 2856356 1800364 61% /\n\
                     @inodes\nFilesystem Files Taken Left Share Mounted on\n\
                     /dev/sda1 658654 109822 548832 17% /\n@load";
        let text = |cpu: &str| sample(cpu).replace("@load", disks);
        feed(&mut parser, &text("cpu  1000 0 500 8000 500 0 0 0 0 0"));
        let outcomes = feed(&mut parser, &text("cpu  1200 0 600 8600 600 0 0 0 0 0"));
        let [Outcome::Stats(stats)] = outcomes.as_slice() else {
            panic!("{outcomes:?}");
        };
        assert_eq!(stats.disks.len(), 1);
        assert_eq!(stats.disks[0].inodes, None);
    }
    #[test]
    fn what_the_machine_is_comes_once_and_the_clock_with_every_sample() {
        let mut parser = Parser::new();
        feed(
            &mut parser,
            "@os\nPRETTY_NAME=\"Debian GNU/Linux 13\"\n@kernel\nLinux 6.12.0\n@info\n\
             cpu= Intel(R)   Xeon(R) Gold 6140 CPU @ 2.30GHz\ncores=4\nvendor=QEMU\n\
             product=Standard PC (Q35 + ICH9, 2009)\nvirt=kvm\n",
        );
        let with_time = |cpu: &str, time: &str| sample(cpu).replace("@end", &format!("@time\n{time}\n@end"));
        feed(
            &mut parser,
            &with_time("cpu  1000 0 500 8000 500 0 0 0 0 0", "1791590000 +0200 CEST"),
        );
        let outcomes = feed(
            &mut parser,
            &with_time("cpu  1200 0 600 8600 600 0 0 0 0 0", "1791590003 -0930 +0930"),
        );
        let [Outcome::Stats(stats)] = outcomes.as_slice() else {
            panic!("{outcomes:?}");
        };
        assert_eq!(
            stats.info,
            Info {
                cpu: "Intel(R) Xeon(R) Gold 6140 CPU @ 2.30GHz".to_owned(),
                cores: Some(4),
                machine: "QEMU Standard PC (Q35 + ICH9, 2009)".to_owned(),
                virtualization: "kvm".to_owned(),
            }
        );

        // Ein Kürzel, das keines ist (BusyBox ohne Zonendaten nennt die Zahl noch einmal), bleibt leer.
        assert_eq!(
            stats.clock,
            Some(Clock {
                time: 1_791_590_003,
                utc_offset: -(9 * 3600 + 30 * 60),
                zone: String::new(),
            })
        );

        // Ohne Uhr in einer Messung gilt nicht die der vorigen.
        let outcomes = feed(&mut parser, &sample("cpu  1400 0 700 9200 700 0 0 0 0 0"));
        let [Outcome::Stats(stats)] = outcomes.as_slice() else {
            panic!("{outcomes:?}");
        };
        assert_eq!(stats.clock, None);
        assert_eq!(stats.info.cores, Some(4), "die festen Angaben bleiben");
    }

    #[test]
    fn empty_and_meaningless_details_of_the_machine_stay_empty() {
        let mut parser = Parser::new();
        feed(
            &mut parser,
            "@os\n@kernel\nLinux 6.12.0\n@info\ncpu=\ncores=0\nvendor=To Be Filled By O.E.M.\n\
             product=Default string\nvirt=none vm\n",
        );
        feed(&mut parser, &sample("cpu  1000 0 500 8000 500 0 0 0 0 0"));
        let outcomes = feed(
            &mut parser,
            &sample("cpu  1200 0 600 8600 600 0 0 0 0 0").replace("@end", "@time\nnow +2 X\n@end"),
        );
        let [Outcome::Stats(stats)] = outcomes.as_slice() else {
            panic!("{outcomes:?}");
        };
        assert_eq!(
            stats.info,
            Info {
                cpu: String::new(),
                cores: None,
                machine: String::new(),
                // „none“ ist eine Auskunft: Der Server sagt, dass er nicht virtuell ist.
                virtualization: "none".to_owned(),
            }
        );
        assert_eq!(stats.clock, None);

        // Der Hersteller steht nicht doppelt da, und was keine Art der Virtualisierung sein kann, gilt nicht.
        let mut parser = Parser::new();
        feed(
            &mut parser,
            "@os\n@kernel\nFreeBSD 14.5\n@info\nvendor=Supermicro\nproduct=supermicro X11\nvirt=$(rm -rf)\n",
        );
        feed(&mut parser, &sample("cpu  1000 0 500 8000 500 0 0 0 0 0"));
        let outcomes = feed(&mut parser, &sample("cpu  1200 0 600 8600 600 0 0 0 0 0"));
        let [Outcome::Stats(stats)] = outcomes.as_slice() else {
            panic!("{outcomes:?}");
        };
        assert_eq!(
            (stats.info.machine.as_str(), stats.info.virtualization.as_str()),
            ("supermicro X11", "")
        );
    }

    #[test]
    fn only_a_real_distance_to_utc_is_one() {
        assert_eq!(utc_offset("+0000"), Some(0));
        assert_eq!(utc_offset("+0545"), Some(5 * 3600 + 45 * 60));
        assert_eq!(utc_offset("-1100"), Some(-11 * 3600));
        for wrong in ["", "+", "0200", "+02", "+2500", "+0260", "+02:00", "é0200"] {
            assert_eq!(utc_offset(wrong), None, "{wrong}");
        }
    }

    #[test]
    fn the_script_survives_single_quoting() {
        // Der Befehl steht in einfachen Anführungszeichen; ein weiteres darin würde ihn zerreißen.
        let inner = SCRIPT.strip_prefix("sh -c '").unwrap().strip_suffix('\'').unwrap();
        assert!(!inner.contains('\''));
    }
}
