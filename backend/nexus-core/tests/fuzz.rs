//! Zufallstests der Eingänge von außen: Jeder Leser bekommt viele verbogene Fassungen echter Eingaben und darf
//! weder abstürzen noch sich festrechnen.
//!
//! Das ersetzt `cargo fuzz` nicht (das sieht, welchen Code eine Eingabe erreicht, und läuft nur unter Linux –
//! siehe `backend/fuzz`), läuft dafür bei jedem `cargo test` mit, auch unter Windows. Der Zufall ist fest: Derselbe
//! Lauf prüft immer dieselben Eingaben. `NEXUS_FUZZ_ROUNDS` setzt die Zahl der Runden je Eingang herauf.

use std::time::{Duration, Instant};

use nexus_core::fuzzing;

/// So lange darf ein Leser für eine Eingabe von ein paar Kilobyte höchstens brauchen.
const PATIENCE: Duration = Duration::from_secs(2);

/// xorshift64*: klein, schnell, und ohne weitere Abhängigkeit.
struct Random(u64);

impl Random {
    fn next(&mut self) -> u64 {
        self.0 ^= self.0 >> 12;
        self.0 ^= self.0 << 25;
        self.0 ^= self.0 >> 27;
        self.0.wrapping_mul(0x2545_F491_4F6C_DD1D)
    }

    fn below(&mut self, limit: usize) -> usize {
        if limit == 0 {
            0
        } else {
            (self.next() % limit as u64) as usize
        }
    }
}

/// Bytes, die Leser gern aus dem Tritt bringen: Grenzen von Zahlen, Trenner, Steuerzeichen, Telnet-Befehle.
const SPICE: [&[u8]; 28] = [
    b"\0",
    b"\n",
    b"\r\n",
    b" ",
    b"\t",
    b"\"",
    b"*",
    b"?",
    b"!",
    b"=",
    b"#",
    b"%h",
    b"/",
    b"..",
    b"-1",
    b"0",
    b"18446744073709551616",
    b"9223372036854775807",
    b"4294967296",
    b"99999999999999999999999999",
    b"\xff",
    b"\xff\xfa",
    b"\xff\xf0",
    b"\xff\xfd",
    b"\xc3",
    b"\xe2\x80\xae",
    b"@",
    b":",
];

/// Eine verbogene Fassung von `seed`: ein paar zufällige Eingriffe hintereinander.
fn mutate(random: &mut Random, seed: &[u8], others: &[&[u8]]) -> Vec<u8> {
    let mut data = seed.to_vec();
    for _ in 0..=random.below(6) {
        let at = random.below(data.len() + 1);
        match random.below(9) {
            0 if !data.is_empty() => {
                let at = at.min(data.len() - 1);
                data[at] ^= 1 << random.below(8);
            }
            1 if !data.is_empty() => {
                let at = at.min(data.len() - 1);
                data[at] = random.next() as u8;
            }
            2 => data.truncate(at),
            3 => {
                let spice = SPICE[random.below(SPICE.len())];
                data.splice(at..at, spice.iter().copied());
            }
            4 if !data.is_empty() => {
                // Ein Stück vervielfachen – so entstehen lange Zeilen und viele Wiederholungen.
                let from = random.below(data.len());
                let to = (from + 1 + random.below(24)).min(data.len());
                let piece = data[from..to].to_vec();
                let times = 1 + random.below(60);
                let repeated: Vec<u8> = piece.iter().copied().cycle().take(piece.len() * times).collect();
                data.splice(at..at, repeated);
            }
            5 if !data.is_empty() => {
                let from = random.below(data.len());
                let to = (from + 1 + random.below(16)).min(data.len());
                data.drain(from..to);
            }
            6 => {
                // Ein Stück einer anderen echten Eingabe hinein.
                let other = others[random.below(others.len())];
                if !other.is_empty() {
                    let from = random.below(other.len());
                    let to = (from + 1 + random.below(40)).min(other.len());
                    data.splice(at..at, other[from..to].iter().copied());
                }
            }
            7 => {
                let count = random.below(12);
                let noise: Vec<u8> = (0..count).map(|_| random.next() as u8).collect();
                data.splice(at..at, noise);
            }
            _ => {
                // Zwei Zeilen tauschen.
                let mut lines: Vec<&[u8]> = data.split(|&byte| byte == b'\n').collect();
                if lines.len() > 1 {
                    let (a, b) = (random.below(lines.len()), random.below(lines.len()));
                    lines.swap(a, b);
                    data = lines.join(&b'\n');
                }
            }
        }
    }

    // Die Leser bekommen in der App nie beliebig viel auf einmal; hier soll die Zeit je Eingabe vergleichbar sein.
    data.truncate(16 * 1024);
    data
}

