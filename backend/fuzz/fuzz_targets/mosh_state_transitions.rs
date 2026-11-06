// Aus mosh-client-rs 0.1.4 (`fuzz/fuzz_targets/state_transitions.rs`), hierher verschoben (NexusSSH P7). Die
// Eingänge stehen in `mosh-client/src/fuzzing.rs`.
#![no_main]

use libfuzzer_sys::fuzz_target;

fuzz_target!(|data: &[u8]| {
    mosh_client::fuzzing::exercise_state_transitions(data);
});
