use super::*;
use proptest::prelude::*;

fn state(text: &[u8]) -> TerminalState {
    let mut state = TerminalState::new(80, 24).unwrap();
    for byte in text {
        state = state.with_predicted_ascii(*byte).unwrap();
    }
    state
}

fn state_with_acknowledgement(text: &[u8], number: u64) -> TerminalState {
    state(text).with_test_echo_acknowledgement(number)
}

fn prediction(mode: PredictionMode) -> LocalPrediction {
    LocalPrediction::new(mode)
}

// NexusSSH (P10): der Bildschirm `base` mit unterstrichen vorhergesagtem `predicted` dahinter.
fn shown(base: &[u8], predicted: &[u8]) -> TerminalState {
    let mut shown = state(base);
    for byte in predicted {
        shown = shown.with_flagged_ascii(*byte).unwrap();
    }
    shown
}

#[test]
fn first_prediction_stays_hidden_until_stock_echo_confirmation() {
    let authoritative = state(b"prompt> ");
    let mut prediction = prediction(PredictionMode::Always);

    assert!(
        !prediction
            .observe_input(2, b"a", &authoritative, 10)
            .unwrap()
    );
    assert!(
        prediction
            .display(&authoritative)
            .display_equivalent(&authoritative)
    );

    let confirmed = state_with_acknowledgement(b"prompt> a", 2);
    prediction.observe_authoritative(&confirmed).unwrap();
    assert!(prediction.pending.is_empty());
    assert_eq!(prediction.confidence, EpochConfidence::Confirmed);

    assert!(prediction.observe_input(3, b"b", &confirmed, 20).unwrap());
    assert!(
        prediction
            .display(&confirmed)
            .display_equivalent(&state(b"prompt> ab"))
    );
}

#[test]
fn acknowledgement_free_authority_preserves_a_confirmed_idle_epoch() {
    let authoritative = state(b"prompt> ");
    let mut prediction = prediction(PredictionMode::Always);

    assert!(
        !prediction
            .observe_input(2, b"a", &authoritative, 10)
            .unwrap()
    );
    let confirmed = state_with_acknowledgement(b"prompt> a", 2);
    prediction.observe_authoritative(&confirmed).unwrap();
    assert_eq!(prediction.confidence, EpochConfidence::Confirmed);

    prediction.observe_authoritative(&confirmed).unwrap();

    assert_eq!(prediction.confidence, EpochConfidence::Confirmed);
    assert!(prediction.observe_input(3, b"b", &confirmed, 20).unwrap());
}

#[test]
fn host_effect_before_echo_acknowledgement_preserves_the_tentative_epoch() {
    let authoritative = state(b"prompt> ");
    let mut prediction = prediction(PredictionMode::Always);

    assert!(
        !prediction
            .observe_input(2, b"a", &authoritative, 10)
            .unwrap()
    );

    let host_effect = state(b"prompt> a");
    prediction.observe_authoritative(&host_effect).unwrap();
    assert_eq!(prediction.confidence, EpochConfidence::Tentative);
    assert_eq!(prediction.pending.len(), 1);

    let acknowledged = state_with_acknowledgement(b"prompt> a", 2);
    prediction.observe_authoritative(&acknowledged).unwrap();
    assert_eq!(prediction.confidence, EpochConfidence::Confirmed);
    assert!(prediction.pending.is_empty());
    assert!(
        prediction
            .observe_input(3, b"b", &acknowledged, 20)
            .unwrap()
    );
}

#[test]
fn tentative_epoch_records_input_beyond_the_first_candidate() {
    let authoritative = state(b"prompt> ");
    let mut prediction = prediction(PredictionMode::Always);

    assert!(
        !prediction
            .observe_input(2, b"a", &authoritative, 10)
            .unwrap()
    );
    assert!(
        !prediction
            .observe_input(3, b"b", &authoritative, 11)
            .unwrap()
    );
    assert_eq!(prediction.pending.len(), 2);

    let both_host_effects = state_with_acknowledgement(b"prompt> ab", 2);
    prediction
        .observe_authoritative(&both_host_effects)
        .unwrap();
    assert_eq!(prediction.confidence, EpochConfidence::Confirmed);
    assert!(prediction.pending.is_empty());
    assert!(
        prediction
            .observe_input(4, b"c", &both_host_effects, 20)
            .unwrap()
    );
}