fn rounds() -> usize {
    std::env::var("NEXUS_FUZZ_ROUNDS")
        .ok()
        .and_then(|text| text.parse().ok())
        .unwrap_or(4000)
}

/// Lässt einen Eingang mit seinen echten Eingaben und vielen verbogenen Fassungen davon laufen.
fn hammer(target: &str, seeds: &[&[u8]]) {
    assert!(fuzzing::TARGETS.contains(&target), "unbekannter Eingang {target}");
    let mut random = Random(0x9E37_79B9_7F4A_7C15 ^ target.len() as u64 ^ (u64::from(target.as_bytes()[0]) << 32));

    let check = |data: &[u8]| {
        let started = Instant::now();
        let outcome = std::panic::catch_unwind(|| fuzzing::run(target, data));
        let taken = started.elapsed();
        assert!(
            outcome.is_ok() && taken < PATIENCE,
            "{target}: {} nach {taken:?} bei der Eingabe {:?}",
            if outcome.is_ok() { "zu langsam" } else { "Panic" },
            String::from_utf8_lossy(data)
        );
    };

    check(b"");
    for seed in seeds {
        check(seed);
        // Jede Länge einmal: Ein Leser muss mit jedem abgeschnittenen Anfang zurechtkommen.
        for end in 0..seed.len().min(600) {
            check(&seed[..end]);
        }
    }

    for round in 0..rounds() {
        let seed = seeds[round % seeds.len()];
        check(&mutate(&mut random, seed, seeds));
    }
}

#[test]
fn the_ssh_config_reader_survives_anything() {
    hammer(
        "sshconfig",
        &[
            b"Host web db-*\n  HostName %h.example.org\n  User deploy\n  Port 2222\n  IdentityFile ~/.ssh/id_%r\n  ProxyJump bastion,ops@gate:2200\n\nHost bastion\n  HostName 192.0.2.1\n  ForwardAgent yes\n\nHost *\n  User fallback\n",
            b"Host=a \"b c\" !d\nMatch all\nUser \"x y\"\n# comment\nHost * !*.internal ?x\nPort=65536\nProxyJump ssh://u@[2001:db8::1]:22\nInclude conf.d/*\n",
            b"host *a*a*a*a*a*a*a*a*a*a*a*a*a*a*b\nhost aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\nproxyjump none\nhostname %%h%h\n",
        ],
    );
}

#[test]
fn the_telnet_negotiation_survives_anything() {
    hammer(
        "telnet",
        &[
            b"\x03\xff\xfd\x18\xff\xfd\x1f\xff\xfb\x01\xff\xfb\x03login: \xff\xfa\x18\x01\xff\xf0\xff\xfd\x00\xff\xfb\x00",
            b"\x01text \xff\xff more\r\n\xff\xf9\xff\xfe\x01\xff\xfc\x1f\xff\xfa\x1f\x00\x50\x00\x18\xff\xf0\r\0",
            b"\x05\xff\xfa\xff\xff\xff\xff\xff\xf0\xff\xfa\x18\x01\xff\xfd\x22\xff\xfb\x05\xff",
        ],
    );
}

