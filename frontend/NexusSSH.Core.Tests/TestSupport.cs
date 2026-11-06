using System.Text;
using System.Text.Json;
using NexusSSH.Core.Backend;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Mock;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Führt alles sofort auf dem aufrufenden Thread aus – Tests haben keinen UI-Thread.</summary>
internal sealed class ImmediateDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}

/// <summary>Beantwortet Rückfragen mit vorbereiteten Antworten und merkt sich, was gefragt wurde.</summary>
internal sealed class FakePrompts : IUserPrompts
{
    public List<ConnectionPrompt> Asked { get; } = [];

    /// <summary>Antwort auf die Host-Schlüssel-Frage.</summary>
    public bool TrustHostKey { get; set; } = true;

    /// <summary>Antwort auf die nächste Geheimnis-Frage; <c>null</c> = abbrechen.</summary>
    public SecretAnswer? Secret { get; set; }

    /// <summary>Wenn gesetzt, bleiben Rückfragen offen, bis sie abgebrochen werden – wie ein Dialog, den niemand bedient.</summary>
    public bool NeverAnswer { get; set; }

    /// <summary>Antwort auf eine Ja/Nein-Frage; die Fragen selbst stehen in <see cref="Confirmed"/>.</summary>
    public bool Confirm { get; set; }

    public List<string> Confirmed { get; } = [];

    /// <summary>Antwort auf die Frage nach einem Befehl; die Fragen selbst stehen in <see cref="Commands"/>.</summary>
    public bool AnswerCommand { get; set; }

    public List<(string Server, string Command)> Commands { get; } = [];

    /// <summary>Ob die letzte Frage sagte, dass in ein Terminal getippt wird.</summary>
    public bool InTerminal { get; private set; }

    /// <summary>Läuft, während nach einem Befehl gefragt wird.</summary>
    public Action? Asking { get; set; }

    public async Task<bool> ConfirmCommandAsync(string server, string command, bool inTerminal, CancellationToken cancellation)
    {
        Commands.Add((server, command));
        InTerminal = inTerminal;
        Asking?.Invoke();
        await WaitAsync(cancellation);
        return AnswerCommand;
    }

    public Task<bool> ConfirmAsync(string title, string text, string confirm)
    {
        Confirmed.Add(title);
        return Task.FromResult(Confirm);
    }

    public int Cancelled { get; private set; }

    public async Task<bool> ConfirmHostKeyAsync(ConnectionPrompt.HostKey prompt, CancellationToken cancellation)
    {
        Asked.Add(prompt);
        await WaitAsync(cancellation);
        return TrustHostKey;
    }

    public async Task<SecretAnswer?> AskSecretAsync(ConnectionPrompt.Secret prompt, CancellationToken cancellation)
    {
        Asked.Add(prompt);
        await WaitAsync(cancellation);
        return Secret;
    }

    private async Task WaitAsync(CancellationToken cancellation)
    {
        if (!NeverAnswer)
        {
            return;
        }

        // Ohne RunContinuationsAsynchronously läuft die Fortsetzung noch im Cancel()-Aufruf – der Test sieht
        // das Ergebnis also sofort.
        var pending = new TaskCompletionSource();
        using var registration = cancellation.Register(() => pending.TrySetCanceled(cancellation));
        try
        {
            await pending.Task;
        }
        catch (OperationCanceledException)
        {
            Cancelled++;
            throw;
        }
    }
}

internal static class TestShell
{
    /// <summary>Shell über dem Mock-Backend ohne Timer; die Sprache ist fest Englisch.</summary>
    public static ShellViewModel Create(out MockBackend backend, AppSettings? settings = null, FakePrompts? prompts = null)
    {
        L.Init(AppLanguage.English);
        backend = new MockBackend();
        return new ShellViewModel(backend, new ImmediateDispatcher(), settings ?? new AppSettings(), prompts ?? new FakePrompts());
    }

    /// <summary>Shell über einem Backend, dessen Verbindungen der Test selbst führt.</summary>
    public static ShellViewModel Create(ScriptedBackend backend, FakePrompts? prompts = null)
    {
        L.Init(AppLanguage.English);
        return new ShellViewModel(backend, new ImmediateDispatcher(), new AppSettings(), prompts ?? new FakePrompts());
    }
}

/// <summary>Ein Backend, dessen Verbindungen der Test von Hand durch ihre Zustände führt.</summary>
internal sealed class ScriptedBackend(params ServerProfile[] profiles) : INexusBackend, IProfileStore, ITransferQueue
{
    public List<ScriptedConnection> Connections { get; } = [];

    public ScriptedConnection Last => Connections[^1];

    public IProfileStore Profiles => this;

    public ITransferQueue Transfers => this;

    public IReadOnlyList<TransferItem> Items => [];