#[test]
fn acknowledgement_that_outpaces_host_effect_clears_candidates() {
    let authoritative = state(b"prompt> ");
    let mut prediction = prediction(PredictionMode::Always);

    prediction
        .observe_input(2, b"a", &authoritative, 10)
        .unwrap();
    let acknowledgement_without_effect = state_with_acknowledgement(b"prompt> ", 2);
    prediction
        .observe_authoritative(&acknowledgement_without_effect)
        .unwrap();

    assert_eq!(prediction.confidence, EpochConfidence::Tentative);
    assert!(prediction.pending.is_empty());
}

#[test]
fn acknowledgement_free_authority_clears_only_a_diverged_pending_projection() {
    let authoritative = state(b"prompt> ");
    let mut prediction = prediction(PredictionMode::Always);
    prediction.confidence = EpochConfidence::Confirmed;
    assert!(
        prediction
            .observe_input(2, b"a", &authoritative, 10)
            .unwrap()
    );

    prediction.observe_authoritative(&authoritative).unwrap();
    assert_eq!(prediction.confidence, EpochConfidence::Confirmed);
    assert_eq!(prediction.pending.len(), 1);
    assert!(
        prediction
            .display(&authoritative)
            .display_equivalent(&state(b"prompt> a"))
    );

    prediction
        .observe_authoritative(&state(b"changed"))
        .unwrap();
    assert_eq!(prediction.confidence, EpochConfidence::Tentative);
    assert!(prediction.pending.is_empty());
}

#[test]
fn matching_authority_confirms_visible_predictions_without_changing_projection() {
    let authoritative = state(b"a");
    let mut prediction = prediction(PredictionMode::Always);
    prediction.confidence = EpochConfidence::Confirmed;
    prediction
        .observe_input(3, b"b", &authoritative, 20)
        .unwrap();
    let displayed = prediction.display(&authoritative).clone();

    let confirmed = displayed.with_test_echo_acknowledgement(3);
    prediction.observe_authoritative(&confirmed).unwrap();

    assert_eq!(prediction.confidence, EpochConfidence::Confirmed);
    assert!(prediction.pending.is_empty());
    assert!(
        prediction
            .display(&confirmed)
            .display_equivalent(&confirmed)
    );
}

#[test]
fn one_divergence_or_control_input_clears_the_visible_projection() {
    let authoritative = state(b"a");
    let mut prediction = prediction(PredictionMode::Always);
    prediction.confidence = EpochConfidence::Confirmed;
    prediction
        .observe_input(3, b"b", &authoritative, 20)
        .unwrap();

    prediction
        .observe_authoritative(&state_with_acknowledgement(b"x", 3))
        .unwrap();
    assert_eq!(prediction.confidence, EpochConfidence::Tentative);
    assert!(prediction.pending.is_empty());

    prediction.confidence = EpochConfidence::Confirmed;
    prediction
        .observe_input(4, b"c", &authoritative, 30)
        .unwrap();
    assert!(
        prediction
            .observe_input(5, b"\x7f", &authoritative, 31)
            .unwrap()
    );
    assert_eq!(prediction.confidence, EpochConfidence::Tentative);
    assert!(prediction.pending.is_empty());
}

#[test]
fn paste_clears_all_tentative_candidates() {
    let authoritative = state(b"a");
    let mut prediction = prediction(PredictionMode::Always);

    assert!(
        !prediction
            .observe_input(2, b"b", &authoritative, 10)
            .unwrap()
    );
    assert!(
        !prediction
            .observe_input(3, b"c", &authoritative, 11)
            .unwrap()
    );
    assert_eq!(prediction.pending.len(), 2);
    assert!(
        !prediction
            .observe_input(4, b"pasted", &authoritative, 12)
            .unwrap()
    );
    assert!(prediction.pending.is_empty());
}

#[test]
fn erase_and_mixed_input_never_enter_the_projection() {
    let authoritative = state(b"prompt> ");
    let mut prediction = prediction(PredictionMode::Always);
    prediction.confidence = EpochConfidence::Confirmed;

    assert!(
        prediction
            .observe_input(2, b"a", &authoritative, 10)
            .unwrap()
    );
    prediction
        .observe_authoritative(&state_with_acknowledgement(b"prompt> ", 1))
        .unwrap();
    assert!(
        prediction
            .display(&authoritative)
            .display_equivalent(&state(b"prompt> a"))
    );

    assert!(
        prediction
            .observe_input(3, b"\x08", &authoritative, 11)
            .unwrap()
    );
    assert!(prediction.pending.is_empty());
    assert_eq!(prediction.confidence, EpochConfidence::Tentative);
    assert!(
        prediction
            .display(&authoritative)
            .display_equivalent(&authoritative)
    );

    prediction.confidence = EpochConfidence::Confirmed;
    assert!(
        prediction
            .observe_input(4, b"b", &authoritative, 12)
            .unwrap()
    );
    assert!(
        prediction
            .observe_input(5, b"\x7f", &authoritative, 13)
            .unwrap()
    );
    assert!(prediction.pending.is_empty());
    assert_eq!(prediction.confidence, EpochConfidence::Tentative);

    prediction.confidence = EpochConfidence::Confirmed;
    assert!(
        prediction
            .observe_input(6, b"c", &authoritative, 14)
            .unwrap()
    );
    assert!(
        prediction
            .observe_input(7, b"d\x08", &authoritative, 15)
            .unwrap()
    );
    assert!(prediction.pending.is_empty());
    assert_eq!(prediction.confidence, EpochConfidence::Tentative);
    assert!(
        prediction
            .display(&authoritative)
            .display_equivalent(&authoritative)
    );
}

