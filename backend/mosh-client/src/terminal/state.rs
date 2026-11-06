use core::fmt;

use super::{TerminalDifference, TerminalError, TerminalOperation};
use crate::limits::{MAX_TERMINAL_COLUMNS, MAX_TERMINAL_ROWS, MAX_TERMINAL_VISIBLE_CELLS};

// NexusSSH (P2): vt100 speichert höchstens so viele Bytes je Zelle und lässt weitere
// Kombinationszeichen fallen (`vt100/cell.rs`, `Cell::append`).
const VT100_CELL_CONTENT_BYTES: usize = 22;
// NexusSSH (P9): So viele Zeichen eines Fenstertitels gehen hinaus.
pub(crate) const MAX_TITLE_CHARS: usize = 256;

#[derive(Clone)]
pub(crate) struct TerminalState {
    screen: vt100::Screen,
    echo_acknowledgement: Option<u64>,
    // NexusSSH (P9): Glocke und Fenstertitel aus dem Zustand des Servers. Das Original ließ beide
    // fallen; mosh-server schickt sie in den Host-Bytes (`BEL`, OSC 0/2), wenn sie sich ändern.
    bell_count: u64,
    title: Option<String>,
}

impl fmt::Debug for TerminalState {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        let (rows, columns) = self.screen.size();
        formatter
            .debug_struct("TerminalState")
            .field("rows", &rows)
            .field("columns", &columns)
            .field("echo_acknowledgement", &self.echo_acknowledgement)
            .field("bell_count", &self.bell_count)
            .field("title", &self.title.as_ref().map(|_| "[REDACTED]"))
            .field("contents", &"[REDACTED]")
            .finish()
    }
}

impl TerminalState {
    pub(crate) fn new(columns: u32, rows: u32) -> Result<Self, TerminalError> {
        let (rows, columns) = checked_size(columns, rows)?;
        Ok(Self {
            screen: vt100::Parser::new(rows, columns, 0).screen().clone(),
            echo_acknowledgement: None,
            bell_count: 0,
            title: None,
        })
    }

    pub(crate) fn apply(&self, difference: &TerminalDifference) -> Result<Self, TerminalError> {
        let mut next = self.clone();
        for operation in &difference.operations {
            match operation {
                TerminalOperation::HostBytes(bytes) => next.apply_host_bytes(bytes)?,
                TerminalOperation::Resize { columns, rows } => {
                    let (rows, columns) = checked_size(*columns, *rows)?;
                    next.screen.set_size(rows, columns);
                }
                TerminalOperation::EchoAcknowledgement(number) => {
                    if next
                        .echo_acknowledgement
                        .is_some_and(|current| *number < current)
                    {
                        return Err(TerminalError::InvalidOperation);
                    }
                    next.echo_acknowledgement = Some(*number);
                }
            }
        }
        Ok(next)
    }

    pub(crate) const fn screen(&self) -> &vt100::Screen {
        &self.screen
    }

    pub(crate) const fn echo_acknowledgement(&self) -> Option<u64> {
        self.echo_acknowledgement
    }

    // NexusSSH (P9): wie oft die Glocke bisher klang (je `BEL` in den Host-Bytes).
    pub(crate) const fn bell_count(&self) -> u64 {
        self.bell_count
    }

    // NexusSSH (P9): der letzte Fenstertitel (OSC 0 oder 2), ohne Steuerzeichen und gekürzt.
    pub(crate) fn title(&self) -> Option<&str> {
        self.title.as_deref()
    }

    #[cfg(test)]
    pub(crate) fn with_test_echo_acknowledgement(mut self, number: u64) -> Self {
        self.echo_acknowledgement = Some(number);
        self
    }

    pub(crate) fn with_predicted_ascii(&self, byte: u8) -> Result<Self, TerminalError> {
        debug_assert!(byte.is_ascii_graphic() || byte == b' ');
        let mut predicted = self.clone();
        predicted.apply_host_bytes(&[byte])?;
        Ok(predicted)
    }

    // NexusSSH (P10): wie `with_predicted_ascii`, nur unterstrichen (wie im Original, `flagging`).
    // Danach gilt wieder der Stil von vorher – `attributes_formatted` setzt zurück und stellt her.
    pub(crate) fn with_flagged_ascii(&self, byte: u8) -> Result<Self, TerminalError> {
        debug_assert!(byte.is_ascii_graphic() || byte == b' ');
        let mut bytes = b"\x1b[4m".to_vec();
        bytes.push(byte);
        bytes.extend(self.screen.attributes_formatted());
        let mut predicted = self.clone();
        predicted.apply_host_bytes(&bytes)?;
        Ok(predicted)
    }

