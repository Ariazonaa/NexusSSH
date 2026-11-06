#![no_main]

libfuzzer_sys::fuzz_target!(|data: &[u8]| nexus_core_fuzz::run("ftp_list", data));
