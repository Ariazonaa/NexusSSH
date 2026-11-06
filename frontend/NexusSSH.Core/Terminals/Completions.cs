using NexusSSH.Core.Models;

namespace NexusSSH.Core.Terminals;

/// <summary>
/// Was die Terminal-Seite beim Tippen ergänzen kann – über die gespeicherten Befehle hinaus: Unterbefehle und
/// Schalter bekannter Programme (feste Tabellen) und die Namen im Verzeichnis der Shell. Die App schickt der Seite
/// **Listen von Kandidaten**; verglichen wird dort, mit dem, was seit dem letzten Enter getippt wurde. Nie
/// umgekehrt: Kein getipptes Zeichen verlässt die Seite (siehe <c>noteTyped</c> in <c>terminal.js</c>).
/// </summary>
public static class Completions
{
    /// <summary>Mehr Namen eines Verzeichnisses bekommt die Seite nicht; ein größeres wird nicht ergänzt.</summary>
    public const int MaxNames = 2000;

    /// <summary>Länger ist kein Name, den jemand ergänzt haben will.</summary>
    public const int MaxNameLength = 255;


    /// <summary>So viele Unterverzeichnisse bekommt die Seite samt Inhalt mit – für Pfade eine Ebene tiefer.</summary>
    public const int MaxChildDirectories = 16;

    /// <summary>Hat ein Unterverzeichnis mehr Einträge, geht es ohne Inhalt mit.</summary>
    public const int MaxChildren = 200;

    /// <summary>So viele Programmnamen, Dienste, Container und Hosts gehen höchstens an die Seite.</summary>
    public const int MaxWords = 4000;

    /// <summary>
    /// Die Namen der Programme im Suchpfad des Servers, wie die Seite sie als erstes Wort einer Zeile ergänzen darf.
    /// Der Kern hat sie schon geprüft; hier noch einmal, weil sie getippt werden: Buchstaben, Ziffern,
    /// <c>.</c>, <c>_</c>, <c>+</c>, <c>-</c>, vorn ein Buchstabe oder eine Ziffer.
    /// </summary>
    public static IReadOnlyList<string> CommandNames(IEnumerable<string?> names)
        => Words(names, static c => c is '.' or '_' or '+' or '-', 64);

    /// <summary>Die Adressen der gespeicherten Server, für <c>ssh</c>, <c>scp</c> und Verwandte: nur schlichte Hostnamen.</summary>
    public static IReadOnlyList<string> Hosts(IEnumerable<ServerProfile> profiles)
        => Words(profiles.Where(profile => !profile.IsSerial).Select(profile => profile.Host), static c => c is '.' or '_' or '-', 255);

    private static List<string> Words(IEnumerable<string?> words, Func<char, bool> alsoAllowed, int maxLength)
        =>
        [
            .. words
                .OfType<string>()
                .Where(word => word.Length > 0 && word.Length <= maxLength
                    && char.IsAsciiLetterOrDigit(word[0])
                    && word.All(c => char.IsAsciiLetterOrDigit(c) || alsoAllowed(c)))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(word => word, StringComparer.Ordinal)
                .Take(MaxWords),
        ];