    pub(crate) fn display_equivalent(&self, other: &Self) -> bool {
        self.screen.state_formatted() == other.screen.state_formatted()
    }

    fn apply_host_bytes(&mut self, bytes: &[u8]) -> Result<(), TerminalError> {
        validate_host_bytes(bytes)?;
        let (rows, columns) = self.screen.size();
        let mut parser =
            vt100::Parser::new_with_callbacks(rows, columns, 0, TerminalCallbacks::default());
        *parser.screen_mut() = self.screen.clone();
        parser.process(bytes);
        if let Some(error) = parser.callbacks().unsupported_sequence {
            return Err(error);
        }
        validate_screen(parser.screen())?;
        self.screen = parser.screen().clone();
        let callbacks = parser.callbacks();
        self.bell_count = self.bell_count.saturating_add(callbacks.bells);
        if let Some(title) = &callbacks.title {
            self.title = Some(title.clone());
        }
        Ok(())
    }
}

pub(crate) fn is_valid_size(columns: u32, rows: u32) -> bool {
    checked_size(columns, rows).is_ok()
}

fn checked_size(columns: u32, rows: u32) -> Result<(u16, u16), TerminalError> {
    let cells = usize::try_from(columns)
        .ok()
        .and_then(|columns| usize::try_from(rows).ok().map(|rows| columns * rows));
    if columns == 0
        || columns > MAX_TERMINAL_COLUMNS
        || rows == 0
        || rows > MAX_TERMINAL_ROWS
        || cells.is_none_or(|cells| cells > MAX_TERMINAL_VISIBLE_CELLS)
    {
        return Err(TerminalError::InvalidTerminalSize);
    }
    Ok((
        u16::try_from(rows).map_err(|_| TerminalError::InvalidTerminalSize)?,
        u16::try_from(columns).map_err(|_| TerminalError::InvalidTerminalSize)?,
    ))
}

// NexusSSH (P9): Der Titel ist Text des Servers und geht später in einer OSC-Folge hinaus: ohne
// Steuerzeichen (C0, DEL, C1 – U+009C beendete die Folge), gültiges UTF-8, höchstens
// `MAX_TITLE_CHARS` Zeichen.
fn clean_title(raw: &[u8]) -> String {
    String::from_utf8_lossy(raw)
        .chars()
        .filter(|character| !character.is_control())
        .take(MAX_TITLE_CHARS)
        .collect()
}

fn validate_host_bytes(bytes: &[u8]) -> Result<(), TerminalError> {
    core::str::from_utf8(bytes).map_err(|_| TerminalError::InvalidUtf8)?;
    let mut parser = vte::Parser::new();
    let mut budget = ScalarBudget::default();
    parser.advance(&mut budget, bytes);
    let prints_before_sentinel = budget.prints;
    parser.advance(&mut budget, b"X");
    if let Some(error) = budget.error {
        return Err(error);
    }
    if budget.prints != prints_before_sentinel + 1 || budget.last_printed != Some('X') {
        return Err(TerminalError::IncompleteSequence);
    }
    Ok(())
}

fn validate_screen(screen: &vt100::Screen) -> Result<(), TerminalError> {
    let (rows, columns) = screen.size();
    for row in 0..rows {
        for column in 0..columns {
            let Some(cell) = screen.cell(row, column) else {
                continue;
            };
            if cell.contents().len() > VT100_CELL_CONTENT_BYTES {
                return Err(TerminalError::TooManyScalarsInCell);
            }
        }
    }
    Ok(())
}

#[derive(Default)]
struct ScalarBudget {
    scalars: usize,
    prints: usize,
    last_printed: Option<char>,
    error: Option<TerminalError>,
}

impl ScalarBudget {
    fn reset_cluster(&mut self) {
        self.scalars = 0;
    }
}

impl vte::Perform for ScalarBudget {
    fn print(&mut self, character: char) {
        self.prints += 1;
        self.last_printed = Some(character);
        let Some(width) = crate::terminal_width::width(character) else {
            self.error = Some(TerminalError::UnsupportedControl);
            return;
        };
        if width == 0 {
            if self.scalars == 0 {
                self.error = Some(TerminalError::TooManyScalarsInCell);
                return;
            }
            // NexusSSH (P2): Mehr Kombinationszeichen, als eine Zelle fasst (mosh-server nimmt bis zu
            // 32 Bytes, vt100 22), lässt vt100 fallen – die Zelle zeigt dann weniger, die Sitzung
            // bleibt. Im Original endete sie hier (mehr als 8 Zeichen oder 18 Bytes in einer Zelle).
            self.scalars = self.scalars.saturating_add(1);
        } else {
            self.scalars = 1;
        }
    }

