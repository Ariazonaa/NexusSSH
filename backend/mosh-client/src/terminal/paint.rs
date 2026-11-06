use super::TerminalState;
use crate::limits::MAX_OUTPUT_CHUNK_BYTES;

const INITIAL_PROFILE_POLICY_RESET: &[u8] = b"\x1b[?1001l\x1b[?1004l\x1b[?1015l";

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum PaintError {
    OutputTooLarge,
}

pub(crate) struct TerminalPainter;

impl TerminalPainter {
    pub(crate) fn full(state: &TerminalState) -> Result<Vec<u8>, PaintError> {
        let formatted = state.screen().state_formatted();
        let mut output = Vec::with_capacity(INITIAL_PROFILE_POLICY_RESET.len() + formatted.len());
        output.extend_from_slice(INITIAL_PROFILE_POLICY_RESET);
        output.extend_from_slice(&formatted);
        bounded(output)
    }

    pub(crate) fn incremental(
        previous: &TerminalState,
        current: &TerminalState,
    ) -> Result<Vec<u8>, PaintError> {
        if previous.screen().size() != current.screen().size() {
            return Self::full(current);
        }
        bounded(current.screen().state_diff(previous.screen()))
    }

    // NexusSSH (P9): Glocke und Fenstertitel hinter das Bild hängen – als `BEL` bzw. OSC 0, so wie
    // ein Terminal sie liest. Der Titel ist schon gesäubert (`TerminalState::title`).
    pub(crate) fn with_effects(
        mut output: Vec<u8>,
        title: Option<&str>,
        bell: bool,
    ) -> Result<Vec<u8>, PaintError> {
        if let Some(title) = title {
            output.extend_from_slice(b"\x1b]0;");
            output.extend_from_slice(title.as_bytes());
            output.push(0x07);
        }
        if bell {
            output.push(0x07);
        }
        bounded(output)
    }
}

fn bounded(output: Vec<u8>) -> Result<Vec<u8>, PaintError> {
    if output.len() > MAX_OUTPUT_CHUNK_BYTES {
        return Err(PaintError::OutputTooLarge);
    }
    Ok(output)
}

#[cfg(test)]
mod tests {
    use prost::Message as _;

    use super::*;
    use crate::terminal::{
        TerminalDifference, WireHostBytes, WireHostDifference, WireHostOperation, WireHostSize,
    };

    fn apply(state: &TerminalState, bytes: &[u8]) -> TerminalState {
        let wire = WireHostDifference {
            operations: vec![WireHostOperation {
                host_bytes: Some(WireHostBytes {
                    bytes: bytes.to_vec(),
                }),
                size: None,
                echo_acknowledgement: None,
            }],
        }
        .encode_to_vec();
        state
            .apply(&TerminalDifference::decode(&wire).unwrap())
            .unwrap()
    }

    fn assert_repaints(source: &TerminalState, paint: &[u8]) {
        let (rows, columns) = source.screen().size();
        let mut projection = vt100::Parser::new(rows, columns, 0);
        projection.process(paint);
        assert_eq!(
            projection.screen().state_formatted(),
            source.screen().state_formatted()
        );
    }

    #[test]
    fn full_repaint_is_self_contained_and_has_no_system_effects() {
        let state = apply(
            &TerminalState::new(80, 24).unwrap(),
            b"\x1b[31mA\x1b[0m\x07\x1b]2;secret title\x07B",
        );
        let paint = TerminalPainter::full(&state).unwrap();
        assert_repaints(&state, &paint);
        assert!(!paint.contains(&0x07));
        assert!(!paint.windows(2).any(|bytes| bytes == b"]2"));
        assert!(!paint.windows(2).any(|bytes| bytes == b"]52"));
        assert!(paint.starts_with(INITIAL_PROFILE_POLICY_RESET));
    }

    #[test]
    fn incremental_repaint_transforms_the_previous_projection() {
        let previous = apply(&TerminalState::new(80, 24).unwrap(), b"first");
        let current = apply(&previous, b"\rsecond");
        let mut projection = vt100::Parser::new(24, 80, 0);
        projection.process(&TerminalPainter::full(&previous).unwrap());
        projection.process(&TerminalPainter::incremental(&previous, &current).unwrap());
        assert_eq!(
            projection.screen().state_formatted(),
            current.screen().state_formatted()
        );
    }

    #[test]
    fn replacement_character_survives_full_and_incremental_repaint() {
        let previous = apply(&TerminalState::new(80, 24).unwrap(), b"BEFORE-");
        let current = apply(&previous, "\u{fffd}-AFTER".as_bytes());
        let full = TerminalPainter::full(&current).unwrap();
        assert!(full.windows(3).any(|bytes| bytes == "\u{fffd}".as_bytes()));
        assert_repaints(&current, &full);

        let mut projection = vt100::Parser::new(24, 80, 0);
        projection.process(&TerminalPainter::full(&previous).unwrap());
        projection.process(&TerminalPainter::incremental(&previous, &current).unwrap());
        assert_eq!(projection.screen().contents(), "BEFORE-\u{fffd}-AFTER");
        assert_eq!(projection.screen().cursor_position(), (0, 14));
    }

