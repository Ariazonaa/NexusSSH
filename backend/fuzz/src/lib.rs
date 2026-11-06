//! Gemeinsames der Ziele für `cargo fuzz`.

use std::cell::RefCell;
use std::sync::Once;

thread_local! {
    /// Die Meldung des letzten Panics – gezeigt wird sie nur, wenn ihn niemand gefangen hat.
    static LAST_PANIC: RefCell<String> = const { RefCell::new(String::new()) };
}

/// Reicht `data` an einen Eingang des Kerns (`nexus_core::fuzzing`) und bricht ab, wenn der mit einem Panic endet.
///
/// libFuzzer wertet von sich aus jeden Panic als Absturz, auch einen, den der Kern absichtlich fängt (der Leser
/// der FTP-Bibliothek stolpert über manche Zeile; `ftp::guarded` macht daraus „nicht lesbar“). Deshalb zählt hier
/// nur, was beim Aufrufer ankommt.
pub fn run(target: &str, data: &[u8]) {
    static HOOK: Once = Once::new();
    HOOK.call_once(|| {
        std::panic::set_hook(Box::new(|info| {
            LAST_PANIC.with(|last| *last.borrow_mut() = info.to_string());
        }));
    });

    if std::panic::catch_unwind(|| nexus_core::fuzzing::run(target, data)).is_err() {
        LAST_PANIC.with(|last| eprintln!("{}", last.borrow()));
        std::process::abort();
    }
}