    fn execute(&mut self, _byte: u8) {
        self.reset_cluster();
    }

    fn hook(&mut self, _params: &vte::Params, _intermediates: &[u8], _ignore: bool, _action: char) {
        self.reset_cluster();
    }

    fn osc_dispatch(&mut self, _params: &[&[u8]], _bell_terminated: bool) {
        self.reset_cluster();
    }

    fn csi_dispatch(
        &mut self,
        _params: &vte::Params,
        _intermediates: &[u8],
        _ignore: bool,
        action: char,
    ) {
        if action != 'm' {
            self.reset_cluster();
        }
    }

    fn esc_dispatch(&mut self, _intermediates: &[u8], _ignore: bool, _byte: u8) {
        self.reset_cluster();
    }
}

#[derive(Default)]
struct TerminalCallbacks {
    unsupported_sequence: Option<TerminalError>,
    bells: u64,
    title: Option<String>,
}

impl vt100::Callbacks for TerminalCallbacks {
    fn audible_bell(&mut self, _screen: &mut vt100::Screen) {
        self.bells = self.bells.saturating_add(1);
    }

    fn set_window_title(&mut self, _screen: &mut vt100::Screen, title: &[u8]) {
        self.title = Some(clean_title(title));
    }

    fn unhandled_char(&mut self, _screen: &mut vt100::Screen, _character: char) {
        self.unsupported_sequence = Some(TerminalError::UnsupportedCharacter);
    }

    fn unhandled_control(&mut self, _screen: &mut vt100::Screen, _byte: u8) {
        self.unsupported_sequence = Some(TerminalError::UnsupportedControl);
    }

    fn unhandled_escape(
        &mut self,
        _screen: &mut vt100::Screen,
        _intermediate_1: Option<u8>,
        _intermediate_2: Option<u8>,
        _byte: u8,
    ) {
        self.unsupported_sequence = Some(TerminalError::UnsupportedEscape);
    }

    // NexusSSH (P2): CSI und OSC, die vt100 nicht kennt (`CSI ? 1001 h`, `CSI ? 1015 h`, OSC 52 mit
    // kaputtem Inhalt – mosh-server reicht sie weiter), werden übergangen wie in jedem Terminal; den
    // Inhalt der Zellen hat mosh-server schon selbst ausgewertet. Im Original endete die Sitzung, und
    // wer dem Benutzer Text auf den Bildschirm brachte, konnte sie so beenden.
}

#[cfg(test)]
mod tests {
    use proptest::prelude::*;
    use prost::Message as _;

    use super::*;
    use crate::terminal::{
        WireEchoAcknowledgement, WireHostBytes, WireHostDifference, WireHostOperation, WireHostSize,
    };

    fn difference(operations: Vec<WireHostOperation>) -> TerminalDifference {
        TerminalDifference::decode(&WireHostDifference { operations }.encode_to_vec()).unwrap()
    }

    fn host_bytes(bytes: &[u8]) -> WireHostOperation {
        WireHostOperation {
            host_bytes: Some(WireHostBytes {
                bytes: bytes.to_vec(),
            }),
            size: None,
            echo_acknowledgement: None,
        }
    }

    #[test]
    fn applies_host_bytes_resize_and_echo_in_wire_order() {
        let initial = TerminalState::new(80, 24).unwrap();
        let next = initial
            .apply(&difference(vec![
                host_bytes("A中Z".as_bytes()),
                WireHostOperation {
                    host_bytes: None,
                    size: Some(WireHostSize {
                        columns: 81,
                        rows: 25,
                    }),
                    echo_acknowledgement: None,
                },
                WireHostOperation {
                    host_bytes: None,
                    size: None,
                    echo_acknowledgement: Some(WireEchoAcknowledgement { number: 9 }),
                },
                host_bytes(b"!"),
            ]))
            .unwrap();

        assert_eq!(next.screen.size(), (25, 81));
        assert_eq!(next.screen.cell(0, 0).unwrap().contents(), "A");
        assert_eq!(next.screen.cell(0, 1).unwrap().contents(), "中");
        assert!(next.screen.cell(0, 2).unwrap().is_wide_continuation());
        assert_eq!(next.screen.cell(0, 3).unwrap().contents(), "Z");
        assert_eq!(next.screen.cell(0, 4).unwrap().contents(), "!");
        assert_eq!(next.echo_acknowledgement(), Some(9));
        assert_eq!(initial.screen.size(), (24, 80));
        assert_eq!(initial.echo_acknowledgement(), None);
        assert!(!initial.screen.cell(0, 0).unwrap().has_contents());

        let later = next.apply(&difference(vec![host_bytes(b"later")])).unwrap();
        assert_eq!(later.echo_acknowledgement(), Some(9));
    }

