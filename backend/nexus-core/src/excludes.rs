//! Ausschlüsse: Muster, die ein Ordner-Transfer auslässt (`.git`, `node_modules`, `*.tmp`).
//!
//! Ein Muster ohne `/` meint den Namen einer Datei oder eines Ordners in jeder Tiefe; eines mit `/` den Pfad
//! unterhalb des übertragenen Ordners, Stück für Stück (`build/*.map` trifft `build/a.map`, nicht
//! `src/build/a.map` und nicht `build/x/a.map`; `/dist` trifft nur `dist` ganz oben). `*` steht für beliebig viele
//! Zeichen, `?` für eines. Ein ausgelassener Ordner wird nicht betreten. Der übertragene Ordner selbst wird nie
//! ausgelassen. Dieselbe Regel steht in der App (`Services/TransferExcludes`) – sie zeigt vorab, was ausgelassen
//! würde; beide Seiten müssen dasselbe sagen. Deshalb zählt Groß- und Kleinschreibung nur bei A–Z nicht, und
//! verglichen wird Zeichen für Zeichen: Was Unicode darüber hinaus gleichsetzt, hängt von der Bibliothek ab.

/// So viele Muster nimmt ein Transfer höchstens.
pub const MAX_PATTERNS: usize = 64;

/// So lang darf ein Muster sein.
pub const MAX_PATTERN: usize = 255;

/// Die Muster eines Transfers, zum Vergleichen vorbereitet.
#[derive(Clone, Debug, Default)]
pub struct Excludes {
    /// Muster ohne `/`, klein geschrieben.
    names: Vec<Vec<char>>,
    /// Muster mit `/`, in Stücke zerlegt.
    paths: Vec<Vec<Vec<char>>>,
}

impl Excludes {
    /// Bereitet die Muster vor. `None`, wenn eines keines sein kann oder es zu viele sind – dann überträgt der Kern
    /// lieber nicht, als etwas anderes auszulassen als gemeint.
    pub fn new(patterns: &[String]) -> Option<Self> {
        if patterns.len() > MAX_PATTERNS {
            return None;
        }

        let mut excludes = Self::default();
        for pattern in patterns {
            // Mit `/` vorn gilt auch ein einzelner Name nur ganz oben (`/dist` trifft `dist`, nicht `src/dist`).
            let anchored = pattern.trim().starts_with('/');
            let pattern = normalized(pattern)?;
            let pieces: Vec<Vec<char>> = pattern.split('/').map(|piece| piece.chars().collect()).collect();
            if pieces.len() == 1 && !anchored {
                excludes.names.extend(pieces);
            } else {
                excludes.paths.push(pieces);
            }
        }

        Some(excludes)
    }

    /// Ob es nichts auszulassen gibt.
    pub fn is_empty(&self) -> bool {
        self.names.is_empty() && self.paths.is_empty()
    }

    /// Ob der Eintrag mit diesem Pfad unterhalb des übertragenen Ordners (mit `/` getrennt) ausgelassen wird.
    /// Gefragt wird je Eintrag beim Ablaufen des Baums – was in einem ausgelassenen Ordner liegt, kommt gar nicht
    /// erst hierher.
    pub fn skips(&self, relative: &str) -> bool {
        if self.is_empty() || relative.is_empty() {
            return false;
        }

        let lower = relative.to_ascii_lowercase();
        let pieces: Vec<Vec<char>> = lower.split('/').map(|piece| piece.chars().collect()).collect();
        let name = pieces.last().map_or(&[][..], Vec::as_slice);
        self.names.iter().any(|pattern| glob(pattern, name))
            || self.paths.iter().any(|pattern| {
                pattern.len() == pieces.len()
                    && pattern.iter().zip(&pieces).all(|(pattern, piece)| glob(pattern, piece))
            })
    }
}

/// Ein Muster, wie verglichen wird: ohne Leerraum außen, ohne `/` am Anfang und Ende, klein geschrieben. `None`,
/// wenn es keines sein kann: leer, zu lang, mit Steuerzeichen, mit einem leeren Stück (`a//b`) oder mit `.`/`..`
/// als Stück.
pub fn normalized(pattern: &str) -> Option<String> {
    let pattern = pattern.trim().trim_matches('/').to_ascii_lowercase();
    let fine = !pattern.is_empty()
        && pattern.chars().count() <= MAX_PATTERN
        && !pattern.chars().any(|c| c.is_control() || c == '\\')
        && pattern
            .split('/')
            .all(|piece| !piece.is_empty() && piece != "." && piece != "..");
    fine.then_some(pattern)
}

// `*` für beliebig viele Zeichen, `?` für eines. Ohne Rekursion (wie in `sshconfig`): Es genügt, sich den letzten
// `*` zu merken.
fn glob(pattern: &[char], text: &[char]) -> bool {
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

#[cfg(test)]
mod tests {
    use super::*;

    fn excludes(patterns: &[&str]) -> Excludes {
        Excludes::new(&patterns.iter().map(|pattern| (*pattern).to_owned()).collect::<Vec<_>>()).unwrap()
    }

    /// Dieselbe Tabelle steht in den Tests der App (`TransferExcludesTests`): Beide Seiten müssen dasselbe sagen.
    #[test]
    fn names_match_at_any_depth_and_paths_only_where_they_say() {
        let rules = excludes(&[
            ".git",
            "node_modules",
            "*.tmp",
            "Thumbs.db",
            "build/*.map",
            "/dist/",
            "a?c",
            "?.txt",
            "Ä*.log",
        ]);
        for (path, skipped) in [
            (".git", true),
            ("src/.git", true),
            (".gitignore", false),
            ("node_modules", true),
            ("web/node_modules", true),
            ("node_modules_old", false),
            ("x.tmp", true),
            ("deep/er/Y.TMP", true),
            ("x.tmp.bak", false),
            ("thumbs.db", true),
            ("build/app.map", true),
            ("build/x/app.map", false),
            ("src/build/app.map", false),
            ("dist", true),
            ("src/dist", false),
            ("abc", true),
            ("ac", false),
            ("src/main.rs", false),
            ("😀.txt", true),
            ("ab.txt", false),
            ("Ärger.LOG", true),
            ("ärger.log", false),
            ("", false),
        ] {
            assert_eq!(rules.skips(path), skipped, "{path:?}");
        }

        assert!(!Excludes::default().skips("anything"));
        assert!(excludes(&[]).is_empty());
    }

    #[test]
    fn what_cannot_be_a_pattern_is_refused_as_a_whole() {
        for bad in ["", "  ", "/", "a//b", "../x", "a/./b", "a\\b", "a\nb"] {
            assert!(Excludes::new(&[bad.to_owned()]).is_none(), "{bad:?}");
        }
        assert!(Excludes::new(&["x".repeat(MAX_PATTERN + 1)]).is_none());
        assert!(Excludes::new(&vec!["a".to_owned(); MAX_PATTERNS + 1]).is_none());
        assert!(Excludes::new(&vec!["a".to_owned(); MAX_PATTERNS]).is_some());
        assert_eq!(normalized("  /Dist/ ").as_deref(), Some("dist"));
    }

    #[test]
    fn a_pattern_with_many_stars_is_decided_at_once() {
        let rules = excludes(&[&format!("{}b", "*a".repeat(40))]);
        let started = std::time::Instant::now();
        assert!(!rules.skips(&"a".repeat(250)));
        assert!(started.elapsed() < std::time::Duration::from_secs(1));
    }
}
