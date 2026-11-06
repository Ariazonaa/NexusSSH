use std::path::Path;
use std::process::{Command, Stdio};
use std::thread;
use std::time::Duration;

pub(crate) const STOCK_1_4_0_KEY_TEXT: &str = "4NeCCgvZFe2RnPgrcU1PQw";
pub(crate) const STOCK_1_4_0_KEY_BYTES: [u8; 16] = [
    0xe0, 0xd7, 0x82, 0x0a, 0x0b, 0xd9, 0x15, 0xed, 0x91, 0x9c, 0xf8, 0x2b, 0x71, 0x4d, 0x4f, 0x43,
];

pub(crate) fn assert_stock_1_4_0(program: &str) {
    let version = Command::new(program)
        .arg("--version")
        .output()
        .unwrap_or_else(|error| panic!("failed to run local {program}: {error}"));
    assert!(
        version.status.success()
            && (version.stdout.windows(10).any(|part| part == b"mosh 1.4.0")
                || version.stderr.windows(10).any(|part| part == b"mosh 1.4.0")),
        "fixture requires stock {program} 1.4.0"
    );
}

pub(crate) fn find_detached_pid(output: &std::process::Output) -> Option<u32> {
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

pub(crate) struct DetachedProcessGuard {
    pid: u32,
    terminated: bool,
}

impl DetachedProcessGuard {
    pub(crate) const fn new(pid: u32) -> Self {
        Self {
            pid,
            terminated: false,
        }
    }

    pub(crate) fn signal_kill(&self) -> bool {
        self.has_exited()
            || Command::new("kill")
                .arg("-KILL")
                .arg(self.pid.to_string())
                .stdout(Stdio::null())
                .stderr(Stdio::null())
                .status()
                .is_ok_and(|status| status.success())
    }

    pub(crate) fn has_exited(&self) -> bool {
        !Path::new(&format!("/proc/{}", self.pid)).exists()
    }

    pub(crate) fn terminate(&mut self) -> bool {
        if self.terminated || self.has_exited() {
            self.terminated = true;
            return true;
        }
        if Command::new("kill")
            .arg("-TERM")
            .arg(self.pid.to_string())
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .status()
            .is_err()
        {
            return false;
        }
        if wait_for_exit(self.pid, 100) {
            self.terminated = true;
            return true;
        }
        let _ = Command::new("kill")
            .arg("-KILL")
            .arg(self.pid.to_string())
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .status();
        self.terminated = wait_for_exit(self.pid, 150);
        self.terminated
    }
}

impl Drop for DetachedProcessGuard {
    fn drop(&mut self) {
        let _ = self.terminate();
    }
}

fn wait_for_exit(pid: u32, attempts: usize) -> bool {
    for _ in 0..attempts {
        if !Path::new(&format!("/proc/{pid}")).exists() {
            return true;
        }
        thread::sleep(Duration::from_millis(20));
    }
    false
}
