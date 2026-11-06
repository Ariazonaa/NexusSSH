//! Liest die Konfiguration von OpenSSH (`~/.ssh/config`) und macht aus ihren `Host`-Einträgen Profile
//! (Spec §8, „SSH Config Import“).
//!
//! Übernommen werden `HostName`, `User`, `Port`, `IdentityFile`, `IdentitiesOnly`, `ProxyJump`, `ForwardAgent`,
//! `ServerAliveInterval`/`ServerAliveCountMax`, `LocalForward`/`RemoteForward`/`DynamicForward` (als Tunnel),
//! `Compression`, `ConnectTimeout`, `AddressFamily` und `SetEnv`. Wie bei OpenSSH gilt je Einstellung der erste
//! passende Wert, auch aus Blöcken mit Mustern (`Host *`); Weiterleitungen und Variablen sammeln sich. `Include`
//! wird verfolgt. Von `Match` gelten die Bedingungen, die sich ohne Verbindung und ohne ein Programm zu starten
//! prüfen lassen (`all`, `host`, `originalhost`, `user`, `localuser`); ein Block mit einer anderen (`exec` …)
//! gilt nie – die App startet nichts, was in einer Datei steht. Was für einen Server dasteht und nicht übernommen
//! wird, nennt [`ConfigHost::skipped`]. Die Datei wird nur gelesen.

use std::collections::{HashMap, HashSet};
use std::fs;
use std::io;
use std::path::{Path, PathBuf};
use std::time::SystemTime;

use serde::Serialize;

use crate::profiles::{EnvVar, MAX_JUMPS, Profile, ProfileStore, Tunnel, TunnelKind};

const MAX_INCLUDE_DEPTH: usize = 8;

/// Mehr als das nennt [`ConfigHost::skipped`] nicht – die Liste ist für den Benutzer, nicht für ein Programm.
const MAX_SKIPPED: usize = 24;

/// So viele Server liest der Kern aus der Datei und so viele Blöcke wertet er aus – jeder Server wird gegen jeden
/// Block geprüft. Weit mehr, als eine gewachsene Datei hat; eine Grenze, damit eine so gebaute Datei (sie kann von
/// jemand anderem stammen) den Start nicht aufhält.
#[cfg(not(test))]
const MAX_HOSTS: usize = 5000;
#[cfg(not(test))]
const MAX_BLOCKS: usize = 20_000;

// In den Tests kleiner, damit die Grenze sich prüfen lässt, ohne jedes Mal Sekunden zu rechnen (ohne Optimierung
// braucht die volle Größe eine halbe Minute; gemessen 2026-10-13).
#[cfg(test)]
const MAX_HOSTS: usize = 300;
#[cfg(test)]
const MAX_BLOCKS: usize = 2000;

/// Ein Server aus der Konfiguration, so wie `ssh <alias>` ihn auflösen würde.
#[derive(Clone, Debug, Default, PartialEq, Eq, Serialize)]
pub struct ConfigHost {
    /// Der Name hinter `Host`.
    pub alias: String,
    /// `HostName`, sonst der Alias.
    pub host: String,
    /// `Port`, sonst 22.
    pub port: u16,
    /// `User`, sonst der angemeldete Benutzer.
    pub user: String,
    /// Erste `IdentityFile`, mit aufgelöstem `~`.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub key_path: Option<String>,
    /// `ProxyJump`, wie es dasteht.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub jump: Option<String>,
    /// `ForwardAgent yes`.
    pub forward_agent: bool,
    /// Ob es schon ein Profil mit diesem Namen oder diesem Ziel gibt. Füllt [`mark_existing`].
    pub exists: bool,
    /// `IdentitiesOnly yes`: nur den genannten Schlüssel anbieten, keine weiteren des Agenten.
    #[serde(skip_serializing_if = "std::ops::Not::not")]
    pub identities_only: bool,
    /// `ServerAliveInterval` in Sekunden, in den Grenzen des Profils; 0 = ausdrücklich keine Lebenszeichen.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub keepalive_interval: Option<u32>,
    /// `ServerAliveCountMax`, in den Grenzen des Profils.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub keepalive_max: Option<u32>,
    /// `LocalForward`, `RemoteForward` und `DynamicForward` als Tunnel. Sie lauschen nur für diesen Rechner, auch
    /// wenn in der Datei eine andere Adresse steht.
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub tunnels: Vec<Tunnel>,
    /// `Compression yes`.
    #[serde(skip_serializing_if = "std::ops::Not::not")]
    pub compression: bool,
    /// `ConnectTimeout` in Sekunden, in den Grenzen des Profils.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub connect_timeout: Option<u32>,
    /// `AddressFamily inet`/`inet6` als `ipv4`/`ipv6`.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub address_family: Option<String>,
    /// `SetEnv`, je Name der erste Wert.
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub env: Vec<EnvVar>,
    /// Was für diesen Server in der Datei steht und nicht übernommen wird – die Schlüsselwörter, wie sie dort
    /// geschrieben sind (`ProxyCommand`, `Ciphers` …), jedes einmal.
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub skipped: Vec<String>,
}

/// Wann ein Block gilt.
#[derive(Clone)]
enum Rule {
    /// `Host` mit seinen Mustern.
    Host(Vec<String>),
    /// `Match` mit Bedingungen, die sich hier prüfen lassen – alle müssen zutreffen.
    Match(Vec<Criterion>),
    /// `Match` mit einer Bedingung, die hier niemand prüft: Der Block gilt nie. Der Text nennt sie.
    Never(String),
    /// Hier beginnt eine eingebundene Datei. Was in ihr vor dem ersten `Host` steht, gehört noch zum Block mit dem
    /// `Include`; ihre eigenen Blöcke gelten nur, wenn auch der gilt (wie bei OpenSSH: Ein `Include` in einem
    /// Block, der nicht passt, bringt nichts mit – Hinweis von Codex).
    Enter,
    /// Die eingebundene Datei ist zu Ende: Es geht im Block mit dem `Include` weiter, so wie er davor galt.
    Leave,
}

/// Eine Bedingung hinter `Match`.
#[derive(Clone)]
struct Criterion {
    negated: bool,
    subject: Subject,
    /// Muster, wie hinter `Host`, aber durch Komma getrennt.
    patterns: Vec<String>,
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum Subject {
    All,
    /// Der Name, zu dem verbunden wird: `HostName`, soweit bis hierher gesetzt, sonst der Alias.
    Host,
    /// Der Alias, wie er hinter `ssh` stünde.
    OriginalHost,
    /// Der Benutzer am Server, soweit bis hierher gesetzt, sonst der lokale.
    User,
    LocalUser,
}

/// Eine Zeile eines Blocks.
struct Setting {
    /// Das Schlüsselwort, klein – danach wird gesucht.
    keyword: String,
    /// So, wie es in der Datei steht – für [`ConfigHost::skipped`].
    written: String,
    arguments: Vec<String>,
}

struct Block {
    rule: Rule,
    options: Vec<Setting>,
}

impl Block {
    fn new(rule: Rule) -> Self {
        Self {
            rule,
            options: Vec::new(),
        }
    }
}

/// Die gelesene Konfiguration: ihre Server und die Regeln dahinter. Die Regeln braucht der Import noch einmal, um
/// einen Sprung-Server aufzulösen, der nicht als Alias, sondern ausgeschrieben dasteht.
pub struct Config {
    /// Alle Server, in der Reihenfolge der Datei.
    pub hosts: Vec<ConfigHost>,
    /// Die Dateien, aus denen das stammt: `config` selbst (auch wenn es sie nicht gibt), was sie einbindet, und
    /// die Verzeichnisse, die ein `Include` mit Muster durchsucht.
    pub files: Vec<PathBuf>,
    /// Bedingungen hinter `Match`, die hier niemand prüft (`exec`, `canonical` …) – ihre Blöcke gelten nie. Jede
    /// einmal, für die Anzeige.
    pub unchecked: Vec<String>,
    blocks: Vec<Block>,
    home: PathBuf,
    local_user: String,
}

impl Config {
    /// Liest die Konfiguration in `ssh_dir`. Ohne Datei: keine Server.
    pub fn load(ssh_dir: &Path) -> Self {
        let mut blocks = vec![Block::new(Rule::Host(vec!["*".to_owned()]))];
        let mut files = Vec::new();
        parse_file(&ssh_dir.join("config"), ssh_dir, 0, &mut blocks, &mut files);

        let home = ssh_dir.parent().unwrap_or(ssh_dir).to_owned();
        let local_user = std::env::var("USERNAME")
            .or_else(|_| std::env::var("USER"))
            .unwrap_or_default();

        let mut unchecked: Vec<String> = Vec::new();
        for block in &blocks {
            if let Rule::Never(condition) = &block.rule
                && !unchecked.contains(condition)
                && unchecked.len() < MAX_SKIPPED
            {
                unchecked.push(condition.clone());
            }
        }

        Self {
            hosts: hosts_of(&blocks, &home, &local_user),
            files,
            unchecked,
            blocks,
            home,
            local_user,
        }
    }