    #[test]
    fn server_spacing_character_survives_split_differences_followup_and_resize() {
        let initial = TerminalState::new(80, 24).unwrap();
        let line_start = apply(&initial, "\u{0605}".as_bytes());
        assert_eq!(line_start.screen().cursor_position(), (0, 1));
        let after = apply(&line_start, b"-AFTER");
        assert_eq!(after.screen().cell(0, 0).unwrap().contents(), "\u{0605}");
        assert_eq!(after.screen().cell(0, 1).unwrap().contents(), "-");
        assert_eq!(after.screen().cursor_position(), (0, 7));

        let mut projection = vt100::Parser::new(24, 80, 0);
        projection.process(&TerminalPainter::full(&initial).unwrap());
        for (previous, current) in [(&initial, &line_start), (&line_start, &after)] {
            projection.process(&TerminalPainter::incremental(previous, current).unwrap());
            assert_eq!(
                projection.screen().state_formatted(),
                current.screen().state_formatted()
            );
        }

        let continued = apply(&after, b"Z");
        assert_eq!(continued.screen().contents(), "\u{0605}-AFTERZ");
        assert_eq!(continued.screen().cursor_position(), (0, 8));
        projection.process(&TerminalPainter::incremental(&after, &continued).unwrap());
        assert_eq!(
            projection.screen().state_formatted(),
            continued.screen().state_formatted()
        );

        let resize = WireHostDifference {
            operations: vec![WireHostOperation {
                host_bytes: None,
                size: Some(WireHostSize {
                    columns: 81,
                    rows: 25,
                }),
                echo_acknowledgement: None,
            }],
        }
        .encode_to_vec();
        let resized = continued
            .apply(&TerminalDifference::decode(&resize).unwrap())
            .unwrap();
        assert_repaints(&resized, &TerminalPainter::full(&resized).unwrap());
        assert_eq!(resized.screen().cell(0, 0).unwrap().contents(), "\u{0605}");
    }

    #[test]
    fn blink_hidden_and_reverse_video_survive_incremental_and_full_repaint() {
        let initial = TerminalState::new(80, 24).unwrap();
        let active = apply(&initial, b"\x1b[5mB\x1b[8mH\x1b[?5h");
        let full = TerminalPainter::full(&active).unwrap();
        assert!(full.windows(5).any(|bytes| bytes == b"\x1b[?5h"));
        assert_repaints(&active, &full);

        let mut projection = vt100::Parser::new(24, 80, 0);
        projection.process(&TerminalPainter::full(&initial).unwrap());
        projection.process(&TerminalPainter::incremental(&initial, &active).unwrap());
        assert_eq!(
            projection.screen().state_formatted(),
            active.screen().state_formatted()
        );

        let reset = apply(&active, b"\x1b[25m\x1b[28m\x1b[?5lN");
        let incremental = TerminalPainter::incremental(&active, &reset).unwrap();
        assert!(incremental.windows(5).any(|bytes| bytes == b"\x1b[?5l"));
        projection.process(&incremental);
        assert_eq!(
            projection.screen().state_formatted(),
            reset.screen().state_formatted()
        );
        assert_repaints(&reset, &TerminalPainter::full(&reset).unwrap());
    }

    #[test]
    fn sparse_blank_rows_never_confuse_incremental_repaint() {
        let initial = TerminalState::new(40, 20).unwrap();
        let frames = [
            b"\x1b[1;1HTOP\x1b[18;1HLOWER".as_slice(),
            b"\x1b[1;1H   \x1b[10;1HMIDDLE\x1b[18;1Hlower".as_slice(),
            b"\x1b[10;1H      \x1b[3;1HFINAL".as_slice(),
        ];
        let mut previous = initial;
        let mut projection = vt100::Parser::new(20, 40, 0);
        projection.process(&TerminalPainter::full(&previous).unwrap());

        for update in frames {
            let current = apply(&previous, update);
            projection.process(&TerminalPainter::incremental(&previous, &current).unwrap());
            assert_eq!(
                projection.screen().state_formatted(),
                current.screen().state_formatted()
            );
            previous = current;
        }
    }

    #[test]
    fn size_change_forces_a_self_contained_full_repaint() {
        let previous = apply(&TerminalState::new(80, 24).unwrap(), b"old");
        let resize = WireHostDifference {
            operations: vec![WireHostOperation {
                host_bytes: None,
                size: Some(WireHostSize {
                    columns: 81,
                    rows: 25,
                }),
                echo_acknowledgement: None,
            }],
        }
        .encode_to_vec();
        let current = previous
            .apply(&TerminalDifference::decode(&resize).unwrap())
            .unwrap();
        let incremental = TerminalPainter::incremental(&previous, &current).unwrap();
        assert_eq!(incremental, TerminalPainter::full(&current).unwrap());
        assert_repaints(&current, &incremental);
    }

    // NexusSSH (P9): Titel als OSC 0, Glocke als `BEL`, beides hinter dem Bild und mit der Grenze.
    #[test]
    fn effects_follow_the_paint_as_osc_0_and_bel() {
        assert_eq!(
            TerminalPainter::with_effects(b"PAINT".to_vec(), Some("t;1"), true).unwrap(),
            b"PAINT\x1b]0;t;1\x07\x07"
        );
        assert_eq!(
            TerminalPainter::with_effects(b"PAINT".to_vec(), None, false).unwrap(),
            b"PAINT"
        );
        assert_eq!(
            TerminalPainter::with_effects(vec![0; MAX_OUTPUT_CHUNK_BYTES], None, true),
            Err(PaintError::OutputTooLarge)
        );
    }

    #[test]
    fn bounded_output_rejects_oversized_chunks() {
        assert_eq!(
            bounded(vec![0; MAX_OUTPUT_CHUNK_BYTES + 1]),
            Err(PaintError::OutputTooLarge)
        );
    }
}
