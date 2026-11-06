using NexusSSH.Core.Models;
using NexusSSH.Core.Services;

namespace NexusSSH.Core.Mock;

/// <summary>Kleines Dateisystem im Speicher. Verzeichnisse entstehen aus den Pfaden der Dateien.</summary>
public sealed class MockRemoteFileSystem : IRemoteFileSystem
{
    // Fester Bezugspunkt statt „jetzt“: Die Liste sieht bei jedem Start gleich aus, Tests bleiben stabil.
    private static readonly DateTimeOffset Reference = new(2026, 9, 28, 9, 30, 0, TimeSpan.Zero);

    private readonly Dictionary<string, List<RemoteEntry>> _directories = new(StringComparer.Ordinal)
    {
        [RemotePath.Root] = [],
    };

    private readonly MockTransferQueue? _transfers;
    private readonly string _server;

    /// <param name="transfers">Wohin Up- und Downloads gemeldet werden; ohne Angabe wirken sie sofort und lautlos.</param>
    public MockRemoteFileSystem(string initialPath, MockTransferQueue? transfers = null, string server = "mock")
    {
        _transfers = transfers;
        _server = server;
        InitialPath = RemotePath.Normalize(initialPath);
        EnsureDirectory(InitialPath, hoursAgo: 0);
    }

    public string InitialPath { get; }

    /// <summary>Was „heruntergeladen“ wurde: Pfad auf dem Server und lokaler Zielordner. Für Tests.</summary>
    public List<(string RemotePath, string LocalDirectory)> Downloads { get; } = [];

    /// <summary>Was „hochgeladen“ wurde: lokaler Pfad und Verzeichnis auf dem Server. Für Tests.</summary>
    public List<(string LocalPath, string RemoteDirectory)> Uploads { get; } = [];

    /// <summary>Legt eine Datei an; fehlende Verzeichnisse auf dem Weg entstehen mit.</summary>
    public MockRemoteFileSystem File(string path, long size, int hoursAgo)
    {
        var normalized = RemotePath.Normalize(path);
        var parent = RemotePath.Parent(normalized) ?? RemotePath.Root;
        EnsureDirectory(parent, hoursAgo);
        _directories[parent].RemoveAll(entry => entry.Name == RemotePath.Name(normalized));
        _directories[parent].Add(new RemoteEntry(RemotePath.Name(normalized), IsDirectory: false, size, Reference.AddHours(-hoursAgo), Permissions: 0b110_100_100, Owner: "root", Group: "root"));
        return this;
    }

    /// <summary>Legt einen Ordner an, auch einen leeren; fehlende Verzeichnisse auf dem Weg entstehen mit.</summary>
    public MockRemoteFileSystem Folder(string path, int hoursAgo)
    {
        EnsureDirectory(RemotePath.Normalize(path), hoursAgo);
        return this;
    }

    /// <summary>Legt etwas an, das weder Datei noch Ordner ist – eine Pipe, ein Gerät, ein Socket.</summary>
    public MockRemoteFileSystem Special(string path)
    {
        var normalized = RemotePath.Normalize(path);
        var parent = RemotePath.Parent(normalized) ?? RemotePath.Root;
        EnsureDirectory(parent, hoursAgo: 0);
        _directories[parent].Add(new RemoteEntry(RemotePath.Name(normalized), IsDirectory: false, Size: 0, Reference, Permissions: 0b110_100_100, Owner: "root", Group: "root", IsSpecial: true));
        return this;
    }