#[test]
fn the_reader_of_the_server_load_survives_anything() {
    hammer(
        "monitor",
        &[
            b"@os\nPRETTY_NAME=\"Ubuntu 24.04.1 LTS\"\nID=ubuntu\n@kernel\nLinux 6.8.0-45-generic\n@info\ncpu=Intel(R) Xeon(R)\ncores=4\nvendor=QEMU\nproduct=Standard PC\nvirt=kvm\n@sample\ncpu  4705 150 1120 16250 520 0 30 12 0 0\ncpu0 1200 40 280 4060 130 0 8 3 0 0\ncpu1 1170 37 280 4063 130 0 7 3 0 0\nMemTotal:        8000000 kB\nMemAvailable:    5000000 kB\n@uptime\n123456.78 654321.00\n@disk\nFilesystem 1024-blocks Used Available Capacity Mounted on\n/dev/sda1 100000000 28000000 72000000 28% /\n@disks\n/dev/sda1 100000000 28000000 72000000 28% /\n/dev/sdb1 500000000 470000000 30000000 94% /srv/data\ntmpfs 800000 0 800000 0% /run\n@inodes\n/dev/sda1 6553600 262144 6291456 4% /\n@load\n0.42 0.36 0.30 1/234 5678\n@net\neth0: 123456 100 0 0 0 0 0 0 654321 90 0 0 0 0 0 0\n@time\n1791628800 +0200 CEST\n@end\n",
            b"@os\nFreeBSD\n@sample\nkern.cp_time: 100 0 50 5 900\nkern.cp_times: 50 0 25 2 450 50 0 25 3 450\nhw.physmem: 8000000000\n@disks\n/dev/ada0p2 20000000 5000000 15000000 25% 120000 800000 13% /\n@load\n{ 0.10 0.20 0.30 }\n@time\n1791628800 +0000 UTC\n@end\n@sample\nunavailable\n@end\n",
            // Gefunden 2026-10-10: Zähler, deren Summe nicht mehr in 64 Bit passt.cpu0 1 1 1 1\n@disks\na 0 0 0 0% /\nb 1 2 -3 400% /mnt x y\n@time\n-1 +9999 X\n@end\n",
        ],
    );
}

#[test]
fn the_reader_of_ls_survives_anything() {
    hammer(
        "ls",
        &[
            b"total 12\ndrwxr-xr-x 2 1000 1000 4096 Oct  9 12:30 www\n-rw-r--r-- 1 0 0 5 Jan  1  2024 readme.txt\nlrwxrwxrwx 1 0 0 7 Oct  9 12:30 link -> target\nprw------- 1 0 0 0 Feb 29 23:59 pipe.fifo\ncrw-rw-rw- 1 0 0 1, 3 Oct  9 12:30 null\n",
            // Gefunden 2026-10-10: eine Uhrzeit, mit der die Rechnung überlief. 4294967296 -1 18446744073709551616 Dec 32 25:61 a b  c\nls: cannot access 'x': No such file or directory\n?--------- ? ? ? ? ? broken\n",
        ],
    );
}

#[test]
fn the_reader_of_ftp_lists_survives_anything() {
    hammer(
        "ftp_list",
        &[
            b"type=dir;modify=20261009123000;perm=el;unix.mode=0755; www\ntype=file;size=5;modify=20240101000000.123;unix.owner=0;unix.group=0; readme.txt\ntype=cdir; .\ntype=pdir; ..\ntype=OS.unix=slink:/target; link\n",
            b"drwxr-xr-x   2 ftp      ftp          4096 Oct 09 12:30 www\n-rw-r--r--   1 ftp      ftp             5 Jan 01  2024 readme.txt\n10-09-26  12:30PM       <DIR>          Documents\n10-09-26  12:30PM                 1234 file name.txt\nlrwxrwxrwx   1 ftp ftp 7 Oct 09 12:30 link -> target\n",
            // Gefunden 2026-10-10: Daran stürzte der Leser der FTP-Bibliothek ab (Zeichen aus mehreren Bytes).modify=99999999999999; x\ntype=;=;;; \n13-45-99  25:99XM  -5 y\n",
        ],
    );
}

