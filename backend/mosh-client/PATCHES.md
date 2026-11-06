# Eingecheckt: mosh-client-rs 0.1.4

Dieser Ordner ist eine eingecheckte Kopie von [`wandcs/mosh-client-rs`](https://github.com/wandcs/mosh-client-rs)
(Paket `mosh-client`, MIT OR Apache-2.0, Urheber laut `LICENSE-MIT`: „di“ / `wandcs`). Die Lizenzdateien
(`LICENSE-MIT`, `LICENSE-APACHE`) und die des eingebetteten `vt100` (`src/vt100/LICENSE`, Jesse Luehrs) bleiben
unverändert. Die Bibliothek ist nicht auf crates.io veröffentlicht; gepflegt wird der Stand hier.

## Quelle

| | |
| --- | --- |
| Tag | `v0.1.4`, Tag-Objekt `e64157ad21e6d86a38cfbaceee1043c3c83d2f0d` → Commit `8a45c8e68aa586afa5bebc241fad6d8d80259536` |
| Archiv | `https://github.com/wandcs/mosh-client-rs/archive/refs/tags/v0.1.4.tar.gz`, 261 426 Bytes |
| SHA-256 | `a8af30dad7980b657a6baa39396e1cd10db3e10bee8a80b3f79d9cb43a5ac26a` |

Übernommen wurde der ganze Inhalt des Archivs außer `Cargo.lock` und `fuzz/Cargo.lock` (es gilt die
`Cargo.lock` des Arbeitsbereichs) und `AGENTS.md` (Arbeitsanweisungen für den Arbeitsablauf des Originalprojekts,
die hier nicht gelten).

## Änderungen

Jede Änderung hat einen eigenen Commit und mindestens einen Test der Bibliothek.

### P6 – Versionen des Arbeitsbereichs

`Cargo.toml`: Das Original pinnt `aes =0.9.1`, `itoa =1.0.18`, `miniz_oxide =0.9.1`, `prost =0.14.4`,
`unicode-width =0.2.2`, `vte =0.15.0` und `proptest =1.11.0` – neben `nexus-core` (über russh `aes 0.9.3`) ließ
sich das nicht auflösen. Die Versionen stehen jetzt in `[workspace.dependencies]` von `backend/Cargo.toml` (ohne
`=`, dieselben Mindestfassungen), die Features wie im Original. `publish = false` statt `["crates-io"]`.
`zeroize` kommt mit dem Feature `alloc` des Arbeitsbereichs (über russh ohnehin an).
`prost` darf das Feature `no-recursion-limit` nicht bekommen (sonst fällt die Grenze von 100 Ebenen).
Test: Die Tests der Bibliothek laufen mit den aufgelösten Fassungen (`aes 0.9.3`) unverändert durch.

### P4 – Tests unter Windows

`src/lib.rs`: `test_support` gibt es in jedem Test-Build (`#[cfg(test)]` statt `#[cfg(all(test, target_os =
"linux"))]`) – die Unit-Tests holen daraus auch unter Windows `STOCK_1_4_0_KEY_BYTES`, und im Original brach
`cargo test` dort mit E0432 ab. Die Hilfen für die Stock-Tests bleiben unter Windows ungenutzt (`allow(dead_code)`
nur dort); `Bootstrap::with_test_server_addr` gibt es nur noch, wo die Stock-Tests laufen.
Test: `cargo test -p mosh-client --lib` unter Windows (`x86_64-pc-windows-msvc`): 129 bestanden, ohne Warnung.

### P3 – Empfangsfehler unter Windows

`src/session/driver.rs` (`classify_receive`, neu `is_discarded_datagram_error`): Unter Windows liefert
`recv_from` nach einem ICMP „Port unreachable“ `ConnectionReset` (WSAECONNRESET) und bei einem Datagramm über
dem Puffer von 2 048 Bytes WSAEMSGSIZE (10040). Im Original beendete beides die Sitzung (die Liste behebbarer
Fehler kennt beide nicht und gilt erst im Zustand `Active`) – ein Server, der kurz nicht lauscht, eine Firewall
mit REJECT oder irgendwer mit einem großen Datagramm an den Port genügte. Jetzt verwirft der Treiber in jedem
Zustand nur dieses eine Datagramm und empfängt weiter.
Test: `session::tests::unit::reset_and_oversized_receive_errors_drop_only_that_datagram` (zwei `ConnectionReset`
noch vor der ersten Antwort, danach antwortet der Server, die Sitzung ist `Responsive`; 10040 zählt nur unter
Windows). Ohne die Änderung scheitert er. Dazu läuft unter Windows der vorhandene Integrationstest
`public_graceful_close_has_a_bounded_wait_without_a_peer` durch (im Original `Err(Io(ConnectionReset))`).

### P1 – Ein verlorenes Fragment beendet die Sitzung nicht mehr

`src/fragment.rs` (`FragmentReassembler::push`): Im Original lehnte der Reassembler eine neue Kennung ab,
solange eine unvollständige Anweisung lag (`TooManyIncompleteInstructions`), und der Treiber beendete die Sitzung
mit `ResourceLimit`. Nach einem verlorenen Fragment schickt `mosh-server` den nächsten Stand aber unter neuer
Kennung, die alte Anweisung wird nie mehr vollständig – auf einer Leitung mit Verlust endete jede Sitzung beim
ersten verlorenen Fragment. Jetzt verdrängt eine neue Kennung die unvollständige Anweisung, wie in `mosh-server`
selbst (`FragmentAssembly::add_fragment`). Es bleibt bei höchstens einer unvollständigen Anweisung und 1 MiB;
die Fehlerart `TooManyIncompleteInstructions` fällt weg (`driver.rs`), `docs/limits.md` nennt die neue Regel.
Tests: `fragment::tests::a_new_identifier_displaces_the_incomplete_instruction_and_count_limits_fail_before_mutation`
und `one_complete_fragment_displaces_the_incomplete_message` (die beiden Tests der alten Regel, umgestellt),
`a_lost_fragment_does_not_block_the_next_instruction` (neu) und der Stock-Test
`session::tests::stock::recovery::stock_1_4_0_private_session_survives_lost_fragments` (120 × 40, das Relais
verwirft jedes fünfte Datagramm des Servers ab 400 Bytes, während 300 × 900 Zufallsbytes als Hex ausgegeben
werden; `UdpRelay::drop_every_nth_large_server_datagram`). Ohne die Änderung endet er mit
`Reassembly(TooManyIncompleteInstructions)`.

### P2 – Ausgaben eines Programms beenden die Sitzung nicht mehr

`src/terminal/state.rs`, `src/terminal/mod.rs`, `src/limits.rs`: Die Prüfung des Bildschirms war „fail closed“ –
was das eingebettete vt100 nicht kannte oder nicht fassen konnte, beendete die Sitzung (`SessionError::Protocol`).
`mosh-server` reicht einiges davon weiter; wer dem Benutzer Text auf den Bildschirm brachte (Dateiname, Logzeile),
konnte ihm so die Sitzung beenden. Jetzt:

- Unbekannte CSI (etwa `ESC [ ? 1001 h`, `ESC [ ? 1015 h` – vim mit `ttymouse=urxvt`) und OSC (etwa OSC 52 mit
  Inhalt, der kein Base64 ist) werden übergangen wie in jedem Terminal; den Inhalt der Zellen hat `mosh-server`
  schon selbst ausgewertet. Die Fehlerarten `UnsupportedCsi` und `UnsupportedOsc` fallen weg.
- Mehr Kombinationszeichen, als eine Zelle fasst (`mosh-server` nimmt bis zu 32 Bytes, vt100 22), lässt vt100
  fallen (`vt100/cell.rs`, `Cell::append`) – die Zelle zeigt dann weniger. Die Grenzen „8 Zeichen“
  (`MAX_UNICODE_SCALARS_PER_CELL`) und „18 Bytes vor dem Anhängen“ (`CellEncodingTooLarge`) fallen weg; die
  Prüfung nach dem Anwenden misst die Zelle in Bytes (≤ 22).

Unverändert ein Fehler bleiben ungültiges UTF-8, unvollständige Sequenzen, unbekannte Steuerzeichen und ESC-Folgen
und ein Kombinationszeichen ohne Grundzeichen – das alles schickt `mosh-server` nicht. `docs/limits.md` und
`docs/compatibility.md` nennen die neue Regel.
Tests: sieben Tests in `terminal::state::tests`, die die alte Regel festschrieben, sind umgestellt
(`preserves_bounded_combining_characters`, `accepts_semicolons_in_stock_window_titles_without_a_system_effect`,
`excluded_modes_clipboard_payload_and_full_cells_leave_the_session_running` – früher
`…_still_fail`, `server_spacing_character_keeps_the_per_cell_scalar_limit`,
`unicode_fallback_abort_regression_is_bounded_and_failure_atomic`,
`rejects_invalid_incomplete_unsupported_and_unrepresentable_input`,
`external_policy_modes_are_passed_over_in_both_directions` – früher `accepts_only_verified_external_policy_resets`).
Im Stock-Test `stock_1_4_0_public_session_batch_terminal_compatibility` überleben jetzt alle Fälle, auch die fünf,
die im Original die Sitzung beenden sollten, dazu die Ausgaben aus der Prüfung (`a` + 10 und 20 × U+0301,
`a` + 7 × U+20D0, OSC 52 mit `!!kein-Base64!!`). Ohne die Änderung scheitert er.

### P5 – IPv6

`src/bootstrap.rs`, `src/packet.rs`, `src/session/driver.rs`: Das Original kannte nur IPv4 (`Ipv4Addr`,
`SocketAddrV4` bis zum Socket, `bind 0.0.0.0:0`). Jetzt nimmt `Bootstrap::parse` jede `impl Into<IpAddr>`
(bisherige Aufrufe mit `Ipv4Addr` bleiben gültig), `Bootstrap::server_addr` gibt eine `SocketAddr` zurück, und der
Socket gehört zur Familie des Servers (`local_bind_addr`: `0.0.0.0:0` bzw. `[::]:0`, weiter auf allen Adressen,
damit ein Wechsel der eigenen Adresse gelingt). Die Quelle jedes Datagramms vergleicht `is_same_endpoint`: bei IPv6
Adresse und Port, den Scope nur, wenn der erwartete einen nennt – Flow-Label und Scope, die `recv_from` mitliefert,
dürfen nicht stören. In den Stock-Tests nimmt das Relais eine `SocketAddr` und `start_stock_server_on` die Adresse,
an die `mosh-server -i` bindet.
Tests: `tests/bootstrap.rs` `binds_the_record_to_an_ipv6_address`,
`packet::tests::unit::client_receiver_keeps_an_ipv6_server_endpoint_fixed`,
`session::tests::unit::a_session_runs_against_an_ipv6_server` (Server auf `[::1]`, ein Datagramm von einem anderen
Port wird verworfen; ohne IPv6-Loopback übersprungen) und der Stock-Test
`session::tests::stock::stock_1_4_0_public_session_runs_over_ipv6` (`mosh-server -i ::1`).

### P8 – Feststellung zur Größe der Datagramme

`src/crypto.rs`, `src/packet.rs`: `ocb3` 0.2.0 hat einen Off-by-one – mit `L_TABLE_SIZE = 7` verschlüsselt es nur
Nachrichten unter 2 048 Bytes, entschlüsselt aber genau 2 048 noch und greift dann auf `ll[7]` zu (Panik).
Erreichbar ist das nicht: Der Klartext eines Datagramms ist höchstens 2 048 − 24 = 2 024 Bytes. Damit das so bleibt,
stellt `packet.rs` zur Übersetzungszeit fest, dass `MAX_PLAINTEXT_BYTES < OCB3_MESSAGE_LIMIT_BYTES`
(`1 << (L_TABLE_SIZE + 4)`); wer `MAX_DATAGRAM_BYTES` anhebt, bekommt einen Fehler beim Bauen (nachgeprüft mit
2 048 + 24: E0080).
Test: `crypto::tests::the_largest_datagram_body_stays_below_the_ocb3_limit` (2 024 und 2 047 Bytes hin und zurück,
2 048 verweigert schon das Verschlüsseln).

### P9 – Glocke und Fenstertitel in der VT-Ausgabe

`src/terminal/state.rs`, `src/terminal/paint.rs`, `src/session/driver.rs`: Im Original kamen Glocke und
Fenstertitel nie beim Terminal an (die Rückrufe von vt100 taten nichts, der Painter gibt nur den Bildschirm aus) –
in Mosh-Tabs fehlten die Marke der Glocke und der Titel am Reiter. Jetzt merkt sich `TerminalState`, wie oft die
Glocke klang (`bell_count`, je `BEL` in den Host-Bytes) und den letzten Fenstertitel (`title`, aus OSC 0 und 2;
OSC 1 nennt nur das Symbol). Der Titel ist Text des Servers: Steuerzeichen fallen weg (C0, DEL, C1 – U+009C
beendete sonst die OSC-Folge), ungültiges UTF-8 wird U+FFFD, höchstens 256 Zeichen (`clean_title`). Zeichen, die
die Schreibrichtung umkehren, bleiben – die säubert die App wie jeden Titel. Der Treiber hängt beides hinter das
Bild (`TerminalPainter::with_effects`, mit der Grenze von 2 MiB): den Titel als `ESC ] 0 ; <titel> BEL` bei jedem
ganzen Bild und wenn er sich ändert, die Glocke als ein `BEL`, sobald der Zähler über den zuletzt ausgegebenen
steigt. Beides kommt aus dem Zustand des Servers, nicht aus der Vorhersage – sonst klänge die Glocke zweimal, wenn
die Anzeige zwischen Vorhersage und Server wechselt. Das Bild selbst (`TerminalPainter::full`/`incremental`) bleibt
ohne beides. `docs/compatibility.md` nennt die neue Regel.
Tests: `terminal::state::tests::bell_count_and_title_follow_the_host_bytes` (Zählen, OSC 0/1/2, Säubern, Kürzen,
ein Fehler ändert nichts), `drops_system_effects_from_authoritative_state` (erweitert),
`terminal::paint::tests::effects_follow_the_paint_as_osc_0_and_bel`,
`session::tests::unit::bell_and_title_reach_the_vt_output` (Treiber gegen einen erfundenen Server: Titel mit
U+009C, Glocke, nur Glocke, nichts, ganzes Bild) und der Stock-Test
`session::tests::stock::stock_1_4_0_public_session_passes_bell_and_title_on`.

### P10 – Unbestätigte Vorhersage unterstrichen

`src/prediction.rs`, `src/terminal/state.rs`: Im Original sah ein vorhergesagtes Zeichen aus wie eines, das der
Server geschickt hat; lag die Vorhersage falsch, stand für einen Augenblick Text da, als käme er vom Server. Das
Mosh des Originals (C++) unterstreicht unbestätigte Zeichen bei langsamer Leitung und im Funkloch (`flagging`) –
so jetzt auch hier. `TerminalState::with_flagged_ascii` schreibt das Zeichen mit `SGR 4` und stellt danach den
Stil von vorher wieder her (`attributes_formatted` von vt100: zurücksetzen, dann Farbe, fett usw.).
`LocalPrediction` hält die Vorhersage zweimal: `projected` ohne Unterstrich zum Vergleich mit dem Bildschirm des
Servers (sonst passte keine Vorhersage je), `flagged` mit Unterstrich zum Zeigen. Unterstrichen wird, wenn die
Leitung langsam ist (Abstand der Bilder über 30 ms) oder der Server seit 250 ms nicht bestätigt hat – dieselben
Schwellen wie für `Adaptive`, die jetzt auch in `Always` gemessen werden (`slow_link`, `glitch`, vorher
`adaptive_…`); `next_deadline_ms` weckt dafür auch dort nach 250 ms. In `Adaptive` ist eine sichtbare Vorhersage
damit immer unterstrichen, in `Always` auf schneller Leitung nicht (sonst flackerte bei jedem Tastendruck ein
Strich). `update_policy` meldet eine Änderung auch, wenn nur der Unterstrich wechselt.
Tests: `terminal::state::tests::a_flagged_prediction_is_underlined_and_the_style_after_it_stays`,
`prediction::tests::always_underlines_only_on_a_slow_link_or_during_a_glitch`,
`underlined_predictions_confirm_against_the_plain_server_screen`; angepasst die beiden Tests zu `Adaptive`
(erwarten den Unterstrich) und `prediction_age_and_capacity_are_bounded` (in `Always` weckt zuerst das Funkloch).

### P7 – Werkzeuge: Format, Clippy, Zufallstests, Prüfung der Abhängigkeiten

- **Format:** `rustfmt.toml` mit `max_width = 100` im Ordner der Bibliothek (die Vorgabe von rustfmt, nach der das
  Original formatiert ist; der Arbeitsbereich nimmt 120). Damit meldet `cargo fmt --all --check` am Original
  nichts; formatiert hat `cargo fmt` nur die eigenen Änderungen. Die Dateien von vt100 tragen weiter
  `#[rustfmt::skip]`.
- **Clippy** (`--workspace --all-targets -- -D warnings`, Windows 1.99 und Linux 1.98): Im Original scheiterten
  nur Tests: `assert!(….is_empty())` → `assert_eq!(…, [])` (`tests/network_fixture.rs`, `src/timing/tests.rs`),
  `[b'0', b'1', b'2']` → `*b"012"` (`terminal/state.rs`), `chunks_exact(2)` → `as_chunks::<2>()` (Stock-Test).
  Mit Clippy 1.99.1 (`assert_is_empty`, 2026-10-24) dazu im Stock-Test `assert!(!….is_empty())` → `assert_ne!(…, [])`.
  Mit Glocke und Titel (P9) wurde die Zukunft von `SessionDriver::run` größer als 16 KiB (`large_futures`) –
  `run` gibt sie jetzt in einer `Box` zurück (`run_unboxed` ist das Original). Gezielte Ausnahme nur
  `naive_bytecount` in zwei eigenen Tests (die Ausgabe ist klein; das Original hält es in `bootstrap.rs` ebenso).
- **Zufallstests:** Die zwei Ziele für `cargo fuzz` liegen jetzt in `backend/fuzz` (`mosh_untrusted_parsers`,
  `mosh_state_transitions`; der eigene Arbeitsbereich `fuzz/` des Originals samt `deny.toml` fällt weg). Die
  Eingänge in `src/fuzzing.rs` gibt es auch in Test-Builds (`cfg(any(fuzzing, test))`, ebenso
  `SynchronizationState::fuzz_retained_counts`); `fuzzing::tests` treibt sie bei jedem `cargo test` mit festem
  Zufall (xorshift, 5 000 Runden, `NEXUS_FUZZ_ROUNDS` für mehr) – mit verbogenen echten Eingaben, auch den
  Ausgaben, die vor P2 die Sitzung beendeten. `cargo fuzz` je Ziel 120 s unter Linux (nightly): 1,35 Mio. bzw.
  2,48 Mio. Läufe, kein Fund.
- **Abhängigkeiten:** Neu in `Cargo.lock` sind `ocb3`, `dbl`, `prost`, `prost-derive` (mit `itertools`, `either`,
  `anyhow`), `vte` (mit `arrayvec`), `unicode-width`, nur für Tests `proptest`, `rand_xorshift`, `unarray`,
  `core_detect` – alle MIT und/oder Apache-2.0. Die Abfrage bei OSV (dieselbe Quelle wie osv-scanner in
  `security.yml`) nennt für keine der Fassungen eine Meldung (2026-10-24). `cargo deny` benutzt das Projekt nicht.

### P11 – Mindestfassung von Rust 1.89

`Cargo.toml`: `rust-version` von 1.88 auf 1.89 (2026-11-06). Cargo wählt Abhängigkeiten nach der kleinsten
Mindestfassung im Arbeitsbereich, und die stand allein hier: Mit 1.88 blieb `pageant` auf 0.2.2 und `russh` auf
0.64 – beide mit Sicherheitsmeldungen, die erst 0.2.5 bzw. 0.65 beheben (beide verlangen 1.89). Am Code der
Bibliothek ändert das nichts; die Tests der Bibliothek laufen unverändert durch.