    /// <summary>
    /// Legt einen symbolischen Link an. <paramref name="target"/> steht so im Link, wie es hier steht; relativ gilt es
    /// vom Ordner des Links aus. Gibt es das Ziel, nimmt der Link dessen Art und Größe an – sonst zeigt er ins Leere
    /// (<see cref="RemoteEntry.IsDangling"/>, wie vom Kern gemeldet: weder Datei noch Ordner).
    /// </summary>
    public MockRemoteFileSystem Link(string path, string target)
    {
        var normalized = RemotePath.Normalize(path);
        var parent = RemotePath.Parent(normalized) ?? RemotePath.Root;
        EnsureDirectory(parent, hoursAgo: 0);
        var name = RemotePath.Name(normalized);
        var resolved = target.StartsWith('/') ? RemotePath.Normalize(target) : RemotePath.Combine(parent, target);
        _directories[parent].RemoveAll(entry => entry.Name == name);
        _directories[parent].Add(Find(resolved) is { } destination
            ? destination with { Name = name, IsLink = true, LinkTarget = target }
            : new RemoteEntry(name, IsDirectory: false, Size: 0, Reference, IsLink: true, Permissions: 0b111_111_111, Owner: "root", Group: "root", IsSpecial: true, LinkTarget: target, IsDangling: true));
        return this;
    }

    /// <summary>
    /// Ob die Liste das Ziel eines Links nennt. Ausgeschaltet verhält sich das Dateisystem wie ein Dateizugriff über
    /// Befehle: kein Ziel, und ob ein Link ins Leere zeigt, ist unbekannt. Für Tests.
    /// </summary>
    public bool ListsLinkTargets { get; set; } = true;

    /// <summary>Ob <see cref="ReadLinkAsync"/> das Ziel nennt – ausgeschaltet wie bei einem Server, der <c>readlink</c> nicht kann (DSM). Für Tests.</summary>
    public bool ReadsLinks { get; set; } = true;

    private readonly HashSet<string> _unlisted = new(StringComparer.Ordinal);

    /// <summary>
    /// Legt einen Link an, dessen Ziel die Liste nicht nennt (wie bei einem Dateizugriff über Befehle, DSM): kein Ziel,
    /// ob er ins Leere zeigt, ist unbekannt – nur <see cref="ReadLinkAsync"/> nennt es auf Nachfrage.
    /// </summary>
    public MockRemoteFileSystem LinkUnlisted(string path, string target)
    {
        Link(path, target);
        _unlisted.Add(RemotePath.Normalize(path));
        return this;
    }

    private static RemoteEntry WithoutTarget(RemoteEntry entry)
        => entry with { LinkTarget = null, IsDangling = false, IsSpecial = entry.IsSpecial && !entry.IsDangling };

    /// <summary>Wonach <see cref="ReadLinkAsync"/> gefragt wurde, der Reihe nach. Für Tests.</summary>
    public List<string> LinksRead { get; } = [];

    public Task<string?> ReadLinkAsync(string path, CancellationToken cancellationToken = default) => Run<string?>(() =>
    {
        var normalized = RemotePath.Normalize(path);
        LinksRead.Add(normalized);
        if (!ReadsLinks)
        {
            throw new RemoteFileException($"{path}: Operation unsupported", "failed");
        }

        var entry = Find(normalized) ?? throw new RemoteFileException($"{path}: No such file or directory", "not_found");
        return entry is { IsLink: true, LinkTarget: { } target }
            ? target
            : throw new RemoteFileException($"{path}: not a link", "failed");
    });

    /// <summary>Synchroner Zugriff für das Mock-Terminal (<c>ls</c>).</summary>
    public IReadOnlyList<RemoteEntry> List(string path)
    {
        if (!_directories.TryGetValue(RemotePath.Normalize(path), out var entries))
        {
            throw new RemoteFileException($"{path}: No such file or directory", "not_found");
        }

        // Wie der Kern: Ordner zuerst, dann die Namen so, wie man sie liest (datei-2 vor datei-10).
        return [.. entries
            .OrderByDescending(entry => entry.IsDirectory)
            .ThenBy(entry => entry.Name, Format.NameOrder)];
    }

