mod client_history;
mod driver;
mod reachability;

pub(crate) use self::driver::{DriverError, SessionChannels, SessionCommand, SessionDriver};
pub use self::reachability::{SessionInterruption, SessionReachability, SessionReachabilityWatch};

use std::fmt;
use std::io::ErrorKind;
use std::sync::Mutex;

use tokio::sync::{mpsc, watch};

use crate::Bootstrap;
use crate::limits::MAX_INPUT_COMMAND_BYTES;
use crate::prediction::PredictionMode;
use crate::terminal::is_valid_size;

/// The externally visible lifecycle of a Session.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
#[non_exhaustive]
pub enum SessionState {
    /// The local UDP endpoint exists, but no authenticated remote state has arrived.
    Connecting,
    /// At least one authenticated remote terminal state has been accepted.
    Active,
    /// The Session task has stopped or was dropped.
    Closed,
}

/// The expected reason a Session task stopped.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
#[non_exhaustive]
pub enum SessionExit {
    /// A locally requested graceful close completed or reached its bounded ACK wait.
    LocalClosed,
    /// The authenticated peer requested a graceful close.
    RemoteClosed,
    /// The owning application explicitly cancelled the Session.
    Cancelled,
    /// The Session handle or its output consumer was dropped.
    OwnerDropped,
}

/// A failure reported by the Session task.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
#[non_exhaustive]
pub enum SessionError {
    /// Local socket setup or datagram I/O failed.
    Io(ErrorKind),
    /// The requested initial terminal size is outside the documented limits.
    InvalidTerminalSize,
    /// No complete authenticated remote state arrived before the attachment deadline.
    ConnectionTimeout,
    /// Authenticated input violated the supported protocol or terminal contract.
    Protocol,
    /// A bounded protocol or output resource limit was reached.
    ResourceLimit,
    /// A monotonic protocol identifier or clock value was exhausted.
    StateExhausted,
    /// An internal state invariant could not be satisfied.
    InternalState,
}

impl fmt::Display for SessionError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Io(kind) => write!(formatter, "Session I/O failed: {kind:?}"),
            Self::InvalidTerminalSize => formatter.write_str("invalid terminal size"),
            Self::ConnectionTimeout => formatter.write_str("Session attachment timed out"),
            Self::Protocol => formatter.write_str("remote protocol state is invalid"),
            Self::ResourceLimit => formatter.write_str("Session resource limit reached"),
            Self::StateExhausted => formatter.write_str("Session state space exhausted"),
            Self::InternalState => formatter.write_str("Session internal state is unavailable"),
        }
    }
}

impl std::error::Error for SessionError {}

/// A command rejected before it entered the Session task.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
#[non_exhaustive]
pub enum SessionCommandError {
    /// The Session is cancelled or closed.
    Closed,
    /// One input command exceeded the documented byte limit.
    InputTooLarge,
    /// The requested terminal size is outside the documented limits.
    InvalidTerminalSize,
}

impl fmt::Display for SessionCommandError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Closed => formatter.write_str("Session is closed"),
            Self::InputTooLarge => formatter.write_str("input command is too large"),
            Self::InvalidTerminalSize => formatter.write_str("invalid terminal size"),
        }
    }
}

impl std::error::Error for SessionCommandError {}

/// The application-owned control and output side of one Mosh Session.
///
/// The matching [`SessionTask`] must be polled on the application's executor.
pub struct Session {
    commands: mpsc::Sender<SessionCommand>,
    output: mpsc::Receiver<Vec<u8>>,
    cancellation: watch::Sender<bool>,
    graceful_close: watch::Sender<bool>,
    close_requested: Mutex<bool>,
    state: watch::Receiver<SessionState>,
    reachability: watch::Receiver<SessionReachability>,
}

impl Session {
    /// Creates a local Session and its independently driven protocol task.
    ///
    /// Local prediction uses the standard [`PredictionMode::Adaptive`] policy.
    ///
    /// # Errors
    ///
    /// Returns [`SessionError::InvalidTerminalSize`] for an unsupported initial
    /// size, or [`SessionError::Io`] when the local UDP endpoint cannot be
    /// created.
    pub async fn connect(
        bootstrap: Bootstrap,
        columns: u32,
        rows: u32,
    ) -> Result<(Self, SessionTask), SessionError> {
        Self::connect_with_prediction_mode(bootstrap, columns, rows, PredictionMode::default())
            .await
    }

    /// Creates a local Session with an explicit local-prediction display policy.
    ///
    /// Prediction never changes authenticated terminal authority. [`PredictionMode::Always`]
    /// displays only eligible predictions from a confirmed epoch, while
    /// [`PredictionMode::Never`] displays only authenticated server state.
    ///
    /// # Errors
    ///
    /// Returns [`SessionError::InvalidTerminalSize`] for an unsupported initial
    /// size, or [`SessionError::Io`] when the local UDP endpoint cannot be
    /// created.
    pub async fn connect_with_prediction_mode(
        bootstrap: Bootstrap,
        columns: u32,
        rows: u32,
        prediction_mode: PredictionMode,
    ) -> Result<(Self, SessionTask), SessionError> {
        let (driver, channels) = SessionDriver::connect(bootstrap, columns, rows, prediction_mode)
            .await
            .map_err(SessionError::from)?;
        let SessionChannels {
            commands,
            output,
            cancellation,
            graceful_close,
            state,
            reachability,
        } = channels;
        Ok((
            Self {
                commands,
                output,
                cancellation,
                graceful_close,
                close_requested: Mutex::new(false),
                state,
                reachability,
            },
            SessionTask { driver },
        ))
    }