#[test]
fn the_readers_of_command_output_survive_anything() {
    hammer(
        "exec",
        &[
            b"  PID USER     %CPU %MEM   RSS COMMAND\n    1 root      0.0  0.1 12345 /sbin/init splash\n 4242 www-data 12.5  3.4 99999 nginx: worker process\n",
            b"12\t/srv/data/www\n4096\t/srv/data/logs dir\n8192\t/srv/data\n",
            b"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855  /srv/a\nSHA256 (/srv/b) = e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855\n-\n18446744073709551615 total\n",
        ],
    );
}

#[test]
fn the_x11_handshake_survives_anything() {
    hammer(
        "x11",
        &[
            b"l\0\x0b\0\0\0\x12\0\x10\0\0\0MIT-MAGIC-COOKIE-1\0\x000123456789abcdef",
            b"B\0\0\x0b\0\0\0\x12\0\x10\0\0MIT-MAGIC-COOKIE-1\0\x000123456789abcdef",
            b"l\0\x0b\0\0\0\xff\xff\xff\xff\0\0",
        ],
    );
}

#[test]
fn the_socks_proxy_survives_anything() {
    hammer(
        "socks",
        &[
            b"\x05\x02\x00\x02",
            b"\x05\x01\x00\x03\x0bexample.org\x01\xbb",
            b"\x05\x01\x00\x04\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\x01\x00\x16",
            b"\x04\x01\x00\x50\xc0\x00\x02\x01user\0",
            b"\x04\x01\x01\xbb\0\0\0\x01u\0example.org\0",
            b"\x05\x01\x00\x03\xffshort",
        ],
    );
}

#[test]
fn the_key_file_reader_survives_anything() {
    let samples = fuzzing::key_file_samples();
    let seeds: Vec<&[u8]> = samples.iter().map(Vec::as_slice).collect();
    hammer("keyfile", &seeds);
}

#[test]
fn the_reader_of_what_mosh_server_says_survives_anything() {
    hammer(
        "mosh_bootstrap",
        &[
            b"Welcome!\n@nexus-mosh\n\r\nMOSH CONNECT 60001 4NeCCgvZFe2RnPgrcU1PQw\r\n",
            b"@nexus-mosh\nMOSH CONNECT 65535 /I5GzbjCBbcV57yFlKEjsQ\nmosh-server (mosh 1.4.0)\n@nexus-no-mosh\n",
            b"MOSH CONNECT 0 4NeCCgvZFe2RnPgrcU1PQw\nMOSH CONNECT 60002 4NeCCgvZFe2RnPgrcU1PQ=\n@nexus-mosh\r\r\n",
        ],
    );
}

#[test]
fn the_stream_under_an_sftp_session_survives_anything() {
    // Erstes Byte: Größe der Stücke. Dann Pakete: Version, Status mit unbekanntem Code, Namen, Daten.
    hammer(
        "sftp_stream",
        &[
            b"\x03\0\0\0\x05\x02\0\0\0\x03\0\0\0\x15\x65\0\0\0\x03\xff\xff\xff\xfe\0\0\0\x06denied\0\0\0\0",
            b"\x07\0\0\0\x15\x68\0\0\0\x08\0\0\0\x02\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0",
            b"\x01\0\0\0\x0d\x67\0\0\0\x01\0\0\0\x04\x65\0\0\0\0\0\0\0\0",
            b"\x02\0\0\0\0\0\0\0\x09\x65\0\0\0\x01\0\0\x01",
        ],
    );
}