    #[test]
    fn rejects_decreasing_echo_acknowledgements() {
        let acknowledged = TerminalState::new(80, 24)
            .unwrap()
            .apply(&difference(vec![WireHostOperation {
                host_bytes: None,
                size: None,
                echo_acknowledgement: Some(WireEchoAcknowledgement { number: 9 }),
            }]))
            .unwrap();

        assert_eq!(
            acknowledged
                .apply(&difference(vec![WireHostOperation {
                    host_bytes: None,
                    size: None,
                    echo_acknowledgement: Some(WireEchoAcknowledgement { number: 8 }),
                }]))
                .unwrap_err(),
            TerminalError::InvalidOperation
        );
    }

    #[test]
    fn preserves_bounded_combining_characters() {
        let state = TerminalState::new(80, 24)
            .unwrap()
            .apply(&difference(vec![host_bytes("e\u{301}".as_bytes())]))
            .unwrap();
        assert_eq!(state.screen.cell(0, 0).unwrap().contents(), "e\u{301}");

        let eight_scalars = format!("e{}", "\u{301}".repeat(7));
        assert!(
            TerminalState::new(80, 24)
                .unwrap()
                .apply(&difference(vec![host_bytes(eight_scalars.as_bytes())]))
                .is_ok()
        );
        // NexusSSH (P2): Mehr Zeichen als früher erlaubt behält vt100 bis zu 22 Bytes, den Rest lässt
        // es fallen.
        let nine_scalars = format!("e{}", "\u{301}".repeat(8));
        let state = TerminalState::new(80, 24)
            .unwrap()
            .apply(&difference(vec![host_bytes(nine_scalars.as_bytes())]))
            .unwrap();
        assert_eq!(state.screen.cell(0, 0).unwrap().contents(), nine_scalars);
        let zalgo = format!("e{}X", "\u{301}".repeat(20));
        let state = TerminalState::new(80, 24)
            .unwrap()
            .apply(&difference(vec![host_bytes(zalgo.as_bytes())]))
            .unwrap();
        assert_eq!(
            state.screen.cell(0, 0).unwrap().contents(),
            format!("e{}", "\u{301}".repeat(9))
        );
        assert_eq!(state.screen.cell(0, 1).unwrap().contents(), "X");
        assert_eq!(state.screen.cursor_position(), (0, 2));
    }

    #[test]
    fn preserves_valid_replacement_character_and_cursor_position() {
        let state = TerminalState::new(80, 24)
            .unwrap()
            .apply(&difference(vec![host_bytes(
                "BEFORE-\u{fffd}-AFTER".as_bytes(),
            )]))
            .unwrap();

        assert_eq!(state.screen.contents(), "BEFORE-\u{fffd}-AFTER");
        assert_eq!(state.screen.cell(0, 7).unwrap().contents(), "\u{fffd}");
        assert_eq!(state.screen.cursor_position(), (0, 14));

        let continued = state.apply(&difference(vec![host_bytes(b"Z")])).unwrap();
        assert_eq!(continued.screen.contents(), "BEFORE-\u{fffd}-AFTERZ");
        assert_eq!(continued.screen.cursor_position(), (0, 15));
    }

    #[test]
    fn accepts_semicolons_in_stock_window_titles_without_a_system_effect() {
        let initial = TerminalState::new(80, 24).unwrap();
        for selector in *b"012" {
            let bytes = [b"\x1b]".as_slice(), &[selector], b";hello;world\x07X"].concat();
            let next = initial
                .apply(&difference(vec![host_bytes(&bytes)]))
                .unwrap();
            assert_eq!(next.screen.contents(), "X");
            assert_eq!(next.screen.cursor_position(), (0, 1));
            assert!(
                !next
                    .screen
                    .state_formatted()
                    .windows(2)
                    .any(|s| s == b"\x1b]")
            );
        }
        // NexusSSH (P2): OSC 52 mit kaputtem Inhalt wird übergangen.
        let next = initial
            .apply(&difference(vec![host_bytes(b"\x1b]52;c;%%%\x07X")]))
            .unwrap();
        assert_eq!(next.screen.contents(), "X");
    }

