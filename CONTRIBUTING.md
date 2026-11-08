# Contributing

Pull requests are welcome. Open one against `main`; small and focused changes are the easiest to review and merge.

Before you send it, from the repository root:

```powershell
dotnet build NexusSSH.slnx
dotnet test --solution NexusSSH.slnx
dotnet format NexusSSH.slnx --verify-no-changes
```

If you touched the Rust core, also run `cargo test`, `cargo clippy -- -D warnings` and `cargo fmt --check` for
`backend/Cargo.toml` (see the README for the full commands).

Code comments and the documentation in `docs/` are German; `FEATURES.md`, `CHANGELOG.md`, identifiers and commit messages are
English ([Conventional Commits](https://www.conventionalcommits.org/)). A pull request in English is fine.

## Translations

The interface speaks German, English, French and Spanish. **French and Spanish were machine-translated and have not
been proofread by a native speaker** — corrections are very welcome, and so are new languages. New languages are
not planned by the maintainer; they come in through pull requests.

All texts live in dictionaries in `frontend/NexusSSH.Core/Localization`, one per language, keyed by the same names:

| File      | Language            |
|-----------|---------------------|
| `L.cs`    | German and English  |
| `L.Fr.cs` | French              |
| `L.Es.cs` | Spanish             |

### Improving an existing translation

Edit the text on the right-hand side of the entry. Keep the key, the placeholders (`{0}`, `{1}` — their order may
change) and a trailing `…` where the other languages have one. Many texts are button labels or column headers with
little room, so keep them about as long as the English ones.

### Adding a language

1. Copy `L.Es.cs` to `L.<Code>.cs` (for example `L.It.cs`), rename the dictionary inside (`Es` → `It`) and
   translate every value. Translate from English; the German text is the original if the English is unclear.
2. In `L.cs`: append the language to the `AppLanguage` enum (**at the end** — the choice in the settings counts by
   position), and add it to `Init`, `FromCulture`, `Texts`, `Languages` and `Culture`.
3. In `frontend/NexusSSH.Core/ViewModels/ShellViewModel.Commands.cs`: add the names of the keys (Ctrl, Shift,
   Page Up, Page Down) to the `Named(...)` calls and the function below them.
4. In `frontend/NexusSSH.App/Views/SettingsView.xaml`: add a `ComboBoxItem` with the language's own name to
   `LanguageBox`, in the same position as in the enum.
5. Set `LanguageCode` in your dictionary to the two letters shown in the status bar.

The tests in `LocalizationTests` check that every language has the same keys and placeholders and that every text
formats — they will tell you what is missing. To see the result, pick the language in the settings and restart, or
start the app with sample data: `NexusSSH.exe --demo`.

Things that are *not* translated: names of protocols and programs (SSH, SFTP, sudo, …), file names and paths, and
the English messages of the Rust core, which appear only as a detail behind a translated sentence.