    event Action? IProfileStore.Changed
    {
        add { }
        remove { }
    }

    event Action? ITransferQueue.Changed
    {
        add { }
        remove { }
    }

    public event Action<string>? Warning
    {
        add { }
        remove { }
    }

    public void SetKeepAlive(int seconds, int missed)
    {
    }

    public void SetX11Display(int display)
    {
    }

    /// <summary>Was die Shell an Tunneln schalten wollte: Verbindung, Kennung, an oder aus.</summary>
    public List<(IServerConnection Connection, string Tunnel, bool On)> TunnelRequests { get; } = [];

    public event Action<TunnelNotice>? TunnelChanged;

    public void SetTunnel(IServerConnection connection, string tunnelId, bool on) => TunnelRequests.Add((connection, tunnelId, on));

    /// <summary>Spielt eine Meldung des Kerns über einen Tunnel ein.</summary>
    public void Report(TunnelNotice notice) => TunnelChanged?.Invoke(notice);

    public Task<ProbeReport> ProbeAsync(ServerProfile profile, CancellationToken cancellation = default)
        => Task.FromResult(new ProbeReport("methods", []));

    /// <summary>Die Shells, die dieses Backend für ein lokales Terminal nennt.</summary>
    public List<LocalShell> Shells { get; } = [];

    public Task<IReadOnlyList<LocalShell>> LocalShellsAsync(CancellationToken cancellation = default)
        => Task.FromResult<IReadOnlyList<LocalShell>>([.. Shells]);

    public void RememberUnlockedKeys(bool remember)
    {
    }

    public void ForgetUnlockedKeys()
    {
    }

    public IServerConnection Connect(ServerProfile profile, IServerConnection? share = null)
    {
        Connections.Add(new ScriptedConnection(profile, share as ScriptedConnection));
        return Last;
    }

    public IReadOnlyList<ServerProfile> GetProfiles() => profiles;

    public ServerProfile Save(ServerProfile profile) => profile;

    public void Delete(string id)
    {
    }

    public SshConfigListing ReadSshConfig() => new(null, []);

    public IReadOnlyList<KnownHost> KnownHosts() => [];

    public bool ForgetHost(string name, string? authority = null) => false;

    public KnownHost TrustAuthority(string patterns, string key) => new(patterns, "ssh-ed25519", "SHA256:test", true);

    public int ImportSshConfig(IReadOnlyList<string> aliases, bool linked = false) => 0;

    public GeneratedKey GenerateKey(string name, string? passphrase, string comment) => new(name, "ssh-ed25519 AAAA test");

    public void ForgetSecrets(string id)
    {
    }

    public void Cancel(string id)
    {
    }

    public void Suspend()
    {
    }

    public void Pause(string id)
    {
    }

    public bool Move(string id, QueueMove move) => false;

    public void PauseAll()
    {
    }

    public void SetLimit(long bytesPerSecond)
    {
    }

    public void ClearFinished()
    {
    }

    public bool Retry(string id) => false;
}

/// <param name="Shared">Die Verbindung, die beim Aufbau zum Mitbenutzen genannt wurde.</param>
internal sealed class ScriptedConnection(ServerProfile profile, ScriptedConnection? shared) : IServerConnection, ITerminalSession
{
    public event Action<ReadOnlyMemory<byte>>? Output;

    public event Action<ConnectionStatus>? StatusChanged;

    public event Action<ConnectionPrompt>? PromptRequested;

    public event Action<string>? Banner;

    public ServerProfile Profile { get; } = profile;

    public ScriptedConnection? Shared { get; } = shared;

    public int Starts { get; private set; }

    public bool Started => Starts > 0;

    public bool Disposed { get; private set; }

    public (int Columns, int Rows) Size { get; private set; }

    public List<string> Input { get; } = [];

    /// <summary>Jeder Aufruf von <see cref="SetPaused"/>, in der Reihenfolge.</summary>
    public List<bool> Paused { get; } = [];

    public ITerminalSession Terminal => this;

    public IRemoteFileSystem? Files => null;

    public IServerMonitor? Monitor { get; set; }

    public ConnectionStatus Status { get; private set; } = new(ConnectionState.Idle);

    public void Start() => Starts++;

    /// <summary>Was geschrieben wurde, Byte für Byte – für Zeichensätze, die kein UTF-8 sind.</summary>
    public List<byte[]> RawInput { get; } = [];

    public void Write(ReadOnlyMemory<byte> input)
    {
        Input.Add(Encoding.UTF8.GetString(input.Span));
        RawInput.Add(input.ToArray());
    }

    public void Resize(int columns, int rows) => Size = (columns, rows);

    public void SetPaused(bool paused) => Paused.Add(paused);

    public void Dispose() => Disposed = true;