    #[test]
    fn title_callbacks_receive_the_complete_semicolon_separated_text() {
        #[derive(Default)]
        struct Titles {
            icon: Vec<u8>,
            title: Vec<u8>,
        }

        impl vt100::Callbacks for Titles {
            fn set_window_icon_name(&mut self, _: &mut vt100::Screen, icon: &[u8]) {
                self.icon = icon.to_vec();
            }

            fn set_window_title(&mut self, _: &mut vt100::Screen, title: &[u8]) {
                self.title = title.to_vec();
            }
        }

        let mut parser = vt100::Parser::new_with_callbacks(24, 80, 0, Titles::default());
        parser.process(b"\x1b]0;hello;world\x07");
        assert_eq!(parser.callbacks().icon, b"hello;world");
        assert_eq!(parser.callbacks().title, b"hello;world");
        parser.process("\x1b]1;中;文\x07".as_bytes());
        assert_eq!(parser.callbacks().icon, "中;文".as_bytes());
        parser.process(b"\x1b]2;one;two;three\x07");
        assert_eq!(parser.callbacks().title, b"one;two;three");
    }

    #[test]
    fn stock_blink_hidden_and_reverse_video_are_represented_in_screen_state() {
        let initial = TerminalState::new(80, 24).unwrap();
        let active = initial
            .apply(&difference(vec![host_bytes(b"\x1b[5mX\x1b[8mY\x1b[?5h")]))
            .unwrap();
        let formatted = active.screen.state_formatted();
        assert!(formatted.windows(4).any(|s| s == b"[5mX"));
        assert!(formatted.windows(4).any(|s| s == b"[8mY"));
        assert!(formatted.windows(3).any(|s| s == b"?5h"));
        assert!(active.screen.reverse_video());
        let blink = active.screen.cell(0, 0).unwrap();
        assert!(blink.blink());
        assert!(!blink.hidden());
        let hidden = active.screen.cell(0, 1).unwrap();
        assert!(hidden.blink());
        assert!(hidden.hidden());
        assert!(!hidden.inverse());

        let reset = active
            .apply(&difference(vec![host_bytes(b"\x1b[25m\x1b[28m\x1b[?5lZ")]))
            .unwrap();
        assert_eq!(reset.screen.contents(), "XYZ");
        assert!(!reset.screen.reverse_video());
        assert!(!reset.screen.cell(0, 2).unwrap().blink());
        assert!(!reset.screen.cell(0, 2).unwrap().hidden());
        let diff = reset.screen.state_diff(&active.screen);
        assert!(diff.windows(3).any(|s| s == b"?5l"));
    }

    // NexusSSH (P2): Im Original hieß der Test `excluded_modes_clipboard_payload_and_cell_limits_still_fail`
    // – genau diese Ausgaben (aus der Prüfung gegen mosh-server 1.4.0) beendeten die Sitzung.
    #[test]
    fn excluded_modes_clipboard_payload_and_full_cells_leave_the_session_running() {
        let initial = TerminalState::new(80, 24)
            .unwrap()
            .apply(&difference(vec![host_bytes(b"stable\r\n")]))
            .unwrap();
        for bytes in [
            b"\x1b[?1001h".as_slice(),
            b"\x1b[?1015h",
            b"\x1b]52;c;!!kein-Base64!!\x07",
            b"\x1b]52;c;%%%\x07",
        ] {
            let next = initial.apply(&difference(vec![host_bytes(bytes)])).unwrap();
            assert_eq!(next.screen.contents(), "stable");
            assert_eq!(next.screen.cursor_position(), (1, 0));
        }
        for cell in [
            format!("A{}", "\u{301}".repeat(10)),
            format!("A{}", "\u{301}".repeat(20)),
            format!("A{}", "\u{20d0}".repeat(7)),
            format!("A{}", "\u{1ab0}".repeat(7)),
        ] {
            let next = initial
                .apply(&difference(vec![host_bytes(cell.as_bytes())]))
                .unwrap();
            let kept = next.screen.cell(1, 0).unwrap().contents();
            assert!(cell.starts_with(kept) && kept.len() <= VT100_CELL_CONTENT_BYTES);
            assert_eq!(next.screen.cursor_position(), (1, 1));
        }
    }