    /// Queues ordered terminal input. Empty input is accepted as a no-op.
    ///
    /// # Errors
    ///
    /// Returns [`SessionCommandError::InputTooLarge`] when `bytes` exceeds the
    /// documented per-command limit, or [`SessionCommandError::Closed`] after
    /// cancellation or task shutdown.
    pub async fn send_input(&self, bytes: Vec<u8>) -> Result<(), SessionCommandError> {
        if bytes.len() > MAX_INPUT_COMMAND_BYTES {
            return Err(SessionCommandError::InputTooLarge);
        }
        self.send_command(SessionCommand::Input(bytes)).await
    }

    /// Queues an ordered remote terminal resize.
    ///
    /// # Errors
    ///
    /// Returns [`SessionCommandError::InvalidTerminalSize`] for dimensions
    /// outside the documented limits, or [`SessionCommandError::Closed`] after
    /// cancellation or task shutdown.
    pub async fn resize(&self, columns: u32, rows: u32) -> Result<(), SessionCommandError> {
        if !is_valid_size(columns, rows) {
            return Err(SessionCommandError::InvalidTerminalSize);
        }
        self.send_command(SessionCommand::Resize { columns, rows })
            .await
    }

    /// Queues a self-contained full repaint after earlier accepted output.
    ///
    /// # Errors
    ///
    /// Returns [`SessionCommandError::Closed`] after cancellation or task
    /// shutdown.
    pub async fn request_repaint(&self) -> Result<(), SessionCommandError> {
        self.send_command(SessionCommand::Repaint).await
    }

    /// Requests an idempotent authenticated close exchange with the peer.
    ///
    /// Commands accepted before this call remain ordered before the close.
    /// Later input, resize, and repaint requests return
    /// [`SessionCommandError::Closed`]. Use [`Session::cancel`] when the local
    /// task must stop promptly without waiting for the peer.
    pub fn close(&self) {
        let mut requested = self
            .close_requested
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        if !*requested {
            *requested = true;
            self.graceful_close.send_replace(true);
        }
    }

    /// Requests prompt, idempotent cancellation outside the bounded command queue.
    pub fn cancel(&self) {
        self.cancellation.send_replace(true);
    }

    /// Returns the latest lifecycle state without waiting.
    #[must_use]
    pub fn state(&self) -> SessionState {
        if self.state.has_changed().is_err() {
            SessionState::Closed
        } else {
            *self.state.borrow()
        }
    }

    /// Waits for and returns the next lifecycle state.
    pub async fn state_changed(&mut self) -> SessionState {
        if self.state.changed().await.is_err() {
            SessionState::Closed
        } else {
            *self.state.borrow_and_update()
        }
    }

    /// Returns the latest reachability observation without waiting.
    #[must_use]
    pub fn reachability(&self) -> SessionReachability {
        *self.reachability.borrow()
    }

    /// Creates an independently owned latest-value reachability observer.
    #[must_use]
    pub fn subscribe_reachability(&self) -> SessionReachabilityWatch {
        SessionReachabilityWatch::new(self.reachability.clone())
    }

    /// Receives the next bounded, ordered VT output chunk.
    pub async fn next_output(&mut self) -> Option<Vec<u8>> {
        self.output.recv().await
    }

    async fn send_command(&self, command: SessionCommand) -> Result<(), SessionCommandError> {
        let permit = self
            .commands
            .reserve()
            .await
            .map_err(|_| SessionCommandError::Closed)?;
        let close_requested = self
            .close_requested
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        if *close_requested || *self.cancellation.borrow() || self.state() == SessionState::Closed {
            return Err(SessionCommandError::Closed);
        }
        permit.send(command);
        Ok(())
    }
}

/// The Session-owned socket, timers, protocol state, and terminal state.
#[must_use = "a SessionTask must be polled for the Session to make progress"]
pub struct SessionTask {
    driver: SessionDriver,
}

impl SessionTask {
    /// Runs the Session until cancellation, owner shutdown, or failure.
    ///
    /// # Errors
    ///
    /// Returns [`SessionError::ConnectionTimeout`] when no complete
    /// authenticated remote state arrives within the attachment deadline, or
    /// another stable category for I/O, protocol, resource, or state failure.
    pub async fn run(self) -> Result<SessionExit, SessionError> {
        let state = self.driver.state_tx.clone();
        let result = self.driver.run().await;
        state.send_replace(SessionState::Closed);
        result.map_err(SessionError::from)
    }
}

#[cfg(test)]
mod tests;