    public void Set(ConnectionStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(status);
    }

    public void Emit(string text) => Output?.Invoke(Encoding.UTF8.GetBytes(text));

    public void EmitBytes(byte[] data) => Output?.Invoke(data);

    public void Ask(ConnectionPrompt prompt) => PromptRequested?.Invoke(prompt);

    public void ShowBanner(string text) => Banner?.Invoke(text);
}

/// <summary>Editor, der nichts startet: merkt sich, was geöffnet wurde, und „speichert“ auf Zuruf.</summary>
internal sealed class FakeEditorHost : IEditorHost
{
    private readonly Dictionary<string, Action> _watched = [];

    public List<string> Launched { get; } = [];

    /// <summary>Lässt den Start des Editors scheitern.</summary>
    public bool Fails { get; set; }

    public IReadOnlyCollection<string> Watched => _watched.Keys;

    public bool Launch(string localPath)
    {
        if (Fails)
        {
            return false;
        }

        Launched.Add(localPath);
        return true;
    }

    public IDisposable Watch(string localPath, Action saved)
    {
        _watched[localPath] = saved;
        return new Unwatch(() => _watched.Remove(localPath));
    }

    /// <summary>Was zum Vergleichen geöffnet wurde: linke und rechte Datei.</summary>
    public List<(string Left, string Right)> Compared { get; } = [];

    /// <summary>Was das Vergleichsprogramm antwortet.</summary>
    public CompareOutcome Comparing { get; set; } = CompareOutcome.Started;

    public CompareOutcome Compare(string left, string right)
    {
        if (Comparing == CompareOutcome.Started)
        {
            Compared.Add((left, right));
        }

        return Comparing;
    }

    /// <summary>Der Benutzer hat im Editor gespeichert.</summary>
    public void Save(string localPath) => _watched[localPath]();

    private sealed class Unwatch(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}

/// <summary>
/// Lokale Pfade für Tests, die nur als Text durchgereicht oder zerlegt werden. Die Tests laufen auch unter Linux
/// (CI): Ein Pfad wie <c>C:\data\a.txt</c> ist dort ein einziger Dateiname, weil <c>\</c> kein Trenner ist.
/// </summary>
internal static class LocalPath
{
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\" : "/";

    public static string Of(params string[] parts) => Path.Combine([Root, .. parts]);

    /// <summary>Wie <see cref="Of"/>, mit abschließendem Trenner – so, wie ein Ordner oft übergeben wird.</summary>
    public static string Folder(params string[] parts) => Of(parts) + Path.DirectorySeparatorChar;
}

/// <summary>Ein eigener Ordner je Test, der am Ende wieder verschwindet.</summary>
internal sealed class TempFolder : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("nexusssh-test-").FullName;

    public string File(string name, string content = "")
    {
        var path = System.IO.Path.Combine(Path, name);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>
/// Steht für den Rust-Kern: merkt sich jede Anfrage und antwortet mit dem, was der Test vorgibt. Ereignisse und
/// Terminal-Ausgabe löst der Test selbst aus.
/// </summary>
internal sealed class FakeTransport : ICoreTransport
{
    public List<JsonElement> Requests { get; } = [];

    public List<(ulong Session, string Text)> Written { get; } = [];

    /// <summary>Antwort je Anfrageart; ohne Eintrag kommt <c>{"ok":true}</c>.</summary>
    public Dictionary<string, string> Responses { get; } = new()
    {
        ["profiles.list"] = """{"ok":true,"profiles":[]}""",
    };

    public bool Disposed { get; private set; }

    public event Action<string>? Event;

    public event Action<ulong, ReadOnlyMemory<byte>>? Output;

    public string Request(string json)
    {
        var request = JsonDocument.Parse(json).RootElement;
        Requests.Add(request);
        return Responses.GetValueOrDefault(request.GetProperty("op").GetString()!, """{"ok":true}""");
    }

    public bool Write(ulong session, ReadOnlySpan<byte> data)
    {
        Written.Add((session, System.Text.Encoding.UTF8.GetString(data)));
        return true;
    }

    public void Raise(string json) => Event?.Invoke(json);

    public void RaiseOutput(ulong session, string text) => Output?.Invoke(session, System.Text.Encoding.UTF8.GetBytes(text));

    /// <summary>Die Anfragen einer Art, in der Reihenfolge ihres Eintreffens.</summary>
    public IEnumerable<JsonElement> Sent(string op) => Requests.Where(request => request.GetProperty("op").GetString() == op);

    public void Dispose() => Disposed = true;
}

/// <summary>
/// <see cref="L"/> ist prozessweit. Tests, die die Sprache umstellen oder von ihr abhängen, laufen deshalb in
/// dieser Collection nacheinander.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LanguageCollection
{
    public const string Name = "Language";
}