    #[test]
    fn server_spacing_characters_occupy_cells_at_start_and_after_text() {
        for (character, width) in [
            ('\u{0605}', 1),
            ('\u{070f}', 1),
            ('\u{08e2}', 1),
            ('\u{09be}', 1),
            ('\u{17a4}', 1),
            ('\u{17d8}', 1),
            ('\u{302e}', 2),
            ('\u{3248}', 2),
        ] {
            let text = format!("{character}-AFTER");
            let state = TerminalState::new(80, 24)
                .unwrap()
                .apply(&difference(vec![host_bytes(text.as_bytes())]))
                .unwrap();
            assert_eq!(
                state.screen.cell(0, 0).unwrap().contents(),
                character.to_string()
            );
            assert_eq!(state.screen.cell(0, width).unwrap().contents(), "-");
            assert_eq!(state.screen.cursor_position(), (0, width + 6));

            let following = format!("X{character}-AFTER");
            let state = TerminalState::new(80, 24)
                .unwrap()
                .apply(&difference(vec![host_bytes(following.as_bytes())]))
                .unwrap();
            assert_eq!(
                state.screen.cell(0, 1).unwrap().contents(),
                character.to_string()
            );
            assert_eq!(state.screen.cell(0, 1 + width).unwrap().contents(), "-");
            assert_eq!(state.screen.cursor_position(), (0, width + 7));
        }
    }

    #[test]
    fn server_spacing_character_keeps_the_per_cell_scalar_limit() {
        let initial = TerminalState::new(80, 24).unwrap();
        let eight_scalars = format!("\u{0605}{}", "\u{301}".repeat(7));
        let accepted = initial
            .apply(&difference(vec![host_bytes(eight_scalars.as_bytes())]))
            .unwrap();
        assert_eq!(
            accepted.screen.cell(0, 0).unwrap().contents(),
            eight_scalars
        );

        // NexusSSH (P2): Was nicht mehr in die Zelle passt (U+0605 hat zwei Bytes), lässt vt100 fallen.
        let eleven_scalars = format!("\u{0605}{}", "\u{301}".repeat(10));
        let truncated = initial
            .apply(&difference(vec![host_bytes(eleven_scalars.as_bytes())]))
            .unwrap();
        assert_eq!(
            truncated.screen.cell(0, 0).unwrap().contents(),
            format!("\u{0605}{}", "\u{301}".repeat(8))
        );
        assert!(!initial.screen.cell(0, 0).unwrap().has_contents());
    }

    #[test]
    fn preserves_printable_unicode_categories_without_special_cases() {
        let samples = [
            ("A", 1),         // ASCII
            ("é", 1),         // precomposed Latin
            ("Ω", 1),         // Greek
            ("م", 1),         // Arabic
            ("क", 1),         // Devanagari
            ("中", 2),        // CJK
            ("🙂", 2),        // supplementary-plane emoji
            ("\u{10437}", 1), // supplementary-plane letter
            ("\u{e000}", 1),  // private-use scalar
            ("\u{fffd}", 1),  // replacement character
            ("\u{a0}", 1),    // nonbreaking space
        ];

        for (sample, width) in samples {
            let text = format!("X{sample}Y");
            let state = TerminalState::new(80, 24)
                .unwrap()
                .apply(&difference(vec![host_bytes(text.as_bytes())]))
                .unwrap();
            assert_eq!(state.screen.cell(0, 1).unwrap().contents(), sample);
            assert_eq!(state.screen.cursor_position(), (0, 2 + width));
            assert_eq!(state.screen.cell(0, 1 + width).unwrap().contents(), "Y");
        }

        for cluster in ["e\u{301}", "x\u{200d}", "x\u{fe0f}", "x\u{2d7f}"] {
            let state = TerminalState::new(80, 24)
                .unwrap()
                .apply(&difference(vec![host_bytes(cluster.as_bytes())]))
                .unwrap();
            assert_eq!(state.screen.cell(0, 0).unwrap().contents(), cluster);
            assert_eq!(state.screen.cursor_position(), (0, 1));
        }
    }

    #[test]
    fn rejects_del_control_even_after_printable_text() {
        let stable = TerminalState::new(80, 24)
            .unwrap()
            .apply(&difference(vec![host_bytes(b"stable")]))
            .unwrap();

        for bytes in [b"\x7f".as_slice(), b"X\x7fY".as_slice()] {
            assert_eq!(
                stable
                    .apply(&difference(vec![host_bytes(bytes)]))
                    .unwrap_err(),
                TerminalError::UnsupportedControl
            );
            assert_eq!(stable.screen.contents(), "stable");
        }
    }

    #[test]
    fn still_rejects_unhandled_c1_characters() {
        let state = TerminalState::new(80, 24).unwrap();
        assert_eq!(
            state
                .apply(&difference(vec![host_bytes("X\u{80}Y".as_bytes())]))
                .unwrap_err(),
            TerminalError::UnsupportedControl
        );
        assert_eq!(state.screen.contents(), "");
    }