    /// <summary>Die Tabellen der bekannten Programme. Reiner ASCII-Text, von Hand gepflegt.</summary>
    public static IReadOnlyList<CommandTable> Tables { get; } =
    [
        new("git",
            [
                "add", "bisect", "blame", "branch", "checkout", "cherry-pick", "clean", "clone", "commit", "config", "diff", "fetch", "grep",
                "init", "log", "merge", "mv", "pull", "push", "rebase", "reflog", "remote", "reset", "restore", "revert", "rm", "show",
                "stash", "status", "switch", "tag", "worktree",
            ],
            ["--version", "--help", "--no-pager", "-C"],
            new Dictionary<string, string[]>
            {
                ["add"] = ["--all", "--patch", "--update", "-A", "-p", "-u"],
                ["branch"] = ["--all", "--delete", "--move", "--remotes", "--show-current", "-a", "-d", "-D", "-m", "-r"],
                ["checkout"] = ["--track", "-b", "-B", "--"],
                ["clean"] = ["--dry-run", "--force", "-d", "-f", "-n", "-x"],
                ["clone"] = ["--branch", "--depth", "--recurse-submodules", "--single-branch"],
                ["commit"] = ["--all", "--amend", "--message", "--no-edit", "--no-verify", "--signoff", "-a", "-m", "-s"],
                ["diff"] = ["--cached", "--name-only", "--staged", "--stat"],
                ["fetch"] = ["--all", "--prune", "--tags"],
                ["log"] = ["--all", "--graph", "--oneline", "--patch", "--stat", "-n", "-p"],
                ["merge"] = ["--abort", "--continue", "--ff-only", "--no-ff", "--squash"],
                ["pull"] = ["--ff-only", "--rebase", "--tags"],
                ["push"] = ["--force-with-lease", "--set-upstream", "--tags", "-u"],
                ["rebase"] = ["--abort", "--continue", "--interactive", "--onto", "--skip", "-i"],
                ["reset"] = ["--hard", "--mixed", "--soft"],
                ["restore"] = ["--source", "--staged", "--worktree"],
                ["stash"] = ["apply", "drop", "list", "pop", "push", "show"],
                ["status"] = ["--branch", "--short", "-s"],
                ["switch"] = ["--create", "--detach", "-c"],
            },
            ["add", "blame", "checkout", "diff", "mv", "restore", "rm"]),
        new("docker",
            [
                "build", "compose", "cp", "exec", "images", "info", "inspect", "logs", "network", "ps", "pull", "push", "restart", "rm",
                "rmi", "run", "start", "stats", "stop", "system", "tag", "top", "volume",
            ],
            ["--help", "--version"],
            new Dictionary<string, string[]>
            {
                ["build"] = ["--file", "--no-cache", "--pull", "--tag", "-f", "-t"],
                ["compose"] = ["build", "down", "exec", "logs", "ps", "pull", "restart", "up"],
                ["exec"] = ["--env", "--interactive", "--tty", "--user", "--workdir", "-e", "-it", "-u", "-w"],
                ["images"] = ["--all", "--quiet", "-a", "-q"],
                ["logs"] = ["--follow", "--since", "--tail", "--timestamps", "-f", "-n", "-t"],
                ["ps"] = ["--all", "--filter", "--quiet", "-a", "-q"],
                ["rm"] = ["--force", "--volumes", "-f", "-v"],
                ["run"] = ["--detach", "--env", "--name", "--network", "--publish", "--rm", "--volume", "-d", "-e", "-it", "-p", "-v"],
                ["system"] = ["df", "info", "prune"],
            },
            ["build"]),
        new("systemctl",
            [
                "cat", "daemon-reload", "disable", "edit", "enable", "is-active", "is-enabled", "list-timers", "list-unit-files",
                "list-units", "mask", "reload", "restart", "show", "start", "status", "stop", "unmask",
            ],
            ["--failed", "--full", "--no-pager", "--now", "--type=service", "--user", "-l"],
            new Dictionary<string, string[]>(),
            []),
        new("apt", Apt, AptFlags, new Dictionary<string, string[]>(), []),
        new("apt-get", Apt, AptFlags, new Dictionary<string, string[]>(), []),
        new("journalctl",
            [],
            [
                "--boot", "--follow", "--grep", "--lines", "--no-pager", "--output", "--priority", "--reverse", "--since", "--unit",
                "--until", "-b", "-e", "-f", "-k", "-n", "-o", "-p", "-r", "-u", "-x",
            ],
            new Dictionary<string, string[]>(),
            []),
        new("tar",
            [],
            [
                "--create", "--directory", "--exclude=", "--extract", "--file", "--gzip", "--list", "--strip-components=", "--verbose",
                "--xz", "-C", "-cf", "-czf", "-tf", "-tzf", "-xf", "-xzf",
            ],
            new Dictionary<string, string[]>(),
            []),
    ];

    /// <summary>Programme, hinter denen ein Name aus dem Verzeichnis der Shell erwartet wird.</summary>
    public static IReadOnlyList<string> PathCommands { get; } =
    [
        ".", "bat", "cat", "cd", "chgrp", "chmod", "chown", "cp", "diff", "du", "file", "head", "less", "ln", "ls", "mkdir", "more", "mv",
        "nano", "pushd", "rm", "rmdir", "source", "stat", "tail", "tar", "touch", "unzip", "vi", "vim", "wc", "zip",
    ];

    /// <summary>Davon die, hinter denen nur ein Verzeichnis Sinn hat.</summary>
    public static IReadOnlyList<string> DirectoryCommands { get; } = ["cd", "pushd", "rmdir"];

    private static string[] Apt => ["autoremove", "full-upgrade", "install", "list", "policy", "purge", "remove", "search", "show", "update", "upgrade"];

    private static string[] AptFlags => ["--dry-run", "--no-install-recommends", "--yes", "-y"];

    /// <summary>
    /// Die Namen eines Verzeichnisses, wie die Seite sie ergänzen darf. Sie kommen vom Server: Ein Name mit einem
    /// Steuerzeichen oder einem Zeichen, das die Schreibrichtung dreht, fällt weg (er ließe sich weder sicher
    /// zeigen noch tippen), ebenso ein überlanger; hat das Verzeichnis mehr Namen als die Obergrenze, gibt es keine
    /// – eine halbe Liste verspräche, was sie nicht hält.
    /// </summary>
    public static IReadOnlyList<PathName> Names(IReadOnlyList<RemoteEntry> entries)
    {
        if (entries.Count > MaxNames)
        {
            return [];
        }

        return
        [
            .. entries
                .Where(entry => IsPlain(entry.Name))
                .OrderBy(entry => entry.Name, StringComparer.Ordinal)
                .Select(entry => new PathName(entry.Name, entry.IsDirectory)),
        ];
    }

    private static bool IsPlain(string name)
        => name.Length is > 0 and <= MaxNameLength
            && name is not ("." or "..")
            && !name.Contains('/')
            && Format.Visible(name) == name
            && !name.Any(char.IsControl);
}

/// <summary>Ein bekanntes Programm: seine Unterbefehle, seine Schalter, die Schalter je Unterbefehl, und hinter welchen Unterbefehlen ein Pfad steht.</summary>
public sealed record CommandTable(
    string Command,
    IReadOnlyList<string> Subs,
    IReadOnlyList<string> Flags,
    IReadOnlyDictionary<string, string[]> SubFlags,
    IReadOnlyList<string> PathSubs);

/// <summary>
/// Ein Name im Verzeichnis der Shell; <see cref="Dir"/> = ein Verzeichnis. <see cref="Children"/>: bei einigen
/// Verzeichnissen ihr Inhalt, für Pfade eine Ebene tiefer – <c>null</c>, wo er nicht mitgeht.
/// </summary>
public sealed record PathName(string Name, bool Dir, IReadOnlyList<PathName>? Children = null);