    /// Was `ssh <name>` aus der Konfiguration machen würde – auch für einen Namen, der dort nicht als Alias steht:
    /// Dann gelten die Blöcke mit Mustern (`Host *`).
    fn resolve(&self, name: &str) -> ConfigHost {
        resolve(name, &self.blocks, &self.home, &self.local_user)
    }
}

/// Alle Server der Konfiguration in `ssh_dir`, in der Reihenfolge der Datei. Ohne Datei: leer.
pub fn read(ssh_dir: &Path) -> Vec<ConfigHost> {
    Config::load(ssh_dir).hosts
}

/// Pfad der Datei, die [`read`] liest – für die Anzeige.
pub fn config_path(ssh_dir: &Path) -> PathBuf {
    ssh_dir.join("config")
}

/// Vermerkt, welche Server es schon als Profil gibt.
pub fn mark_existing(hosts: &mut [ConfigHost], store: &ProfileStore) {
    for host in hosts {
        host.exists = store
            .list()
            .iter()
            .any(|profile| profile.name.eq_ignore_ascii_case(&host.alias))
            || store.find(&host.host, host.port, &host.user).is_some();
    }
}

/// Größe und Änderungszeit je Datei – daran ist zu sehen, ob sich die Konfiguration geändert hat, ohne sie zu lesen.
pub type Stamp = Vec<Option<(u64, SystemTime)>>;

/// Der Stand der genannten Dateien; eine, die es nicht gibt, zählt als `None` (und fällt auf, sobald es sie gibt).
pub fn stamp(files: &[PathBuf]) -> Stamp {
    files
        .iter()
        .map(|file| {
            let meta = fs::metadata(file).ok()?;
            Some((meta.len(), meta.modified().ok()?))
        })
        .collect()
}

fn parse_file(path: &Path, ssh_dir: &Path, depth: usize, blocks: &mut Vec<Block>, files: &mut Vec<PathBuf>) {
    // Die Tiefe allein begrenzt nichts: Eine Datei, die sich mehrfach selbst einbindet, würde auf jeder Ebene
    // vervielfacht. Deshalb auch eine Grenze für alle Dateien zusammen und für die Größe einer einzelnen – gelesen
    // wird die Konfiguration bei jedem Start.
    const MAX_FILES: usize = 256;
    const MAX_SIZE: u64 = 1024 * 1024;
    if files.len() >= MAX_FILES {
        return;
    }

    files.push(path.to_owned());
    if fs::metadata(path).is_ok_and(|meta| meta.len() > MAX_SIZE) {
        return;
    }

    // Nur gewöhnliche Dateien: Ein Gerät (`Include /dev/zero`) hat die Größe 0 und hört nie auf.
    if !path.is_file() {
        return;
    }

    let Ok(text) = fs::read_to_string(path) else {
        return;
    };

    parse_text(&text, ssh_dir, depth, blocks, files);
}

// Die Server, die die Blöcke beim Namen nennen – ohne Muster und Verneinungen, jeder einmal.
fn hosts_of(blocks: &[Block], home: &Path, local_user: &str) -> Vec<ConfigHost> {
    let mut seen = HashSet::new();
    let mut hosts = Vec::new();
    let named = blocks.iter().filter_map(|block| match &block.rule {
        Rule::Host(patterns) => Some(patterns),
        Rule::Match(_) | Rule::Never(_) | Rule::Enter | Rule::Leave => None,
    });
    for alias in named.flatten() {
        // Jeder Server wird gegen alle Blöcke geprüft: Mehr als diese Zahl nennt die Liste nicht, damit eine
        // riesige (oder so gebaute) Datei den Start nicht aufhält.
        if hosts.len() >= MAX_HOSTS {
            break;
        }

        if alias.starts_with('!')
            || alias.len() > MAX_PATTERN
            || alias.contains(['*', '?'])
            || !seen.insert(alias.to_lowercase())
        {
            continue;
        }

        hosts.push(resolve(alias, blocks, home, local_user));
    }

    hosts
}

/// Für die Zufallstests ([`crate::fuzzing`]): liest einen Text wie eine Konfiguration, ohne die Platte anzufassen
/// (`Include` bleibt aus), und wertet jeden Server darin aus.
pub(crate) fn exercise(text: &str) {
    let mut blocks = vec![Block::new(Rule::Host(vec!["*".to_owned()]))];
    parse_text(text, Path::new("."), MAX_INCLUDE_DEPTH, &mut blocks, &mut Vec::new());
    for host in hosts_of(&blocks, Path::new("home"), "user") {
        if let Some(jump) = &host.jump {
            for spec in jump.split(',') {
                let _ = parse_jump(spec.trim());
            }
        }
    }
}

fn parse_text(text: &str, ssh_dir: &Path, depth: usize, blocks: &mut Vec<Block>, files: &mut Vec<PathBuf>) {
    for line in text.lines() {
        // Was danach käme, sind spätere Blöcke – und bei OpenSSH gewinnt der erste Wert: Es fehlte höchstens eine
        // Vorgabe vom Ende der Datei.
        if blocks.len() >= MAX_BLOCKS {
            return;
        }

        let Some((written, arguments)) = split_line(line) else {
            continue;
        };

        let keyword = written.to_lowercase();
        match keyword.as_str() {
            "host" => blocks.push(Block::new(Rule::Host(arguments))),
            "match" => blocks.push(Block::new(match_rule(&arguments))),
            "include" if depth < MAX_INCLUDE_DEPTH => {
                // Eine eingebundene Datei gilt im Zusammenhang des Blocks, in dem `Include` steht; danach geht
                // es in diesem Block weiter (`Rule::Enter`, `Rule::Leave`).
                for argument in &arguments {
                    files.extend(include_directory(argument, ssh_dir));
                    for file in include_targets(argument, ssh_dir) {
                        blocks.push(Block::new(Rule::Enter));
                        parse_file(&file, ssh_dir, depth + 1, blocks, files);
                        blocks.push(Block::new(Rule::Leave));
                    }
                }
            }
            _ => {
                if let Some(block) = blocks.last_mut()
                    && !arguments.is_empty()
                {
                    block.options.push(Setting {
                        keyword,
                        written,
                        arguments,
                    });
                }
            }
        }
    }
}

/// Liest, was hinter `Match` steht. Eine Bedingung, die sich hier nicht prüfen lässt, macht den ganzen Block
/// ungültig: Er gilt dann nie, statt vielleicht zu Unrecht.
fn match_rule(arguments: &[String]) -> Rule {
    let mut criteria = Vec::new();
    let mut words = arguments.iter();
    while let Some(word) = words.next() {
        let (negated, name) = match word.strip_prefix('!') {
            Some(rest) => (true, rest.to_lowercase()),
            None => (false, word.to_lowercase()),
        };
        let subject = match name.as_str() {
            "all" => {
                criteria.push(Criterion {
                    negated,
                    subject: Subject::All,
                    patterns: Vec::new(),
                });
                continue;
            }
            "host" => Subject::Host,
            "originalhost" => Subject::OriginalHost,
            "user" => Subject::User,
            "localuser" => Subject::LocalUser,
            // `exec` startet ein Programm, `canonical` und `final` brauchen einen zweiten Durchgang, den es hier
            // nicht gibt, der Rest hängt an der laufenden Verbindung oder an neueren Fassungen von OpenSSH.
            other => {
                let shown: String = other.chars().filter(|c| c.is_ascii_alphanumeric()).take(32).collect();
                return Rule::Never(if shown.is_empty() { "?".to_owned() } else { shown });
            }
        };
        let patterns: Vec<String> = words
            .next()
            .map(|patterns| {
                patterns
                    .split(',')
                    .filter(|pattern| !pattern.is_empty())
                    .map(str::to_owned)
                    .collect()
            })
            .unwrap_or_default();

        // Ohne Muster ist es keine Bedingung – OpenSSH nimmt eine solche Datei gar nicht an.
        if patterns.is_empty() {
            return Rule::Never(name);
        }

        criteria.push(Criterion {
            negated,
            subject,
            patterns,
        });
    }

    // `all` steht allein und unverneint, sonst ist die Zeile ungültig (so auch bei OpenSSH; Hinweis von Codex).
    let all = criteria
        .iter()
        .filter(|criterion| criterion.subject == Subject::All)
        .count();
    let invalid = all > 0 && (criteria.len() > 1 || criteria[0].negated);
    if criteria.is_empty() || invalid {
        Rule::Never(if invalid { "all".to_owned() } else { "?".to_owned() })
    } else {
        Rule::Match(criteria)
    }
}

/// Zerlegt eine Zeile in Schlüsselwort (wie geschrieben) und Argumente. `None` für Leerzeilen und Kommentare.
fn split_line(line: &str) -> Option<(String, Vec<String>)> {
    let line = line.trim();
    if line.is_empty() || line.starts_with('#') {
        return None;
    }

    let end = line.find(|c: char| c.is_whitespace() || c == '=').unwrap_or(line.len());
    let keyword = line[..end].to_owned();
    let rest = line[end..].trim_start_matches(|c: char| c.is_whitespace() || c == '=');

    let mut arguments = Vec::new();
    let mut current = String::new();
    let mut quoted = false;
    let mut started = false;
    for character in rest.chars() {
        match character {
            '"' => {
                quoted = !quoted;
                started = true;
            }
            c if c.is_whitespace() && !quoted => {
                if started {
                    arguments.push(std::mem::take(&mut current));
                    started = false;
                }
            }
            // Kommentar hinter den Argumenten.
            '#' if !quoted && !started => break,
            c => {
                current.push(c);
                started = true;
            }
        }
    }
    if started {
        arguments.push(current);
    }

    Some((keyword, arguments))
}

/// Dateien hinter einem `Include`: relativ zu `~/.ssh`, mit `*` und `?` im Dateinamen.
fn include_path(argument: &str, ssh_dir: &Path) -> PathBuf {
    let home = ssh_dir.parent().unwrap_or(ssh_dir);
    let expanded = expand_home(argument, home);
    if expanded.is_absolute() {
        expanded
    } else {
        ssh_dir.join(expanded)
    }
}

/// Das Verzeichnis, das ein `Include` mit Muster durchsucht. Kommt dort eine Datei hinzu oder fällt eine weg,
/// ändert sich seine Änderungszeit – so fällt auch eine Datei auf, die erst später auf das Muster passt.
fn include_directory(argument: &str, ssh_dir: &Path) -> Option<PathBuf> {
    let path = include_path(argument, ssh_dir);
    let name = path.file_name()?.to_str()?;
    if name.contains(['*', '?']) {
        path.parent().map(Path::to_owned)
    } else {
        None
    }
}

fn include_targets(argument: &str, ssh_dir: &Path) -> Vec<PathBuf> {
    let path = include_path(argument, ssh_dir);

    let Some(name) = path.file_name().and_then(|name| name.to_str()) else {
        return Vec::new();
    };
    if !name.contains(['*', '?']) {
        return vec![path];
    }

    let Some(Ok(directory)) = path.parent().map(fs::read_dir) else {
        return Vec::new();
    };
    let mut files: Vec<PathBuf> = directory
        .flatten()
        .filter(|entry| entry.file_name().to_str().is_some_and(|file| glob(name, file)))
        .map(|entry| entry.path())
        .filter(|file| file.is_file())
        .collect();
    files.sort();
    files
}

fn expand_home(value: &str, home: &Path) -> PathBuf {
    let rest = value
        .strip_prefix("~/")
        .or_else(|| value.strip_prefix("~\\"))
        .or_else(|| value.strip_prefix("%d/"));
    match rest {
        // Stück für Stück anhängen, damit unter Windows die richtigen Trenner entstehen.
        Some(rest) => rest
            .split(['/', '\\'])
            .filter(|part| !part.is_empty())
            .fold(home.to_owned(), |path, part| path.join(part)),
        None => PathBuf::from(value),
    }
}

/// Länger ist kein Name eines Servers oder Benutzers; ein Muster oder Text darüber passt nie. So bleibt auch ein
/// Vergleich mit einer überlangen Zeile der Datei kurz.
const MAX_PATTERN: usize = 1024;

/// Mustervergleich wie bei `Host`: `*` für beliebig viele, `?` für genau ein Zeichen, ohne Rücksicht auf
/// Groß- und Kleinschreibung.
fn glob(pattern: &str, text: &str) -> bool {
    glob_with(pattern, text, true)
}

/// Wie [`glob`]; mit `fold` ohne Rücksicht auf Groß- und Kleinschreibung (Namen von Servern), sonst genau
/// (Benutzer).
fn glob_with(pattern: &str, text: &str, fold: bool) -> bool {
    if pattern.len() > MAX_PATTERN || text.len() > MAX_PATTERN {
        return false;
    }

    // Der häufigste Fall, und der, der bei vielen Servern zählt (jeder wird gegen jeden Block geprüft): ein Name
    // ohne Platzhalter. Dann genügt ein Vergleich, ohne etwas anzulegen.
    if pattern.is_ascii() && text.is_ascii() && !pattern.contains(['*', '?']) {
        return if fold {
            pattern.eq_ignore_ascii_case(text)
        } else {
            pattern == text
        };
    }

    let letters = |value: &str| -> Vec<char> {
        if fold {
            value.to_lowercase().chars().collect()
        } else {
            value.chars().collect()
        }
    };
    let (pattern, text) = (letters(pattern), letters(text));

    // Ohne Rekursion: Es genügt, sich den letzten `*` zu merken und ihn bei einem Fehlschlag ein Zeichen mehr
    // schlucken zu lassen. Jeder `*` einzeln durchprobiert rechnete bei `*a*a*a*…` praktisch endlos.
    let (mut at, mut seen) = (0, 0);
    let mut star: Option<(usize, usize)> = None;
    while seen < text.len() {
        match pattern.get(at) {
            Some('*') => {
                star = Some((at, seen));
                at += 1;
            }
            Some(&expected) if expected == '?' || expected == text[seen] => {
                at += 1;
                seen += 1;
            }
            _ => match star {
                Some((star_at, taken)) => {
                    star = Some((star_at, taken + 1));
                    at = star_at + 1;
                    seen = taken + 1;
                }
                None => return false,
            },
        }
    }

    pattern[at..].iter().all(|&rest| rest == '*')
}

// Eine Liste von Mustern passt, wenn eines passt und kein verneintes (`!muster`).
fn applies(patterns: &[String], name: &str, fold: bool) -> bool {
    let mut positive = false;
    for pattern in patterns {
        match pattern.strip_prefix('!') {
            Some(negated) if glob_with(negated, name, fold) => return false,
            Some(_) => {}
            None => positive |= glob_with(pattern, name, fold),
        }
    }

    positive
}

/// Was die App aus der Datei übernimmt – alles andere steht in [`ConfigHost::skipped`], außer es ist ohne Belang.
const TAKEN: [&str; 16] = [
    "hostname",
    "port",
    "user",
    "identityfile",
    "identitiesonly",
    "proxyjump",
    "forwardagent",
    "serveraliveinterval",
    "serveralivecountmax",
    "localforward",
    "remoteforward",
    "dynamicforward",
    "compression",
    "connecttimeout",
    "addressfamily",
    "setenv",
];

/// Angaben, deren Fehlen in der App nichts ändert: Sie betreffen das Programm `ssh` selbst (sein Protokoll, seine
/// Anzeige, seine gemeinsam benutzten Verbindungen, seinen Agenten) und nicht den Server.
const WITHOUT_MEANING: [&str; 16] = [
    "addkeystoagent",
    "usekeychain",
    "controlmaster",
    "controlpath",
    "controlpersist",
    "loglevel",
    "visualhostkey",
    "hashknownhosts",
    "updatehostkeys",
    "escapechar",
    "tcpkeepalive",
    "ipqos",
    "batchmode",
    "permitlocalcommand",
    "enableescapecommandline",
    "ignoreunknown",
];

fn resolve(alias: &str, blocks: &[Block], home: &Path, local_user: &str) -> ConfigHost {
    // Der erste Wert gewinnt – deshalb stehen bei OpenSSH die allgemeinen Blöcke am Ende der Datei. Die Blöcke
    // der Reihe nach: Ein `Match host` sieht den `HostName`, der bis dahin gesetzt ist.
    let mut options: HashMap<&str, &[String]> = HashMap::new();
    let mut forwards: Vec<&Setting> = Vec::new();
    let mut variables: Vec<&str> = Vec::new();
    let mut skipped: Vec<String> = Vec::new();
    let first = |options: &HashMap<&str, &[String]>, keyword: &str| -> Option<String> {
        options.get(keyword).and_then(|arguments| arguments.first()).cloned()
    };
    let host_name = |options: &HashMap<&str, &[String]>| {
        first(options, "hostname")
            .map(|name| name.replace("%h", alias).replace("%%", "%"))
            .unwrap_or_else(|| alias.to_owned())
    };

    // Ob der Block gilt, in dem gerade gelesen wird – und für jede offene eingebundene Datei, ob der Block mit
    // ihrem `Include` galt.
    let mut active = true;
    let mut including: Vec<bool> = Vec::new();
    for block in blocks {
        match &block.rule {
            Rule::Enter => including.push(active),
            Rule::Leave => active = including.pop().unwrap_or(false),
            rule => {
                active = including.iter().all(|open| *open)
                    && match rule {
                        Rule::Host(patterns) => applies(patterns, alias, true),
                        Rule::Match(criteria) => criteria.iter().all(|criterion| {
                            // Namen von Servern ohne Rücksicht auf Groß- und Kleinschreibung, Benutzer genau – so
                            // hält es OpenSSH (Hinweis von Codex, an `ssh -G` nachgeprüft).
                            let met = match criterion.subject {
                                Subject::All => true,
                                Subject::Host => applies(&criterion.patterns, &host_name(&options), true),
                                Subject::OriginalHost => applies(&criterion.patterns, alias, true),
                                Subject::User => applies(
                                    &criterion.patterns,
                                    &first(&options, "user").unwrap_or_else(|| local_user.to_owned()),
                                    false,
                                ),
                                Subject::LocalUser => applies(&criterion.patterns, local_user, false),
                            };
                            met != criterion.negated
                        }),
                        Rule::Never(_) | Rule::Enter | Rule::Leave => false,
                    };
            }
        }
        if !active {
            continue;
        }

        for setting in &block.options {
            match setting.keyword.as_str() {
                "localforward" | "remoteforward" | "dynamicforward" => forwards.push(setting),
                "setenv" => variables.extend(setting.arguments.iter().map(String::as_str)),
                keyword if TAKEN.contains(&keyword) => {
                    options.entry(keyword).or_insert(setting.arguments.as_slice());
                }
                keyword if WITHOUT_MEANING.contains(&keyword) => {}
                _ => skip(&mut skipped, &setting.written),
            }
        }
    }

    let host = host_name(&options);
    let user = first(&options, "user").unwrap_or_else(|| local_user.to_owned());
    let key_path = first(&options, "identityfile").map(|file| {
        expand_home(
            &file.replace("%h", &host).replace("%r", &user).replace("%u", local_user),
            home,
        )
        .display()
        .to_string()
    });
    let yes = |keyword: &str| first(&options, keyword).is_some_and(|value| value.eq_ignore_ascii_case("yes"));
    let number = |keyword: &str| first(&options, keyword).and_then(|value| value.parse::<u32>().ok());

    // Weiterleitungen: jede Zeile eine, in der Reihenfolge der Datei. Was sich nicht als Tunnel fassen lässt
    // (Unix-Sockets, entfernte dynamische), steht bei dem, was nicht übernommen wird.
    let mut tunnels: Vec<Tunnel> = Vec::new();
    for setting in forwards {
        match forward(&setting.keyword, &setting.arguments) {
            Some(tunnel) if tunnels.len() < crate::profiles::MAX_TUNNELS => {
                if !tunnels.iter().any(|known| {
                    (known.kind, known.listen_port, &known.target_host, known.target_port)
                        == (tunnel.kind, tunnel.listen_port, &tunnel.target_host, tunnel.target_port)
                }) {
                    tunnels.push(tunnel);
                }
            }
            _ => skip(&mut skipped, &setting.written),
        }
    }

    // Variablen: `NAME=wert`, je Name der erste. Was kein Name oder kein Wert sein kann, fällt weg.
    let mut env: Vec<EnvVar> = Vec::new();
    for variable in variables {
        if let Some((name, value)) = variable.split_once('=')
            && crate::profiles::env_name(name)
            && crate::profiles::env_value(value)
            && env.len() < crate::profiles::MAX_ENV
            && env.iter().all(|known| known.name != name)
        {
            env.push(EnvVar {
                name: name.to_owned(),
                value: value.to_owned(),
            });
        }
    }

    ConfigHost {
        alias: alias.to_owned(),
        port: first(&options, "port").and_then(|port| port.parse().ok()).unwrap_or(22),
        identities_only: key_path.is_some() && yes("identitiesonly"),
        key_path,
        jump: first(&options, "proxyjump").filter(|jump| !jump.eq_ignore_ascii_case("none")),
        forward_agent: yes("forwardagent"),
        // Zwischen „keine“ und dem kürzesten Abstand des Profils gibt es nichts: Dort gilt der kürzeste.
        keepalive_interval: number("serveraliveinterval").map(|seconds| match seconds {
            0 => 0,
            seconds => seconds.clamp(5, 3600),
        }),
        keepalive_max: number("serveralivecountmax").map(|count| {
            count.clamp(
                *crate::profiles::KEEPALIVE_MAX.start(),
                *crate::profiles::KEEPALIVE_MAX.end(),
            )
        }),
        tunnels,
        compression: yes("compression"),
        connect_timeout: number("connecttimeout")
            .filter(|seconds| *seconds > 0)
            .map(|seconds| seconds.clamp(3, 120)),
        address_family: first(&options, "addressfamily").and_then(|family| match family.to_lowercase().as_str() {
            "inet" => Some("ipv4".to_owned()),
            "inet6" => Some("ipv6".to_owned()),
            _ => None,
        }),
        env,
        skipped,
        host,
        user,
        exists: false,
    }
}

// Merkt ein Schlüsselwort, das nicht übernommen wird – einmal, gekürzt und nur aus Zeichen, die ein Schlüsselwort
// haben kann: Die Liste wird dem Benutzer gezeigt, und die Datei kann auch von jemand anderem stammen.
fn skip(skipped: &mut Vec<String>, written: &str) {
    let shown: String = written.chars().filter(|c| c.is_ascii_alphanumeric()).take(40).collect();
    if !shown.is_empty()
        && skipped.len() < MAX_SKIPPED
        && !skipped.iter().any(|known| known.eq_ignore_ascii_case(&shown))
    {
        skipped.push(shown);
    }
}

/// Eine Weiterleitung aus der Datei als Tunnel: `LocalForward [adresse:]port host:port`, `RemoteForward` ebenso,
/// `DynamicForward [adresse:]port`. Die Adresse, an der gelauscht würde, zählt nicht – der Tunnel lauscht nur für
/// diesen Rechner (bzw. am Server nur für den Server); das Netz zu öffnen, ist eine Entscheidung in der App.
/// `None` für alles andere: Unix-Sockets, `RemoteForward` ohne Ziel, unlesbare Ports.
fn forward(keyword: &str, arguments: &[String]) -> Option<Tunnel> {
    let (_, listen_port) = host_and_port(arguments.first()?)?;
    let (kind, target) = match keyword {
        "dynamicforward" if arguments.len() == 1 => (TunnelKind::Dynamic, None),
        "localforward" if arguments.len() == 2 => (TunnelKind::Local, Some(arguments[1].as_str())),
        "remoteforward" if arguments.len() == 2 => (TunnelKind::Remote, Some(arguments[1].as_str())),
        _ => return None,
    };
    let (target_host, target_port) = match target {
        None => (String::new(), 0),
        // Ein Ziel hat immer einen Host: `host:port` oder `[v6]:port`.
        Some(target) => match host_and_port(target)? {
            (Some(host), port) => (host.to_owned(), port),
            (None, _) => return None,
        },
    };

    Tunnel {
        kind,
        listen_port,
        target_host,
        target_port,
        auto: true,
        ..Tunnel::default()
    }
    .normalized()
}

// Zerlegt `port`, `host:port` oder `[v6]:port` – so streng wie OpenSSH: Eine Adresse mit Doppelpunkten steht in
// eckigen Klammern, sonst gibt es genau einen Doppelpunkt (`::1:80` ist nichts), und ein Pfad (Unix-Socket) ist
// kein Host. Der Port ist nie 0. Hinweis von Codex: Vorher zählte nur, was hinter dem letzten Doppelpunkt steht.
fn host_and_port(argument: &str) -> Option<(Option<&str>, u16)> {
    let (host, port) = match argument.strip_prefix('[') {
        Some(rest) => {
            let (host, port) = rest.split_once("]:")?;
            (Some(host), port)
        }
        None => match argument.split_once(':') {
            Some((host, port)) if !port.contains(':') => (Some(host), port),
            Some(_) => return None,
            None => (None, argument),
        },
    };
    let plain = |host: &str| {
        !host.is_empty()
            && host
                .chars()
                .all(|c| c.is_ascii_alphanumeric() || matches!(c, '.' | '-' | '_' | ':' | '*'))
    };
    if host.is_some_and(|host| !plain(host)) || !port.chars().all(|c| c.is_ascii_digit()) {
        return None;
    }

    Some((host, port.parse::<u16>().ok().filter(|port| *port != 0)?))
}

/// Ein Sprung-Server, wie er hinter `ProxyJump` steht: `[user@]host[:port]` oder `ssh://…`.
fn parse_jump(spec: &str) -> Option<(Option<String>, String, u16)> {
    let spec = spec.strip_prefix("ssh://").unwrap_or(spec).trim_end_matches('/');
    let (user, address) = match spec.rsplit_once('@') {
        Some((user, address)) => (Some(user.to_owned()), address),
        None => (None, spec),
    };

    // IPv6 steht in eckigen Klammern, sonst wäre der Port nicht abzugrenzen.
    let (host, port) = if let Some(rest) = address.strip_prefix('[') {
        let (host, tail) = rest.split_once(']')?;
        (host, tail.strip_prefix(':'))
    } else {
        match address.rsplit_once(':') {
            Some((host, port)) if !host.contains(':') => (host, Some(port)),
            _ => (address, None),
        }
    };
    let port = match port {
        Some(port) => port.parse().ok()?,
        None => 22,
    };

    (!host.is_empty()).then(|| (user, host.to_owned(), port))
}

/// Legt für die genannten Server Profile an und liefert sie. Sprung-Server werden mit angelegt, wenn es für sie
/// noch kein Profil gibt. Server, die sich nicht als Profil eignen, werden übersprungen.
///
/// Mit `linked` bleiben die genannten Profile mit ihrem Eintrag der Datei verknüpft (`Profile::ssh_alias`):
/// [`refresh`] liest ihre Angaben dann bei jedem Start neu – wer die Datei pflegt, pflegt nicht doppelt.
pub fn import(store: &mut ProfileStore, config: &Config, names: &[String], linked: bool) -> io::Result<Vec<Profile>> {
    let mut created = Vec::new();
    for name in names {
        if let Some(host) = config.hosts.iter().find(|host| host.alias == *name)
            && let Some(id) = profile_for(store, config, host, &mut created, 0)?
            && linked
            && let Some(profile) = store.get(&id).cloned()
            && profile.ssh_alias.as_deref() != Some(host.alias.as_str())
        {
            let saved = store.save(Profile {
                ssh_alias: Some(host.alias.clone()),
                ..profile
            })?;
            if let Some(known) = created.iter_mut().find(|known| known.id == saved.id) {
                *known = saved;
            }
        }
    }

    Ok(created)
}

/// Liest die Angaben verknüpfter Profile neu aus der Datei: Adresse, Port, Benutzer, Schlüssel und Agent
/// Forwarding. Hat das Profil eine Identität, bleiben Benutzer und Schlüssel, wie sie sind: Die hat der Benutzer in
/// der App ausdrücklich gewählt, und die App schreibt beides bei jedem Speichern aus ihr ins Profil – die Datei
/// bestimmt dann nur noch, wo der Server steht. Der Sprung-Server wird nachgezogen, wenn er in der Datei ein einzelner Name ist, zu dem es ein
/// Profil gibt; sonst bleibt er, wie er ist. Ein Profil, dessen Eintrag es nicht mehr gibt, bleibt unverändert –
/// es soll nicht verschwinden oder unbrauchbar werden, weil jemand die Datei umbenannt hat. Liefert, wie viele
/// Profile sich geändert haben.
pub fn refresh(store: &mut ProfileStore, config: &Config) -> io::Result<usize> {
    let mut changed = 0;
    for profile in store.list().to_vec() {
        let Some(alias) = profile.ssh_alias.as_deref() else {
            continue;
        };
        let Some(host) = config.hosts.iter().find(|host| host.alias == alias) else {
            continue;
        };

        let jump = match host.jump.as_deref().map(str::trim) {
            None | Some("" | "none") => None,
            Some(name) => store
                .list()
                .iter()
                .find(|other| other.id != profile.id && other.name.eq_ignore_ascii_case(name))
                .map(|other| other.id.clone())
                .or_else(|| profile.jump.clone()),
        };
        let (user, key_path) = if profile.identity.is_some() {
            (profile.user.clone(), profile.key_path.clone())
        } else {
            (host.user.clone(), host.key_path.clone())
        };
        let Ok(updated) = (Profile {
            host: host.host.clone(),
            port: host.port,
            user,
            key_path,
            forward_agent: host.forward_agent,
            // Gehört zur Anmeldung wie der Schlüssel: Mit einer Identität bleibt es deren Sache.
            identities_only: if profile.identity.is_some() {
                profile.identities_only
            } else {
                host.identities_only
            },
            jump,
            ..profile.clone()
        })
        .normalized() else {
            continue;
        };

        if updated != profile {
            store.save(updated)?;
            changed += 1;
        }
    }

    Ok(changed)
}

/// Kennung des Profils zu einem Server der Konfiguration; legt es an, wenn es noch keines gibt.
fn profile_for(
    store: &mut ProfileStore,
    config: &Config,
    host: &ConfigHost,
    created: &mut Vec<Profile>,
    depth: usize,
) -> io::Result<Option<String>> {
    // Ein Alias der Datei ist das Profil gleichen Namens – auch wenn dessen Ziel inzwischen ein anderes ist. Ein
    // ausgeschriebener Sprung-Server hat keinen eigenen Namen: Für ihn zählt nur das Ziel.
    let from_file = config.hosts.iter().any(|known| known.alias == host.alias);
    if let Some(existing) = store
        .list()
        .iter()
        .find(|profile| from_file && profile.name.eq_ignore_ascii_case(&host.alias))
        .or_else(|| store.find(&host.host, host.port, &host.user))
    {
        return Ok(Some(existing.id.clone()));
    }

    // Heißt schon ein anderes Profil wie der Server, bekommt dieser Zugang einen Namen, der ihn unterscheidet.
    let taken = !from_file
        && store
            .list()
            .iter()
            .any(|profile| profile.name.eq_ignore_ascii_case(&host.alias));
    let name = match (taken, host.port) {
        (false, _) => host.alias.clone(),
        (true, 22) => format!("{}@{}", host.user, host.alias),
        (true, port) => format!("{}@{}:{port}", host.user, host.alias),
    };

    let jump = match host.jump.as_deref() {
        Some(list) if depth < MAX_JUMPS => jump_chain(store, config, host, list, created, depth)?,
        _ => None,
    };

    let Ok(profile) = (Profile {
        name,
        host: host.host.clone(),
        port: host.port,
        user: host.user.clone(),
        key_path: host.key_path.clone(),
        forward_agent: host.forward_agent,
        identities_only: host.identities_only,
        keepalive_interval: host.keepalive_interval,
        keepalive_max: host.keepalive_max,
        tunnels: host.tunnels.clone(),
        compression: host.compression,
        connect_timeout: host.connect_timeout,
        address_family: host.address_family.clone(),
        env: host.env.clone(),
        jump,
        ..Profile::default()
    })
    .normalized() else {
        return Ok(None);
    };

    let saved = store.save(profile)?;
    created.push(saved.clone());
    Ok(Some(saved.id))
}

/// Das Profil, über das `target` erreicht wird. `ProxyJump a,b` heißt: erst `a`, von dort `b`, von dort das Ziel –
/// es entsteht also eine Kette, und geliefert wird ihr letztes Glied.
fn jump_chain(
    store: &mut ProfileStore,
    config: &Config,
    target: &ConfigHost,
    list: &str,
    created: &mut Vec<Profile>,
    depth: usize,
) -> io::Result<Option<String>> {
    let mut via: Option<String> = None;
    for (index, spec) in list
        .split(',')
        .map(str::trim)
        .filter(|spec| !spec.is_empty())
        .enumerate()
    {
        let Some(server) = jump_server(config, target, spec) else {
            return Ok(None);
        };

        via = match via {
            // Der erste Sprung ist ein gewöhnlicher Server mit allem, was seine eigene Konfiguration sagt.
            None if index == 0 => profile_for(store, config, &server, created, depth + 1)?,
            // Jeder weitere wird über den davor erreicht, was auch immer sein eigener Eintrag vorsieht.
            Some(previous) => hop_through(store, &server, &previous, created)?,
            None => return Ok(None),
        };
        if via.is_none() {
            return Ok(None);
        }
    }

    Ok(via)
}

/// Der Server hinter einem Glied von `ProxyJump`: meist der Alias eines anderen Eintrags, sonst
/// `[user@]host[:port]`.
fn jump_server(config: &Config, target: &ConfigHost, spec: &str) -> Option<ConfigHost> {
    if let Some(host) = config
        .hosts
        .iter()
        .find(|host| host.alias.eq_ignore_ascii_case(spec) && host.alias != target.alias)
    {
        return Some(host.clone());
    }

    // Ausgeschrieben: Wie bei OpenSSH gilt für den Namen die Konfiguration (`Host *` mit Schlüssel und Benutzer);
    // was in der Angabe selbst steht, geht vor.
    let (user, name, port) = parse_jump(spec)?;
    let mut server = config.resolve(&name);
    if spec.trim_end_matches('/').ends_with(&format!(":{port}")) {
        server.port = port;
    }
    if let Some(user) = user {
        server.user = user;
    }
    server.forward_agent = false;
    // Was die Datei sonst für den Namen hergibt, gehört zum Ziel einer eigenen Sitzung, nicht zu einem Sprung.
    (server.tunnels, server.env, server.skipped) = (Vec::new(), Vec::new(), Vec::new());

    // Steht derselbe Server auch unter einem Alias in der Datei, ist der gemeint – sonst entstünden zwei Profile für
    // dasselbe Ziel, und welches die anderen benutzen, hinge von der Reihenfolge ab.
    let named = config.hosts.iter().find(|host| {
        host.alias != target.alias
            && host.host.eq_ignore_ascii_case(&server.host)
            && host.port == server.port
            && host.user == server.user
    });
    Some(named.cloned().unwrap_or(server))
}

/// Ein Profil für `server`, das über das Profil `via` verbindet. Gibt es schon eines mit genau diesem Weg, wird
/// es benutzt; ein vorhandenes Profil mit anderem Weg bleibt unangetastet, und es entsteht ein eigenes.
fn hop_through(
    store: &mut ProfileStore,
    server: &ConfigHost,
    via: &str,
    created: &mut Vec<Profile>,
) -> io::Result<Option<String>> {
    if let Some(existing) = store.list().iter().find(|profile| {
        profile.host.eq_ignore_ascii_case(&server.host)
            && profile.port == server.port
            && profile.user == server.user
            && profile.jump.as_deref() == Some(via)
    }) {
        return Ok(Some(existing.id.clone()));
    }

    let taken = store
        .list()
        .iter()
        .any(|profile| profile.name.eq_ignore_ascii_case(&server.alias));
    let name = match store.get(via) {
        Some(previous) if taken => format!("{} via {}", server.alias, previous.name),
        _ => server.alias.clone(),
    };

    let Ok(profile) = (Profile {
        name,
        host: server.host.clone(),
        port: server.port,
        user: server.user.clone(),
        key_path: server.key_path.clone(),
        jump: Some(via.to_owned()),
        ..Profile::default()
    })
    .normalized() else {
        return Ok(None);
    };

    let saved = store.save(profile)?;
    created.push(saved.clone());
    Ok(Some(saved.id))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn ssh_dir(name: &str, config: &str) -> PathBuf {
        let directory = std::env::temp_dir()
            .join(format!("nexus-core-sshconfig-{}-{name}", std::process::id()))
            .join(".ssh");
        let _ = fs::remove_dir_all(&directory);
        fs::create_dir_all(&directory).unwrap();
        fs::write(directory.join("config"), config).unwrap();
        directory
    }

    fn store(name: &str) -> ProfileStore {
        let directory = std::env::temp_dir().join(format!("nexus-core-sshconfig-{}-{name}-data", std::process::id()));
        let _ = fs::remove_dir_all(&directory);
        ProfileStore::load(directory.join("profiles.json")).unwrap().0
    }

    #[test]
    fn hosts_take_the_first_matching_value_like_openssh() {
        let directory = ssh_dir(
            "first",
            "# Kommentar\n\
             Host web01 web-alias\n\
             \tHostName 10.0.1.10\n\
             \tUser deploy\n\
             \tPort 2222\n\
             \tIdentityFile ~/.ssh/id_web\n\
             \n\
             Host db\n\
             \tHostName=db.example.org\n\
             \tUser \"db admin\"   # mit Leerzeichen\n\
             \n\
             Host *.example.org !db\n\
             \tUser wildcard\n\
             \n\
             Host *\n\
             \tUser fallback\n\
             \tPort 22022\n\
             \tForwardAgent yes\n",
        );

        let hosts = read(&directory);
        assert_eq!(
            hosts.iter().map(|host| host.alias.as_str()).collect::<Vec<_>>(),
            ["web01", "web-alias", "db"],
            "Muster mit * sind keine Server"
        );

        let web = &hosts[0];
        assert_eq!(
            (web.host.as_str(), web.port, web.user.as_str()),
            ("10.0.1.10", 2222, "deploy")
        );
        assert!(web.forward_agent, "ForwardAgent kommt aus „Host *“");
        let key = PathBuf::from(web.key_path.clone().unwrap());
        assert_eq!(key, directory.join("id_web"));

        let db = &hosts[2];
        assert_eq!(
            (db.host.as_str(), db.port, db.user.as_str()),
            ("db.example.org", 22022, "db admin")
        );
        assert_eq!(db.key_path, None);
    }

    #[test]
    fn a_missing_config_is_an_empty_list() {
        let directory = std::env::temp_dir().join(format!("nexus-core-sshconfig-{}-none", std::process::id()));
        assert!(read(&directory).is_empty());
    }

    #[test]
    fn includes_are_followed_and_match_blocks_skipped() {
        let directory = ssh_dir(
            "include",
            "Include config.d/*.conf\n\
             Host main\n\
             \tHostName main.example.org\n\
             Match host main exec \"true\"\n\
             \tUser from-match\n\
             Match all\n\
             \tUser everyone\n",
        );
        fs::create_dir_all(directory.join("config.d")).unwrap();
        fs::write(
            directory.join("config.d").join("a.conf"),
            "Host included\n  HostName %h.internal\n  User inc\n",
        )
        .unwrap();
        fs::write(directory.join("config.d").join("ignored.txt"), "Host nope\n").unwrap();

        let hosts = read(&directory);
        assert_eq!(
            hosts.iter().map(|host| host.alias.as_str()).collect::<Vec<_>>(),
            ["included", "main"]
        );
        assert_eq!(hosts[0].host, "included.internal");
        assert_eq!(hosts[0].user, "inc");
        assert_eq!(
            hosts[1].user, "everyone",
            "„Match all“ gilt, ein Match mit Bedingung nicht"
        );
    }

    #[test]
    fn patterns_match_like_host_lines() {
        assert!(glob("*", "anything"));
        assert!(glob("web??", "WEB01"));
        assert!(!glob("web??", "web1"));
        assert!(glob("*.example.org", "db.example.org"));
        assert!(!glob("*.example.org", "example.org"));

        let patterns: Vec<String> = vec!["*.example.org".into(), "!db.*".into()];
        assert!(applies(&patterns, "web.example.org", true));
        assert!(!applies(&patterns, "db.example.org", true));

        // Namen von Servern ohne Rücksicht auf die Schreibung, Benutzer genau.
        let users: Vec<String> = vec!["Alice".into()];
        assert!(applies(&users, "alice", true) && !applies(&users, "alice", false));
        assert!(applies(&users, "Alice", false));

        // Was länger ist als jeder Name, passt nie – auch nicht auf `*`.
        assert!(!glob("*", &"a".repeat(MAX_PATTERN + 1)));
        assert!(!glob(&"*".repeat(MAX_PATTERN + 1), "a"));
    }

    #[test]
    fn a_pattern_with_many_stars_is_decided_at_once() {
        // Jeder `*` einzeln durchprobiert bräuchte hierfür länger, als irgendwer wartet.
        let pattern = format!("{}b", "*a".repeat(40));
        let text = "a".repeat(200);

        let started = std::time::Instant::now();
        assert!(!glob(&pattern, &text));
        assert!(glob(&pattern, &format!("{text}b")));
        assert!(started.elapsed() < std::time::Duration::from_secs(1));

        assert!(glob("*", ""));
        assert!(glob("a*b*c", "aXXbYYc"));
        assert!(!glob("a*b*c", "aXXbYY"));
        assert!(glob("?*", "x"));
        assert!(!glob("?", ""));
        assert!(glob("**a**", "bab"));
    }

    #[test]
    fn jump_specifications_are_parsed() {
        assert_eq!(parse_jump("bastion"), Some((None, "bastion".into(), 22)));
        assert_eq!(
            parse_jump("admin@bastion:2200"),
            Some((Some("admin".into()), "bastion".into(), 2200))
        );
        assert_eq!(
            parse_jump("ssh://admin@[2001:db8::1]:2200"),
            Some((Some("admin".into()), "2001:db8::1".into(), 2200))
        );
        assert_eq!(parse_jump("2001:db8::1"), Some((None, "2001:db8::1".into(), 22)));
        assert_eq!(parse_jump("host:keinport"), None);
    }

    #[test]
    fn importing_creates_profiles_and_links_jump_hosts() {
        let directory = ssh_dir(
            "import",
            "Host bastion\n  HostName bastion.example.org\n  User jump\n\
             Host inner\n  HostName 10.0.0.5\n  User app\n  ProxyJump bastion\n\
             Host literal\n  HostName 10.0.0.6\n  User app\n  ProxyJump ops@gate.example.org:2200\n\
             Host broken\n  HostName -oProxyCommand=calc\n  User app\n",
        );
        let mut store = store("import");
        let hosts = Config::load(&directory);

        let created = import(
            &mut store,
            &hosts,
            &["inner".to_owned(), "literal".to_owned(), "broken".to_owned()],
            false,
        )
        .unwrap();
        let names: Vec<&str> = created.iter().map(|profile| profile.name.as_str()).collect();
        assert_eq!(
            names,
            ["bastion", "inner", "gate.example.org", "literal"],
            "Sprung-Server kommen mit, der unbrauchbare Eintrag nicht"
        );

        let by_name = |name: &str| {
            store
                .list()
                .iter()
                .find(|profile| profile.name == name)
                .unwrap()
                .clone()
        };
        assert_eq!(by_name("inner").jump, Some(by_name("bastion").id));
        let gate = by_name("gate.example.org");
        assert_eq!((gate.user.as_str(), gate.port), ("ops", 2200));
        assert_eq!(by_name("literal").jump, Some(gate.id));

        // Ein zweiter Import legt nichts doppelt an.
        assert!(
            import(&mut store, &hosts, &["inner".to_owned()], false)
                .unwrap()
                .is_empty()
        );
        let mut marked = hosts.hosts.clone();
        mark_existing(&mut marked, &store);
        assert_eq!(
            marked.iter().map(|host| host.exists).collect::<Vec<_>>(),
            [true, true, true, false]
        );
    }

    #[test]
    fn a_written_out_jump_host_follows_the_config_and_prefers_its_alias() {
        let directory = ssh_dir(
            "written-out",
            "Host plain
  HostName 10.0.0.9
  User plain
  ProxyJump root@gate.example.org:22
             Host other
  HostName 10.0.0.10
  User app
  ProxyJump gate2.example.org
             Host third
  HostName 10.0.0.11
  User app
  ProxyJump ops@gate2.example.org:2200
             Host bastion
  HostName gate.example.org
  User root
             Host *
  IdentityFile ~/.ssh/id_all
  User fallback
",
        );
        let mut store = store("written-out");
        let config = Config::load(&directory);

        // Der ausgeschriebene Sprung-Server ist derselbe wie „bastion“ weiter unten in der Datei: Es entsteht dieses
        // eine Profil – unter seinem Namen und mit seinem Schlüssel, nicht ein zweites namens „gate.example.org“.
        let created = import(&mut store, &config, &["plain".to_owned(), "bastion".to_owned()], false).unwrap();
        let names: Vec<&str> = created.iter().map(|profile| profile.name.as_str()).collect();
        assert_eq!(names, ["bastion", "plain"]);
        assert_eq!(created[1].jump.as_deref(), Some(created[0].id.as_str()));
        assert!(
            created[0]
                .key_path
                .as_deref()
                .is_some_and(|key| key.ends_with("id_all"))
        );

        // Ohne Alias gilt für den Namen, was die Konfiguration für ihn sagt – wie bei `ssh gate2.example.org`:
        // Benutzer und Schlüssel aus `Host *`, nicht der Benutzer des Ziels.
        let created = import(&mut store, &config, &["other".to_owned()], false).unwrap();
        let gate = &created[0];
        assert_eq!(
            (gate.name.as_str(), gate.user.as_str(), gate.port),
            ("gate2.example.org", "fallback", 22)
        );
        assert!(gate.key_path.as_deref().is_some_and(|key| key.ends_with("id_all")));

        // Was in der Angabe selbst steht, geht vor – das ist ein anderer Zugang und damit ein eigenes Profil.
        let created = import(&mut store, &config, &["third".to_owned()], false).unwrap();
        let other_gate = store.get(created.last().unwrap().jump.as_deref().unwrap()).unwrap();
        assert_eq!(
            (other_gate.name.as_str(), other_gate.user.as_str(), other_gate.port),
            ("ops@gate2.example.org:2200", "ops", 2200)
        );
        assert_ne!(other_gate.id, gate.id);
    }

    #[test]
    fn several_jumps_become_a_chain_in_the_order_they_are_passed() {
        let directory = ssh_dir(
            "chain",
            "Host edge\n  HostName edge.example.org\n  User e\n\
             Host middle\n  HostName 10.1.0.1\n  User m\n\
             Host target\n  HostName 10.2.0.1\n  User t\n  ProxyJump edge,middle\n\
             Host direct-middle\n  HostName 10.1.0.1\n  User m\n",
        );
        let mut store = store("chain");
        let hosts = Config::load(&directory);

        let created = import(&mut store, &hosts, &["target".to_owned()], false).unwrap();
        let by_name = |store: &ProfileStore, name: &str| {
            store
                .list()
                .iter()
                .find(|profile| profile.name == name)
                .unwrap_or_else(|| panic!("kein Profil {name}"))
                .clone()
        };

        assert_eq!(
            created.iter().map(|profile| profile.name.as_str()).collect::<Vec<_>>(),
            ["edge", "middle", "target"]
        );
        let names: Vec<String> = store
            .chain(&by_name(&store, "target").id)
            .unwrap()
            .into_iter()
            .map(|profile| profile.name)
            .collect();
        assert_eq!(
            names,
            ["edge", "middle", "target"],
            "erst edge, von dort middle, von dort das Ziel"
        );

        // Ein zweiter Import benutzt die Kette wieder.
        assert!(
            import(&mut store, &hosts, &["target".to_owned()], false)
                .unwrap()
                .is_empty()
        );
    }

    #[test]
    fn a_hop_that_already_exists_with_another_way_is_left_alone() {
        let directory = ssh_dir(
            "chain-existing",
            "Host edge\n  HostName edge.example.org\n  User e\n\
             Host middle\n  HostName 10.1.0.1\n  User m\n\
             Host target\n  HostName 10.2.0.1\n  User t\n  ProxyJump edge,middle\n",
        );
        let mut store = store("chain-existing");
        let hosts = Config::load(&directory);

        // „middle“ gibt es schon – als Server, der direkt erreicht wird.
        let direct = import(&mut store, &hosts, &["middle".to_owned()], false).unwrap();
        assert_eq!(direct[0].jump, None);

        import(&mut store, &hosts, &["target".to_owned()], false).unwrap();

        let profiles = store.list();
        let middle = profiles.iter().find(|profile| profile.name == "middle").unwrap();
        assert_eq!(middle.jump, None, "das vorhandene Profil behält seinen Weg");
        let via = profiles
            .iter()
            .find(|profile| profile.name == "middle via edge")
            .unwrap();
        let target = profiles.iter().find(|profile| profile.name == "target").unwrap();
        assert_eq!(target.jump.as_deref(), Some(via.id.as_str()));
        assert_eq!(
            via.jump.as_deref(),
            profiles
                .iter()
                .find(|profile| profile.name == "edge")
                .map(|edge| edge.id.as_str())
        );
    }

    #[test]
    fn a_linked_profile_follows_its_entry_in_the_file() {
        let directory = ssh_dir(
            "linked",
            "Host web\n  HostName 10.0.0.1\n  User deploy\n  Port 2200\n  ProxyJump gate\nHost gate\n  HostName gate.example.org\n  User ops\n",
        );
        let mut store = store("linked");
        let created = import(&mut store, &Config::load(&directory), &["web".to_owned()], true).unwrap();
        let web = created.iter().find(|profile| profile.name == "web").unwrap().clone();
        let gate = created.iter().find(|profile| profile.name == "gate").unwrap().clone();
        assert_eq!(web.ssh_alias.as_deref(), Some("web"));
        // Der Sprung-Server entstand nur nebenbei: Verknüpft ist, was der Benutzer gewählt hat.
        assert_eq!(gate.ssh_alias, None);

        // Unverändert gelesen, ändert sich nichts.
        assert_eq!(refresh(&mut store, &Config::load(&directory)).unwrap(), 0);

        // Die Datei ändert sich – das Profil folgt, mit allem, was der Benutzer daran sonst eingestellt hat.
        store
            .save(Profile {
                group: "Kunden".into(),
                notes: "bleibt".into(),
                ..web.clone()
            })
            .unwrap();
        fs::write(
            directory.join("config"),
            "Host web\n  HostName 10.0.0.99\n  User root\n  IdentityFile /keys/id_web\nHost gate\n  HostName gate.example.org\n  User ops\n",
        )
        .unwrap();
        assert_eq!(refresh(&mut store, &Config::load(&directory)).unwrap(), 1);

        let now = store.get(&web.id).unwrap();
        assert_eq!(
            (now.host.as_str(), now.port, now.user.as_str()),
            ("10.0.0.99", 22, "root")
        );
        assert_eq!(now.key_path.as_deref(), Some("/keys/id_web"));
        assert_eq!(now.jump, None, "ProxyJump steht nicht mehr in der Datei");
        assert_eq!(
            (now.group.as_str(), now.notes.as_str(), now.id.as_str()),
            ("Kunden", "bleibt", web.id.as_str())
        );

        // Das nicht verknüpfte Profil bleibt, wie es ist – auch wenn sein Eintrag sich ändert.
        fs::write(
            directory.join("config"),
            "Host web\n  HostName 10.0.0.99\n  User root\nHost gate\n  HostName other.example.org\n",
        )
        .unwrap();
        refresh(&mut store, &Config::load(&directory)).unwrap();
        assert_eq!(store.get(&gate.id).unwrap().host, "gate.example.org");
    }

    #[test]
    fn a_linked_profile_whose_entry_is_gone_stays_as_it_was() {
        let directory = ssh_dir("linked-gone", "Host web\n  HostName 10.0.0.1\n  User deploy\n");
        let mut store = store("linked-gone");
        let web = import(&mut store, &Config::load(&directory), &["web".to_owned()], true).unwrap()[0].clone();

        fs::write(directory.join("config"), "Host other\n  HostName 10.0.0.2\n").unwrap();

        assert_eq!(refresh(&mut store, &Config::load(&directory)).unwrap(), 0);
        assert_eq!(store.get(&web.id).unwrap(), &web);
    }

    #[test]
    fn linking_later_marks_the_profile_that_already_exists() {
        let directory = ssh_dir("link-later", "Host web\n  HostName 10.0.0.1\n  User deploy\n");
        let mut store = store("link-later");
        let config = Config::load(&directory);
        let plain = import(&mut store, &config, &["web".to_owned()], false).unwrap()[0].clone();
        assert_eq!(plain.ssh_alias, None);

        // Noch einmal gewählt, diesmal verknüpft: kein zweites Profil, das vorhandene bekommt den Verweis.
        assert!(
            import(&mut store, &config, &["web".to_owned()], true)
                .unwrap()
                .is_empty()
        );
        assert_eq!(store.list().len(), 1);
        assert_eq!(store.get(&plain.id).unwrap().ssh_alias.as_deref(), Some("web"));
    }

    #[test]
    fn match_blocks_apply_where_their_condition_can_be_checked() {
        let dir = ssh_dir(
            "match",
            "Host web db\n  HostName %h.example.org\n\n\
             Host other\n  HostName other.example.net\n  User root\n\n\
             Match host *.example.org\n  User deploy\n  Port 2222\n\n\
             Match originalhost db\n  IdentityFile ~/.ssh/db_key\n\n\
             Match host *.example.org user deploy\n  ForwardAgent yes\n\n\
             Match !host *.example.org\n  Compression yes\n\n\
             Match user root\n  ConnectTimeout 5\n\n\
             Match localuser definitely-nobody\n  Port 1\n\n\
             Match exec \"test -f /tmp/x\"\n  User mallory\n  Port 9\n\n\
             Match canonical host *\n  User mallory\n\n\
             Match final all\n  User mallory\n\n\
             Match host\n  User mallory\n\n\
             Match all\n  ServerAliveInterval 60\n",
        );
        let config = Config::load(&dir);
        let host = |alias: &str| config.hosts.iter().find(|host| host.alias == alias).unwrap().clone();

        // `Match host` sieht den Namen, zu dem verbunden wird – nicht den Alias.
        let web = host("web");
        assert_eq!(
            (web.host.as_str(), web.user.as_str(), web.port),
            ("web.example.org", "deploy", 2222)
        );
        assert!(web.forward_agent && !web.compression);
        assert_eq!((web.key_path, web.keepalive_interval), (None, Some(60)));

        // `Match originalhost` sieht den Alias.
        assert!(host("db").key_path.unwrap().ends_with("db_key"));

        // Die Verneinung, und `Match user` mit dem Benutzer, der bis dahin feststeht.
        let other = host("other");
        assert_eq!((other.user.as_str(), other.port), ("root", 22));
        assert!(other.compression && !other.forward_agent);
        assert_eq!(other.connect_timeout, Some(5));
        assert_eq!(web.connect_timeout, None);

        // Was sich nicht prüfen lässt, gilt nie – und wird genannt, jede Bedingung einmal.
        assert!(
            config
                .hosts
                .iter()
                .all(|host| host.user != "mallory" && host.port != 9 && host.port != 1)
        );
        assert_eq!(config.unchecked, ["exec", "canonical", "final", "host"]);
    }

    #[test]
    fn keepalive_identities_and_the_small_wishes_are_taken_within_the_limits_of_a_profile() {
        let dir = ssh_dir(
            "more",
            "Host a\n  IdentityFile ~/.ssh/a\n  IdentitiesOnly yes\n  ServerAliveInterval 2\n  ServerAliveCountMax 0\n  \
             AddressFamily inet6\n  ConnectTimeout 500\n  Compression yes\n  SetEnv LANG=C FOO=bar \"WITH=a b\"\n  \
             SetEnv LANG=zweite 1BAD=x NOVALUE\n\n\
             Host b\n  IdentitiesOnly yes\n  ServerAliveInterval 0\n  ServerAliveCountMax 99\n  AddressFamily any\n  \
             ConnectTimeout 0\n  Compression no\n\n\
             Host c\n  ServerAliveInterval 100000\n  ConnectTimeout 1\n",
        );
        let hosts = read(&dir);
        let (a, b, c) = (&hosts[0], &hosts[1], &hosts[2]);

        assert!(a.identities_only && a.compression);
        assert_eq!((a.keepalive_interval, a.keepalive_max), (Some(5), Some(1)));
        assert_eq!(
            (a.address_family.as_deref(), a.connect_timeout),
            (Some("ipv6"), Some(120))
        );
        let env: Vec<(&str, &str)> = a.env.iter().map(|v| (v.name.as_str(), v.value.as_str())).collect();
        assert_eq!(env, [("LANG", "C"), ("FOO", "bar"), ("WITH", "a b")]);

        // Ohne Schlüssel gibt es nichts, worauf „nur dieser“ zeigen könnte; 0 heißt ausdrücklich: keine.
        assert!(!b.identities_only && !b.compression);
        assert_eq!((b.keepalive_interval, b.keepalive_max), (Some(0), Some(20)));
        assert_eq!((b.address_family.as_deref(), b.connect_timeout), (None, None));
        assert_eq!(
            (c.keepalive_interval, c.keepalive_max, c.connect_timeout),
            (Some(3600), None, Some(3))
        );
    }

    #[test]
    fn forwards_become_tunnels_that_listen_for_this_computer_only() {
        let dir = ssh_dir(
            "forwards",
            "Host a\n  LocalForward 8080 localhost:80\n  LocalForward 127.0.0.1:5433 db.internal:5432\n  \
             LocalForward *:9000 [::1]:9001\n  LocalForward [::1]:9002 10.0.0.1:1\n  RemoteForward 0.0.0.0:2222 localhost:22\n  \
             DynamicForward 1080\n  DynamicForward localhost:1081\n  LocalForward 8080 localhost:80\n  \
             LocalForward /tmp/sock localhost:80\n  LocalForward 8081 /var/run/docker.sock\n  RemoteForward 7000\n  \
             LocalForward 0 localhost:80\n  LocalForward 99999 localhost:80\n  LocalForward 8082 nohost\n  \
             LocalForward 8083 \"bad host\":80\n  GatewayPorts yes\n\n\
             Host *\n  DynamicForward 1082\n",
        );
        let host = read(&dir).remove(0);
        let shown: Vec<(TunnelKind, u16, &str, u16)> = host
            .tunnels
            .iter()
            .map(|tunnel| {
                (
                    tunnel.kind,
                    tunnel.listen_port,
                    tunnel.target_host.as_str(),
                    tunnel.target_port,
                )
            })
            .collect();
        assert_eq!(
            shown,
            [
                (TunnelKind::Local, 8080, "localhost", 80),
                (TunnelKind::Local, 5433, "db.internal", 5432),
                (TunnelKind::Local, 9000, "::1", 9001),
                (TunnelKind::Local, 9002, "10.0.0.1", 1),
                (TunnelKind::Remote, 2222, "localhost", 22),
                (TunnelKind::Dynamic, 1080, "", 0),
                (TunnelKind::Dynamic, 1081, "", 0),
                (TunnelKind::Dynamic, 1082, "", 0),
            ]
        );

        // Auch `*:9000`, `0.0.0.0:2222` und `GatewayPorts yes` öffnen nichts für das Netz. `GatewayPorts` steht bei
        // dem, was nicht übernommen wurde – zusammen mit den Weiterleitungen, die keine Tunnel sein können.
        assert!(
            host.tunnels
                .iter()
                .all(|tunnel| !tunnel.open && tunnel.auto && tunnel.id.is_empty())
        );
        assert_eq!(host.skipped, ["GatewayPorts", "LocalForward", "RemoteForward"]);
    }

    #[test]
    fn what_is_not_taken_over_is_named_once_as_it_is_written() {
        let dir = ssh_dir(
            "skipped",
            "Host a\n  ProxyCommand ssh -W %h:%p bastion\n  Ciphers aes256-ctr\n  ciphers aes128-ctr\n  \
             StrictHostKeyChecking no\n  ForwardX11 yes\n  SendEnv LANG\n  ControlMaster auto\n  AddKeysToAgent yes\n  \
             LogLevel DEBUG\n  Weird<script>Key\u{202e} x\n  User me\n\n\
             Host b\n  HostName b.example.org\n\n\
             Host *\n  UserKnownHostsFile /dev/null\n",
        );
        let hosts = read(&dir);

        // Was nur das Programm `ssh` betrifft, fehlt in der Liste; was übernommen wird, auch.
        assert_eq!(
            hosts[0].skipped,
            [
                "ProxyCommand",
                "Ciphers",
                "StrictHostKeyChecking",
                "ForwardX11",
                "SendEnv",
                "WeirdscriptKey",
                "UserKnownHostsFile"
            ]
        );
        assert_eq!(hosts[1].skipped, ["UserKnownHostsFile"]);

        // Die Liste hat ein Ende.
        let many: String = (0..100).map(|index| format!("  Option{index} x\n")).collect();
        let dir = ssh_dir("skipped-many", &format!("Host a\n{many}"));
        assert_eq!(read(&dir)[0].skipped.len(), MAX_SKIPPED);
    }

    #[test]
    fn an_import_brings_tunnels_keepalive_and_the_rest_and_a_refresh_only_what_belongs_to_the_sign_in() {
        let dir = ssh_dir(
            "import-more",
            "Host a\n  HostName a.example.org\n  User me\n  IdentityFile ~/.ssh/a\n  IdentitiesOnly yes\n  \
             ServerAliveInterval 30\n  ServerAliveCountMax 4\n  LocalForward 8080 localhost:80\n  DynamicForward 1080\n  \
             Compression yes\n  ConnectTimeout 20\n  AddressFamily inet\n  SetEnv LANG=C\n",
        );
        let mut store = store("import-more");
        let created = import(&mut store, &Config::load(&dir), &["a".to_owned()], true).unwrap();
        let profile = created.into_iter().next().unwrap();

        assert!(profile.identities_only && profile.compression);
        assert_eq!((profile.keepalive_interval, profile.keepalive_max), (Some(30), Some(4)));
        assert_eq!(
            (profile.connect_timeout, profile.address_family.as_deref()),
            (Some(20), Some("ipv4"))
        );
        assert_eq!(profile.env.len(), 1);
        assert_eq!(profile.tunnels.len(), 2);
        // Jeder Tunnel hat eine Kennung bekommen und lauscht nur für diesen Rechner.
        assert!(
            profile
                .tunnels
                .iter()
                .all(|tunnel| !tunnel.id.is_empty() && !tunnel.open)
        );

        // Die Datei ändert sich: Die Anmeldung folgt, Tunnel und die übrigen Wünsche bleiben, wie sie importiert
        // wurden – ein Port, der hier lauscht, entsteht nicht dadurch, dass sich eine Datei ändert.
        fs::write(
            dir.join("config"),
            "Host a\n  HostName a.example.org\n  User me\n  IdentityFile ~/.ssh/a\n  LocalForward 9999 localhost:1\n  \
             ServerAliveInterval 300\n",
        )
        .unwrap();
        assert_eq!(refresh(&mut store, &Config::load(&dir)).unwrap(), 1);
        let after = store.get(&profile.id).unwrap().clone();
        assert!(!after.identities_only);
        assert_eq!(after.tunnels, profile.tunnels);
        assert_eq!((after.keepalive_interval, after.compression), (Some(30), true));
    }

    #[test]
    fn an_include_inside_a_block_that_does_not_apply_brings_nothing() {
        let dir = ssh_dir(
            "include-gate",
            "Host web\n  Include inner\n  Compression yes\n\n\
             Host other\n  Include open\n\n\
             Match host never.example.org\n  Include open\n\n\
             Host db\n  HostName db.example.org\n",
        );
        // Die eingebundene Datei: erst Zeilen ohne Block (sie gehören zum Block mit dem `Include`), dann eigene.
        fs::write(
            dir.join("inner"),
            "User inner\nHost web\n  Port 1234\nHost db\n  Port 99\nHost *\n  ConnectTimeout 9\n",
        )
        .unwrap();
        fs::write(
            dir.join("open"),
            "Match all\n  ForwardAgent yes\nHost *\n  User mallory\n",
        )
        .unwrap();
        let config = Config::load(&dir);
        let host = |alias: &str| config.hosts.iter().find(|host| host.alias == alias).unwrap().clone();

        // In `web` eingebunden: Was darin für `web` steht, gilt – und nach dem `Include` geht es in `web` weiter.
        let web = host("web");
        assert_eq!(
            (web.user.as_str(), web.port, web.connect_timeout),
            ("inner", 1234, Some(9))
        );
        assert!(web.compression && !web.forward_agent);

        // `db` steht in der eingebundenen Datei, aber unter `Host web`: Für `db` gilt nichts davon – auch nicht
        // `Host *` darin und nicht `Compression` danach.
        let db = host("db");
        assert_eq!((db.port, db.connect_timeout, db.compression), (22, None, false));
        assert!(!db.forward_agent && db.user != "mallory" && db.user != "inner");

        // `Match all` und `Host *` in einer Datei, die ein Block einbindet, der nicht passt, gelten nicht – wohl
        // aber für den Server, dessen Block sie einbindet.
        assert!(!web.forward_agent && web.user != "mallory");
        let other = host("other");
        assert!(other.forward_agent);
        assert_eq!(other.user, "mallory");
    }

    #[test]
    fn a_match_line_openssh_would_refuse_never_applies() {
        let dir = ssh_dir(
            "match-invalid",
            "Host a\n  User Alice\n\n\
             Match all host *\n  Port 1\n\n\
             Match !all\n  Port 2\n\n\
             Match user \"\"\n  Port 3\n\n\
             Match !user \"\"\n  Port 4\n\n\
             Match host ,\n  Port 5\n\n\
             Match user alice\n  Port 6\n\n\
             Match user Alice\n  ConnectTimeout 7\n\n\
             Match host A\n  Compression yes\n",
        );
        let config = Config::load(&dir);
        let a = &config.hosts[0];

        // Benutzer genau, Server ohne Rücksicht auf die Schreibung – und keine der ungültigen Zeilen gilt.
        assert_eq!((a.port, a.connect_timeout, a.compression), (22, Some(7), true));
        assert_eq!(config.unchecked, ["all", "user", "host"]);
    }

    #[test]
    fn only_what_openssh_reads_as_a_tcp_forward_becomes_a_tunnel() {
        for (argument, expected) in [
            ("8080", Some((None, 8080))),
            ("localhost:8080", Some((Some("localhost"), 8080))),
            ("*:8080", Some((Some("*"), 8080))),
            ("[::1]:8080", Some((Some("::1"), 8080))),
            ("0", None),
            ("70000", None),
            ("+80", None),
            ("", None),
            ("host:", None),
            (":80", None),
            ("::1:80", None),
            ("a:b:80", None),
            ("/tmp/sock", None),
            ("/tmp/sock:8080", None),
            ("[::1]8080", None),
            ("[/tmp/x]:80", None),
            ("ho st:80", None),
        ] {
            assert_eq!(host_and_port(argument), expected, "{argument:?}");
        }

        let words = |text: &str| -> Vec<String> { text.split(' ').map(str::to_owned).collect() };
        assert!(forward("localforward", &words("8080 localhost:80")).is_some());
        assert!(forward("localforward", &words("/tmp/sock:8080 host:80")).is_none());
        assert!(forward("localforward", &words("8080 ::1:80")).is_none());
        assert!(forward("localforward", &words("8080 80")).is_none());
        assert!(forward("localforward", &words("8080:host:80")).is_none());
        assert!(forward("remoteforward", &words("9090")).is_none());
        assert!(forward("dynamicforward", &words("1080 extra")).is_none());
        assert!(forward("dynamicforward", &words("[::1]:1080")).is_some());
    }

    #[test]
    fn a_huge_file_is_read_up_to_a_limit_and_quickly() {
        // Zehntausende Blöcke und Server, dazu überlange Namen: gelesen wird bis zur Grenze, in kurzer Zeit.
        let mut text = String::new();
        for index in 0..(MAX_BLOCKS + 500) {
            text.push_str(&format!("Host h{index}\n  Port 22\n"));
        }
        text.push_str(&format!(
            "Host {}\n  Port 1\nMatch host *{}b\n  Port 2\n",
            "a".repeat(5000),
            "a".repeat(5000)
        ));
        let dir = ssh_dir("huge", &text);

        let started = std::time::Instant::now();
        let config = Config::load(&dir);
        assert_eq!(config.hosts.len(), MAX_HOSTS);
        assert!(
            started.elapsed() < std::time::Duration::from_secs(20),
            "{:?}",
            started.elapsed()
        );
    }
}
