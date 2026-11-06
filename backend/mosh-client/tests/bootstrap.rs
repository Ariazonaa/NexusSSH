use std::net::{Ipv4Addr, Ipv6Addr, SocketAddr, SocketAddrV4};

use mosh_client::{Bootstrap, BootstrapError};

const TEST_KEY: &str = "4NeCCgvZFe2RnPgrcU1PQw";

fn parse(output: &[u8]) -> Result<Bootstrap, BootstrapError> {
    Bootstrap::parse(Ipv4Addr::new(192, 0, 2, 10), output)
}

#[test]
fn parses_one_record_amid_surrounding_output() {
    let output = b"banner\nMOSH CONNECT 60054 4NeCCgvZFe2RnPgrcU1PQw\nversion output\n";

    let bootstrap = parse(output).unwrap();

    assert_eq!(
        bootstrap.server_addr(),
        SocketAddr::V4(SocketAddrV4::new(Ipv4Addr::new(192, 0, 2, 10), 60054))
    );
}

// NexusSSH (P5): Der Server darf auch eine IPv6-Adresse haben.
#[test]
fn binds_the_record_to_an_ipv6_address() {
    let server = Ipv6Addr::new(0x2001, 0xdb8, 0, 0, 0, 0, 0, 10);
    let bootstrap =
        Bootstrap::parse(server, b"MOSH CONNECT 60054 4NeCCgvZFe2RnPgrcU1PQw\n").unwrap();

    assert_eq!(bootstrap.server_addr(), SocketAddr::from((server, 60054)));
    assert!(format!("{bootstrap:?}").contains("[2001:db8::a]:60054"));
}

#[test]
fn accepts_crlf_and_a_record_without_a_final_newline() {
    assert!(parse(b"notice\r\nMOSH CONNECT 1 4NeCCgvZFe2RnPgrcU1PQw\r\n").is_ok());
    assert!(parse(b"MOSH CONNECT 65535 4NeCCgvZFe2RnPgrcU1PQw").is_ok());
}

#[test]
fn ignores_non_utf8_and_controls_outside_the_record() {
    let output = b"\xff\0untrusted banner\nMOSH CONNECT 60054 4NeCCgvZFe2RnPgrcU1PQw\n\x1b[0m";

    assert!(parse(output).is_ok());
}

#[test]
fn rejects_missing_and_ambiguous_records() {
    assert_eq!(
        parse(b"ordinary output").unwrap_err(),
        BootstrapError::MissingConnectRecord
    );
    assert_eq!(
        parse(
            b"MOSH CONNECT 60054 4NeCCgvZFe2RnPgrcU1PQw\nMOSH CONNECT 60055 4NeCCgvZFe2RnPgrcU1PQw"
        )
        .unwrap_err(),
        BootstrapError::AmbiguousConnectRecord
    );
    assert_eq!(
        parse(b"MOSH CONNECT bad record\nMOSH CONNECT 60054 4NeCCgvZFe2RnPgrcU1PQw").unwrap_err(),
        BootstrapError::AmbiguousConnectRecord
    );
}

#[test]
fn rejects_noncanonical_field_whitespace_and_extra_fields() {
    for output in [
        b" MOSH CONNECT 60054 4NeCCgvZFe2RnPgrcU1PQw".as_slice(),
        b"MOSH  CONNECT 60054 4NeCCgvZFe2RnPgrcU1PQw".as_slice(),
        b"MOSH\tCONNECT 60054 4NeCCgvZFe2RnPgrcU1PQw".as_slice(),
        b"MOSH CONNECT\t60054 4NeCCgvZFe2RnPgrcU1PQw".as_slice(),
        b"MOSH CONNECT 60054 4NeCCgvZFe2RnPgrcU1PQw ".as_slice(),
        b"MOSH CONNECT 60054 4NeCCgvZFe2RnPgrcU1PQw extra".as_slice(),
        b"MOSH CONNECT 60054 4NeCCgvZFe2RnPgrcU1PQw\r".as_slice(),
    ] {
        assert_eq!(
            parse(output).unwrap_err(),
            BootstrapError::MalformedConnectRecord
        );
    }
}

#[test]
fn rejects_invalid_ports() {
    for port in ["", "0", "65536", "999999", "+1", "-1", "6O054"] {
        let output = format!("MOSH CONNECT {port} {TEST_KEY}");
        let expected = if port.is_empty() {
            BootstrapError::MalformedConnectRecord
        } else {
            BootstrapError::InvalidPort
        };
        assert_eq!(parse(output.as_bytes()).unwrap_err(), expected);
    }
}

#[test]
fn rejects_invalid_keys() {
    for key in [
        "",
        "short",
        "4NeCCgvZFe2RnPgrcU1PQw==",
        "4NeCCgvZFe2RnPgrcU1PQ_",
        "4NeCCgvZFe2RnPgrcU1PQ!",
        "4NeCCgvZFe2RnPgrcU1PQx",
    ] {
        let output = format!("MOSH CONNECT 60054 {key}");
        let expected = if key.is_empty() {
            BootstrapError::MalformedConnectRecord
        } else {
            BootstrapError::InvalidKey
        };
        assert_eq!(parse(output.as_bytes()).unwrap_err(), expected);
    }
}

#[test]
fn enforces_byte_and_line_limits_before_parsing() {
    let mut at_byte_limit = vec![b'x'; 4096];
    let record = b"MOSH CONNECT 60054 4NeCCgvZFe2RnPgrcU1PQw";
    at_byte_limit[..record.len()].copy_from_slice(record);
    at_byte_limit[record.len()] = b'\n';
    assert!(parse(&at_byte_limit).is_ok());

    let oversized = vec![b'x'; 4097];
    assert_eq!(
        parse(&oversized).unwrap_err(),
        BootstrapError::OutputTooLarge
    );

    let mut at_line_limit = b"line\n".repeat(63);
    at_line_limit.extend_from_slice(b"MOSH CONNECT 60054 4NeCCgvZFe2RnPgrcU1PQw\n");
    assert!(parse(&at_line_limit).is_ok());

    let too_many_lines = b"line\n".repeat(65);
    assert_eq!(
        parse(&too_many_lines).unwrap_err(),
        BootstrapError::TooManyLines
    );
}

#[test]
fn debug_and_error_formats_do_not_disclose_keys() {
    let bootstrap = parse(format!("MOSH CONNECT 60054 {TEST_KEY}").as_bytes()).unwrap();
    let debug = format!("{bootstrap:?}");
    assert!(!debug.contains(TEST_KEY));
    assert!(debug.contains("[REDACTED]"));

    let invalid_key = "this-is-not-a-secret!!";
    let error = parse(format!("MOSH CONNECT 60054 {invalid_key}").as_bytes()).unwrap_err();
    let formatted = format!("{error:?} {error}");
    assert!(!formatted.contains(invalid_key));
}
