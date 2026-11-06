#![cfg(target_os = "linux")]

use std::net::Ipv4Addr;
use std::path::Path;
use std::process::{Command, Output, Stdio};
use std::thread;
use std::time::Duration;

use mosh_client::Bootstrap;
use zeroize::Zeroize as _;

const STOCK_VERSION: &[u8] = b"mosh 1.4.0";

#[test]
#[ignore = "requires a locally installed stock mosh-server 1.4.0"]
fn stock_1_4_0_bootstrap_output_matches_the_parser() {
    let version = run(Command::new("mosh-server").arg("--version"));
    assert!(
        version.status.success() && output_contains(&version, STOCK_VERSION),
        "fixture requires stock mosh-server 1.4.0"
    );

    let mut command = Command::new("mosh-server");
    command.args([
        "new",
        "-i",
        "127.0.0.1",
        "-p",
        "60060:60069",
        "--",
        "/bin/true",
    ]);
    let mut output = run(&mut command);
    let detached_pid = find_detached_pid(&output)
        .unwrap_or_else(|| panic!("stock server did not report its detached process identifier"));
    let mut server = ServerGuard::new(detached_pid);

    assert!(output.status.success(), "stock server bootstrap failed");
    let bootstrap = Bootstrap::parse(Ipv4Addr::LOCALHOST, &output.stdout)
        .unwrap_or_else(|error| panic!("stock bootstrap output was rejected: {error}"));
    assert!((60060..=60069).contains(&bootstrap.server_addr().port()));
    drop(bootstrap);
    output.stdout.zeroize();
    output.stderr.zeroize();

    assert!(server.terminate(), "stock server fixture did not clean up");
}

fn run(command: &mut Command) -> Output {
    command
        .output()
        .unwrap_or_else(|error| panic!("failed to run local stock server fixture: {error}"))
}

fn output_contains(output: &Output, needle: &[u8]) -> bool {
    contains(&output.stdout, needle) || contains(&output.stderr, needle)
}

fn contains(haystack: &[u8], needle: &[u8]) -> bool {
    haystack
        .windows(needle.len())
        .any(|window| window == needle)
}

fn find_detached_pid(output: &Output) -> Option<u32> {
    find_pid(&output.stdout).or_else(|| find_pid(&output.stderr))
}

fn find_pid(bytes: &[u8]) -> Option<u32> {
    const MARKER: &[u8] = b"pid = ";
    let start = bytes
        .windows(MARKER.len())
        .position(|window| window == MARKER)?
        + MARKER.len();
    let digit_count = bytes[start..]
        .iter()
        .take_while(|byte| byte.is_ascii_digit())
        .count();
    let digits = &bytes[start..start + digit_count];
    if digits.is_empty() {
        return None;
    }

    let pid = digits.iter().try_fold(0_u32, |value, byte| {
        value.checked_mul(10)?.checked_add(u32::from(*byte - b'0'))
    })?;
    (pid > 1).then_some(pid)
}

struct ServerGuard {
    pid: u32,
    terminated: bool,
}

impl ServerGuard {
    const fn new(pid: u32) -> Self {
        Self {
            pid,
            terminated: false,
        }
    }

    fn terminate(&mut self) -> bool {
        if self.terminated || !process_exists(self.pid) {
            self.terminated = true;
            return true;
        }

        let status = Command::new("kill")
            .arg("-TERM")
            .arg(self.pid.to_string())
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .status();
        if status.is_err() {
            return false;
        }

        for _ in 0..50 {
            if !process_exists(self.pid) {
                self.terminated = true;
                return true;
            }
            thread::sleep(Duration::from_millis(20));
        }

        false
    }
}

impl Drop for ServerGuard {
    fn drop(&mut self) {
        let _ = self.terminate();
    }
}

fn process_exists(pid: u32) -> bool {
    Path::new(&format!("/proc/{pid}")).exists()
}
