use core::fmt;

/// An error found while parsing untrusted `mosh-server` bootstrap output.
///
/// Variants deliberately contain neither the input text nor the session key so
/// that formatting an error cannot disclose bootstrap secrets.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
#[non_exhaustive]
pub enum BootstrapError {
    /// The bootstrap output exceeded the byte limit.
    OutputTooLarge,
    /// The bootstrap output exceeded the line limit.
    TooManyLines,
    /// No connect record was present.
    MissingConnectRecord,
    /// More than one possible connect record was present.
    AmbiguousConnectRecord,
    /// The connect record did not use the required field layout.
    MalformedConnectRecord,
    /// The UDP port was not a decimal value in the range 1 through 65535.
    InvalidPort,
    /// The session key was not a canonical 22-byte unpadded standard Base64 value.
    InvalidKey,
}

impl fmt::Display for BootstrapError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        let message = match self {
            Self::OutputTooLarge => "bootstrap output exceeds the byte limit",
            Self::TooManyLines => "bootstrap output exceeds the line limit",
            Self::MissingConnectRecord => "bootstrap output has no connect record",
            Self::AmbiguousConnectRecord => "bootstrap output has multiple connect records",
            Self::MalformedConnectRecord => "bootstrap connect record is malformed",
            Self::InvalidPort => "bootstrap UDP port is invalid",
            Self::InvalidKey => "bootstrap session key is invalid",
        };

        formatter.write_str(message)
    }
}

impl std::error::Error for BootstrapError {}