    public Task<IReadOnlyList<RemoteEntry>> ListAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Run(() =>
        {
            Listed.Add(RemotePath.Normalize(path));
            if (FailNextList is { } code)
            {
                FailNextList = null;
                throw new RemoteFileException("Timeout", code);
            }

            if (Denied.Contains(RemotePath.Normalize(path)))
            {
                throw new RemoteFileException("Permission denied", "permission_denied");
            }

            // Ohne Ziele in der Liste ist auch „ins Leere“ unbekannt – der Link ist dann nur ein Link, kein Sonderfall.
            var folder = RemotePath.Normalize(path);
            return (IReadOnlyList<RemoteEntry>)[.. List(path).Select(entry => entry.IsLink && (!ListsLinkTargets || _unlisted.Contains(RemotePath.Combine(folder, entry.Name))) ? WithoutTarget(entry) : entry)];
        });
    }

    /// <summary>Welche Verzeichnisse aufgelistet wurden, der Reihe nach. Für Tests.</summary>
    public List<string> Listed { get; } = [];

    /// <summary>Verzeichnisse, die sich betreten, aber nicht auflisten lassen (wie <c>/home</c> ohne Leserecht). Für Tests.</summary>
    public HashSet<string> Denied { get; } = new(StringComparer.Ordinal);

    public Task CreateDirectoryAsync(string path) => Run(() =>
    {
        var normalized = RemotePath.Normalize(path);
        if (Find(normalized) is not null)
        {
            throw new RemoteFileException($"{path} already exists", "failed");
        }

        Entries(RemotePath.Parent(normalized) ?? RemotePath.Root, path);
        EnsureDirectory(normalized, hoursAgo: 0);
        return true;
    });

    public Task CreateFileAsync(string path) => Run(() =>
    {
        var normalized = RemotePath.Normalize(path);
        if (Find(normalized) is not null)
        {
            throw new RemoteFileException($"{path} already exists", "exists");
        }

        Entries(RemotePath.Parent(normalized) ?? RemotePath.Root, path);
        File(normalized, size: 0, hoursAgo: 0);
        return true;
    });

    /// <summary>Das Konto darf keine Links anlegen (wie unter Windows ohne Adminrechte). Für Tests.</summary>
    public bool LinksDenied { get; set; }

    public Task CreateLinkAsync(string path, string target) => Run(() =>
    {
        var (link, source) = (RemotePath.Normalize(path), RemotePath.Normalize(target));
        if (LinksDenied)
        {
            throw new RemoteFileException("Permission denied", "permission_denied");
        }

        if (Find(link) is not null)
        {
            throw new RemoteFileException($"{path} already exists", "exists");
        }

        var entry = Find(source) ?? throw new RemoteFileException($"{target}: No such file or directory", "not_found");
        Entries(RemotePath.Parent(link) ?? RemotePath.Root, path).Add(entry with { Name = RemotePath.Name(link), IsLink = true, LinkTarget = target });
        return true;
    });

    public Task RenameAsync(string from, string to) => Run(() =>
    {
        var (source, target) = (RemotePath.Normalize(from), RemotePath.Normalize(to));
        var entry = Find(source) ?? throw new RemoteFileException($"{from}: No such file or directory", "not_found");
        if (Find(target) is not null)
        {
            throw new RemoteFileException($"{to} already exists", "exists");
        }

        var targetParent = Entries(RemotePath.Parent(target) ?? RemotePath.Root, to);
        Entries(RemotePath.Parent(source)!, from).Remove(entry);
        targetParent.Add(entry with { Name = RemotePath.Name(target) });

        // Ein Verzeichnis nimmt alles darunter mit.
        foreach (var key in _directories.Keys.Where(key => key == source || key.StartsWith(source + "/", StringComparison.Ordinal)).ToList())
        {
            _directories.Remove(key, out var moved);
            _directories[target + key[source.Length..]] = moved!;
        }

        return true;
    });

    // Wie `cp -RPp` und `mv`: Ein Verzeichnis nimmt alles darunter mit; was am Ziel liegt, wird ersetzt.
    public Task CopyAsync(IReadOnlyList<(string From, string To)> pairs, bool move = false) => Run(() =>
    {
        foreach (var (from, to) in pairs)
        {
            var (source, target) = (RemotePath.Normalize(from), RemotePath.Normalize(to));
            var entry = Find(source) ?? throw new RemoteFileException($"{from}: No such file or directory", "not_found");
            if (target == source || target.StartsWith(source + "/", StringComparison.Ordinal))
            {
                throw new RemoteFileException($"{to} cannot be passed to a command", "unsafe_name");
            }

            var targetParent = Entries(RemotePath.Parent(target) ?? RemotePath.Root, to);
            targetParent.RemoveAll(existing => existing.Name == RemotePath.Name(target));
            targetParent.Add(entry with { Name = RemotePath.Name(target) });
            foreach (var key in _directories.Keys.Where(key => key == source || key.StartsWith(source + "/", StringComparison.Ordinal)).ToList())
            {
                _directories[target + key[source.Length..]] = [.. _directories[key]];
                if (move)
                {
                    _directories.Remove(key);
                }
            }

            if (move)
            {
                Entries(RemotePath.Parent(source)!, from).Remove(entry);
            }
        }

        return true;
    });

    /// <summary>Für Tests: was <c>sudo</c> verlangt – <c>null</c> = nichts, leer = es gibt kein <c>sudo</c>.</summary>
    public string? SudoPassword { get; set; }

    public bool IsElevated { get; private set; }

    public Task ElevateAsync(string? password = null) => Run(() =>
    {
        IsElevated = SudoPassword switch
        {
            null => true,
            "" => throw new RemoteFileException("sudo is not installed on the server", "no_sudo"),
            _ when password is null => throw new RemoteFileException("sudo asks for a password", "sudo_password"),
            var expected when password == expected => true,
            _ => throw new RemoteFileException("Sorry, try again.", "sudo_refused"),
        };
        return true;
    });

    private readonly Dictionary<string, byte[]> _contents = new(StringComparer.Ordinal);

    /// <summary>Gibt einer Datei einen Inhalt (und legt sie an) – für Vorschau und Editor.</summary>
    public MockRemoteFileSystem Content(string path, byte[] content, int hoursAgo = 1)
    {
        File(path, content.Length, hoursAgo);
        _contents[RemotePath.Normalize(path)] = content;
        return this;
    }

    public MockRemoteFileSystem Content(string path, string text, int hoursAgo = 1) => Content(path, System.Text.Encoding.UTF8.GetBytes(text), hoursAgo);

    /// <summary>Der Inhalt, den eine Datei gerade hat; für Tests.</summary>
    public byte[] ContentOf(string path) => Bytes(RemotePath.Normalize(path), Find(RemotePath.Normalize(path))!);

    // Die Beispieldaten haben keinen Inhalt; gezeigt wird ein ausgedachter, der zur Art der Datei passt.
    private byte[] Bytes(string path, RemoteEntry entry)
        => _contents.TryGetValue(path, out var content) ? content : System.Text.Encoding.UTF8.GetBytes(Sample(entry.Name));

    private static string Sample(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".json" => "{\n  \"name\": \"demo\",\n  \"port\": 8080,\n  \"debug\": false\n}\n",
        ".yml" or ".yaml" => "# Beispiel\nservices:\n  web:\n    image: \"nginx:1.27\"\n    ports:\n      - 8080\n",
        ".html" => "<!doctype html>\n<html>\n  <!-- Beispiel -->\n  <body class=\"demo\">Hello</body>\n</html>\n",
        _ => $"# {name} – Beispielinhalt\n[server]\nlisten = 8080\nname = \"demo\"  # nur zum Ansehen\n",
    };

    public Task<FilePart> ReadAsync(string path, long offset, int length) => Run(() =>
    {
        var normalized = RemotePath.Normalize(path);
        var entry = Find(normalized) ?? throw new RemoteFileException($"{path}: No such file or directory", "not_found");
        if (entry.IsDirectory || entry.IsSpecial)
        {
            throw new RemoteFileException($"{path} is not a regular file", "not_a_file");
        }

        var content = Bytes(normalized, entry);
        var start = (int)Math.Clamp(offset, 0, content.Length);
        return new FilePart(content[start..Math.Min(content.Length, start + Math.Clamp(length, 0, FilePart.MaxLength))], content.Length);
    });

    public Task WriteAsync(string path, ReadOnlyMemory<byte> content) => Run(() =>
    {
        var normalized = RemotePath.Normalize(path);
        if (Find(normalized) is { IsDirectory: true })
        {
            throw new RemoteFileException($"{path} is not a regular file", "not_a_file");
        }

        Content(normalized, content.ToArray(), hoursAgo: 0);
        return true;
    });

    /// <summary>Wer gerade welche Datei mitliest – für Tests, die Text „schreiben“ (<see cref="Append"/>).</summary>
    private readonly List<(string Path, Action<string> Text, Action<string?> Ended)> _readers = [];

    // Die Beispieldaten haben keinen Inhalt: Mitgelesen wird, was eine Demo zeigen kann – ein paar Zeilen, die wie
    // ein Protokoll aussehen. Einen Ordner oder nichts kann man nicht mitlesen.
    public IDisposable? Follow(string path, Action<string> text, Action<string?> ended)
    {
        var normalized = RemotePath.Normalize(path);
        if (Find(normalized) is not { IsDirectory: false })
        {
            ended($"tail: cannot open '{path}' for reading: No such file or directory");
            return null;
        }

        var reader = (normalized, text, ended);
        _readers.Add(reader);
        text($"[{RemotePath.Name(normalized)}] service started\n[{RemotePath.Name(normalized)}] listening on :8080\n");
        return new Reader(() => _readers.Remove(reader));
    }

    /// <summary>Hängt Text an eine Datei – wer sie mitliest, bekommt ihn.</summary>
    public void Append(string path, string text)
    {
        foreach (var reader in _readers.Where(reader => reader.Path == RemotePath.Normalize(path)).ToList())
        {
            reader.Text(text);
        }
    }

    private sealed class Reader(Action stop) : IDisposable
    {
        public void Dispose() => stop();
    }

    /// <summary>Für Tests: die Summe, die der „Server“ für einen Pfad nennt – statt der ausgedachten.</summary>
    public Func<string, string?>? Checksum { get; set; }

    // Die Beispieldaten haben keinen Inhalt: Als Summe dient, was eine Datei ausmacht – Name und Größe.
    public Task<IReadOnlyList<string?>> ChecksumsAsync(IReadOnlyList<string> paths) => Run<IReadOnlyList<string?>>(() =>
    [
        .. paths.Select(path => Find(RemotePath.Normalize(path)) is not { IsDirectory: false } file ? null
            : Checksum is { } own ? own(RemotePath.Normalize(path))
            : Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{file.Name}:{file.Size}")))),
    ]);

    public Task<IReadOnlyList<FolderUsage>> UsageAsync(string path) => Run<IReadOnlyList<FolderUsage>>(() =>
    {
        var directory = RemotePath.Normalize(path);
        long Below(string folder) => _directories
            .Where(entry => entry.Key == folder || entry.Key.StartsWith(folder.TrimEnd('/') + "/", StringComparison.Ordinal))
            .Sum(entry => entry.Value.Where(file => !file.IsDirectory).Sum(file => file.Size));
        return
        [
            .. Entries(directory, path).Select(entry => new FolderUsage(
                RemotePath.Combine(directory, entry.Name),
                entry.IsDirectory ? Below(RemotePath.Combine(directory, entry.Name)) : entry.Size)),
            new FolderUsage(directory, Below(directory)),
        ];
    });

    public Task DeleteAsync(string path) => Run(() =>
    {
        var normalized = RemotePath.Normalize(path);
        var entry = Find(normalized) ?? throw new RemoteFileException($"{path}: No such file or directory", "not_found");
        Entries(RemotePath.Parent(normalized)!, path).Remove(entry);
        foreach (var key in _directories.Keys.Where(key => key == normalized || key.StartsWith(normalized + "/", StringComparison.Ordinal)).ToList())
        {
            _directories.Remove(key);
        }

        return true;
    });

    public Task SetPermissionsAsync(string path, int mode, bool recursive = false) => Run(() =>
    {
        // Wie der Kern: In einem ganzen Baum bleiben Ordner betretbar, wo gelesen werden darf.
        var bits = mode & Format.PermissionBits;
        Update(path, recursive, (entry, inTree) => entry with { Permissions = inTree && entry.IsDirectory ? bits | ((bits & 0b100_100_100) >> 2) : bits });
        return true;
    });

    public Task SetOwnerAsync(string path, string? owner, string? group, bool recursive = false) => Run(() =>
    {
        Update(path, recursive, (entry, _) => entry with { Owner = owner ?? entry.Owner, Group = group ?? entry.Group });
        return true;
    });

    /// <summary>Wie oft der Kanal für Dateien neu geöffnet wurde. Für Tests.</summary>
    public int Reopened { get; private set; }

    /// <summary>Lässt das nächste Auflisten mit dieser Fehlerart scheitern – ein Dateisystem, das nicht antwortet. Für Tests.</summary>
    public string? FailNextList { get; set; }

    public Task ReopenAsync() => Run(() =>
    {
        IsElevated = false;
        return ++Reopened;
    });

    /// <summary>Was die nächste Frage nach einem Baum scheitern lässt – für Tests.</summary>
    public string? FailNextTree { get; set; }

    // Wie der Kern: alles unter dem Verzeichnis, dessen Name den Text enthält – nahe Treffer zuerst.
    /// <summary>Für Tests: Die nächste Suche meldet, sie habe nach so vielen Einträgen aufgehört.</summary>
    public long? FindStopsAfter { get; set; }

    public Task<FoundEntries> FindAsync(string path, string name) => Run(() => new FoundEntries(Find(path, name), FindStopsAfter));

    private IReadOnlyList<RemoteTreeEntry> Find(string path, string name)
    {
        var root = RemotePath.Normalize(path);
        if (!_directories.ContainsKey(root))
        {
            throw new RemoteFileException($"{path}: No such file or directory", "not_found");
        }

        var needle = name.Trim();
        var prefix = root == RemotePath.Root ? root : root + "/";
        return [.. _directories
            .Where(directory => directory.Key == root || directory.Key.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(directory => directory.Key.Count(character => character == '/'))
            .ThenBy(directory => directory.Key, StringComparer.Ordinal)
            .SelectMany(directory => directory.Value
                .Where(entry => needle.Length > 0 && entry.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))
                .Select(entry => new RemoteTreeEntry(RemotePath.Combine(directory.Key, entry.Name), string.Empty, entry.IsDirectory, entry.Size, entry.Modified)))];
    }

    /// <summary>Was gepackt und entpackt wurde, der Reihe nach – für Tests.</summary>
    public List<string> Archived { get; } = [];

    // Nachgespielt: Das Archiv entsteht als Datei, entpackt wird nichts.
    public Task PackAsync(string directory, IReadOnlyList<string> names, string archive) => Run(() =>
    {
        var target = RemotePath.Combine(RemotePath.Normalize(directory), archive);
        if (Find(target) is not null)
        {
            throw new RemoteFileException("the archive already exists", "command_failed");
        }

        Archived.Add($"pack {target}: {string.Join(", ", names)}");
        File(target, size: 1024, hoursAgo: 0);
        return true;
    });

    public Task UnpackAsync(string directory, string archive) => Run(() =>
    {
        var source = RemotePath.Combine(RemotePath.Normalize(directory), archive);
        if (Find(source) is null)
        {
            throw new RemoteFileException($"tar: {archive}: Cannot open: No such file or directory", "command_failed");
        }

        Archived.Add($"unpack {source}");
        return true;
    });

    // Wie der Kern: erst die Verzeichnisse, jedes vor denen darin, dann die Dateien; Links und Pipes fehlen.
    public Task<IReadOnlyList<RemoteTreeEntry>> TreeAsync(string path, IReadOnlyList<string>? exclude = null, bool excluded = false) => Run<IReadOnlyList<RemoteTreeEntry>>(() =>
    {
        if (FailNextTree is { } code)
        {
            FailNextTree = null;
            throw new RemoteFileException("refused", code);
        }

        var normalized = RemotePath.Normalize(path);
        if (LocalTarget(normalized, string.Empty) is not { } top || Find(normalized) is not { } entry)
        {
            throw new RemoteFileException($"{path}: No such file or directory", "not_found");
        }

        if (!entry.IsDirectory)
        {
            return [new RemoteTreeEntry(normalized, top, false, entry.Size, entry.Modified)];
        }

        var directories = new List<RemoteTreeEntry> { new(normalized, top, true, 0, entry.Modified) };
        var files = new List<RemoteTreeEntry>();
        var rules = new TransferExcludes(exclude);
        var skipped = new List<RemoteTreeEntry>();
        for (var index = 0; index < directories.Count; index++)
        {
            var directory = directories[index];
            foreach (var child in _directories[directory.RemotePath].OrderBy(child => child.Name, StringComparer.Ordinal))
            {
                if (child.IsSpecial || (child.IsLink && !child.IsDirectory))
                {
                    continue;
                }

                var remote = RemotePath.Combine(directory.RemotePath, child.Name);
                var local = directory.LocalPath + "/" + Path.GetFileName(LocalTarget(remote, string.Empty));
                if (rules.Skips(remote[(normalized.TrimEnd('/').Length + 1)..]))
                {
                    skipped.Add(new RemoteTreeEntry(remote, local, child.IsDirectory, 0, child.Modified));
                    continue;
                }

                (child.IsDirectory ? directories : files).Add(new RemoteTreeEntry(remote, local, child.IsDirectory, child.IsDirectory ? 0 : child.Size, child.Modified));
            }
        }

        return excluded ? [.. skipped] : [.. directories, .. files];
    });

    // Ändert einen Eintrag und, wenn verlangt, alles darunter.
    private void Update(string path, bool recursive, Func<RemoteEntry, bool, RemoteEntry> change)
    {
        var normalized = RemotePath.Normalize(path);
        var entry = Find(normalized) ?? throw new RemoteFileException($"{path}: No such file or directory", "not_found");
        var entries = Entries(RemotePath.Parent(normalized)!, path);
        var inTree = recursive && entry.IsDirectory;
        entries[entries.IndexOf(entry)] = change(entry, inTree);
        if (!inTree)
        {
            return;
        }

        foreach (var key in _directories.Keys.Where(key => key == normalized || key.StartsWith(normalized + "/", StringComparison.Ordinal)).ToList())
        {
            _directories[key] = [.. _directories[key].Select(child => change(child, true))];
        }
    }

    /// <summary>Für Tests: mit welcher Regel und unter welchem Namen jeder Transfer begann.</summary>
    public List<(string Source, TransferKeep Keep, string? Name)> Rules { get; } = [];

    /// <summary>Für Tests: mit welchen Ausschlüssen jeder Transfer begann.</summary>
    public List<IReadOnlyList<string>> Excludes { get; } = [];

    public string Upload(string localPath, string remoteDirectory, string? remoteName = null, TransferKeep keep = TransferKeep.None, IReadOnlyList<string>? exclude = null)
    {
        Excludes.Add(exclude ?? []);
        Rules.Add((localPath, keep, remoteName));
        var name = remoteName ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(localPath));
        long size = System.IO.File.Exists(localPath) ? new FileInfo(localPath).Length : 0;
        var directory = RemotePath.Normalize(remoteDirectory);
        var entries = Entries(directory, remoteDirectory);
        entries.RemoveAll(entry => entry.Name == name);
        entries.Add(new RemoteEntry(name, IsDirectory: false, size, Reference));
        Uploads.Add((localPath, directory));
        return _transfers?.Add(name, _server, TransferDirection.Upload, size) ?? string.Empty;
    }

    // Das nachgespielte Dateisystem kennt den Abgleich als einen Transfer nicht: Der Aufrufer überträgt einzeln.
    public string? UploadSome(string localFolder, string remoteFolder, IReadOnlyList<string> files) => null;

    public string? DownloadSome(string remoteFolder, string localFolder, IReadOnlyList<string> files) => null;

    // Zwei nachgespielte Dateisysteme kennen einander nicht: Es bleibt der Weg über den Zwischenordner.
    public string? SendTo(string remotePath, IRemoteFileSystem target, string targetDirectory, string? targetName = null, TransferKeep keep = TransferKeep.None, IReadOnlyList<string>? exclude = null) => null;

    public string Download(string remotePath, string localDirectory, long? limit = null, TransferKeep keep = TransferKeep.None, string? localName = null, IReadOnlyList<string>? exclude = null)
    {
        Excludes.Add(exclude ?? []);
        Rules.Add((RemotePath.Normalize(remotePath), keep, localName));
        Downloads.Add((RemotePath.Normalize(remotePath), localDirectory));
        return _transfers?.Add(RemotePath.Name(remotePath), _server, TransferDirection.Download, Find(RemotePath.Normalize(remotePath))?.Size ?? 0)
            ?? string.Empty;
    }

    // Wie der Kern, nur einfacher: Was hier kein Dateiname sein kann, wird ersetzt.
    public string? LocalTarget(string remotePath, string localDirectory)
    {
        var name = RemotePath.Name(RemotePath.Normalize(remotePath));
        return name.Length == 0
            ? null
            : Path.Combine(localDirectory, string.Join('_', name.Split(Path.GetInvalidFileNameChars())));
    }

    // Die Summe aller Dateien unter dem Verzeichnis – was `du` auf einem Server ohne Blockgrößen nennen würde.
    public Task<long?> FolderSizeAsync(string path) => Run<long?>(() =>
    {
        var directory = RemotePath.Normalize(path);
        Entries(directory, path);
        return _directories
            .Where(entry => entry.Key == directory || entry.Key.StartsWith(directory.TrimEnd('/') + "/", StringComparison.Ordinal))
            .Sum(entry => entry.Value.Where(file => !file.IsDirectory).Sum(file => file.Size));
    });

    private RemoteEntry? Find(string normalized)
        => RemotePath.Parent(normalized) is { } parent && _directories.TryGetValue(parent, out var entries)
            ? entries.FirstOrDefault(entry => entry.Name == RemotePath.Name(normalized))
            : null;

    private List<RemoteEntry> Entries(string directory, string original)
        => _directories.TryGetValue(directory, out var entries)
            ? entries
            : throw new RemoteFileException($"{original}: No such file or directory", "not_found");

    private void EnsureDirectory(string path, int hoursAgo)
    {
        if (_directories.ContainsKey(path))
        {
            return;
        }

        var parent = RemotePath.Parent(path) ?? RemotePath.Root;
        EnsureDirectory(parent, hoursAgo);
        _directories[path] = [];
        _directories[parent].Add(new RemoteEntry(RemotePath.Name(path), IsDirectory: true, Size: 0, Reference.AddHours(-hoursAgo), Permissions: 0b111_101_101, Owner: "root", Group: "root"));
    }

    // Fehler als gescheiterte Aufgabe statt als Ausnahme beim Aufruf – so verhält sich auch das echte Backend.
    private static Task<T> Run<T>(Func<T> operation)
    {
        try
        {
            return Task.FromResult(operation());
        }
        catch (RemoteFileException e)
        {
            return Task.FromException<T>(e);
        }
    }
}