#[test]
fn prediction_age_and_capacity_are_bounded() {
    let authoritative = state(b"");
    let mut prediction = prediction(PredictionMode::Always);
    prediction.confidence = EpochConfidence::Confirmed;

    for state in 1..=MAX_PENDING_PREDICTION_SCALARS {
        assert!(
            prediction
                .observe_input(u64::try_from(state).unwrap(), b"x", &authoritative, 10,)
                .unwrap()
        );
    }
    assert!(
        prediction
            .observe_input(33, b"x", &authoritative, 10)
            .unwrap()
    );
    assert!(prediction.pending.is_empty());
    assert_eq!(prediction.confidence, EpochConfidence::Tentative);

    prediction.confidence = EpochConfidence::Confirmed;
    prediction
        .observe_input(34, b"x", &authoritative, 20)
        .unwrap();
    // NexusSSH (P10): Vor dem Ablauf weckt in `Always` das Funkloch (Unterstrich nach 250 ms).
    assert_eq!(prediction.next_deadline_ms(), Some(270));
    assert!(prediction.update_policy(270, None));
    assert_eq!(prediction.next_deadline_ms(), Some(10_020));
    assert!(!prediction.expire(10_019));
    assert!(prediction.expire(10_020));
    assert!(prediction.pending.is_empty());
}

#[test]
fn never_keeps_only_authoritative_display_state() {
    let authoritative = state(b"prompt> ");
    let mut prediction = prediction(PredictionMode::Never);

    assert!(
        !prediction
            .observe_input(2, b"a", &authoritative, 10)
            .unwrap()
    );
    assert!(prediction.pending.is_empty());
    assert!(
        prediction
            .display(&authoritative)
            .display_equivalent(&authoritative)
    );
    assert_eq!(prediction.next_deadline_ms(), None);
}

#[test]
fn adaptive_uses_slow_link_hysteresis_without_retracting_visible_input() {
    let authoritative = state(b"prompt> ");
    let mut prediction = prediction(PredictionMode::Adaptive);
    prediction.confidence = EpochConfidence::Confirmed;

    assert!(
        !prediction
            .observe_input(2, b"a", &authoritative, 10)
            .unwrap()
    );
    assert!(!prediction.update_policy(10, None));
    assert!(prediction.update_policy(10, Some(31)));
    assert!(
        prediction
            .display(&authoritative)
            .display_equivalent(&shown(b"prompt> ", b"a"))
    );

    assert!(!prediction.update_policy(11, Some(25)));
    assert!(!prediction.update_policy(12, Some(20)));
    let confirmed = state_with_acknowledgement(b"prompt> a", 2);
    prediction.observe_authoritative(&confirmed).unwrap();
    assert!(!prediction.update_policy(13, Some(20)));

    assert!(!prediction.observe_input(3, b"b", &confirmed, 14).unwrap());
    assert!(
        prediction
            .display(&confirmed)
            .display_equivalent(&confirmed)
    );
}

#[test]
fn adaptive_temporarily_displays_a_prediction_during_a_glitch() {
    let authoritative = state(b"prompt> ");
    let mut prediction = prediction(PredictionMode::Adaptive);
    prediction.confidence = EpochConfidence::Confirmed;

    assert!(
        !prediction
            .observe_input(2, b"a", &authoritative, 100)
            .unwrap()
    );
    assert_eq!(prediction.next_deadline_ms(), Some(350));
    assert!(!prediction.update_policy(349, Some(20)));
    assert!(prediction.update_policy(350, Some(20)));
    assert!(
        prediction
            .display(&authoritative)
            .display_equivalent(&shown(b"prompt> ", b"a"))
    );

    let confirmed = state_with_acknowledgement(b"prompt> a", 2);
    prediction.observe_authoritative(&confirmed).unwrap();
    assert!(!prediction.update_policy(351, Some(20)));
    assert!(!prediction.observe_input(3, b"b", &confirmed, 352).unwrap());
}

