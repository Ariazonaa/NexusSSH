//! Independent, unofficial Mosh client implementation.
//!
//! The crate exposes validated bootstrap values and a small asynchronous
//! Session API. SSH authentication, host trust, and `mosh-server` startup
//! remain the caller's responsibility.

#![forbid(unsafe_code)]
#![deny(rustdoc::broken_intra_doc_links)]

// The MIT-licensed vt100 0.16.2 screen implementation is kept private here so
// the source package uses the same U+FFFD and cell-width fixes as a Git checkout.
extern crate self as vt100;

#[allow(dead_code)]
#[rustfmt::skip]
#[path = "vt100/attrs.rs"]
mod attrs;
#[rustfmt::skip]
#[path = "vt100/callbacks.rs"]
mod callbacks;
#[allow(dead_code)]
#[rustfmt::skip]
#[path = "vt100/cell.rs"]
mod cell;
#[allow(dead_code)]
#[allow(clippy::too_many_lines)]
#[rustfmt::skip]
#[path = "vt100/grid.rs"]
mod grid;
#[allow(dead_code)]
#[rustfmt::skip]
#[path = "vt100/parser.rs"]
mod parser;
#[allow(clippy::too_many_lines)]
#[rustfmt::skip]
#[path = "vt100/perform.rs"]
mod perform;
#[allow(dead_code)]
#[allow(clippy::too_many_arguments, clippy::too_many_lines, clippy::collapsible_if)]
#[rustfmt::skip]
#[path = "vt100/row.rs"]
mod row;
#[allow(dead_code)]
#[allow(clippy::too_many_lines)]
#[rustfmt::skip]
#[path = "vt100/screen.rs"]
mod screen;
#[rustfmt::skip]
#[path = "vt100/term.rs"]
mod term;

pub(crate) use attrs::Color;
pub(crate) use callbacks::Callbacks;
pub(crate) use cell::Cell;
pub(crate) use parser::Parser;
pub(crate) use screen::{MouseProtocolEncoding, MouseProtocolMode, Screen};

mod bootstrap;
#[allow(dead_code)]
mod crypto;
mod error;
#[allow(dead_code)]
mod fragment;
// NexusSSH (P7): auch in Tests – `fuzzing::tests` treibt die Eingänge bei jedem `cargo test` mit
// festem Zufall.
#[cfg(any(fuzzing, test))]
#[doc(hidden)]
pub mod fuzzing;
#[allow(dead_code)]
mod instruction;
mod limits;
#[allow(dead_code)]
mod packet;
#[allow(dead_code)]
mod prediction;
mod session;
#[allow(dead_code)]
mod synchronization;
#[allow(dead_code)]
mod terminal;
mod terminal_width;
// NexusSSH (P4): Die Unit-Tests brauchen den festen Schlüssel auch unter Windows; die Hilfen für die
// Stock-Tests bleiben dort ungenutzt.
#[cfg(test)]
#[cfg_attr(not(target_os = "linux"), allow(dead_code))]
mod test_support;
#[allow(dead_code)]
mod timing;

pub use bootstrap::Bootstrap;
pub use error::BootstrapError;
pub use prediction::PredictionMode;
pub use session::{
    Session, SessionCommandError, SessionError, SessionExit, SessionInterruption,
    SessionReachability, SessionReachabilityWatch, SessionState, SessionTask,
};