    #[test]
    fn unicode_fallback_abort_regression_is_bounded_and_failure_atomic() {
        let stable = TerminalState::new(80, 24)
            .unwrap()
            .apply(&difference(vec![host_bytes(b"stable")]))
            .unwrap();

        for combining_marks in 0..8 {
            let cell = format!("e{}", "\u{301}".repeat(combining_marks));
            assert!(
                stable
                    .apply(&difference(vec![
                        host_bytes(b"\r"),
                        host_bytes(cell.as_bytes())
                    ]))
                    .is_ok()
            );
        }

        // NexusSSH (P2): Ein Fehler lässt den Zustand weiter unverändert; mehr Kombinationszeichen sind
        // aber keiner mehr – geprüft wird das mit einem Zeichen ohne Grundzeichen.
        let rejected = "\u{301}";
        assert_eq!(
            stable
                .apply(&difference(vec![
                    host_bytes(b"\rX"),
                    host_bytes(rejected.as_bytes())
                ]))
                .unwrap_err(),
            TerminalError::TooManyScalarsInCell
        );
        assert_eq!(stable.screen.contents(), "stable");
        let many = format!("e{}", "\u{301}".repeat(30));
        assert!(
            stable
                .apply(&difference(vec![
                    host_bytes(b"\r"),
                    host_bytes(many.as_bytes())
                ]))
                .is_ok()
        );
    }

    #[test]
    fn rejects_invalid_incomplete_unsupported_and_unrepresentable_input() {
        let state = TerminalState::new(80, 24).unwrap();
        assert_eq!(
            state
                .apply(&difference(vec![host_bytes(&[0xff])]))
                .unwrap_err(),
            TerminalError::InvalidUtf8
        );
        assert_eq!(
            state
                .apply(&difference(vec![host_bytes(b"\x1b[")]))
                .unwrap_err(),
            TerminalError::IncompleteSequence
        );
        // NexusSSH (P2): Unbekannte CSI und zu volle Zellen sind kein Fehler mehr.
        let unknown = state
            .apply(&difference(vec![host_bytes(b"\x1b[999zA")]))
            .unwrap();
        assert_eq!(unknown.screen.contents(), "A");
        let too_many_bytes = format!("e{}", "\u{1ab0}".repeat(7));
        let truncated = state
            .apply(&difference(vec![host_bytes(too_many_bytes.as_bytes())]))
            .unwrap();
        assert_eq!(
            truncated.screen.cell(0, 0).unwrap().contents(),
            format!("e{}", "\u{1ab0}".repeat(6))
        );
    }

    #[test]
    fn validates_terminal_dimensions_and_visible_cells() {
        assert_eq!(
            TerminalState::new(0, 24).unwrap_err(),
            TerminalError::InvalidTerminalSize
        );
        assert_eq!(
            TerminalState::new(MAX_TERMINAL_COLUMNS + 1, 24).unwrap_err(),
            TerminalError::InvalidTerminalSize
        );
        assert_eq!(
            TerminalState::new(500, 200).unwrap().screen.size(),
            (200, 500)
        );
    }

    #[test]
    fn drops_system_effects_from_authoritative_state() {
        let state = TerminalState::new(80, 24)
            .unwrap()
            .apply(&difference(vec![host_bytes(
                b"A\x07\x1b]2;secret title\x07\x1b]52;c;c2VjcmV0\x07B",
            )]))
            .unwrap();
        assert_eq!(state.screen.contents(), "AB");
        // NexusSSH (P9): Glocke und Titel bleiben dabei im Zustand – aus dem Bild sind sie weiter draußen.
        assert_eq!(state.bell_count(), 1);
        assert_eq!(state.title(), Some("secret title"));
    }