// NexusSSH (P10)
#[test]
fn always_underlines_only_on_a_slow_link_or_during_a_glitch() {
    let authoritative = state(b"prompt> ");
    let mut prediction = prediction(PredictionMode::Always);
    prediction.confidence = EpochConfidence::Confirmed;

    assert!(
        prediction
            .observe_input(2, b"a", &authoritative, 100)
            .unwrap()
    );
    assert!(!prediction.update_policy(100, Some(20)));
    assert!(
        prediction
            .display(&authoritative)
            .display_equivalent(&state(b"prompt> a"))
    );

    assert_eq!(prediction.next_deadline_ms(), Some(350));
    assert!(!prediction.update_policy(349, Some(20)));
    assert!(prediction.update_policy(350, Some(20)));
    assert!(
        prediction
            .display(&authoritative)
            .display_equivalent(&shown(b"prompt> ", b"a"))
    );

    let confirmed = state_with_acknowledgement(b"prompt> a", 2);
    prediction.observe_authoritative(&confirmed).unwrap();
    assert!(
        prediction
            .display(&confirmed)
            .display_equivalent(&confirmed)
    );
    assert!(!prediction.update_policy(351, Some(20)));
    assert!(prediction.observe_input(3, b"b", &confirmed, 352).unwrap());
    assert!(
        prediction
            .display(&confirmed)
            .display_equivalent(&state(b"prompt> ab"))
    );

    assert!(prediction.update_policy(353, Some(31)));
    assert!(
        prediction
            .display(&confirmed)
            .display_equivalent(&shown(b"prompt> a", b"b"))
    );
}

// NexusSSH (P10)
#[test]
fn underlined_predictions_confirm_against_the_plain_server_screen() {
    let authoritative = state(b"prompt> ");
    let mut prediction = prediction(PredictionMode::Adaptive);
    prediction.confidence = EpochConfidence::Confirmed;

    assert!(!prediction.update_policy(10, Some(31)));
    assert!(
        prediction
            .observe_input(2, b"a", &authoritative, 11)
            .unwrap()
    );
    assert!(
        prediction
            .observe_input(3, b"b", &authoritative, 12)
            .unwrap()
    );
    assert!(
        prediction
            .display(&authoritative)
            .display_equivalent(&shown(b"prompt> ", b"ab"))
    );

    let first = state_with_acknowledgement(b"prompt> a", 2);
    prediction.observe_authoritative(&first).unwrap();
    assert_eq!(prediction.confidence, EpochConfidence::Confirmed);
    assert_eq!(prediction.pending.len(), 1);
    assert!(
        prediction
            .display(&first)
            .display_equivalent(&shown(b"prompt> a", b"b"))
    );

    let both = state_with_acknowledgement(b"prompt> ab", 3);
    prediction.observe_authoritative(&both).unwrap();
    assert!(prediction.pending.is_empty());
    assert!(prediction.display(&both).display_equivalent(&both));
}

proptest! {
    #![proptest_config(ProptestConfig::with_cases(64))]

    #[test]
    fn ordered_host_effect_prefixes_and_lagging_acknowledgements_confirm(
        bytes in prop::collection::vec(0x20_u8..0x7f, 1..17),
        acknowledgement_selector in 0_usize..16,
    ) {
        let base = state(b"");
        let mut prediction = prediction(PredictionMode::Always);
        for (index, byte) in bytes.iter().enumerate() {
            prediction
                .observe_input(
                    u64::try_from(index).unwrap() + 2,
                    &[*byte],
                    &base,
                    u64::try_from(index).unwrap(),
                )
                .unwrap();
        }

        for prefix in 0..=bytes.len() {
            prediction.observe_authoritative(&state(&bytes[..prefix])).unwrap();
            prop_assert_eq!(prediction.pending.len(), bytes.len());
            prop_assert_eq!(prediction.confidence, EpochConfidence::Tentative);
        }

        let acknowledged = acknowledgement_selector % bytes.len() + 1;
        let acknowledgement = u64::try_from(acknowledged).unwrap() + 1;
        prediction
            .observe_authoritative(
                &state(&bytes).with_test_echo_acknowledgement(acknowledgement),
            )
            .unwrap();

        prop_assert_eq!(prediction.confidence, EpochConfidence::Confirmed);
        prop_assert!(prediction.pending.is_empty());
    }
}