    // NexusSSH (P9): Glocke und Fenstertitel aus den Host-Bytes; OSC 1 nennt nur das Symbol, der Titel
    // ist gesäubert und gekürzt, ein Fehler lässt beide unverändert.
    #[test]
    fn bell_count_and_title_follow_the_host_bytes() {
        let initial = TerminalState::new(80, 24).unwrap();
        assert_eq!((initial.bell_count(), initial.title()), (0, None));

        let first = initial
            .apply(&difference(vec![host_bytes(b"\x07\x07\x1b]0;one;two\x07")]))
            .unwrap();
        assert_eq!((first.bell_count(), first.title()), (2, Some("one;two")));
        let icon = first
            .apply(&difference(vec![host_bytes(b"\x1b]1;icon\x07")]))
            .unwrap();
        assert_eq!(icon.title(), Some("one;two"));
        let renamed = icon
            .apply(&difference(vec![host_bytes(b"\x1b]2;three\x07\x07")]))
            .unwrap();
        assert_eq!((renamed.bell_count(), renamed.title()), (3, Some("three")));

        let hostile = renamed
            .apply(&difference(vec![host_bytes(
                "\x1b]2;a\u{9c}b\u{85}c\u{7f}d\u{202e}e\x07".as_bytes(),
            )]))
            .unwrap();
        assert_eq!(hostile.title(), Some("abcd\u{202e}e"));
        let long = format!("\x1b]2;{}\x07", "x".repeat(MAX_TITLE_CHARS + 50));
        let long = renamed
            .apply(&difference(vec![host_bytes(long.as_bytes())]))
            .unwrap();
        assert_eq!(long.title().unwrap().chars().count(), MAX_TITLE_CHARS);

        assert!(
            renamed
                .apply(&difference(vec![
                    host_bytes(b"\x07\x1b]2;lost\x07"),
                    host_bytes(&[0xff])
                ]))
                .is_err()
        );
        assert_eq!((renamed.bell_count(), renamed.title()), (3, Some("three")));
    }

    // NexusSSH (P2): Im Original hieß der Test `accepts_only_verified_external_policy_resets` und
    // verlangte für `CSI ? 1015 h` einen Fehler.
    #[test]
    fn external_policy_modes_are_passed_over_in_both_directions() {
        let state = TerminalState::new(80, 24).unwrap();
        for mode in [
            b"\x1b[?1001l".as_slice(),
            b"\x1b[?1004l",
            b"\x1b[?1015l",
            b"\x1b[?1001h",
            b"\x1b[?1015h",
        ] {
            let next = state.apply(&difference(vec![host_bytes(mode)])).unwrap();
            assert_eq!(
                next.screen.state_formatted(),
                state.screen.state_formatted()
            );
        }
    }

    #[test]
    fn suppresses_verified_focus_reporting_toggles() {
        let state = TerminalState::new(80, 24).unwrap();
        for toggle in [b"\x1b[?1004h".as_slice(), b"\x1b[?1004l"] {
            let next = state.apply(&difference(vec![host_bytes(toggle)])).unwrap();
            assert_eq!(next.screen.contents(), state.screen.contents());
        }
    }

    // NexusSSH (P10)
    #[test]
    fn a_flagged_prediction_is_underlined_and_the_style_after_it_stays() {
        let styled = TerminalState::new(80, 24)
            .unwrap()
            .apply(&difference(vec![host_bytes(b"\x1b[1;31m> ")]))
            .unwrap();
        let flagged = styled.with_flagged_ascii(b'a').unwrap();
        let plain = styled.with_predicted_ascii(b'a').unwrap();
        let next = flagged.with_predicted_ascii(b'b').unwrap();

        let a = next.screen.cell(0, 2).unwrap();
        assert_eq!(a.contents(), "a");
        assert!(a.underline());
        assert!(a.bold());
        assert_eq!(a.fgcolor(), vt100::Color::Idx(1));
        let b = next.screen.cell(0, 3).unwrap();
        assert_eq!(b.contents(), "b");
        assert!(!b.underline());
        assert!(b.bold());
        assert_eq!(b.fgcolor(), vt100::Color::Idx(1));

        assert!(!flagged.display_equivalent(&plain));
        assert_eq!(
            flagged.screen.cursor_position(),
            plain.screen.cursor_position()
        );

        let underlined = TerminalState::new(80, 24)
            .unwrap()
            .apply(&difference(vec![host_bytes(b"\x1b[4m> ")]))
            .unwrap()
            .with_flagged_ascii(b'a')
            .unwrap()
            .with_predicted_ascii(b'b')
            .unwrap();
        assert!(underlined.screen.cell(0, 3).unwrap().underline());
    }

    proptest! {
        #![proptest_config(ProptestConfig::with_cases(64))]

        #[test]
        fn ordered_ascii_chunks_converge_to_the_concatenated_update(
            chunks in prop::collection::vec(
                prop::collection::vec(0x20_u8..0x7f, 0..32),
                1..16,
            )
        ) {
            let operations = chunks.iter().map(|chunk| host_bytes(chunk)).collect();
            let chunked = TerminalState::new(80, 24)
                .unwrap()
                .apply(&difference(operations))
                .unwrap();
            let concatenated = chunks.concat();
            let single = TerminalState::new(80, 24)
                .unwrap()
                .apply(&difference(vec![host_bytes(&concatenated)]))
                .unwrap();

            prop_assert_eq!(
                chunked.screen.state_formatted(),
                single.screen.state_formatted()
            );
        }
    }
}
